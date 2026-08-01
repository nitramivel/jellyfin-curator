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
using Jellyfin.Plugin.Curator.Core.Playlists;
using Jellyfin.Plugin.Curator.Core.Recommendations;
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

    /// <summary>What the daily maintenance pass did.</summary>
    /// <param name="Skipped">True when a run was in progress and nothing was touched.</param>
    /// <param name="Sync">The playlist reconcile result, or null when skipped.</param>
    /// <param name="RecommendationsRefreshed">Recommendation playlists rebuilt against current watch activity.</param>
    /// <param name="SummariesPruned">Condensed summaries dropped for items no longer in the library.</param>
    public sealed record MaintenanceResult(
        bool Skipped,
        PlaylistSyncResult? Sync,
        int RecommendationsRefreshed,
        int SummariesPruned);

    /// <summary>
    /// The end-to-end run: scan → propose → reconcile → build playlists →
    /// publish home screen rows. Shared by the scheduled task and the manual
    /// trigger so both take exactly the same path.
    /// </summary>
    public class CuratorRunService : IDisposable
    {
        private readonly ILibraryScanner _libraryScanner;
        private readonly IUserActivityProvider _userActivityProvider;
        private readonly ILlmProviderFactory _providerFactory;
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
            ILlmProviderFactory providerFactory,
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

            // Two profiles, because the two passes are different jobs. Discovery is
            // one call over the whole library looking for threads; the viewer passes
            // are one call each, every run, doing a narrower job — five of six calls
            // on a measured run. Either falling back to the default means a run that
            // has chosen nothing behaves exactly as it always did.
            //
            // Pricing lives on the profile, so each pass is costed at what it
            // actually charges rather than at whatever the other one does.
            // Normalized once and both passes resolved against that one list. Doing it
            // per resolve is not idempotent on an install that predates the profile
            // list — the migrated profile is synthesized anew each time, with a new id
            // — so the two passes would compare as different profiles and the run
            // would build two identical providers and call itself a mixed run.
            var profiles = ModelProfiles.Normalize(config);
            var discoveryProfile = ModelProfiles.Resolve(profiles, config.DiscoveryModelProfileId);
            var personalProfile = ModelProfiles.Resolve(profiles, config.PersonalModelProfileId);

            var sameProfile = string.Equals(discoveryProfile.Id, personalProfile.Id, StringComparison.Ordinal);
            var provider = _providerFactory.Create(discoveryProfile, config.EnableThinking);
            var personalProvider = sameProfile
                ? provider
                : _providerFactory.Create(personalProfile, config.EnableThinking);

            // The headline model is discovery's. Per-call records carry their own
            // phase and rates, so a mixed run is still readable call by call.
            runLog.SetProvider(
                discoveryProfile.Provider.ToString(),
                provider.ModelId,
                discoveryProfile.InputCostPerMillion,
                discoveryProfile.OutputCostPerMillion,
                discoveryProfile.CachedInputCostPerMillion);

            if (!sameProfile)
            {
                _logger.LogInformation(
                    "Curator: discovery on '{Discovery}' ({DiscoveryModel}), viewer passes on '{Personal}' ({PersonalModel})",
                    discoveryProfile.Name,
                    provider.ModelId,
                    personalProfile.Name,
                    personalProvider.ModelId);
            }

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
                condensed,
                config.SendConsolidatedTags,
                config.SurfaceAllCollections);
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
                config.EffectiveSharedCategorySize,
                config.MaxSharedCategories);
            var personalLimits = new CategoryLimits(
                config.MinPersonalCategorySize,
                config.EffectivePersonalCategorySize,
                config.MaxPersonalCategories);

            var settings = new ProposalRunSettings(
                config.BatchSize,
                config.MaxOutputTokens,
                config.TokenBudget,
                discoveryProfile.InputCostPerMillion,
                discoveryProfile.OutputCostPerMillion,
                discoveryProfile.CachedInputCostPerMillion,
                config.UseBatchApi,

                // MaxTagsPerItem governs the RAW scraped list and is normally 0, so
                // leaving it in charge would substitute consolidated tags onto every
                // record and then write none of them. Consolidated lists are already
                // short, so the ceiling they were built under is the right cap.
                config.SendConsolidatedTags
                    ? Math.Max(config.MaxTagsPerItem, Math.Max(1, config.MaxConsolidatedTags))
                    : config.MaxTagsPerItem,
                sharedLimits,
                personalLimits);

            // Same settings, the viewer pass's own prices. Everything else about the
            // two passes — budgets, caps, limits — is deliberately shared; only what
            // the call costs differs, and only when the owner has pointed the two
            // passes at different profiles.
            var personalSettings = sameProfile
                ? settings
                : settings with
                {
                    InputCostPerMillion = personalProfile.InputCostPerMillion,
                    OutputCostPerMillion = personalProfile.OutputCostPerMillion,
                    CachedCostPerMillion = personalProfile.CachedInputCostPerMillion,
                };

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
                // The rows this library already has, so the pass can reuse a name for
                // a thread it already found rather than coining a new one and leaving
                // CategoryIdentity to rescue the row on member overlap alone. Shared
                // only: a viewer's personal rows are not this pass's to rename.
                .ProposeAsync(
                    provider,
                    records,
                    settings,
                    activity: null,
                    cancellationToken,
                    runLog,
                    [.. existing
                        .Where(c => c.OwnerUserId is null)
                        .Select(c => new ExistingCategory(c.Name, c.Description))])
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
                            personalProvider, records, candidates, personalSettings, activity, cancellationToken, runLog, userId)
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

                        // Credited to the model that actually invented it, which is the
                        // viewer pass's — not discovery's, when the two differ.
                        var definition = MergeIntoStore(existing, category, personalProvider.ModelId, ownerUserId: userId, runLog);
                        allCategoryIds.Add(definition.Id);
                        await _playlistService
                            .SyncCategoryAsync(
                                definition,
                                CategoryAudience.For(definition.OwnerUserId, targetUsers),
                                cancellationToken)
                            .ConfigureAwait(false);
                        LogCategoryBuilt(runLog, definition, [userId], "personal");
                    }
                }
            }

            Report(progress, runLog, 85);

            await RetireMissingCategoriesAsync(config, allCategoryIds, runLog, cancellationToken).ConfigureAwait(false);

            await EnforceCategoryCapsAsync(config, runLog, cancellationToken).ConfigureAwait(false);

            // 5b. Per-viewer recommendation playlists, merged from the categories
            // each viewer now has. No model call: every category already carries the
            // model's own ordering of its members.
            await BuildRecommendationsAsync(config, targetUsers, runLog, cancellationToken).ConfigureAwait(false);

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
        /// Builds each viewer's recommendation playlist by merging the categories
        /// they hold into one ranked list.
        /// </summary>
        /// <remarks>
        /// Never fatal. This is a convenience row on top of the run's real output,
        /// so a failure here must not lose the categories and playlists the run has
        /// already paid a model to produce.
        /// </remarks>
        private async Task<int> BuildRecommendationsAsync(
            PluginConfiguration config,
            IReadOnlyList<Guid> targetUsers,
            IRunLog? runLog,
            CancellationToken cancellationToken)
        {
            if (!config.RecommendationPlaylists)
            {
                return 0;
            }

            var stored = _categoryStore.GetAll();
            var built = 0;

            // One scan for the whole pass rather than one per viewer, and only when
            // a model is going to read it. Condensed summaries are substituted the
            // same way the run does it, so the re-rank reads the text the rest of the
            // pipeline reads rather than the raw provider synopsis.
            IReadOnlyDictionary<Guid, MediaItemRecord> descriptions =
                config.ModelRankedRecommendations
                    ? _libraryScanner
                        .ScanLibrary(
                            includeEpisodes: config.IncludeEpisodes,
                            surfacedCollections: config.SurfacedCollections,
                            condensedSummaries: LoadCondensedSummaries(config),
                            useCondensedTags: config.SendConsolidatedTags,
                            surfaceAllCollections: config.SurfaceAllCollections)
                        .ToDictionary(r => r.Id)
                    : new Dictionary<Guid, MediaItemRecord>();

            foreach (var userId in targetUsers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // A category belongs to this viewer's ranking if it is shared
                    // (OwnerUserId null — every viewer has it) or was invented for
                    // them. Another viewer's personal category is not theirs to be
                    // recommended from.
                    var mine = stored
                        .Where(c => c.OwnerUserId is null || c.OwnerUserId == userId)
                        .Select(c => new RankedCategory(c.Members, c.OwnerUserId == userId))
                        .ToList();

                    if (mine.Count == 0)
                    {
                        continue;
                    }

                    // Only the items actually in play need looking up, which for a
                    // viewer's own categories is a small slice of the library.
                    var candidates = mine.SelectMany(c => c.Members).Distinct().ToArray();
                    var activity = _userActivityProvider.GetActivity(userId, candidates);
                    var ranked = RecommendationRanker.Rank(
                        mine,
                        activity,
                        new RecommendationOptions(config.MaxRecommendations, config.RecommendationsIncludeWatched));

                    // Selection is done; only the order is still open. A model reads
                    // the top slice and says what this viewer should see first, which
                    // is the part a weighted sum is bad at. Off by default — it is the
                    // one thing about this playlist that costs money.
                    if (config.ModelRankedRecommendations && ranked.Count > 1)
                    {
                        ranked = await ReorderRecommendationsAsync(
                            config, userId, ranked, activity, descriptions, runLog, cancellationToken).ConfigureAwait(false);
                    }

                    var playlistId = await _playlistService
                        .SyncRecommendationsAsync(
                            userId,
                            config.RecommendationPlaylistName,
                            ranked,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (playlistId is not null)
                    {
                        built++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        ex,
                        "Curator: could not build recommendations for user {UserId} — {Message}",
                        userId,
                        ex.Message);
                }
            }

            _logger.LogInformation("Curator: built {Count} recommendation playlist(s)", built);
            runLog?.Step("recommendations.built", $"Built {built} recommendation playlist(s)", new Dictionary<string, object?>
            {
                ["playlistCount"] = built,
                ["name"] = config.RecommendationPlaylistName,
                ["maxItems"] = config.MaxRecommendations,
                ["includeWatched"] = config.RecommendationsIncludeWatched,
            });

            return built;
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
        private IReadOnlyDictionary<Guid, CondensedSummary>? LoadCondensedSummaries(PluginConfiguration config)
        {
            // Either switch alone is reason to read the store: summaries and tags are
            // built together but sent independently.
            if (!config.UseCondensedSummaries && !config.SendConsolidatedTags)
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

                // Overviews are only substituted when that switch is on; the record
                // carries both and the scanner picks what it was told to use.
                return config.UseCondensedSummaries
                    ? stored
                    : stored.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value with { Text = string.Empty });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Curator: could not read condensed summaries; using the full overviews");
                return null;
            }
        }

        /// <summary>
        /// Asks a model to put one viewer's shortlist in a better order.
        /// </summary>
        /// <remarks>
        /// Never fatal, and never destructive. A failed call, an unusable answer or a
        /// model that omits half the list all leave the weighted order in place —
        /// <see cref="RecommendationParser"/> treats the answer as a preference over
        /// the shortlist rather than a replacement for it, so the worst case is that
        /// the call was wasted rather than that somebody's row lost items.
        /// <para>
        /// Only the top slice is sent. A spotlight row is seen a few items at a time,
        /// so ordering the head is nearly all the value; the tail keeps the weighted
        /// order and is appended.
        /// </para>
        /// </remarks>
        private async Task<IReadOnlyList<Guid>> ReorderRecommendationsAsync(
            PluginConfiguration config,
            Guid userId,
            IReadOnlyList<Guid> ranked,
            IReadOnlyDictionary<Guid, UserActivity> activity,
            IReadOnlyDictionary<Guid, MediaItemRecord> descriptions,
            IRunLog? runLog,
            CancellationToken cancellationToken)
        {
            var headSize = config.MaxRecommendationsToRank > 0
                ? Math.Min(config.MaxRecommendationsToRank, ranked.Count)
                : ranked.Count;
            var head = ranked.Take(headSize).ToList();
            var tail = ranked.Skip(headSize).ToList();

            try
            {
                var records = head
                    .Where(descriptions.ContainsKey)
                    .Select(id => descriptions[id])
                    .ToList();

                if (records.Count != head.Count)
                {
                    // A shortlist entry we cannot describe cannot be numbered for the
                    // model without shifting every index after it. Not worth a call.
                    _logger.LogInformation(
                        "Curator: skipping recommendation re-rank for {User}; {Missing} shortlist item(s) could not be described",
                        userId,
                        head.Count - records.Count);
                    return ranked;
                }

                var profiles = ModelProfiles.Normalize(config);
                var profile = ModelProfiles.Resolve(profiles, config.RecommendationModelProfileId);
                var provider = _providerFactory.Create(profile, config.EnableThinking);

                var watched = activity
                    .Where(kv => kv.Value.Played || kv.Value.PlayCount > 0)
                    .Select(kv => kv.Key)
                    .ToHashSet();

                var request = new LlmRequest(
                    RecommendationPromptBuilder.BuildSystemPrompt(records.Count),
                    RecommendationPromptBuilder.BuildUserPrompt(records, watched),
                    string.Empty,
                    config.MaxOutputTokens,
                    ResponseShape.RecommendationOrder);

                var result = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                var reordered = RecommendationParser.Reorder(result.Text, head);

                _logger.LogInformation(
                    "Curator: re-ranked {Count} recommendation(s) for {User} on '{Profile}' ({Discarded} discarded, {Missing} left in place)",
                    records.Count,
                    userId,
                    profile.Name,
                    reordered.DiscardedCount,
                    reordered.MissingCount);

                runLog?.Step(
                    "recommendations.reranked",
                    $"Re-ranked {records.Count} recommendation(s) for one viewer",
                    new Dictionary<string, object?>
                    {
                        ["userId"] = userId.ToString(),
                        ["ranked"] = records.Count,
                        ["heldBack"] = tail.Count,
                        ["discarded"] = reordered.DiscardedCount,
                        ["missing"] = reordered.MissingCount,
                        ["modelProfile"] = profile.Name,
                    });

                return [.. reordered.Ordered, .. tail];
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                // The weighted order is a perfectly good answer on its own — it is
                // what shipped for months. Losing the re-rank is not losing the row.
                _logger.LogWarning(
                    ex,
                    "Curator: could not re-rank recommendations for {User}; keeping the weighted order — {Message}",
                    userId,
                    ex.Message);
                return ranked;
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

            // Named separately because they need not be the default, or each other.
            // Without these the snapshot reports one model for a run that used two,
            // which is the kind of thing a bug report is read wrongly because of.
            // Resolved by hand rather than through Resolve, which throws on an empty
            // list — see the note above.
            var passProfile = (string? wanted) => string.IsNullOrWhiteSpace(wanted)
                ? profile
                : normalized.Profiles.FirstOrDefault(
                    p => string.Equals(p.Id, wanted, StringComparison.Ordinal)) ?? profile;

            return new Dictionary<string, object?>
            {
                ["modelProfile"] = profile?.Name,
                ["modelProfileId"] = profile?.Id,
                ["modelProfileCount"] = normalized.Profiles.Count,
                ["discoveryModelProfile"] = passProfile(config.DiscoveryModelProfileId)?.Name,
                ["personalModelProfile"] = passProfile(config.PersonalModelProfileId)?.Name,
                ["provider"] = profile?.Provider.ToString(),
                ["model"] = profile?.Model,
                ["baseUrl"] = profile?.BaseUrl,
                ["batchSize"] = config.BatchSize,
                ["maxOutputTokens"] = config.MaxOutputTokens,
                ["tokenBudget"] = config.TokenBudget,
                ["maxSharedCategories"] = config.MaxSharedCategories,
                ["maxStoredSharedCategories"] = config.EffectiveStoredSharedCategories,
                ["maxStoredPersonalCategories"] = config.EffectiveStoredPersonalCategories,
                ["minSharedCategorySize"] = config.MinSharedCategorySize,
                ["maxPersonalCategories"] = config.MaxPersonalCategories,
                ["maxCategoryMembers"] = config.MaxCategoryMembers,
                ["maxSharedCategorySize"] = config.EffectiveSharedCategorySize,
                ["maxPersonalCategorySize"] = config.EffectivePersonalCategorySize,
                ["minPersonalCategorySize"] = config.MinPersonalCategorySize,
                ["minWatchedForPersonalization"] = config.MinWatchedForPersonalization,
                ["surfacedCollections"] = config.SurfaceAllCollections
                    ? "(every collection)"
                    : config.SurfacedCollections,
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
            // The retention caps, not the per-run ones. A run proposes at most
            // MaxSharedCategories threads; the store is allowed to hold a larger
            // library of them built up over several runs. Tying the two capped the
            // collection at one pass's worth and made every run past the number
            // delete a category — which loses its identity and returns as a new row.
            var doomed = CategoryRetention.SelectForRemoval(
                _categoryStore.GetAll(),
                config.EffectiveStoredSharedCategories,
                config.EffectiveStoredPersonalCategories);

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
            PluginConfiguration config,
            HashSet<Guid> liveCategoryIds,
            IRunLog runLog,
            CancellationToken cancellationToken)
        {
            var grace = Math.Max(0, config.CategoryRetirementGraceRuns);

            foreach (var stored in _categoryStore.GetAll())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Claimed by this run: the clock goes back to zero.
                if (liveCategoryIds.Contains(stored.Id))
                {
                    if (stored.MissedRuns != 0)
                    {
                        stored.MissedRuns = 0;
                        _categoryStore.Save(stored);
                    }

                    continue;
                }

                stored.MissedRuns++;
                _categoryStore.Save(stored);

                if (stored.UserPlaylists.TrueForAll(link => link.PlaylistId is null))
                {
                    continue;
                }

                // Grace. The model coins largely different threads each run, so a
                // category going missing once usually means the run phrased things
                // differently rather than the taste having changed — and stripping
                // the row immediately makes it flicker out and back weekly. Waiting
                // costs nothing: the row stays, and a category that really is gone
                // still loses it, just a run or two later.
                if (stored.MissedRuns <= grace)
                {
                    _logger.LogInformation(
                        "Curator: category '{Category}' was not proposed this run ({Missed} of {Grace} allowed); keeping its row for now",
                        stored.Name,
                        stored.MissedRuns,
                        grace);

                    runLog.Step(
                        "category.missed",
                        $"Category '{stored.Name}' was not proposed this run; row kept ({stored.MissedRuns} of {grace} allowed)",
                        new Dictionary<string, object?>
                        {
                            ["categoryId"] = stored.Id.ToString(),
                            ["name"] = stored.Name,
                            ["missedRuns"] = stored.MissedRuns,
                            ["graceRuns"] = grace,
                        });
                    continue;
                }

                _logger.LogInformation(
                    "Curator: category '{Category}' has not been proposed for {Missed} runs; removing its playlists but keeping the definition",
                    stored.Name,
                    stored.MissedRuns);
                await _playlistService.RemoveCategoryPlaylistsAsync(stored, cancellationToken).ConfigureAwait(false);

                runLog.Step(
                    "category.retired",
                    $"Category '{stored.Name}' has not been proposed for {stored.MissedRuns} runs; playlists removed, definition kept",
                    new Dictionary<string, object?>
                    {
                        ["categoryId"] = stored.Id.ToString(),
                        ["name"] = stored.Name,
                        ["missedRuns"] = stored.MissedRuns,
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
        /// <summary>
        /// The daily housekeeping pass: reconcile, refresh, prune. Costs nothing —
        /// no model call is made anywhere in here.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Everything a category run leaves behind drifts between runs. Playlists get
        /// deleted by hand, items leave the library, and — the reason this is daily
        /// rather than weekly — people watch things. The recommendation playlists are
        /// ranked by what a viewer has *not* seen, so they go stale the moment
        /// somebody watches something. Rebuilding them nightly is free and keeps a
        /// spotlight row honest between the weekly runs that cost money.
        /// </para>
        /// <para>
        /// Skips entirely while a run is in progress. A run is rewriting the same
        /// playlists and definitions this would reconcile, and the two racing would
        /// have maintenance delete something the run had not finished claiming yet.
        /// </para>
        /// </remarks>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What was reconciled, refreshed and pruned.</returns>
        public async Task<MaintenanceResult> RunMaintenanceAsync(CancellationToken cancellationToken)
        {
            if (IsRunning)
            {
                _logger.LogInformation("Curator: a run is in progress; skipping maintenance this time");
                return new MaintenanceResult(true, null, 0, 0);
            }

            var config = Plugin.Instance?.Configuration
                ?? throw new InvalidOperationException("Curator: plugin configuration unavailable.");

            // Reconciles playlists against stored categories and re-publishes the
            // home screen rows at the end of it.
            var sync = await SyncPlaylistsAsync(cancellationToken).ConfigureAwait(false);

            // Recommendations are refreshed by their own task now, on a much shorter
            // cadence than this one — they track watch activity, which changes
            // through the day. Doing it here as well would be duplicate work for a
            // result that is already minutes old.
            var prunedSummaries = PruneSummaries();

            _logger.LogInformation(
                "Curator maintenance: {Rebuilt} playlist(s) rebuilt, {RemovedCategories} empty categor(ies) removed, "
                + "{RemovedPlaylists} orphaned playlist(s) deleted, {Pruned} stale summary/summaries pruned",
                sync.CategoriesRebuilt,
                sync.CategoriesRemoved,
                sync.PlaylistsRemoved,
                prunedSummaries);

            return new MaintenanceResult(false, sync, 0, prunedSummaries);
        }

        /// <summary>
        /// Rebuilds every target viewer's recommendation playlist against their
        /// current watch activity. No model call.
        /// </summary>
        /// <remarks>
        /// Its own entry point, and its own scheduled task, because it wants a very
        /// different cadence from anything else here. The ranking is driven by what
        /// a viewer has <em>not</em> watched, so it goes stale the moment somebody
        /// watches something — several times a day is reasonable, where a category
        /// run is weekly because it costs money.
        /// <para>
        /// Skips while a run is in progress: a run rebuilds these itself at the end,
        /// and the two racing would have one overwrite the other's work half-done.
        /// </para>
        /// </remarks>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>How many playlists were rebuilt; -1 when skipped.</returns>
        public async Task<int> RefreshRecommendationsAsync(CancellationToken cancellationToken)
        {
            if (IsRunning)
            {
                _logger.LogInformation(
                    "Curator: a run is in progress and rebuilds these itself; skipping the recommendation refresh");
                return -1;
            }

            var config = Plugin.Instance?.Configuration
                ?? throw new InvalidOperationException("Curator: plugin configuration unavailable.");

            if (!config.RecommendationPlaylists)
            {
                _logger.LogInformation("Curator: recommendation playlists are switched off; nothing to refresh");
                return 0;
            }

            var targetUsers = ResolveTargetUsers(config);
            return await BuildRecommendationsAsync(config, targetUsers, null, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Drops condensed summaries for items that have left the library.
        /// </summary>
        /// <remarks>
        /// Never fatal, and deliberately conservative: if the scan comes back empty
        /// the prune is skipped entirely rather than treated as "every item is gone",
        /// which would throw away the whole summary cache the first time a library
        /// was briefly unavailable.
        /// </remarks>
        private int PruneSummaries()
        {
            try
            {
                var items = _libraryScanner.ScanLibrary(includeEpisodes: false);
                if (items.Count == 0)
                {
                    _logger.LogWarning(
                        "Curator maintenance: library scan returned nothing; leaving the summary cache alone");
                    return 0;
                }

                return _summaryStore.Prune([.. items.Select(i => i.Id)]);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Curator maintenance: could not prune summaries — {Message}", ex.Message);
                return 0;
            }
        }

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

                // The category's own audience, not the whole target list. A personal
                // category belongs to one viewer; passing everyone here is what put
                // every viewer on every row, once a night, since this pass became a
                // scheduled task.
                var audience = CategoryAudience.For(category.OwnerUserId, targetUsers);
                if (audience.Count == 0)
                {
                    continue;
                }

                var before = category.UserPlaylists.Count(link => link.PlaylistId is not null);
                await _playlistService
                    .SyncCategoryAsync(category, audience, cancellationToken)
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
