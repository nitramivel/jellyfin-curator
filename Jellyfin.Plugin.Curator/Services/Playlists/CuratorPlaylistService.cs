using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Core.Playlists;
using Jellyfin.Plugin.Curator.Services.Categories;
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
        private readonly ILogger<CuratorPlaylistService> _logger;

        public CuratorPlaylistService(
            IPlaylistManager playlistManager,
            ILibraryManager libraryManager,
            IUserManager userManager,
            ICategoryStore categoryStore,
            ILogger<CuratorPlaylistService> logger)
        {
            _playlistManager = playlistManager;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _categoryStore = categoryStore;
            _logger = logger;
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

            foreach (var userId in targetUserIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_userManager.GetUserById(userId) is null)
                {
                    _logger.LogWarning("Curator: user {UserId} not found; skipping playlist sync for '{Category}'", userId, category.Name);
                    continue;
                }

                var link = category.GetOrAddLink(userId);
                var playlist = ResolvePlaylist(category, link);
                var action = PlaylistSyncDecision.Decide(
                    link.HandedOff,
                    playlist is not null,
                    playlist is not null && HasOwnershipTag(playlist),
                    members.Count > 0);

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
                            "Curator: category '{Category}' is empty; removing playlist for user {UserId} (definition kept)",
                            category.Name,
                            userId);
                        _libraryManager.DeleteItem(playlist!, new DeleteOptions { DeleteFileLocation = true }, true);
                        link.PlaylistId = null;
                        break;

                    case SyncAction.Create:
                        link.PlaylistId = await CreatePlaylistAsync(category, userId, members, cancellationToken).ConfigureAwait(false);
                        break;

                    case SyncAction.Update:
                        await UpdatePlaylistAsync(playlist!, category, members, cancellationToken).ConfigureAwait(false);
                        link.PlaylistId = playlist!.Id;
                        break;
                }
            }

            _categoryStore.Save(category);
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

        private static bool HasOwnershipTag(Playlist playlist)
        {
            return playlist.Tags?.Any(tag => string.Equals(tag, OwnershipTag, StringComparison.OrdinalIgnoreCase)) == true;
        }
    }
}
