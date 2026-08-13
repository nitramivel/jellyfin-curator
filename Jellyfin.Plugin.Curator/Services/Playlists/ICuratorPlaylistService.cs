using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Core.Playlists;
using Jellyfin.Plugin.Curator.Core.Recommendations;

namespace Jellyfin.Plugin.Curator.Services.Playlists
{
    /// <summary>One playlist the sweep judged, named so a person can check it.</summary>
    /// <param name="Name">The playlist's name.</param>
    /// <param name="PlaylistId">Its Jellyfin ID.</param>
    /// <param name="Reason">Why it qualified: <c>ghost</c> or <c>stranded</c>.</param>
    /// <param name="Deleted">Whether it was actually removed on this pass.</param>
    public sealed record EmptyPlaylistCandidate(string Name, string PlaylistId, string Reason, bool Deleted);

    /// <summary>What the empty-playlist sweep found and did.</summary>
    /// <param name="Examined">How many playlists were looked at.</param>
    /// <param name="Candidates">The ones that qualified, most interesting first.</param>
    /// <param name="Deleted">How many were removed. 0 on a preview.</param>
    /// <param name="DirectoriesRemoved">How many leftover folders went with them.</param>
    /// <param name="Applied">Whether this pass deleted anything or only looked.</param>
    public sealed record EmptyPlaylistSweepResult(
        int Examined,
        IReadOnlyList<EmptyPlaylistCandidate> Candidates,
        int Deleted,
        int DirectoriesRemoved,
        bool Applied);

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
        /// Finds — and optionally deletes — playlists that hold nothing and belong to
        /// nobody.
        /// </summary>
        /// <remarks>
        /// The judgement is <see cref="Core.Playlists.EmptyPlaylistSweep"/> and is
        /// deliberately narrow: a viewer's own empty playlist is never touched. See
        /// that type for why "empty" alone is the wrong test.
        /// <para>
        /// Deleting the database row is not enough on its own. These come back — a
        /// playlist directory that outlives its row is re-imported by the next
        /// library scan as a fresh ownerless playlist, which is how they appeared in
        /// the first place. So the leftover directory goes too, and only when it
        /// holds nothing but Jellyfin's own <c>playlist.xml</c>.
        /// </para>
        /// </remarks>
        /// <param name="apply">
        /// False to report what would go without touching anything, which is what
        /// the config page's first click does. True to delete.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What was found, and what was removed.</returns>
        Task<EmptyPlaylistSweepResult> SweepEmptyPlaylistsAsync(bool apply, CancellationToken cancellationToken);

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
        /// <param name="scope">
        /// Which of that viewer's lists. Each scope carries its own tether, so the
        /// three never collide; passing an empty <paramref name="memberIds"/> is how
        /// a scope the owner has switched off is taken away again, through the same
        /// ownership table that empties a category — which is why turning the split
        /// off deletes the per-type lists but hands off any the viewer has untagged.
        /// </param>
        /// <param name="name">The playlist name; the same for every viewer.</param>
        /// <param name="memberIds">The items, most recommended first.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// The playlist's ID so the caller can claim it against orphan cleanup, or
        /// null when there is no playlist (empty list, or handed off).
        /// </returns>
        Task<Guid?> SyncRecommendationsAsync(
            Guid userId,
            RecommendationScope scope,
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
