using System;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Curator.Services.Playlists
{
    /// <summary>
    /// The one way Curator finds a playlist it owns: by the stored GUID, falling
    /// back to the <c>CuratorCategory</c> provider-ID tether stamped on the
    /// playlist at creation.
    /// </summary>
    /// <remarks>
    /// Shared by the service that writes playlists and the home screen section that
    /// reads them, so there is a single implementation of hard rule 3 rather than
    /// two that can drift apart. Never matches by name: duplicate playlist names
    /// are legal in Jellyfin, and Curator gives all six of a shared category's
    /// playlists the same name by design, so a name cannot identify one.
    /// </remarks>
    public static class PlaylistLookup
    {
        /// <summary>
        /// Finds a user's playlist for a category.
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="categoryId">The category the playlist belongs to.</param>
        /// <param name="userId">The user who owns the playlist.</param>
        /// <param name="playlistId">The stored playlist ID, or null if none is recorded.</param>
        /// <returns>The playlist, or null when neither the ID nor the tether finds one.</returns>
        public static Playlist? Find(
            ILibraryManager libraryManager,
            Guid categoryId,
            Guid userId,
            Guid? playlistId)
        {
            ArgumentNullException.ThrowIfNull(libraryManager);

            if (playlistId is { } id && libraryManager.GetItemById(id) is Playlist byId)
            {
                return byId;
            }

            return FindByTether(libraryManager, categoryId.ToString("N"), userId);
        }

        /// <summary>
        /// Finds a playlist by the identity stamped on it, for a given owner.
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="tether">The identity, formatted with "N".</param>
        /// <param name="userId">The user who owns the playlist.</param>
        /// <returns>The playlist, or null.</returns>
        public static Playlist? FindByTether(ILibraryManager libraryManager, string tether, Guid userId)
        {
            ArgumentNullException.ThrowIfNull(libraryManager);

            return libraryManager.GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Playlist],
                Recursive = true,
            }).Items
                .OfType<Playlist>()
                .FirstOrDefault(p => p.OwnerUserId == userId
                    && string.Equals(p.GetProviderId(CuratorPlaylistService.CategoryProviderKey), tether, StringComparison.OrdinalIgnoreCase));
        }
    }
}
