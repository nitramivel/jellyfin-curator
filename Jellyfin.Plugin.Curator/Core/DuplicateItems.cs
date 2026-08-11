using System;
using System.Collections.Generic;
using System.Globalization;
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
    ///
    /// <para>
    /// Title and year alone were not enough, and the reason is that they answer the
    /// wrong question. Two versions of a film are usually the case where the titles
    /// <i>disagree</i> — "Blade Runner" (1982) beside "Blade Runner: The Final Cut"
    /// (2007) is one film under two titles and two years, and the strict key sees two
    /// films. So two exact identities are consulted first, and neither of them is a
    /// similarity test: the alternate-version link the owner created in Jellyfin
    /// itself, and the item's ID at its metadata provider. Title and year remain the
    /// fallback for a row that has neither.
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
        /// <param name="matchOnExternalIds">
        /// Whether two rows sharing a metadata-provider ID count as one title. On by
        /// default, and the setting exists only for a library whose provider IDs are
        /// known to be wrong — a scraper that has stamped two different films with one
        /// TMDb ID would merge them here, and no amount of title strictness downstream
        /// would notice.
        /// </param>
        /// <returns>The collapsed list and the alias map.</returns>
        public static CollapseResult Collapse(
            IReadOnlyList<MediaItemRecord> items,
            bool matchOnExternalIds = true)
        {
            ArgumentNullException.ThrowIfNull(items);

            // An alternate version keys on whatever its PRIMARY keys on, rather than
            // on a key of its own. That is what lets a merged pair join up even when
            // the alternate's title, year and provider ID all disagree — which is the
            // normal state of a file named "…(Director's Cut)" that was never scraped
            // separately.
            var byId = new Dictionary<Guid, MediaItemRecord>(items.Count);
            foreach (var item in items)
            {
                byId.TryAdd(item.Id, item);
            }

            var groups = items
                .Select((item, index) => (Item: item, Index: index))
                .GroupBy(x => KeyOf(VersionRootOf(x.Item, byId), matchOnExternalIds), StringComparer.Ordinal)
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
                    // A row Jellyfin considers an alternate loses to one it does not,
                    // whatever the runtimes say. Every other client renders the
                    // primary and hides the alternate behind a version picker on it,
                    // so keeping the alternate would put a card on the home screen
                    // that appears nowhere else in the server.
                    .OrderBy(x => x.Item.PrimaryVersionId is null ? 0 : 1)
                    .ThenByDescending(x => x.Item.RuntimeMinutes ?? 0)
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
        /// Follows an item's alternate-version links up to the primary row, when that
        /// row is part of this scan.
        /// </summary>
        /// <remarks>
        /// The links come out of a database Curator does not own, so this has to
        /// survive shapes Jellyfin would never write. A row pointing at itself, or a
        /// pair pointing at each other, must neither hang the scan nor — the subtler
        /// failure — resolve <i>asymmetrically</i>: walking a two-item cycle a fixed
        /// number of hops lands each row on the other one, so the pair gets two keys
        /// and stays uncollapsed. A cycle is therefore closed on the lowest ID in it,
        /// which every member of the cycle agrees on.
        /// <para>
        /// An item whose primary was filtered out of the scan — orphaned by a removed
        /// library folder, say — keys on itself, which is the honest answer: the row
        /// it belonged to is not here.
        /// </para>
        /// </remarks>
        private static MediaItemRecord VersionRootOf(
            MediaItemRecord item,
            IReadOnlyDictionary<Guid, MediaItemRecord> byId)
        {
            if (item.PrimaryVersionId is null)
            {
                return item;
            }

            var current = item;
            var walked = new List<MediaItemRecord> { item };

            while (true)
            {
                if (current.PrimaryVersionId is not { } primaryId
                    || !byId.TryGetValue(primaryId, out var primary))
                {
                    return current;
                }

                var revisited = walked.FindIndex(seen => seen.Id == primary.Id);
                if (revisited >= 0)
                {
                    // Closed the loop. The root is the lowest ID in the CYCLE itself,
                    // not in everything walked to reach it — a row that merely leads
                    // into a cycle must not change which row the cycle settles on, or
                    // two members of one cycle answer differently and the group splits.
                    var lowest = walked[revisited];
                    for (var i = revisited + 1; i < walked.Count; i++)
                    {
                        if (walked[i].Id.CompareTo(lowest.Id) < 0)
                        {
                            lowest = walked[i];
                        }
                    }

                    return lowest;
                }

                current = primary;
                walked.Add(primary);
            }
        }

        /// <summary>
        /// The IDs worth drawing out of a list that may already hold two rows for one
        /// title.
        /// </summary>
        /// <remarks>
        /// The collapse above happens before the model sees the library, which fixes
        /// every category built after it and none of the ones already stored. A
        /// category proposed last week holds both IDs, and its playlist was written
        /// from that list, so the row keeps showing two posters until the next full
        /// run replaces the definition — on a weekly schedule, for up to a week.
        /// <para>
        /// So the row applies the same judgement again on the way to the screen. It is
        /// the same pure function over the same keys, which is the point: a backstop
        /// that disagreed with the collapse would be a second opinion about what a
        /// duplicate is, and there is only supposed to be one.
        /// </para>
        /// </remarks>
        /// <param name="items">The rows about to be drawn, in the order they will be drawn.</param>
        /// <param name="matchOnExternalIds">As on <see cref="Collapse"/>.</param>
        /// <returns>The IDs to keep.</returns>
        public static IReadOnlySet<Guid> SurvivingIds(
            IReadOnlyList<MediaItemRecord> items,
            bool matchOnExternalIds = true)
        {
            ArgumentNullException.ThrowIfNull(items);

            return Collapse(items, matchOnExternalIds).Items.Select(i => i.Id).ToHashSet();
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
        /// Two exact identities, then the strict fallback, and every one of them is
        /// an equality test — there is still no fuzzy matching anywhere in here, and
        /// adding some remains the wrong fix.
        /// <list type="number">
        /// <item>The provider ID, when the item has one. Scraped from TMDb, IMDb or
        /// TVDB, so two cuts of one film agree on it and "Freaky Friday" 2003 and 1995
        /// do not.</item>
        /// <item>Otherwise kind, name and year. Name comparison ignores case and
        /// surrounding whitespace only — no stripping of "extended" or "director's
        /// cut" from the title, because a rule loose enough to catch those is loose
        /// enough to merge two films that merely resemble each other.</item>
        /// </list>
        /// The kind is part of every key, so a film and a series never collapse into
        /// one another however they were scraped. Callers pass the item's *version
        /// root*, so an alternate version arrives here as its primary and the third
        /// identity — Jellyfin's own merge — is applied before this is ever reached.
        /// </remarks>
        private static string KeyOf(MediaItemRecord item, bool matchOnExternalIds)
        {
            // A unit separator, which no title or ID can contain, so no two different
            // keys can ever collide by running their parts together.
            const char Separator = '\u001f';

            if (matchOnExternalIds && !string.IsNullOrWhiteSpace(item.ExternalId))
            {
                return string.Concat(
                    "x", Separator.ToString(), item.Kind.ToString(), Separator.ToString(), item.ExternalId);
            }

            return string.Join(
                Separator,
                "t",
                item.Kind.ToString(),
                item.Name.Trim().ToLowerInvariant(),
                item.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        }

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
