using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Core.Playlists;
using Jellyfin.Plugin.Curator.Core.Recommendations;
using Jellyfin.Plugin.Curator.Services.Categories;
using Jellyfin.Plugin.Curator.Services.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services.Playlists
{
    /// <summary>
    /// Default <see cref="ICuratorPlaylistService"/>. Ownership model:
    /// a visible "curator" tag marks a playlist as plugin-created — removing it
    /// hands the playlist to the user permanently. Run timestamp, model, and the
    /// category tether live in provider IDs; the tether allows recovery when a
    /// stored playlist ID goes stale (pattern from SmartLists). Playlists are
    /// never resolved by name.
    /// </summary>
    public class CuratorPlaylistService : ICuratorPlaylistService
    {
        /// <summary>The visible tag marking a playlist as Curator-managed.</summary>
        public const string OwnershipTag = "curator";

        /// <summary>Provider ID key tethering a playlist to its category definition.</summary>
        public const string CategoryProviderKey = "CuratorCategory";

        /// <summary>Provider ID key recording the model that produced the last run.</summary>
        public const string ModelProviderKey = "CuratorModel";

        /// <summary>Provider ID key recording the last run timestamp (ISO 8601 UTC).</summary>
        public const string RunProviderKey = "CuratorRun";

        private readonly IPlaylistManager _playlistManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly ICategoryStore _categoryStore;
        private readonly IUserActivityProvider _activityProvider;
        private readonly ILogger<CuratorPlaylistService> _logger;

        public CuratorPlaylistService(
            IPlaylistManager playlistManager,
            ILibraryManager libraryManager,
            IUserManager userManager,
            ICategoryStore categoryStore,
            IUserActivityProvider activityProvider,
            ILogger<CuratorPlaylistService> logger)
        {
            _activityProvider = activityProvider;
            _playlistManager = playlistManager;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _categoryStore = categoryStore;
            _logger = logger;
        }

        /// <summary>
        /// This viewer's activity for these members, or null when it cannot be read.
        /// </summary>
        /// <remarks>
        /// Ordering is a nicety; building the playlist is not. A provider that throws
        /// costs the viewer a personalized order and nothing else, so this swallows
        /// rather than letting a reconcile pass die partway through the library.
        /// </remarks>
        private IReadOnlyDictionary<Guid, UserActivity>? SafeActivity(Guid userId, IReadOnlyList<Guid> members)
        {
            if (members.Count < 2)
            {
                return null;
            }

            try
            {
                return _activityProvider.GetActivity(userId, members);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Curator: could not read activity for user {UserId}; leaving the model's order", userId);
                return null;
            }
        }

        /// <inheritdoc />
        public async Task SyncCategoryAsync(
            CategoryDefinition category,
            IReadOnlyList<Guid> targetUserIds,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(category);
            ArgumentNullException.ThrowIfNull(targetUserIds);

            var members = ResolveMembers(category);

            // The audience is authoritative: a playlist held by anyone outside it is
            // one this category should never have had, so it is treated exactly as an
            // empty category is for that user — deleted if Curator still owns it,
            // handed off if the tag has gone, never touched if it was handed off
            // before. That reuses the ownership table rather than second-guessing it,
            // and it is what repairs the definitions that a nightly reconcile already
            // spread across every viewer.
            var audience = new HashSet<Guid>(targetUserIds);
            var strays = category.UserPlaylists
                .Select(link => link.UserId)
                .Where(id => !audience.Contains(id))
                .ToList();

            foreach (var userId in targetUserIds.Concat(strays))
            {
                var wanted = audience.Contains(userId);
                cancellationToken.ThrowIfCancellationRequested();

                if (_userManager.GetUserById(userId) is null)
                {
                    _logger.LogWarning("Curator: user {UserId} not found; skipping playlist sync for '{Category}'", userId, category.Name);
                    continue;
                }

                // Ordered for this viewer. Shared rows go to everyone by design, so
                // the order inside a viewer's own copy is the only personalization
                // available that cannot take a row away from somebody. A personal
                // category is already theirs alone and needs no reordering.
                var ordered = wanted && category.OwnerUserId is null
                    ? MemberOrdering.For(
                        members,
                        item => item.Id,
                        SafeActivity(userId, [.. members.Select(item => item.Id)]))
                    : members;

                var link = category.GetOrAddLink(userId);
                var playlist = ResolvePlaylist(category, link);
                var action = PlaylistSyncDecision.Decide(
                    link.HandedOff,
                    playlist is not null,
                    playlist is not null && HasOwnershipTag(playlist),
                    wanted && members.Count > 0);

                switch (action)
                {
                    case SyncAction.Skip:
                    case SyncAction.Nothing:
                        break;

                    case SyncAction.HandOff:
                        link.HandedOff = true;
                        _logger.LogInformation(
                            "Curator: playlist '{Playlist}' ({PlaylistId}) no longer carries the '{Tag}' tag; handing it to user {UserId} permanently",
                            playlist!.Name,
                            playlist.Id,
                            OwnershipTag,
                            userId);
                        break;

                    case SyncAction.Delete:
                        _logger.LogInformation(
                            wanted
                                ? "Curator: category '{Category}' is empty; removing playlist for user {UserId} (definition kept)"
                                : "Curator: category '{Category}' does not belong to user {UserId}; removing their copy",
                            category.Name,
                            userId);
                        _libraryManager.DeleteItem(playlist!, new DeleteOptions { DeleteFileLocation = true }, true);
                        link.PlaylistId = null;
                        break;

                    case SyncAction.Create:
                        link.PlaylistId = await CreatePlaylistAsync(category, userId, ordered, cancellationToken).ConfigureAwait(false);
                        break;

                    case SyncAction.Update:
                        await UpdatePlaylistAsync(playlist!, category, ordered, cancellationToken).ConfigureAwait(false);
                        link.PlaylistId = playlist!.Id;
                        break;
                }
            }

            _categoryStore.Save(category);
        }

        /// <inheritdoc />
        public async Task<Guid?> SyncRecommendationsAsync(
            Guid userId,
            string name,
            IReadOnlyList<Guid> memberIds,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(memberIds);

            if (string.IsNullOrWhiteSpace(name))
            {
                _logger.LogWarning("Curator: no recommendation playlist name configured; skipping");
                return null;
            }

            if (_userManager.GetUserById(userId) is null)
            {
                _logger.LogWarning("Curator: user {UserId} not found; skipping recommendations", userId);
                return null;
            }

            // A synthetic definition, built in memory and never handed to the
            // category store. It exists so this path reuses the same create, update,
            // tag, tether and ordering code as a real category rather than growing a
            // parallel copy of it that could drift on the rules that matter.
            var definition = new CategoryDefinition
            {
                Id = RecommendationRanker.IdentityFor(userId),
                Name = name.Trim(),
                Description = "Ranked recommendations, most recommended first. Built by Curator.",
                Members = [.. memberIds],
                OwnerUserId = userId,
                ModelId = "curator-ranked",
                UpdatedAt = DateTime.UtcNow,
            };

            var members = ResolveMembers(definition);
            var playlist = FindByTether(definition.Id, userId);
            var action = PlaylistSyncDecision.Decide(
                handedOff: false,
                playlistFound: playlist is not null,
                tagPresent: playlist is not null && HasOwnershipTag(playlist),
                hasMembers: members.Count > 0);

            switch (action)
            {
                case SyncAction.HandOff:
                    // The viewer removed the tag, so this playlist is theirs now and
                    // Curator must never touch it again. No stored flag is needed:
                    // the missing tag says so on every future run.
                    _logger.LogInformation(
                        "Curator: recommendation playlist '{Playlist}' for user {UserId} no longer carries the "
                        + "'{Tag}' tag; leaving it to them permanently",
                        playlist!.Name,
                        userId,
                        OwnershipTag);
                    return null;

                case SyncAction.Delete:
                    _logger.LogInformation(
                        "Curator: no recommendations for user {UserId}; removing their playlist", userId);
                    _libraryManager.DeleteItem(playlist!, new DeleteOptions { DeleteFileLocation = true }, true);
                    return null;

                case SyncAction.Create:
                    return await CreatePlaylistAsync(definition, userId, members, cancellationToken)
                        .ConfigureAwait(false);

                case SyncAction.Update:
                    await UpdatePlaylistAsync(playlist!, definition, members, cancellationToken).ConfigureAwait(false);
                    return playlist!.Id;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Finds a viewer's playlist by its provider-ID tether.
        /// </summary>
        /// <remarks>
        /// The recommendation playlist has no stored ID to look up, so the tether is
        /// its only identity. Matching on name instead would be wrong for the reason
        /// hard rule 3 gives: duplicate playlist names are legal in Jellyfin, and a
        /// viewer who made their own "Recommended for You" would have it silently
        /// taken over and overwritten.
        /// </remarks>
        private Playlist? FindByTether(Guid identity, Guid userId)
        {
            var tether = identity.ToString("N");
            return _libraryManager.GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Playlist],
                Recursive = true,
            }).Items
                .OfType<Playlist>()
                .FirstOrDefault(p => p.OwnerUserId == userId
                    && string.Equals(p.GetProviderId(CategoryProviderKey), tether, StringComparison.OrdinalIgnoreCase));
        }

        /// <inheritdoc />
        public async Task RemoveCategoryPlaylistsAsync(CategoryDefinition category, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(category);

            foreach (var link in category.UserPlaylists)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (link.HandedOff || link.PlaylistId is null)
                {
                    continue;
                }

                if (_libraryManager.GetItemById(link.PlaylistId.Value) is Playlist playlist && HasOwnershipTag(playlist))
                {
                    _logger.LogInformation(
                        "Curator: removing playlist '{Playlist}' for user {UserId} (category '{Category}' removed)",
                        playlist.Name,
                        link.UserId,
                        category.Name);
                    _libraryManager.DeleteItem(playlist, new DeleteOptions { DeleteFileLocation = true }, true);
                }

                link.PlaylistId = null;
            }

            _categoryStore.Save(category);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<int> RemoveOrphanedPlaylistsAsync(
            IReadOnlySet<Guid> claimed,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(claimed);

            var playlists = _libraryManager.GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Playlist],
                Recursive = true,
            }).Items.OfType<Playlist>().ToList();

            var deleted = 0;
            foreach (var playlist in playlists)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The tag is the ownership contract. A playlist without it belongs to
                // the user permanently — including one Curator made and they later
                // untagged to keep. Never delete it, whatever the definitions say.
                if (!HasOwnershipTag(playlist))
                {
                    continue;
                }

                if (claimed.Contains(playlist.Id))
                {
                    continue;
                }

                // A recommendation playlist is Curator-owned but no stored category
                // points at it, which is precisely the shape this sweep deletes. It
                // is recognised by its tether being the identity derived from its
                // own owner, so no caller has to remember to claim it — forgetting
                // to would silently delete every viewer's spotlight row on any run
                // that reached here.
                if (IsRecommendationPlaylist(playlist))
                {
                    continue;
                }

                _logger.LogInformation(
                    "Curator: deleting orphaned playlist '{Playlist}' ({PlaylistId}); no stored category claims it",
                    playlist.Name,
                    playlist.Id);

                _libraryManager.DeleteItem(playlist, new DeleteOptions { DeleteFileLocation = true }, true);
                deleted++;
            }

            await Task.CompletedTask.ConfigureAwait(false);
            return deleted;
        }

        /// <summary>
        /// Resolves member IDs to live items, preserving order and dropping items
        /// that have left the library since the run.
        /// </summary>
        private List<BaseItem> ResolveMembers(CategoryDefinition category)
        {
            var items = new List<BaseItem>(category.Members.Count);
            foreach (var memberId in category.Members)
            {
                if (_libraryManager.GetItemById(memberId) is { } item)
                {
                    items.Add(item);
                }
            }

            if (items.Count < category.Members.Count)
            {
                _logger.LogInformation(
                    "Curator: category '{Category}': {Missing} of {Total} members no longer exist in the library",
                    category.Name,
                    category.Members.Count - items.Count,
                    category.Members.Count);
            }

            return items;
        }

        /// <summary>
        /// Resolves the user's playlist by stored GUID; if the stored ID is stale,
        /// attempts recovery via the category tether stamped on the playlist at
        /// creation. Never matches by name — duplicate names are legal.
        /// </summary>
        private Playlist? ResolvePlaylist(CategoryDefinition category, UserPlaylistLink link)
        {
            if (link.PlaylistId is { } playlistId
                && _libraryManager.GetItemById(playlistId) is Playlist byId)
            {
                return byId;
            }

            if (link.PlaylistId is null)
            {
                return null;
            }

            var tether = category.Id.ToString("N");
            var recovered = _libraryManager.GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Playlist],
                Recursive = true,
            }).Items
                .OfType<Playlist>()
                .FirstOrDefault(p => p.OwnerUserId == link.UserId
                    && string.Equals(p.GetProviderId(CategoryProviderKey), tether, StringComparison.OrdinalIgnoreCase));

            if (recovered is not null)
            {
                _logger.LogInformation(
                    "Curator: recovered playlist '{Playlist}' for user {UserId} via category tether; stored playlist ID was stale",
                    recovered.Name,
                    link.UserId);
                link.PlaylistId = recovered.Id;
            }

            return recovered;
        }

        private async Task<Guid?> CreatePlaylistAsync(
            CategoryDefinition category,
            Guid userId,
            IReadOnlyList<BaseItem> members,
            CancellationToken cancellationToken)
        {
            var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = category.Name,
                UserId = userId,
                Public = false,
                MediaType = MediaType.Video,
            }).ConfigureAwait(false);

            if (_libraryManager.GetItemById(result.Id) is not Playlist playlist)
            {
                _logger.LogWarning(
                    "Curator: failed to retrieve newly created playlist {PlaylistId} for category '{Category}'",
                    result.Id,
                    category.Name);
                return null;
            }

            await ApplyStateAsync(playlist, category, members, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Curator: created playlist '{Category}' with {Count} items for user {UserId}",
                category.Name,
                members.Count,
                userId);
            return playlist.Id;
        }

        private async Task UpdatePlaylistAsync(
            Playlist playlist,
            CategoryDefinition category,
            IReadOnlyList<BaseItem> members,
            CancellationToken cancellationToken)
        {
            playlist.Name = category.Name;
            await ApplyStateAsync(playlist, category, members, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Curator: updated playlist '{Category}' to {Count} items for user {UserId}",
                category.Name,
                members.Count,
                playlist.OwnerUserId);
        }

        /// <summary>
        /// Applies members (exact order via direct LinkedChildren assignment),
        /// ownership tag, tether, run metadata, and description, then persists.
        /// </summary>
        private static async Task ApplyStateAsync(
            Playlist playlist,
            CategoryDefinition category,
            IReadOnlyList<BaseItem> members,
            CancellationToken cancellationToken)
        {
            playlist.LinkedChildren = members
                .Select(item => new LinkedChild { ItemId = item.Id, Path = item.Path })
                .ToArray();

            if (!HasOwnershipTag(playlist))
            {
                playlist.Tags = [.. playlist.Tags ?? [], OwnershipTag];
            }

            playlist.Overview = category.Description;
            playlist.SetProviderId(CategoryProviderKey, category.Id.ToString("N"));
            playlist.SetProviderId(ModelProviderKey, category.ModelId);
            playlist.SetProviderId(RunProviderKey, category.UpdatedAt.ToString("o", CultureInfo.InvariantCulture));

            await playlist.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Whether this playlist is a viewer's recommendation list.
        /// </summary>
        /// <remarks>
        /// Self-identifying: the tether holds the identity derived from the owning
        /// user, so a playlist can be recognised without consulting anything else.
        /// A playlist belonging to a different user cannot match, so one viewer's
        /// list can never shield another's.
        /// </remarks>
        private static bool IsRecommendationPlaylist(Playlist playlist)
        {
            var tether = playlist.GetProviderId(CategoryProviderKey);
            if (string.IsNullOrEmpty(tether) || playlist.OwnerUserId == Guid.Empty)
            {
                return false;
            }

            return string.Equals(
                tether,
                RecommendationRanker.IdentityFor(playlist.OwnerUserId).ToString("N"),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasOwnershipTag(Playlist playlist)
        {
            return playlist.Tags?.Any(tag => string.Equals(tag, OwnershipTag, StringComparison.OrdinalIgnoreCase)) == true;
        }
    }
}
