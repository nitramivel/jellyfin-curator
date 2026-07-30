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
        private readonly ILogger<CuratorRunService> _logger;
        private readonly SemaphoreSlim _runLock = new(1, 1);

        public CuratorRunService(
            ILibraryScanner libraryScanner,
            IUserActivityProvider userActivityProvider,
            LlmProviderFactory providerFactory,
            CategoryProposalService proposalService,
            ICategoryStore categoryStore,
            ICuratorPlaylistService playlistService,
            IHomeScreenIntegrationService homeScreenService,
            IUserManager userManager,
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
            _logger = logger;
        }

        /// <summary>
        /// Gets a value indicating whether a run is currently in progress.
        /// </summary>
        public bool IsRunning => _runLock.CurrentCount == 0;

        /// <summary>
        /// Runs the full pipeline. Only one run happens at a time; a second
        /// caller is rejected rather than queued, since runs cost money.
        /// </summary>
        /// <param name="progress">Progress reporter (0-100).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        public async Task RunAsync(IProgress<double>? progress, CancellationToken cancellationToken)
        {
            if (!await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("Curator: a run is already in progress; ignoring this request");
                return;
            }

            try
            {
                await RunCoreAsync(progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _runLock.Release();
            }
        }

        private async Task RunCoreAsync(IProgress<double>? progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration
                ?? throw new InvalidOperationException("Curator: plugin configuration unavailable.");

            var provider = _providerFactory.Create(config);
            var targetUsers = ResolveTargetUsers(config);
            if (targetUsers.Count == 0)
            {
                _logger.LogWarning("Curator: no target users resolved; nothing to build");
                return;
            }

            progress?.Report(2);

            // 1. Scan.
            var records = _libraryScanner.ScanLibrary(config.IncludeEpisodes);
            if (records.Count == 0)
            {
                _logger.LogWarning("Curator: library scan produced no items; nothing to categorize");
                return;
            }

            progress?.Report(5);

            var settings = new ProposalRunSettings(
                config.BatchSize,
                config.MaxOutputTokens,
                config.TokenBudget,
                config.InputCostPerMillion,
                config.OutputCostPerMillion,
                config.UseBatchApi,
                config.MaxTagsPerItem);

            // Personalized playlist runs go once per user, with that user's watch
            // activity attached; everything else is a single shared run.
            var personalized = config.OutputType == OutputKind.Playlist && config.PersonalizedPlaylists;
            var runs = personalized
                ? targetUsers.Select(userId => (UserId: (Guid?)userId, Users: (IReadOnlyList<Guid>)[userId])).ToList()
                : [(UserId: null, Users: targetUsers)];

            if (personalized && targetUsers.Count > 1)
            {
                _logger.LogInformation(
                    "Curator: personalized playlists are on, so the library goes to the model once per user — {Count} runs this pass",
                    targetUsers.Count);
            }

            var allCategoryIds = new HashSet<Guid>();
            var existing = _categoryStore.GetAll();
            var runIndex = 0;

            foreach (var (userId, users) in runs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var activity = userId is { } id && personalized
                    ? _userActivityProvider.GetActivity(id, records.Select(r => r.Id).ToArray())
                    : null;

                // 2-3. Batch and propose.
                var proposalResult = await _proposalService
                    .ProposeAsync(provider, records, settings, activity, cancellationToken)
                    .ConfigureAwait(false);

                var proposalProgress = 5 + (runIndex + 1) * 70.0 / runs.Count;
                progress?.Report(proposalProgress);

                if (proposalResult.Proposals.Count == 0)
                {
                    _logger.LogWarning("Curator: the model proposed no usable categories; leaving existing playlists untouched");
                    runIndex++;
                    continue;
                }

                // 4. Reconcile.
                var reconciled = Reconciler.Reconcile(
                    proposalResult.Proposals,
                    new ReconcilerSettings(config.MinCategorySize, config.MaxCategories));

                _logger.LogInformation(
                    "Curator: {Proposals} proposals reconciled into {Categories} categories{Scope}",
                    proposalResult.Proposals.Count,
                    reconciled.Count,
                    userId is { } u ? $" for user {u}" : string.Empty);

                // 5. Build playlists.
                foreach (var category in reconciled)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var definition = MergeIntoStore(existing, category, provider.ModelId, userId);
                    allCategoryIds.Add(definition.Id);
                    await _playlistService
                        .SyncCategoryAsync(definition, users, cancellationToken)
                        .ConfigureAwait(false);
                }

                runIndex++;
            }

            progress?.Report(85);

            await RetireMissingCategoriesAsync(allCategoryIds, cancellationToken).ConfigureAwait(false);

            progress?.Report(92);

            // 6. Publish home screen rows.
            await _homeScreenService
                .SyncSectionsAsync(_categoryStore.GetAll(), targetUsers, cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(100);
            _logger.LogInformation("Curator: run complete — {Count} categories live", allCategoryIds.Count);
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
            Guid? scopedUserId)
        {
            var now = DateTime.UtcNow;
            var match = CategoryIdentity.FindMatch(existing, category.Name, scopedUserId);

            var definition = match ?? new CategoryDefinition
            {
                Id = Guid.NewGuid(),
                Name = category.Name,
                CreatedAt = now,
            };

            definition.Name = category.Name;
            definition.Description = category.Description;
            definition.Members = [.. category.Members];
            definition.SourceProposalCount = category.SourceProposalCount;
                    definition.SourceProposals = [.. category.SourceProposals];
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
        private async Task RetireMissingCategoriesAsync(HashSet<Guid> liveCategoryIds, CancellationToken cancellationToken)
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
