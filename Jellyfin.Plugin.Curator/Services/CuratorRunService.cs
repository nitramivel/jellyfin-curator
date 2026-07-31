using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Configuration;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Llm;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Core.Reconciliation;
using Jellyfin.Plugin.Curator.Services.Categories;
using Jellyfin.Plugin.Curator.Services.HomeScreen;
using Jellyfin.Plugin.Curator.Services.Library;
using Jellyfin.Plugin.Curator.Services.Llm;
using Jellyfin.Plugin.Curator.Services.Playlists;
using Jellyfin.Plugin.Curator.Services.Runs;
using Jellyfin.Plugin.Curator.Services.Summaries;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services
{
    /// <summary>
    /// What a playlist reconcile changed.
    /// </summary>
    /// <param name="CategoriesRebuilt">Categories that regained a missing playlist.</param>
    /// <param name="CategoriesRemoved">Definitions deleted for holding no playlist.</param>
    /// <param name="PlaylistsRemoved">Curator-owned playlists no definition claimed.</param>
    public sealed record PlaylistSyncResult(
        int CategoriesRebuilt,
        int CategoriesRemoved,
        int PlaylistsRemoved);

    /// <summary>
    /// The end-to-end run: scan → propose → reconcile → build playlists →
    /// publish home screen rows. Shared by the scheduled task and the manual
    /// trigger so both take exactly the same path.
    /// </summary>
    public class CuratorRunService : IDisposable
    {
        private readonly ILibraryScanner _libraryScanner;
        private readonly IUserActivityProvider _userActivityProvider;
        private readonly LlmProviderFactory _providerFactory;
        private readonly CategoryProposalService _proposalService;
        private readonly ICategoryStore _categoryStore;
        private readonly ICuratorPlaylistService _playlistService;
        private readonly IHomeScreenIntegrationService _homeScreenService;
        private readonly IUserManager _userManager;
        private readonly IRunLogStore _runLogStore;
        private readonly ISummaryStore _summaryStore;
        private readonly ILogger<CuratorRunService> _logger;
        private readonly SemaphoreSlim _runLock = new(1, 1);

        /// <summary>
        /// Cancelled when the host disposes this service, which is Jellyfin tearing
        /// down its container — on shutdown, and on every plugin install or update.
        /// <para>
        /// This is a best effort, not a guarantee. It lets a run stop at its next
        /// checkpoint instead of blundering into disposed services, but disposal
        /// order across singletons is not defined, so a run can still be caught
        /// mid-call. <see cref="RunFailure.IsHostTeardown"/> catches what this
        /// misses; the two are a pair.
        /// </para>
        /// </summary>
        private readonly CancellationTokenSource _shutdown = new();

        private bool _disposed;

        /// <summary>
        /// Stored definitions already reused by this run. Reset per run; only ever
        /// touched from the single thread walking the pipeline.
        /// </summary>
        private readonly HashSet<Guid> _claimedDefinitions = [];

        public CuratorRunService(
            ILibraryScanner libraryScanner,
            IUserActivityProvider userActivityProvider,
            LlmProviderFactory providerFactory,
            CategoryProposalService proposalService,
            ICategoryStore categoryStore,
            ICuratorPlaylistService playlistService,
            IHomeScreenIntegrationService homeScreenService,
            IUserManager userManager,
            IRunLogStore runLogStore,
            ISummaryStore summaryStore,
            ILogger<CuratorRunService> logger)
        {
            _libraryScanner = libraryScanner;
            _userActivityProvider = userActivityProvider;
            _providerFactory = providerFactory;
            _proposalService = proposalService;
            _categoryStore = categoryStore;
            _playlistService = playlistService;
            _homeScreenService = homeScreenService;
            _userManager = userManager;
            _runLogStore = runLogStore;
            _summaryStore = summaryStore;
            _logger = logger;
        }

        /// <summary>
        /// Gets a value indicating whether a run is currently in progress.
        /// </summary>
        public bool IsRunning => _runLock.CurrentCount == 0;

        /// <summary>
        /// Gets the run currently in progress, or null when nothing is running.
        /// Exposed so the configuration page can follow a run it just started.
        /// </summary>
        public Guid? CurrentRunId { get; private set; }

        /// <summary>
        /// Runs the full pipeline. Only one run happens at a time; a second
        /// caller is rejected rather than queued, since runs cost money.
        /// </summary>
        /// <param name="progress">Progress reporter (0-100).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="trigger">What started this run, for the run log.</param>
        /// <returns>A task.</returns>
        public async Task RunAsync(
            IProgress<double>? progress,
            CancellationToken cancellationToken,
            string trigger = "manual")
        {
            if (!await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("Curator: a run is already in progress; ignoring this request");
                return;
            }

            var config = Plugin.Instance?.Configuration
                ?? throw new InvalidOperationException("Curator: plugin configuration unavailable.");

            var runLog = _runLogStore.Begin(trigger, DescribeSettings(config));
            CurrentRunId = runLog.RunId;

            // The manual trigger has no token of its own to give us — an HTTP request
            // is long gone by the time a run finishes — so without this link a run
            // started from the config page observes nothing at all when the server
            // goes down. The scheduled task passes Jellyfin's token and always did.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _shutdown.Token);

            try
            {
                await RunCoreAsync(config, progress, runLog, linked.Token).ConfigureAwait(false);
                runLog.Complete();
            }
            catch (Exception ex) when (_shutdown.IsCancellationRequested || RunFailure.IsHostTeardown(ex))
            {
                // Not a fault in this plugin: the container went away underneath the
                // run. Say so plainly in both places, and do not rethrow — there is
                // nobody left to handle it, and a stack trace here reads as a defect.
                _logger.LogWarning("Curator: {Message}", RunFailure.HostTeardownMessage);
                runLog.Fail(RunFailure.HostTeardownMessage);
            }
            catch (Exception ex)
            {
                // Recorded, then rethrown unchanged — the caller still logs and
                // handles it exactly as before.
                runLog.Fail(ex.Message);
                throw;
            }
            finally
            {
                CurrentRunId = null;
                _runLock.Release();
            }
        }

        /// <summary>
        /// Signals any run in progress to stop. Jellyfin disposes its container both
        /// on shutdown and on every plugin install or update.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases resources held by this service.
        /// </summary>
        /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed || !disposing)
            {
                return;
            }

            _disposed = true;

            if (IsRunning)
            {
                _logger.LogInformation(
                    "Curator: the server is shutting down or reloading plugins; asking the run in progress to stop");
            }

            _shutdown.Cancel();
            _shutdown.Dispose();
            _runLock.Dispose();
        }

        private async Task RunCoreAsync(
            PluginConfiguration config,
            IProgress<double>? progress,
            IRunLog runLog,
            CancellationToken cancellationToken)
        {
            _claimedDefinitions.Clear();

            // Resolved once and used for both the call and the costing. Pricing lives
            // on the profile, so reading it from anywhere else would report this run
            // at whatever the previously selected profile charged.
            var modelProfile = ModelProfiles.ResolveDefault(config);
            var provider = _providerFactory.Create(modelProfile, config.EnableThinking);
            runLog.SetProvider(
                modelProfile.Provider.ToString(),
                provider.ModelId,
                modelProfile.InputCostPerMillion,
                modelProfile.OutputCostPerMillion,
                modelProfile.CachedInputCostPerMillion);

            var targetUsers = ResolveTargetUsers(config);
            if (targetUsers.Count == 0)
            {
                _logger.LogWarning("Curator: no target users resolved; nothing to build");
                runLog.Step("users.resolved", "No target users resolved; nothing to build", new Dictionary<string, object?>
                {
                    ["userCount"] = 0,
                });
                return;
            }

            runLog.Step("users.resolved", $"Resolved {targetUsers.Count} target user(s)", new Dictionary<string, object?>
            {
                ["userCount"] = targetUsers.Count,
                ["userIds"] = targetUsers.Select(id => id.ToString()).ToArray(),
            });

            Report(progress, runLog, 2);

            // 1. Scan. Condensed summaries are substituted for overviews on the way
            // out when the owner has switched them on; an empty store simply means
            // nothing is substituted and the run behaves exactly as before.
            var condensed = LoadCondensedSummaries(config);
            var records = _libraryScanner.ScanLibrary(
                config.IncludeEpisodes,
                config.SurfacedCollections,
                ItemReducer.DefaultMaxOverviewLength,
                condensed);
            if (records.Count == 0)
            {
                _logger.LogWarning("Curator: library scan produced no items; nothing to categorize");
                runLog.Step("library.scanned", "Library scan produced no items", new Dictionary<string, object?>
                {
                    ["itemCount"] = 0,
                });
                return;
            }

            runLog.Step("library.scanned", $"Scanned {records.Count} library item(s)", new Dictionary<string, object?>
            {
                ["itemCount"] = records.Count,
                ["includeEpisodes"] = config.IncludeEpisodes,
            });

            Report(progress, runLog, 5);

            // Built once and handed to BOTH the prompt and the Reconciler. That is
            // the whole point of CategoryLimits: there is no second copy of these
            // numbers for the instruction and the enforcement to disagree over.
            var sharedLimits = new CategoryLimits(
                config.MinSharedCategorySize,
                config.MaxCategoryMembers,
                config.MaxSharedCategories);
            var personalLimits = new CategoryLimits(
                config.MinPersonalCategorySize,
                config.MaxCategoryMembers,
                config.MaxPersonalCategories);

            var settings = new ProposalRunSettings(
                config.BatchSize,
                config.MaxOutputTokens,
                config.TokenBudget,
                modelProfile.InputCostPerMillion,
                modelProfile.OutputCostPerMillion,
                modelProfile.CachedInputCostPerMillion,
                config.UseBatchApi,
                config.MaxTagsPerItem,
                sharedLimits,
                personalLimits);

            var personalized = config.OutputType == OutputKind.Playlist && config.PersonalizedPlaylists;

            var allCategoryIds = new HashSet<Guid>();
            var existing = _categoryStore.GetAll();

            // Phase A — shared discovery. One pass over the library with no watch
            // data at all, producing the candidate pool every viewer draws from.
            // Measured on this library, the per-user passes were reinventing the
            // same six themes under different names; discovering them once and
            // sharing the definition keeps one row per theme instead of one per
            // user per theme.
            var discovery = await _proposalService
                .ProposeAsync(provider, records, settings, activity: null, cancellationToken, runLog)
                .ConfigureAwait(false);

            if (discovery.Proposals.Count == 0)
            {
                _logger.LogWarning("Curator: the model proposed no usable categories; leaving existing playlists untouched");
                runLog.Step("discovery.empty", "The model proposed no usable categories", new Dictionary<string, object?>
                {
                    ["batchesCompleted"] = discovery.BatchesCompleted,
                    ["batchesSkipped"] = discovery.BatchesSkipped,
                });
                return;
            }

            var candidates = Reconciler.Reconcile(
                discovery.Proposals,
                new ReconcilerSettings(sharedLimits));

            _logger.LogInformation(
                "Curator: shared discovery produced {Count} candidate categories from {Proposals} proposals",
                candidates.Count,
                discovery.Proposals.Count);

            runLog.Step(
                "discovery.reconciled",
                $"Shared discovery produced {candidates.Count} candidate categories from {discovery.Proposals.Count} proposals",
                new Dictionary<string, object?>
                {
                    ["proposalCount"] = discovery.Proposals.Count,
                    ["candidateCount"] = candidates.Count,
                    ["batchesCompleted"] = discovery.BatchesCompleted,
                    ["batchesSkipped"] = discovery.BatchesSkipped,
                    ["candidates"] = candidates
                        .Select(c => new Dictionary<string, object?>
                        {
                            ["name"] = c.Name,
                            ["description"] = c.Description,
                            ["memberCount"] = c.Members.Count,
                            ["sourceProposalCount"] = c.SourceProposalCount,
                        })
                        .ToArray(),
                });

            Report(progress, runLog, 40);

            if (!personalized)
            {
                // Every category is shared and every target user gets it.
                foreach (var category in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var definition = MergeIntoStore(existing, category, provider.ModelId, ownerUserId: null, runLog);
                    allCategoryIds.Add(definition.Id);
                    await _playlistService
                        .SyncCategoryAsync(definition, targetUsers, cancellationToken)
                        .ConfigureAwait(false);
                    LogCategoryBuilt(runLog, definition, targetUsers, "shared");
                }
            }
            else
            {
                // Phase B — one pass per viewer. The item list is byte-identical to
                // phase A, so it is a cache read; what varies is the candidate list
                // and their history. The model only invents here: shared categories
                // go to everyone (see the loop below), so there is nothing to select.
                var personalByUser = new List<(Guid UserId, IReadOnlyList<ReconciledCategory> Categories)>();
                var userIndex = 0;

                foreach (var userId in targetUsers)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var activity = _userActivityProvider.GetActivity(
                        userId, records.Select(r => r.Id).ToArray());

                    var watched = PersonalizationEligibility.CountWatched(activity);
                    if (!PersonalizationEligibility.IsEligible(watched, config.MinWatchedForPersonalization))
                    {
                        _logger.LogInformation(
                            "Curator: user {User} has watched {Watched} items, below the personalization minimum of {Minimum}; skipping their pass and giving them the shared categories",
                            userId,
                            watched,
                            config.MinWatchedForPersonalization);

                        runLog.Step(
                            "user.skipped",
                            $"User {userId} has watched {watched} items, below the minimum of {config.MinWatchedForPersonalization}; skipped",
                            new Dictionary<string, object?>
                            {
                                ["userId"] = userId.ToString(),
                                ["watchedCount"] = watched,
                                ["minimumWatched"] = config.MinWatchedForPersonalization,
                                ["activityEntries"] = activity.Count,
                            });

                        userIndex++;
                        Report(progress, runLog, 40 + (userIndex * 40.0 / targetUsers.Count));
                        continue;
                    }

                    var personal = await _proposalService
                        .ProposePersonalAsync(
                            provider, records, candidates, settings, activity, cancellationToken, runLog, userId)
                        .ConfigureAwait(false);

                    // Invented categories are reconciled on their own so the same
                    // size and cap rules apply to them as to the shared pool.
                    // Personal categories use their own floor and cap. A viewer with a
                    // handful of watched items cannot support a category as large as
                    // one drawn from the whole library; holding both to the shared
                    // threshold discarded most of what the model invented.
                    var invented = personal.NewProposals.Count > 0
                        ? Reconciler.Reconcile(
                            personal.NewProposals,
                            new ReconcilerSettings(personalLimits))
                        : [];

                    _logger.LogInformation(
                        "Curator: user {User} gained {New} categories of their own",
                        userId,
                        invented.Count);

                    personalByUser.Add((userId, invented));

                    runLog.Step(
                        "user.pass",
                        $"User {userId} gained {invented.Count} categories of their own",
                        new Dictionary<string, object?>
                        {
                            ["userId"] = userId.ToString(),
                            ["watchedCount"] = watched,
                            ["activityEntries"] = activity.Count,
                            ["seriesWithHistory"] = activity.Values.Count(a => a.EpisodesPlayed > 0),
                            ["proposedCount"] = personal.NewProposals.Count,
                            ["batchesSkipped"] = personal.BatchesSkipped,
                            ["invented"] = invented
                                .Select(c => new Dictionary<string, object?>
                                {
                                    ["name"] = c.Name,
                                    ["description"] = c.Description,
                                    ["memberCount"] = c.Members.Count,
                                })
                                .ToArray(),
                        });

                    userIndex++;
                    Report(progress, runLog, 40 + (userIndex * 40.0 / targetUsers.Count));
                }

                // Shared categories: one definition each, given to every target user.
                //
                // These used to be opt-in — a viewer's pass named the ones it wanted,
                // and a category nobody named went unbuilt. On a real library that
                // collapsed. The model was choosing from watch histories that were
                // missing all television (see SeriesActivityRollup), so it declined
                // 16 of 25 offers; three of eight shared categories ended up built for
                // exactly one user, and that user was the one who had watched nothing
                // and so received all of them by the thin-history fallback. A category
                // drawn from the whole shared library belongs to the whole household;
                // the viewer's pass earns its keep by inventing, not by vetoing.
                foreach (var category in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var definition = MergeIntoStore(existing, category, provider.ModelId, ownerUserId: null, runLog);
                    allCategoryIds.Add(definition.Id);
                    await _playlistService
                        .SyncCategoryAsync(definition, targetUsers, cancellationToken)
                        .ConfigureAwait(false);
                    LogCategoryBuilt(runLog, definition, targetUsers, "shared");
                }

                // Personal categories: one definition per user, theirs alone.
                foreach (var (userId, invented) in personalByUser)
                {
                    foreach (var category in invented)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var definition = MergeIntoStore(existing, category, provider.ModelId, ownerUserId: userId, runLog);
                        allCategoryIds.Add(definition.Id);
                        await _playlistService
                            .SyncCategoryAsync(definition, [userId], cancellationToken)
                            .ConfigureAwait(false);
                        LogCategoryBuilt(runLog, definition, [userId], "personal");
                    }
                }
            }

            Report(progress, runLog, 85);

            await RetireMissingCategoriesAsync(allCategoryIds, runLog, cancellationToken).ConfigureAwait(false);

            await EnforceCategoryCapsAsync(config, runLog, cancellationToken).ConfigureAwait(false);

            Report(progress, runLog, 92);

            // 6. Publish home screen rows.
            await _homeScreenService
                .SyncSectionsAsync(_categoryStore.GetAll(), targetUsers, cancellationToken)
                .ConfigureAwait(false);

            runLog.Step("homescreen.synced", "Published home screen rows");

            Report(progress, runLog, 100);
            _logger.LogInformation("Curator: run complete — {Count} categories live", allCategoryIds.Count);
            runLog.Step("run.complete", $"Run complete — {allCategoryIds.Count} categories live", new Dictionary<string, object?>
            {
                ["categoryCount"] = allCategoryIds.Count,
            });
        }

        /// <summary>
        /// Mirrors a progress report into the run log, so a reader following the
        /// file sees the same number the scheduled task shows.
        /// </summary>
        private static void Report(IProgress<double>? progress, IRunLog runLog, double percent)
        {
            progress?.Report(percent);
            runLog.Progress(percent);
        }

        private static void LogCategoryBuilt(
            IRunLog runLog,
            CategoryDefinition definition,
            IReadOnlyList<Guid> recipients,
            string kind)
        {
            runLog.Step(
                "category.built",
                $"Built {kind} category '{definition.Name}' for {recipients.Count} user(s)",
                new Dictionary<string, object?>
                {
                    ["categoryId"] = definition.Id.ToString(),
                    ["name"] = definition.Name,
                    ["description"] = definition.Description,
                    ["kind"] = kind,
                    ["memberCount"] = definition.Members.Count,
                    ["recipients"] = recipients.Select(id => id.ToString()).ToArray(),
                    ["playlists"] = definition.UserPlaylists
                        .Select(link => new Dictionary<string, object?>
                        {
                            ["userId"] = link.UserId.ToString(),
                            ["playlistId"] = link.PlaylistId?.ToString(),
                            ["handedOff"] = link.HandedOff,
                        })
                        .ToArray(),
                });
        }

        /// <summary>
        /// Reads the condensed summaries to send in place of overviews, or null when
        /// the owner has not switched them on.
        /// </summary>
        /// <remarks>
        /// Never fatal. A summary cache is an optimisation, and a run that cannot
        /// read one must still happen — just at the old prompt size.
        /// </remarks>
        private IReadOnlyDictionary<Guid, string>? LoadCondensedSummaries(PluginConfiguration config)
        {
            if (!config.UseCondensedSummaries)
            {
                return null;
            }

            try
            {
                var stored = _summaryStore.GetAll();
                if (stored.Count == 0)
                {
                    _logger.LogInformation(
                        "Curator: condensed summaries are switched on but none are stored; "
                        + "run the summary task to build them");
                    return null;
                }

                return stored.ToDictionary(pair => pair.Key, pair => pair.Value.Text);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Curator: could not read condensed summaries; using the full overviews");
                return null;
            }
        }

        /// <summary>
        /// Snapshots the settings that shaped a run. The API key is deliberately
        /// absent: run logs are the thing someone attaches to a bug report.
        /// </summary>
        private static Dictionary<string, object?> DescribeSettings(PluginConfiguration config)
        {
            // Normalize rather than ResolveDefault: this snapshot is diagnostics, and
            // a run that failed *because* nothing was configured is exactly when its
            // log must still be written. Rule 9 — a log never breaks the run.
            var normalized = ModelProfiles.Normalize(config);
            var profile = normalized.Profiles.FirstOrDefault(
                p => string.Equals(p.Id, normalized.DefaultProfileId, StringComparison.Ordinal));

            return new Dictionary<string, object?>
            {
                ["modelProfile"] = profile?.Name,
                ["modelProfileId"] = profile?.Id,
                ["modelProfileCount"] = normalized.Profiles.Count,
                ["provider"] = profile?.Provider.ToString(),
                ["model"] = profile?.Model,
                ["baseUrl"] = profile?.BaseUrl,
                ["batchSize"] = config.BatchSize,
                ["maxOutputTokens"] = config.MaxOutputTokens,
                ["tokenBudget"] = config.TokenBudget,
                ["maxSharedCategories"] = config.MaxSharedCategories,
                ["minSharedCategorySize"] = config.MinSharedCategorySize,
                ["maxPersonalCategories"] = config.MaxPersonalCategories,
                ["maxCategoryMembers"] = config.MaxCategoryMembers,
                ["minPersonalCategorySize"] = config.MinPersonalCategorySize,
                ["minWatchedForPersonalization"] = config.MinWatchedForPersonalization,
                ["surfacedCollections"] = config.SurfacedCollections,
                ["maxTagsPerItem"] = config.MaxTagsPerItem,
                ["personalizedPlaylists"] = config.PersonalizedPlaylists,
                ["outputType"] = config.OutputType.ToString(),
                ["includeEpisodes"] = config.IncludeEpisodes,
                ["enableThinking"] = config.EnableThinking,
                ["useBatchApi"] = config.UseBatchApi,
                ["autoEnableSections"] = config.AutoEnableSections,
                ["inputCostPerMillion"] = profile?.InputCostPerMillion,
                ["cachedInputCostPerMillion"] = profile?.CachedInputCostPerMillion,
                ["outputCostPerMillion"] = profile?.OutputCostPerMillion,
                ["targetUsers"] = config.TargetUsers.Select(id => id.ToString()).ToArray(),
            };
        }

        /// <summary>
        /// Brings each pool back within its cap by deleting the oldest categories
        /// over it — definition and playlists together.
        /// </summary>
        /// <remarks>
        /// This is the one place a definition is deliberately destroyed rather than
        /// emptied, and it is a deliberate exception to "empty category ≠ deleted
        /// category". That rule exists so a category which happens to produce no
        /// members this run keeps its identity; a cap is a budget the user set, and
        /// honouring it means something has to actually go. Handed-off playlists are
        /// still never touched — <see cref="ICuratorPlaylistService.RemoveCategoryPlaylistsAsync"/>
        /// enforces that, and the definition is removed around them.
        /// </remarks>
        private async Task EnforceCategoryCapsAsync(
            PluginConfiguration config,
            IRunLog runLog,
            CancellationToken cancellationToken)
        {
            var doomed = CategoryRetention.SelectForRemoval(
                _categoryStore.GetAll(),
                config.MaxSharedCategories,
                config.MaxPersonalCategories);

            foreach (var category in doomed)
            {
                cancellationToken.ThrowIfCancellationRequested();

                _logger.LogInformation(
                    "Curator: category '{Category}' is over the {Pool} cap; deleting it and its playlists (last refreshed {UpdatedAt:u})",
                    category.Name,
                    category.OwnerUserId is null ? "shared" : "personal",
                    category.UpdatedAt);

                await _playlistService.RemoveCategoryPlaylistsAsync(category, cancellationToken).ConfigureAwait(false);
                _categoryStore.Delete(category.Id);

                runLog.Step(
                    "category.pruned",
                    $"Deleted '{category.Name}' — over the {(category.OwnerUserId is null ? "shared" : "personal")} cap",
                    new Dictionary<string, object?>
                    {
                        ["categoryId"] = category.Id.ToString(),
                        ["name"] = category.Name,
                        ["kind"] = category.OwnerUserId is null ? "shared" : "personal",
                        ["ownerUserId"] = category.OwnerUserId?.ToString(),
                        ["createdAt"] = category.CreatedAt,
                        ["updatedAt"] = category.UpdatedAt,
                    });
            }
        }

        /// <summary>
        /// Finds the stored definition matching a reconciled category (by name,
        /// which is the user-visible identity of a category across runs) or
        /// creates one, then refreshes it from the latest run.
        /// </summary>
        private CategoryDefinition MergeIntoStore(
            IReadOnlyList<CategoryDefinition> existing,
            ReconciledCategory category,
            string modelId,
            Guid? ownerUserId,
            IRunLog runLog)
        {
            var now = DateTime.UtcNow;
            var match = CategoryIdentity.FindMatch(
                existing,
                category.Name,
                category.Members,
                ownerUserId,
                _claimedDefinitions);

            if (match is not null)
            {
                // Claimed for the rest of the run: two categories that both
                // resemble one stored definition must not both take it, or the
                // second silently overwrites the first.
                _claimedDefinitions.Add(match.Definition.Id);

                if (!match.MatchedByName)
                {
                    _logger.LogInformation(
                        "Curator: '{New}' is '{Old}' renamed ({Similarity:P0} of its items in common); keeping its playlists and home screen row",
                        category.Name,
                        match.Definition.Name,
                        match.Similarity);

                    runLog.Step(
                        "category.renamed",
                        $"'{match.Definition.Name}' → '{category.Name}' — recognised by members, row kept",
                        new Dictionary<string, object?>
                        {
                            ["categoryId"] = match.Definition.Id.ToString(),
                            ["previousName"] = match.Definition.Name,
                            ["name"] = category.Name,
                            ["similarity"] = Math.Round(match.Similarity, 3),
                            ["ownerUserId"] = ownerUserId?.ToString(),
                        });
                }
            }

            var definition = match?.Definition ?? new CategoryDefinition
            {
                Id = Guid.NewGuid(),
                Name = category.Name,
                CreatedAt = now,
                OwnerUserId = ownerUserId,
            };

            definition.Name = category.Name;
            definition.Description = category.Description;
            definition.Members = [.. category.Members];
            definition.SourceProposalCount = category.SourceProposalCount;
            definition.SourceProposals = [.. category.SourceProposals];
            definition.OwnerUserId = ownerUserId;
            definition.UpdatedAt = now;
            definition.ModelId = modelId;

            _categoryStore.Save(definition);
            return definition;
        }

        /// <summary>
        /// Categories the latest run did not produce lose their playlists but keep
        /// their definitions, so a later run that revives the same category reuses
        /// the same identity rather than creating a duplicate.
        /// </summary>
        private async Task RetireMissingCategoriesAsync(
            HashSet<Guid> liveCategoryIds,
            IRunLog runLog,
            CancellationToken cancellationToken)
        {
            foreach (var stale in _categoryStore.GetAll().Where(c => !liveCategoryIds.Contains(c.Id)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (stale.UserPlaylists.TrueForAll(link => link.PlaylistId is null))
                {
                    continue;
                }

                _logger.LogInformation(
                    "Curator: category '{Category}' was not proposed this run; removing its playlists but keeping the definition",
                    stale.Name);
                await _playlistService.RemoveCategoryPlaylistsAsync(stale, cancellationToken).ConfigureAwait(false);

                runLog.Step(
                    "category.retired",
                    $"Category '{stale.Name}' was not proposed this run; playlists removed, definition kept",
                    new Dictionary<string, object?>
                    {
                        ["categoryId"] = stale.Id.ToString(),
                        ["name"] = stale.Name,
                    });
            }
        }

        /// <summary>
        /// Reconciles stored categories against the playlists that actually exist,
        /// without an LLM call.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Drift is real and has several sources: playlists deleted by hand, a
        /// definition left pointing at a playlist that is gone, a run interrupted
        /// part-way. Until now the only reconciliation was a full run, which costs
        /// money.
        /// </para>
        /// <para>
        /// Three passes, in this order. First re-sync every category that still holds
        /// members, which recreates a missing playlist and repairs a stale link
        /// through the category tether. Then delete definitions that end up holding
        /// no playlist at all, along with any playlist they still own. Then delete
        /// Curator-owned playlists no surviving definition claims.
        /// </para>
        /// <para>
        /// Deleting a definition discards its identity: if the model proposes that
        /// thread again it returns as a new row rather than reusing the old one. That
        /// is the trade the owner asked for — a category showing nobody anything is
        /// not worth the row it is holding.
        /// </para>
        /// </remarks>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What changed.</returns>
        public async Task<PlaylistSyncResult> SyncPlaylistsAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration
                ?? throw new InvalidOperationException("Curator: plugin configuration unavailable.");

            var targetUsers = ResolveTargetUsers(config);
            if (targetUsers.Count == 0)
            {
                _logger.LogWarning("Curator: no target users resolved; nothing to sync");
                return new PlaylistSyncResult(0, 0, 0);
            }

            var rebuilt = 0;
            foreach (var category in _categoryStore.GetAll())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (category.Members.Count == 0)
                {
                    continue;
                }

                var before = category.UserPlaylists.Count(link => link.PlaylistId is not null);
                await _playlistService
                    .SyncCategoryAsync(category, targetUsers, cancellationToken)
                    .ConfigureAwait(false);

                if (category.UserPlaylists.Count(link => link.PlaylistId is not null) > before)
                {
                    rebuilt++;
                }
            }

            var removedCategories = 0;
            foreach (var category in _categoryStore.GetAll())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!CategoryRetention.IsEmpty(category))
                {
                    continue;
                }

                await _playlistService
                    .RemoveCategoryPlaylistsAsync(category, cancellationToken)
                    .ConfigureAwait(false);
                _categoryStore.Delete(category.Id);
                removedCategories++;

                _logger.LogInformation(
                    "Curator: removed category '{Category}' — it holds no playlist", category.Name);
            }

            var claimed = _categoryStore.GetAll()
                .SelectMany(c => c.UserPlaylists)
                .Where(link => link.PlaylistId is not null)
                .Select(link => link.PlaylistId!.Value)
                .ToHashSet();

            var removedPlaylists = await _playlistService
                .RemoveOrphanedPlaylistsAsync(claimed, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Curator: playlist sync — {Rebuilt} categor(ies) regained a playlist, "
                + "{RemovedCategories} empty categor(ies) removed, {RemovedPlaylists} orphaned playlist(s) deleted",
                rebuilt,
                removedCategories,
                removedPlaylists);

            await _homeScreenService
                .SyncSectionsAsync(_categoryStore.GetAll(), targetUsers, cancellationToken)
                .ConfigureAwait(false);

            return new PlaylistSyncResult(rebuilt, removedCategories, removedPlaylists);
        }

        /// <summary>
        /// Re-publishes the home screen rows from the stored categories, without an
        /// LLM call.
        /// </summary>
        /// <remarks>
        /// A full run does this at the end, but a run costs money and takes minutes,
        /// so it is a poor way to fix rows that have drifted. They do drift: the
        /// integration is a merge into two other plugins' configuration, and either
        /// can be edited by hand or rewritten by its own plugin between runs.
        /// Only categories that actually hold a playlist get a row, exactly as in a
        /// full run — the two share <see cref="IHomeScreenIntegrationService"/>, so
        /// they cannot disagree about what a row is.
        /// </remarks>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True when the integration reported success.</returns>
        public async Task<bool> SyncHomeScreenAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration
                ?? throw new InvalidOperationException("Curator: plugin configuration unavailable.");

            var targetUsers = ResolveTargetUsers(config);
            if (targetUsers.Count == 0)
            {
                _logger.LogWarning("Curator: no target users resolved; nothing to sync");
                return false;
            }

            var categories = _categoryStore.GetAll();
            var synced = await _homeScreenService
                .SyncSectionsAsync(categories, targetUsers, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Curator: home screen sync {Outcome} — {Count} stored categor(ies), {Users} user(s)",
                synced ? "completed" : "did not run",
                categories.Count,
                targetUsers.Count);

            return synced;
        }

        private IReadOnlyList<Guid> ResolveTargetUsers(PluginConfiguration config)
        {
            if (config.TargetUsers.Length > 0)
            {
                return config.TargetUsers
                    .Where(id => _userManager.GetUserById(id) is not null)
                    .ToArray();
            }

            return _userManager.GetUsers().Select(user => user.Id).ToArray();
        }
    }
}
