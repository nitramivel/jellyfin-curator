using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Core.Recommendations
{
    /// <summary>
    /// Which of a viewer's recommendation playlists is being built.
    /// </summary>
    public enum RecommendationScope
    {
        /// <summary>Everything, films and television together. Always built.</summary>
        Combined = 0,

        /// <summary>Films only.</summary>
        Movies = 1,

        /// <summary>Television only — series, and episodes when those are scanned.</summary>
        Shows = 2,
    }

    /// <summary>
    /// Cuts one viewer's ranked recommendations into the per-type lists.
    ///
    /// <para>
    /// This is a filter over an order that has already been decided, never a second
    /// ranking. <see cref="RecommendationRanker"/> weighs a viewer's categories once
    /// and the result is shared by every scope, so the films list is the films of
    /// that one order and the television list is the television of it. Ranking each
    /// type separately would be a different — and worse — thing: an item's score
    /// comes from how many of the viewer's threads it turns up in, and those threads
    /// mix films and television by design.
    /// </para>
    /// <para>
    /// It also keeps the cost where hard rule 15 puts it. Selection is arithmetic,
    /// and splitting is arithmetic over the same arithmetic; the one paid call this
    /// feature can make is the re-rank, which happens upstream on the combined order
    /// and is therefore still one call per viewer however many lists come out of it.
    /// </para>
    /// </summary>
    public static class RecommendationSplit
    {
        /// <summary>
        /// Takes one scope's slice of a ranked list.
        /// </summary>
        /// <param name="scope">Which list is being built.</param>
        /// <param name="ranked">The viewer's recommendations, most recommended first.</param>
        /// <param name="kinds">
        /// What kind each item is. An item missing from this map is dropped from the
        /// per-type lists: the only way to be absent is to have left the library
        /// since the categories were built, and an item that cannot be resolved
        /// cannot be shown either. It stays in <see cref="RecommendationScope.Combined"/>,
        /// which is the unfiltered order and is left exactly as it was handed in —
        /// that list's contents must not change just because this feature exists.
        /// </param>
        /// <param name="maxItems">How many items this list may hold. 0 means no cap.</param>
        /// <returns>The slice, in the order it was given.</returns>
        public static IReadOnlyList<Guid> Select(
            RecommendationScope scope,
            IReadOnlyList<Guid> ranked,
            IReadOnlyDictionary<Guid, MediaKind> kinds,
            int maxItems)
        {
            ArgumentNullException.ThrowIfNull(ranked);
            ArgumentNullException.ThrowIfNull(kinds);

            IEnumerable<Guid> selected = scope == RecommendationScope.Combined
                ? ranked
                : ranked.Where(id => kinds.TryGetValue(id, out var kind) && Belongs(kind, scope));

            if (maxItems > 0)
            {
                selected = selected.Take(maxItems);
            }

            return [.. selected];
        }

        /// <summary>
        /// Whether an item of this kind belongs in this scope's list.
        /// </summary>
        /// <remarks>
        /// An episode counts as television. Episodes only reach a category when the
        /// owner has asked for them to be scanned, and when they have, a viewer
        /// looking at a television row wants them there — the alternative is a
        /// "shows" list that silently omits half of what the ranker chose.
        /// </remarks>
        private static bool Belongs(MediaKind kind, RecommendationScope scope)
        {
            return scope switch
            {
                RecommendationScope.Movies => kind == MediaKind.Movie,
                RecommendationScope.Shows => kind is MediaKind.Series or MediaKind.Episode,
                _ => true,
            };
        }
    }
}
