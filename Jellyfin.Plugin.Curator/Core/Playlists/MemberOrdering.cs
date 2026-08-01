using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Core.Playlists
{
    /// <summary>
    /// Orders one shared category's members for one viewer.
    ///
    /// <para>
    /// Shared rows go to the whole household, and that is deliberate — they were
    /// once opt-in and it collapsed, because a category nobody picked went unbuilt
    /// for everyone. This is the personalization that does not carry that risk:
    /// every viewer keeps every row, and only the order inside their own copy
    /// differs. A row can lean toward one viewer without disappearing from another,
    /// and because Jellyfin playlists are per-user it needs no client support at
    /// all — it shows up in Infuse and every other client, unlike anything routed
    /// through the home screen plugins.
    /// </para>
    ///
    /// <para>
    /// The model's own order is the primary signal and stays that way. It ranked
    /// members by how strongly each belongs to the thread, which is knowledge about
    /// the category that no viewer's watch history replaces. This only nudges,
    /// within a bounded window — enough to bring a viewer's own favourites to the
    /// front of a sixteen-card row, never enough to turn a category into a list of
    /// things they happen to have watched.
    /// </para>
    /// </summary>
    public static class MemberOrdering
    {
        /// <summary>
        /// Places an explicitly favourited item may rise.
        /// </summary>
        /// <remarks>
        /// The strongest signal a viewer gives deliberately, so it earns the largest
        /// nudge — but still a nudge. A favourite sitting 30th in a thread it barely
        /// belongs to should not lead the row.
        /// </remarks>
        private const int FavouriteBonus = 6;

        /// <summary>Places a highly rated item may rise, and a poorly rated one sinks.</summary>
        private const int RatingBonus = 4;

        /// <summary>Places anything the viewer has actually played may rise.</summary>
        /// <remarks>
        /// Small and deliberately sign-neutral about discovery. A category row is not
        /// a recommendation row: "Comfort Rewatch Sitcoms" wants the ones they
        /// rewatch, while "Daylight Folk Horror" might want the ones they have not
        /// seen. No single rule serves both, so watched gets a nudge rather than a
        /// verdict, and the model's ordering keeps deciding.
        /// </remarks>
        private const int WatchedBonus = 2;

        /// <summary>Ratings at or above this count as liked; at or below <see cref="DislikedAtOrBelow"/>, disliked.</summary>
        private const double LikedAtOrAbove = 7;

        private const double DislikedAtOrBelow = 4;

        /// <summary>
        /// Reorders a category's members for one viewer.
        /// </summary>
        /// <remarks>
        /// A stable sort on the adjusted position, so two items the viewer feels
        /// nothing about keep the model's relative order exactly. With no activity at
        /// all — a new account, or the viewer this category means nothing to — the
        /// output is the input, unchanged.
        /// </remarks>
        /// <typeparam name="T">The member type; Core never sees Jellyfin's own.</typeparam>
        /// <param name="members">The category's members, strongest belonging first.</param>
        /// <param name="idOf">Reads a member's item ID.</param>
        /// <param name="activity">That viewer's watch activity, keyed by item ID.</param>
        /// <returns>The members reordered for this viewer.</returns>
        public static IReadOnlyList<T> For<T>(
            IReadOnlyList<T> members,
            Func<T, Guid> idOf,
            IReadOnlyDictionary<Guid, UserActivity>? activity)
        {
            ArgumentNullException.ThrowIfNull(members);
            ArgumentNullException.ThrowIfNull(idOf);

            if (activity is null || activity.Count == 0 || members.Count < 2)
            {
                return members;
            }

            return [.. members
                .Select((member, index) => (Member: member, Score: index - Bonus(activity, idOf(member))))
                .OrderBy(x => x.Score)
                .Select(x => x.Member)];
        }

        /// <summary>
        /// How many places this item rises for this viewer. Negative sinks it.
        /// </summary>
        private static int Bonus(IReadOnlyDictionary<Guid, UserActivity> activity, Guid id)
        {
            if (!activity.TryGetValue(id, out var watched))
            {
                return 0;
            }

            var bonus = 0;

            if (watched.IsFavorite)
            {
                bonus += FavouriteBonus;
            }

            if (watched.UserRating is { } rating)
            {
                if (rating >= LikedAtOrAbove)
                {
                    bonus += RatingBonus;
                }
                else if (rating <= DislikedAtOrBelow)
                {
                    // The one signal that pushes down. A viewer who scored something
                    // 2 out of 10 has said more clearly than any other signal here
                    // that they do not want it at the front of a row.
                    bonus -= RatingBonus;
                }
            }

            if (watched.Played || watched.PlayCount > 0)
            {
                bonus += WatchedBonus;
            }

            return bonus;
        }
    }
}
