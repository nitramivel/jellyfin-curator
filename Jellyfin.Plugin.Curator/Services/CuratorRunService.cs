using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Configuration;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Core.Reconciliation;
using Jellyfin.Plugin.Curator.Services.Categories;
using Jellyfin.Plugin.Curator.Services.HomeScreen;
using Jellyfin.Plugin.Curator.Services.Library;
using Jellyfin.Plugin.Curator.Services.Llm;
using Jellyfin.Plugin.Curator.Services.Playlists;
using Jellyfin.Plugin.Curator.Services.Runs;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services
{
    /// <summary>
    /// The end-to-end run: scan → propose → reconcile → build playlists →
    /// publish home screen rows. Shared by the scheduled task and the manual
    /// trigger so both take exactly the same path.
    /// </summary>
    public class CuratorRunService
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
        private readonly ILogger<CuratorRunService> _logger;
        private readonly SemaphoreSlim _runLock = new(1, 1);

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

            try
            {
                await RunCoreAsync(config, progress, runLog, cancellationToken).ConfigureAwait(false);
                runLog.Complete();
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

        private async Task RunCoreAsync(
            PluginConfiguration config,
            IProgress<double>? progress,
            IRunLog runLog,
            CancellationToken cancellationToken)
        {
            _claimedDefinitions.Clear();

            var provider = _providerFactory.Create(config);
            runLog.SetProvider(
                config.Provider.ToString(),
                provider.ModelId,
                config.InputCostPerMillion,
                config.OutputCostPerMillion);

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

            // 1. Scan.
            var records = _libraryScanner.ScanLibrary(config.IncludeEpisodes);
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
                config.InputCostPerMillion,
                config.OutputCostPerMillion,
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
                // and their history. The model both selects and invents.
                var sharedSelections = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
                var personalByUser = new List<(Guid UserId, IReadOnlyList<ReconciledCategory> Categories)>();

                // Users who watch too little to personalize. They are skipped before
                // the LLM call — the prompt is the cost and it is paid on send — and
                // fall back to the shared categories, which are already paid for.
                var unpersonalizedUsers = new List<Guid>();
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

                        unpersonalizedUsers.Add(userId);
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

                    foreach (var name in personal.SelectedNames)
                    {
                        if (!sharedSelections.TryGetValue(name, out var users))
                        {
                            users = [];
                            sharedSelections[name] = users;
                        }

                        users.Add(userId);
                    }

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
                        "Curator: user {User} selected {Selected} shared categories and gained {New} of their own",
                        userId,
                        personal.SelectedNames.Count,
                        invented.Count);

                    personalByUser.Add((userId, invented));

                    runLog.Step(
                        "user.pass",
                        $"User {userId} selected {personal.SelectedNames.Count} shared categories and gained {invented.Count} of their own",
                        new Dictionary<string, object?>
                        {
                            ["userId"] = userId.ToString(),
                            ["watchedCount"] = watched,
                            ["activityEntries"] = activity.Count,
                            ["selectedNames"] = personal.SelectedNames.ToArray(),
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

                // Shared categories: one definition each, linked to whoever chose it,
                // plus everyone who never got a pass to choose with.
                foreach (var category in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    sharedSelections.TryGetValue(category.Name, out var chosenBy);
                    var recipients = new List<Guid>(chosenBy ?? []);
                    recipients.AddRange(unpersonalizedUsers);

                    if (recipients.Count == 0)
                    {
                        // Nobody wanted it. Leave the definition unbuilt rather than
                        // forcing a row on everyone.
                        runLog.Step(
                            "category.unwanted",
                            $"Shared category '{category.Name}' was chosen by nobody; not built",
                            new Dictionary<string, object?>
                            {
                                ["name"] = category.Name,
                                ["memberCount"] = category.Members.Count,
                            });
                        continue;
                    }

                    var definition = MergeIntoStore(existing, category, provider.ModelId, ownerUserId: null, runLog);
                    allCategoryIds.Add(definition.Id);
                    await _playlistService
                        .SyncCategoryAsync(definition, recipients, cancellationToken)
                        .ConfigureAwait(false);
                    LogCategoryBuilt(runLog, definition, recipients, "shared");
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
        /// Snapshots the settings that shaped a run. The API key is deliberately
        /// absent: run logs are the thing someone attaches to a bug report.
        /// </summary>
        private static Dictionary<string, object?> DescribeSettings(PluginConfiguration config)
        {
            return new Dictionary<string, object?>
            {
                ["provider"] = config.Provider.ToString(),
                ["model"] = config.Model,
                ["baseUrl"] = config.BaseUrl,
                ["batchSize"] = config.BatchSize,
                ["maxOutputTokens"] = config.MaxOutputTokens,
                ["tokenBudget"] = config.TokenBudget,
                ["maxSharedCategories"] = config.MaxSharedCategories,
                ["minSharedCategorySize"] = config.MinSharedCategorySize,
                ["maxPersonalCategories"] = config.MaxPersonalCategories,
                ["maxCategoryMembers"] = config.MaxCategoryMembers,
                ["minPersonalCategorySize"] = config.MinPersonalCategorySize,
                ["minWatchedForPersonalization"] = config.MinWatchedForPersonalization,
                ["maxTagsPerItem"] = config.MaxTagsPerItem,
                ["personalizedPlaylists"] = config.PersonalizedPlaylists,
                ["outputType"] = config.OutputType.ToString(),
                ["includeEpisodes"] = config.IncludeEpisodes,
                ["enableThinking"] = config.EnableThinking,
                ["useBatchApi"] = config.UseBatchApi,
                ["autoEnableSections"] = config.AutoEnableSections,
                ["inputCostPerMillion"] = config.InputCostPerMillion,
                ["outputCostPerMillion"] = config.OutputCostPerMillion,
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
