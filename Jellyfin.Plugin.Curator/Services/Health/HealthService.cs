using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Configuration;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Health;
using Jellyfin.Plugin.Curator.Core.Llm;
using Jellyfin.Plugin.Curator.Core.Scheduling;
using Jellyfin.Plugin.Curator.Core.Summaries;
using Jellyfin.Plugin.Curator.Services.Categories;
using Jellyfin.Plugin.Curator.Services.HomeScreen;
using Jellyfin.Plugin.Curator.Services.Library;
using Jellyfin.Plugin.Curator.Services.Runs;
using Jellyfin.Plugin.Curator.Services.Summaries;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services.Health
{
    /// <summary>The health check's answer.</summary>
    /// <param name="CheckedAt">When the check ran (UTC).</param>
    /// <param name="Findings">What it found, most severe first.</param>
    /// <param name="Worst">The highest severity present, or Ok when there is nothing to say.</param>
    public sealed record HealthReport(
        DateTime CheckedAt,
        IReadOnlyList<HealthFinding> Findings,
        HealthSeverity Worst);

    /// <summary>
    /// Gathers the facts <see cref="HealthCheck"/> judges.
    /// </summary>
    /// <remarks>
    /// Everything here is read-only and local: no model call, no network, nothing
    /// written. It has to be safe to run on a timer and safe to run while a
    /// category run is in progress.
    /// </remarks>
    public sealed class HealthService
    {
        private readonly ILibraryScanner _libraryScanner;
        private readonly ICategoryStore _categoryStore;
        private readonly ISummaryStore _summaryStore;
        private readonly SummaryDistillService _distillService;
        private readonly IRunLogStore _runLogStore;
        private readonly IHomeScreenIntegrationService _homeScreenService;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly ITaskManager _taskManager;
        private readonly ILogger<HealthService> _logger;

        public HealthService(
            ILibraryScanner libraryScanner,
            ICategoryStore categoryStore,
            ISummaryStore summaryStore,
            SummaryDistillService distillService,
            IRunLogStore runLogStore,
            IHomeScreenIntegrationService homeScreenService,
            ILibraryManager libraryManager,
            IUserManager userManager,
            ITaskManager taskManager,
            ILogger<HealthService> logger)
        {
            _libraryScanner = libraryScanner;
            _categoryStore = categoryStore;
            _summaryStore = summaryStore;
            _distillService = distillService;
            _runLogStore = runLogStore;
            _homeScreenService = homeScreenService;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _taskManager = taskManager;
            _logger = logger;
        }

        /// <summary>The scheduled task key whose cadence "runs have stopped" is judged against.</summary>
        private const string RunTaskKey = "CuratorGenerateCategories";

        /// <summary>
        /// Runs the check.
        /// </summary>
        /// <param name="config">Plugin configuration.</param>
        /// <returns>The report.</returns>
        public HealthReport Check(PluginConfiguration config)
        {
            ArgumentNullException.ThrowIfNull(config);

            var findings = HealthCheck.Evaluate(GatherFacts(config));
            var worst = findings.Count == 0 ? HealthSeverity.Ok : findings[0].Severity;
            return new HealthReport(DateTime.UtcNow, findings, worst);
        }

        private HealthFacts GatherFacts(PluginConfiguration config)
        {
            var runs = SafeList();
            var lastSuccess = runs
                .Where(r => string.Equals(r.Status, "completed", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.FinishedAt ?? r.StartedAt)
                .FirstOrDefault();

            var profiles = ModelProfiles.Normalize(config).Profiles;
            var categories = _categoryStore.GetAll();
            var summaries = _summaryStore.GetAll();
            var library = SafeInspect();
            var (collectionSections, homeScreenSections) = SafePrerequisites();

            return new HealthFacts(
                UtcNow: DateTime.UtcNow,
                LastSuccessfulRun: lastSuccess == default ? null : lastSuccess,
                LastRunStatus: runs.Count > 0 ? runs[0].Status : null,
                LastRunError: runs.Count > 0 ? runs[0].Error : null,
                ExpectedRunIntervalHours: ExpectedRunInterval(),
                ModelProfileCount: profiles.Count,
                ProfilesMissingKey: profiles.Count(NeedsKeyButHasNone),
                CollectionSectionsLoaded: collectionSections,
                HomeScreenSectionsLoaded: homeScreenSections,
                GhostItems: library.Orphaned,
                LibraryItems: library.Items,
                UseCondensedSummaries: config.UseCondensedSummaries,
                ItemsMissingSummary: CountMissingSummaries(config),
                RecommendationsEnabled: config.RecommendationPlaylists,
                TargetUserCount: TargetUserCount(config),
                RecommendationPlaylistCount: CountRecommendationPlaylists(config),
                CategoriesWithoutPlaylist: categories.Count(CategoryRetention.IsEmpty),
                TotalCategories: categories.Count,
                ConsolidateTags: config.ConsolidateTags,
                StoredSummaries: summaries.Count,
                SummariesWithTags: summaries.Values.Count(x => x.Tags.Count > 0),

                // In memory, so it is empty until a pass has run since the last
                // restart. That is the right way round: reporting a stale failure
                // from before a restart would send someone chasing a fixed problem.
                LastSummaryPassDistilled: _distillService.LastResult?.Distilled ?? 0,
                LastSummaryPassFailed: _distillService.LastResult?.Failed ?? 0);
        }

        /// <summary>
        /// How often the category task is set to run, in hours, or null when it has
        /// no timed trigger at all.
        /// </summary>
        /// <remarks>
        /// Read from the schedule rather than assumed, so "runs have stopped" is
        /// measured against what the owner actually asked for. A deliberately
        /// manual-only task returns null and is never reported as stalled.
        /// </remarks>
        private double? ExpectedRunInterval()
        {
            try
            {
                var worker = _taskManager.ScheduledTasks
                    .FirstOrDefault(w => string.Equals(w.ScheduledTask.Key, RunTaskKey, StringComparison.Ordinal));

                if (worker is null)
                {
                    return null;
                }

                var spec = ScheduleTranslator.FromTriggers(worker.Triggers);
                return spec.Mode switch
                {
                    ScheduleMode.Interval => spec.IntervalHours,
                    ScheduleMode.Daily => 24,
                    ScheduleMode.Weekly => 24 * 7,
                    _ => null,
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Curator health: could not read the run schedule");
                return null;
            }
        }

        /// <summary>
        /// Whether a profile's provider needs an API key and does not have one.
        /// </summary>
        /// <remarks>
        /// OpenAI-compatible endpoints are excluded: a local Ollama or LM Studio
        /// legitimately needs no key, and flagging those would make the panel wrong
        /// for exactly the people running models on their own hardware.
        /// </remarks>
        private static bool NeedsKeyButHasNone(ModelProfile profile)
            => profile.Provider != LlmProviderKind.OpenAiCompatible
                && string.IsNullOrWhiteSpace(profile.ApiKey);

        private int CountMissingSummaries(PluginConfiguration config)
        {
            if (!config.UseCondensedSummaries)
            {
                return 0;
            }

            try
            {
                var items = _libraryScanner.ScanLibrary(
                    includeEpisodes: false,
                    surfacedCollections: null,
                    maxOverviewLength: ItemReducer.NoOverviewLimit);

                var plan = SummaryPlan.Create(
                    items,
                    _summaryStore.GetAll(),
                    config.SummaryMinSourceLength,
                    force: false,
                    consolidateTags: config.ConsolidateTags);

                return plan.Work.Count;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Curator health: could not count missing summaries");
                return 0;
            }
        }

        private int TargetUserCount(PluginConfiguration config)
        {
            try
            {
                return config.TargetUsers.Length > 0
                    ? config.TargetUsers.Length
                    : _userManager.GetUsers().Count();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Curator health: could not count target users");
                return 0;
            }
        }

        private int CountRecommendationPlaylists(PluginConfiguration config)
        {
            if (!config.RecommendationPlaylists)
            {
                return 0;
            }

            try
            {
                var name = config.RecommendationPlaylistName?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    return 0;
                }

                // Counted by name here rather than by tether, deliberately. This is
                // a health signal about what the owner would actually see, and the
                // consumer of these — Media Bar, Collection Sections — resolves them
                // by name too. Nothing is modified on the strength of this count, so
                // hard rule 2 is not in play.
                return _libraryManager.GetItemsResult(new InternalItemsQuery
                {
                    IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Playlist],
                    Recursive = true,
                }).Items
                    .OfType<Playlist>()
                    .Count(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Curator health: could not count recommendation playlists");
                return 0;
            }
        }

        private IReadOnlyList<RunLogSummary> SafeList()
        {
            try
            {
                return _runLogStore.List(25);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Curator health: could not read the run log");
                return [];
            }
        }

        private LibraryHealth SafeInspect()
        {
            try
            {
                return _libraryScanner.Inspect();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Curator health: could not inspect the library");
                return new LibraryHealth(0, 0);
            }
        }

        private (bool CollectionSections, bool HomeScreenSections) SafePrerequisites()
        {
            try
            {
                return _homeScreenService.GetPrerequisites();
            }
            catch (Exception ex)
            {
                // Assume present rather than absent: a probe that cannot run is not
                // evidence the plugins are missing, and inventing two Problems from
                // a failed lookup would be worse than saying nothing.
                _logger.LogDebug(ex, "Curator health: could not probe the integration plugins");
                return (true, true);
            }
        }
    }
}
