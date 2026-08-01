using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Core
{
    /// <summary>
    /// Collapses two library rows for the same title into one before the model
    /// sees them.
    ///
    /// <para>
    /// A director's cut and a theatrical cut are two items in Jellyfin and one film
    /// to a viewer. Sent as two, they reach the model with identical titles, years,
    /// genres and overviews, and it does the only sensible thing with them: puts
    /// both in the same category. The row then shows the same poster twice, which
    /// is what prompted this.
    /// </para>
    ///
    /// <para>
    /// The matching has to be conservative, because the failure mode is worse than
    /// the problem. On the library this was built against, "Freaky Friday" appears
    /// as 2003 and 1995 — genuinely different films that a title-only rule would
    /// merge into one, silently removing a film from the library as far as Curator
    /// is concerned. So the year has to agree as well, and a missing year never
    /// matches a present one.
    /// </para>
    /// </summary>
    public static class DuplicateItems
    {
        /// <summary>
        /// The result of collapsing: what to send, and what the dropped rows map to.
        /// </summary>
        /// <param name="Items">The items to send, in their original order.</param>
        /// <param name="Aliases">
        /// Dropped item ID to the ID kept in its place. Watch activity is recorded
        /// against whichever row was actually played, so folding it through this is
        /// what stops collapsing a duplicate from also discarding the viewing history
        /// attached to it.
        /// </param>
        public sealed record CollapseResult(
            IReadOnlyList<MediaItemRecord> Items,
            IReadOnlyDictionary<Guid, Guid> Aliases);

        /// <summary>
        /// Keeps one row per distinct title.
        /// </summary>
        /// <remarks>
        /// The longest runtime wins, which picks the fuller cut — a director's cut
        /// over a theatrical one — and falls back to library order when the runtimes
        /// are equal or unknown, so the choice is stable from run to run rather than
        /// alternating and churning the row.
        /// </remarks>
        /// <param name="items">The scanned library.</param>
        /// <returns>The collapsed list and the alias map.</returns>
        public static CollapseResult Collapse(IReadOnlyList<MediaItemRecord> items)
        {
            ArgumentNullException.ThrowIfNull(items);

            var groups = items
                .Select((item, index) => (Item: item, Index: index))
                .GroupBy(x => KeyOf(x.Item))
                .ToList();

            if (groups.Count == items.Count)
            {
                return new CollapseResult(items, new Dictionary<Guid, Guid>());
            }

            var aliases = new Dictionary<Guid, Guid>();
            var kept = new List<(MediaItemRecord Item, int Index)>(groups.Count);

            foreach (var group in groups)
            {
                var winner = group
                    .OrderByDescending(x => x.Item.RuntimeMinutes ?? 0)
                    .ThenBy(x => x.Index)
                    .First();

                kept.Add(winner);

                foreach (var loser in group.Where(x => x.Item.Id != winner.Item.Id))
                {
                    aliases[loser.Item.Id] = winner.Item.Id;
                }
            }

            return new CollapseResult(
                [.. kept.OrderBy(x => x.Index).Select(x => x.Item)],
                aliases);
        }

        /// <summary>
        /// Moves activity recorded against a dropped row onto the row kept for it.
        /// </summary>
        /// <remarks>
        /// Without this, collapsing would quietly cost a viewer their history: they
        /// watched the theatrical cut, the director's cut is what gets sent, and the
        /// film reads as never seen. Where both rows carry activity the stronger
        /// wins — a viewer who played one and merely opened the other has watched the
        /// film once, not been ambivalent about it.
        /// </remarks>
        /// <param name="activity">Activity as read from Jellyfin, keyed by item ID.</param>
        /// <param name="aliases">The alias map from <see cref="Collapse"/>.</param>
        /// <returns>Activity keyed by the IDs actually being sent.</returns>
        public static IReadOnlyDictionary<Guid, UserActivity> FoldActivity(
            IReadOnlyDictionary<Guid, UserActivity> activity,
            IReadOnlyDictionary<Guid, Guid> aliases)
        {
            ArgumentNullException.ThrowIfNull(activity);
            ArgumentNullException.ThrowIfNull(aliases);

            if (aliases.Count == 0)
            {
                return activity;
            }

            var folded = new Dictionary<Guid, UserActivity>(activity.Count);
            foreach (var (id, value) in activity)
            {
                var target = aliases.TryGetValue(id, out var kept) ? kept : id;
                folded[target] = folded.TryGetValue(target, out var existing)
                    ? Stronger(existing, value)
                    : value;
            }

            return folded;
        }

        /// <summary>
        /// The key two rows must share to count as the same title.
        /// </summary>
        /// <remarks>
        /// Kind, name and year, and nothing softer. Name comparison ignores case and
        /// surrounding whitespace only — no fuzzy matching, no stripping of "extended"
        /// or "director's cut" from the title, because a rule loose enough to catch
        /// those is loose enough to merge two films that merely resemble each other.
        /// Two rows a viewer would call the same film almost always carry the same
        /// title already; that is why they look duplicated in the first place.
        /// </remarks>
        private static (MediaKind Kind, string Name, int? Year) KeyOf(MediaItemRecord item)
            => (item.Kind, item.Name.Trim().ToLowerInvariant(), item.Year);

        private static UserActivity Stronger(UserActivity a, UserActivity b) => new()
        {
            Played = a.Played || b.Played,
            PlayCount = Math.Max(a.PlayCount, b.PlayCount),
            IsFavorite = a.IsFavorite || b.IsFavorite,
            UserRating = a.UserRating ?? b.UserRating,
            DaysSinceLastPlayed = Min(a.DaysSinceLastPlayed, b.DaysSinceLastPlayed),
            EpisodeCount = Math.Max(a.EpisodeCount ?? 0, b.EpisodeCount ?? 0) is var e && e > 0 ? e : null,
            EpisodesPlayed = Math.Max(a.EpisodesPlayed ?? 0, b.EpisodesPlayed ?? 0) is var p && p > 0 ? p : null,
        };

        private static int? Min(int? a, int? b)
            => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
    }
}
