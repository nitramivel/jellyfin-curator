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
        /// Brings one viewer's recommendation playlist in line with a ranked list of
        /// items.
        /// </summary>
        /// <remarks>
        /// Unlike a category this has no stored definition, so its identity is
        /// derived from the user ID and carried on the playlist as the usual
        /// provider-ID tether. It is still found by that tether and never by name —
        /// hard rule 3 — and it still hands off permanently the moment its ownership
        /// tag is removed, exactly like a category playlist.
        /// </remarks>
        /// <param name="userId">The viewer.</param>
        /// <param name="name">The playlist name; the same for every viewer.</param>
        /// <param name="memberIds">The items, most recommended first.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// The playlist's ID so the caller can claim it against orphan cleanup, or
        /// null when there is no playlist (empty list, or handed off).
        /// </returns>
        Task<Guid?> SyncRecommendationsAsync(
            Guid userId,
            string name,
            IReadOnlyList<Guid> memberIds,
            CancellationToken cancellationToken);

        /// <summary>
        /// Deletes every Curator-owned playlist that no stored category claims.
        /// </summary>
        /// <param name="claimed">Playlist IDs the stored definitions still point at.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>How many playlists were deleted.</returns>
        Task<int> RemoveOrphanedPlaylistsAsync(IReadOnlySet<Guid> claimed, CancellationToken cancellationToken);
    }
}
