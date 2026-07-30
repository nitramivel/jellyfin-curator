using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Services.Playlists
{
    /// <summary>
    /// Creates, updates, and removes the Jellyfin playlists backing one category,
    /// per target user. Persists link changes back through the category store.
    /// </summary>
    public interface ICuratorPlaylistService
    {
        /// <summary>
        /// Brings each target user's playlist in line with the category definition:
        /// creates missing playlists, updates existing ones in member order, removes
        /// the playlist (keeping the definition) when the category is empty, and
        /// permanently hands off any playlist whose ownership tag was removed.
        /// </summary>
        /// <param name="category">The category definition; its links are mutated and saved.</param>
        /// <param name="targetUserIds">The users to sync.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        Task SyncCategoryAsync(CategoryDefinition category, IReadOnlyList<Guid> targetUserIds, CancellationToken cancellationToken);

        /// <summary>
        /// Removes all of the category's playlists that Curator still owns (tagged,
        /// not handed off). The definition itself is left to the caller.
        /// </summary>
        /// <param name="category">The category definition; its links are mutated and saved.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        Task RemoveCategoryPlaylistsAsync(CategoryDefinition category, CancellationToken cancellationToken);

        /// <summary>
        /// Deletes every Curator-owned playlist that no stored category claims.
        /// </summary>
        /// <param name="claimed">Playlist IDs the stored definitions still point at.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>How many playlists were deleted.</returns>
        Task<int> RemoveOrphanedPlaylistsAsync(IReadOnlySet<Guid> claimed, CancellationToken cancellationToken);
    }
}
