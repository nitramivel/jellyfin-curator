using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Configuration;
using Jellyfin.Plugin.Curator.Core.Context;
using Jellyfin.Plugin.Curator.Core.HomeScreen;
using Jellyfin.Plugin.Curator.Core.Llm;
using Jellyfin.Plugin.Curator.Services.HomeScreen;
using Jellyfin.Plugin.Curator.Services.Llm;
using Jellyfin.Plugin.Curator.Services.Runs;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services.Context
{
    /// <summary>
    /// What one refresh of the context rows did.
    /// </summary>
    /// <param name="Rows">Rows registered.</param>
    /// <param name="TitlesBought">Sets of titles bought from a model this pass.</param>
    /// <param name="TitlesCulled">Stored sets dropped as expired or obsolete.</param>
    /// <param name="Places">Distinct locations read.</param>
    /// <param name="Error">What went wrong, or null.</param>
    public sealed record ContextRowRefreshResult(
        int Rows,
        int TitlesBought,
        int TitlesCulled,
        int Places,
        string? Error = null);

    /// <summary>
    /// Decides what the context rows are showing, names them, and publishes them.
    ///
    /// <para>
    /// This is the one place that reads the weather, chooses a title and registers a
    /// row, and they belong together for a reason that is easy to miss: the title is
    /// fixed at registration and the contents are worked out when the home screen
    /// asks. Decided in two places they drift, and a row titled for rain fills
    /// itself from a clear sky. So this writes a <see cref="ContextRowSnapshot"/>
    /// naming the exact conditions each row was registered for, and the row draws
    /// its cards from that rather than from the clock.
    /// </para>
    ///
    /// <para>
    /// Everything expensive is cached against something that repeats. Coordinates
    /// resolve once per process; conditions are read at most every half hour;
    /// titles are bought once per <i>set of conditions</i> and then rotated, so a
    /// place that produces thirty distinct conditions costs thirty calls in total
    /// rather than two per refresh forever.
    /// </para>
    /// </summary>
    public class ContextRowService
    {
        private readonly IWeatherService _weatherService;
        private readonly IContextRowStore _store;
        private readonly IHomeScreenIntegrationService _homeScreen;
        private readonly ILlmProviderFactory _providerFactory;
        private readonly IUserManager _userManager;
        private readonly IRunLogStore _runLogStore;
        private readonly ILogger<ContextRowService> _logger;

        /// <summary>
        /// Serializes refreshes, because two of them racing spends twice for nothing.
        /// </summary>
        /// <remarks>
        /// Both Curator tasks that publish rows carry a startup trigger — this one,
        /// and Publish Home Screen Rows, which refreshes context rows too because it
        /// is the one that retries while the other plugin finishes starting. At
        /// startup they therefore run at the same moment, and without this they both
        /// read the title store, both miss the cache for the current conditions, and
        /// both buy the same set of titles. Measured on the owner's server: two runs
        /// logged at the identical millisecond, $0.0026 and $0.0034, for one
        /// condition that should have cost $0.0034 once.
        /// <para>
        /// The second caller <em>waits</em> rather than backing out, and then finds
        /// the titles the first one cached and spends nothing. Backing out would be
        /// cheaper by one registration write and would lose the retry the redundancy
        /// exists for.
        /// </para>
        /// </remarks>
        private readonly SemaphoreSlim _refreshLock = new(1, 1);

        public ContextRowService(
            IWeatherService weatherService,
            IContextRowStore store,
            IHomeScreenIntegrationService homeScreen,
            ILlmProviderFactory providerFactory,
            IUserManager userManager,
            IRunLogStore runLogStore,
            ILogger<ContextRowService> logger)
        {
            _weatherService = weatherService;
            _store = store;
            _homeScreen = homeScreen;
            _providerFactory = providerFactory;
            _userManager = userManager;
            _runLogStore = runLogStore;
            _logger = logger;
        }

        /// <summary>The last refresh, for the health check and the config page.</summary>
        public ContextRowRefreshResult? LastResult { get; private set; }

        /// <summary>
        /// Reads the weather, titles the rows, and publishes them.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What it did.</returns>
        public async Task<ContextRowRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private async Task<ContextRowRefreshResult> RefreshCoreAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.ContextRows)
            {
                return Finish(new ContextRowRefreshResult(0, 0, 0, 0));
            }

            if (config.SectionDelivery != SectionDelivery.Integrated)
            {
                // These rows have no playlist, and Collection Sections can only show
                // a row by naming one. Said plainly rather than left as two rows that
                // never appear.
                _logger.LogInformation(
                    "Curator: the context rows need the Curator row source; they are not published under Collection Sections");
                return Finish(new ContextRowRefreshResult(0, 0, 0, 0));
            }

            var perUser = config.WeatherLocationMode == WeatherLocationMode.PerUser;
            var users = ResolveTargetUsers(config);
            if (users.Count == 0)
            {
                _logger.LogInformation("Curator: no target users, so there is nobody to publish context rows for");
                return Finish(new ContextRowRefreshResult(0, 0, 0, 0));
            }

            // One read per distinct place, not per viewer. Six viewers in one town is
            // one call to a free public API, not six.
            var places = perUser
                ? users.Select(config.LocationFor).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [config.WeatherLocation?.Trim() ?? string.Empty];

            foreach (var place in places.Where(p => p.Length > 0))
            {
                await _weatherService.RefreshAsync(place, cancellationToken).ConfigureAwait(false);
            }

            var titles = new Dictionary<string, ContextTitleSet>(_store.GetTitles(), StringComparer.Ordinal);
            var snapshots = new List<ContextRowSnapshot>();
            var registrations = new List<ContextRowRegistration>();
            var bought = 0;

            var utcNow = DateTime.UtcNow;
            var titler = config.ContextRowTitleMode == ContextRowTitleMode.Model
                ? OpenTitler(config)
                : null;

            try
            {
                // Shared rows when everyone is under one sky; a row each when they are
                // not. Duplicating an identical row per viewer would multiply the
                // registrations and change nothing anybody sees.
                var audiences = perUser
                    ? users.Select(u => (Owner: u, Audience: (IReadOnlyList<Guid>)[u])).ToList()
                    : [(Owner: Guid.Empty, Audience: (IReadOnlyList<Guid>)users)];

                foreach (var (owner, audience) in audiences)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var built = await BuildRowAsync(
                        config, owner, audience, perUser, titles, titler, utcNow, cancellationToken)
                        .ConfigureAwait(false);

                    if (built is not { } row)
                    {
                        continue;
                    }

                    bought += row.Bought ? 1 : 0;
                    snapshots.Add(row.Snapshot);
                    registrations.Add(row.Registration);
                }
            }
            finally
            {
                titler?.RunLog?.Complete();
            }

            // Culled AFTER the draws above stamped LastUsedUtc, so the conditions in
            // play right now can never be culled out from under the row using them.
            var (kept, expired, obsolete) = ContextTitles.Prune(
                [.. titles.Values],
                utcNow,
                config.ContextTitleRetentionDays,
                ContextTitlePromptBuilder.StyleVersion);

            _store.SaveTitles(kept);
            _store.SaveSnapshots(snapshots);

            if (expired + obsolete > 0)
            {
                _logger.LogInformation(
                    "Curator: culled {Expired} unused and {Obsolete} obsolete set(s) of row titles",
                    expired,
                    obsolete);
            }

            var published = registrations.Count > 0
                && await _homeScreen.SyncContextRowsAsync(registrations, cancellationToken).ConfigureAwait(false);

            if (!published && registrations.Count > 0)
            {
                return Finish(new ContextRowRefreshResult(
                    0, bought, expired + obsolete, places.Count, "Home Screen Sections did not accept the rows"));
            }

            _logger.LogInformation(
                "Curator: published {Rows} context row(s) across {Places} location(s); {Bought} title set(s) bought, {Culled} culled",
                registrations.Count,
                places.Count,
                bought,
                expired + obsolete);

            return Finish(new ContextRowRefreshResult(registrations.Count, bought, expired + obsolete, places.Count));
        }

        private sealed record BuiltRow(
            ContextRowSnapshot Snapshot,
            ContextRowRegistration Registration,
            bool Bought);

        /// <summary>
        /// Works out one row: its conditions, its title, and its registration.
        /// </summary>
        private async Task<BuiltRow?> BuildRowAsync(
            PluginConfiguration config,
            Guid owner,
            IReadOnlyList<Guid> audience,
            bool perUser,
            Dictionary<string, ContextTitleSet> titles,
            Titler? titler,
            DateTime utcNow,
            CancellationToken cancellationToken)
        {
            var location = perUser ? config.LocationFor(owner) : config.WeatherLocation?.Trim() ?? string.Empty;
            var reading = _weatherService.Current(location);

            var localTime = reading.LocalTimeOfDay(utcNow) ?? DateTime.Now.TimeOfDay;
            var daypart = ContextVocabulary.DaypartFor(localTime);

            // Unlike the weather row it replaced, this one is still drawn without a
            // reading. It has a second half to stand on — the hour is always known —
            // so a server that cannot reach Open-Meteo loses precision here rather
            // than losing the row.
            var context = reading.IsUsable
                ? new ViewingContext(reading.Words, daypart)
                : ViewingContext.ClockOnly(daypart);

            var title = Named(config.ContextRowName, PluginConfiguration.DefaultContextRowName);
            var bought = false;

            if (titler is not null)
            {
                var condition = ContextTitles.ConditionKey(context);

                // A set written by an older generation of the prompt is treated as
                // absent rather than merely prunable. Pruning happens after the
                // draws, so honouring one here would show the old wording once more
                // and only replace it an hour later — on the conditions in play
                // right now, which is exactly where the owner is looking.
                if (!titles.TryGetValue(condition, out var set)
                    || set.Style != ContextTitlePromptBuilder.StyleVersion)
                {
                    set = null;

                    var written = await titler
                        .WriteAsync(context, config.ContextTitlesPerCondition, cancellationToken)
                        .ConfigureAwait(false);

                    if (written.Count > 0)
                    {
                        set = new ContextTitleSet(
                            condition, written, 0, utcNow, titler.ModelId, ContextTitlePromptBuilder.StyleVersion);
                        bought = true;
                    }
                }

                if (set is not null && ContextTitles.Draw(set, ContextTitles.OffsetFor(owner), utcNow) is { } drawn)
                {
                    title = drawn.Title;
                    titles[condition] = drawn.Updated;
                }
            }

            var sectionId = perUser
                ? SectionConfigMerger.ContextSectionIdFor(owner)
                : SectionConfigMerger.ContextSectionId;

            var section = new DesiredSection(
                sectionId,
                title,
                Math.Max(0, config.MaxContextRowItems),
                config.EffectiveContextRowOrderIndex);

            var snapshot = new ContextRowSnapshot(
                sectionId,
                owner,
                context.Weather,
                context.Daypart,
                title,
                reading.Place,
                utcNow);

            var registration = new ContextRowRegistration(
                new SectionRegistrationRequest(
                    section,
                    CuratorContextSectionResults.ContextRowKey,
                    typeof(CuratorContextSectionResults)),
                audience);

            return new BuiltRow(snapshot, registration, bought);
        }

        /// <summary>
        /// The model, its profile's rates, and the run log the calls are recorded in.
        /// </summary>
        /// <remarks>
        /// Opened only when a model is actually going to be asked for something —
        /// hard rule 12's gate. Most refreshes find every condition already in the
        /// cache and buy nothing, and a free pass writing a run log several times a
        /// day would evict the category runs from a directory that keeps fifty.
        /// </remarks>
        private sealed class Titler
        {
            public required ILlmProvider Provider { get; init; }

            public required RunLogModel Model { get; init; }

            public IRunLog? RunLog { get; set; }

            public required Func<IRunLog> OpenLog { get; init; }

            public required ILogger Logger { get; init; }

            public string ModelId => Provider.ModelId;

            public async Task<IReadOnlyList<string>> WriteAsync(
                ViewingContext context,
                int count,
                CancellationToken cancellationToken)
            {
                var wanted = Math.Clamp(count, 1, 12);
                var request = new LlmRequest(
                    ContextTitlePromptBuilder.BuildSystemPrompt(wanted),
                    ContextTitlePromptBuilder.BuildUserPrompt(context, wanted),
                    string.Empty,

                    // Small answer, small cap. Thinking counts against this, so it is
                    // not as tight as the output alone would need.
                    MaxOutputTokens: 1200,
                    ResponseShape.ContextTitles);

                RunLog ??= OpenLog();
                var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

                try
                {
                    var result = await Provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                    var titles = ContextTitlePromptBuilder.Parse(result.Text);

                    RunLog.LlmCall(
                        "titles",
                        1,
                        1,
                        null,
                        System.Diagnostics.Stopwatch.GetElapsedTime(startedAt),
                        request,
                        result,
                        titles.Count > 0 ? "ok" : "unusable",
                        titles.Count > 0 ? null : "No usable titles in the response",
                        Model);

                    return titles;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Recorded, because a call that threw was still billed — and then
                    // swallowed, because the fallback is the owner's own row name and
                    // a failed title must not cost anybody their row.
                    RunLog.LlmCall(
                        "titles",
                        1,
                        1,
                        null,
                        System.Diagnostics.Stopwatch.GetElapsedTime(startedAt),
                        request,
                        null,
                        "failed",
                        ex.Message,
                        Model);

                    Logger.LogWarning(ex, "Curator: could not write row titles; falling back to the configured name");
                    return [];
                }
            }
        }

        private Titler? OpenTitler(PluginConfiguration config)
        {
            try
            {
                // Resolved exactly the way every other pass resolves its profile, so
                // a blank id lands on the default rather than on nothing.
                var profile = ModelProfiles.Resolve(config, config.ContextTitleModelProfileId);
                var provider = _providerFactory.Create(profile, config.EnableThinking);

                return new Titler
                {
                    Provider = provider,
                    Model = new RunLogModel(
                        profile.Provider.ToString(),
                        provider.ModelId,
                        new RunLogPricing(
                            profile.InputCostPerMillion,
                            profile.CachedInputCostPerMillion,
                            profile.OutputCostPerMillion,
                            profile.InputCostPerMillion > 0 || profile.OutputCostPerMillion > 0)),
                    Logger = _logger,
                    OpenLog = () =>
                    {
                        var log = _runLogStore.Begin(
                            "context-titles",
                            new Dictionary<string, object?>
                            {
                                ["titleMode"] = config.ContextRowTitleMode.ToString(),
                                ["titlesPerCondition"] = config.ContextTitlesPerCondition,
                                ["retentionDays"] = config.ContextTitleRetentionDays,
                                ["locationMode"] = config.WeatherLocationMode.ToString(),
                            },
                            trackAsCurrent: false);

                        // Hard rule 12: a pass that opens a log must name its model,
                        // or the Runs tab renders the whole run as "unknown model".
                        log.SetProvider(
                            profile.Provider.ToString(),
                            provider.ModelId,
                            profile.InputCostPerMillion,
                            profile.OutputCostPerMillion,
                            profile.CachedInputCostPerMillion);
                        return log;
                    },
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Curator: could not open a model for row titles; the rows keep their configured names");
                return null;
            }
        }

        private IReadOnlyList<Guid> ResolveTargetUsers(PluginConfiguration config)
        {
            if (config.TargetUsers.Length > 0)
            {
                return [.. config.TargetUsers.Where(id => _userManager.GetUserById(id) is not null)];
            }

            try
            {
                return [.. _userManager.GetUsers().Select(u => u.Id)];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Curator: could not read the user list for context rows");
                return [];
            }
        }

        private static string Named(string? configured, string fallback)
            => string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();

        private ContextRowRefreshResult Finish(ContextRowRefreshResult result)
        {
            LastResult = result;
            return result;
        }
    }
}
