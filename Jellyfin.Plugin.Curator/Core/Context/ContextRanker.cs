using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Curator.Core.Context
{
    /// <summary>
    /// Picks and orders the items for the context row.
    ///
    /// <para>
    /// One row, answering both halves of the moment at once: the weather outside and
    /// the hour of the day. It was two rows, and the reason it is not is arithmetic
    /// rather than taste — measured on a 202-item library, "cloudy and morning"
    /// described exactly <b>one</b> film, and rain-and-morning and storm-and-morning
    /// described none. A row demanding both would therefore be absent every morning,
    /// which is when somebody most plausibly opens Jellyfin looking for something to
    /// put on.
    /// </para>
    ///
    /// <para>
    /// So the match is <b>graded, not strict</b>. An item suiting both leads; one
    /// suiting the weather comes next, then one suiting the hour, then one suiting
    /// weather near enough to stand in. That keeps the row honest — what leads it
    /// really does fit the moment — while making it drawable on a bright Tuesday
    /// morning as well as a wet Friday night.
    /// </para>
    ///
    /// <para>
    /// This spends no model call, and that is the design rather than an optimisation.
    /// The judgement was bought once during the condensing pass and cached against
    /// the item; what is left is matching two small sets of words. Hard rule 15 says
    /// the same about recommendations and it applies with more force here, because
    /// this runs inside the request that draws the home screen.
    /// </para>
    /// </summary>
    public static class ContextRanker
    {
        /// <summary>
        /// How few matching items make a row not worth drawing.
        /// </summary>
        /// <remarks>
        /// A home screen row holding one card does not read as a thin row, it reads
        /// as a broken one — and a context row is a claim ("this suits right now")
        /// that a single card cannot support.
        /// </remarks>
        public const int MinimumRowLength = 3;

        /// <summary>What one item scores for each weather word it genuinely claims.</summary>
        /// <remarks>
        /// Above <see cref="DaypartWeight"/> because weather is the more selective
        /// signal. There are four dayparts and the busiest holds a third of a
        /// library, while a weather word is worth something closer to a real filter —
        /// so an item that suits the sky says more about the moment than one that
        /// merely suits the evening.
        /// </remarks>
        public const int WeatherWeight = 3;

        /// <summary>What it scores for suiting the current part of the day.</summary>
        public const int DaypartWeight = 2;

        /// <summary>What a near-enough weather word scores when the exact one is absent.</summary>
        /// <remarks>
        /// Deliberately below both: a stand-in is what keeps a thunderstorm row from
        /// being empty, not a claim that the item suits thunder.
        /// </remarks>
        public const int StandInWeight = 1;

        /// <summary>
        /// The length below which a row is topped up with near-misses.
        /// </summary>
        /// <remarks>
        /// <see cref="MinimumRowLength"/> is the point below which a row is not worth
        /// drawing; this is the point below which it is worth drawing but looks thin.
        /// A home screen shows roughly this many cards before scrolling, so a row of
        /// four beside rows of twenty reads as a mistake even when every one of the
        /// four is a good answer.
        /// <para>
        /// Only ever used to <em>add</em>. A row that already has this many is left
        /// strictly alone, so a well-stocked evening is never diluted to make a
        /// starved morning work — which is the whole reason this is a threshold
        /// rather than another weight.
        /// </para>
        /// </remarks>
        public const int ComfortableRowLength = 12;

        /// <summary>
        /// Builds the context row.
        /// </summary>
        /// <param name="rankedIds">
        /// The viewer's items, best first, as <c>RecommendationRanker</c> ordered
        /// them. Position is the tie-break, so it must be the viewer's full ordering
        /// rather than a shortlist — an item that suits tonight exactly should not be
        /// missing because it sat 90th on a list cut at 75.
        /// </param>
        /// <param name="affinities">Context affinities by item ID; a missing item has none.</param>
        /// <param name="context">The conditions right now.</param>
        /// <param name="maxItems">How many items the row may hold. 0 means no cap.</param>
        /// <returns>
        /// The row's items, best fit first, or empty when too few match to make a row
        /// worth drawing.
        /// </returns>
        public static IReadOnlyList<Guid> Rank(
            IReadOnlyList<Guid> rankedIds,
            IReadOnlyDictionary<Guid, ItemContextAffinity> affinities,
            ViewingContext context,
            int maxItems)
        {
            ArgumentNullException.ThrowIfNull(rankedIds);
            ArgumentNullException.ThrowIfNull(affinities);
            ArgumentNullException.ThrowIfNull(context);

            var daypart = ContextVocabulary.WordFor(context.Daypart);
            var related = RelatedWords(context.Weather);
            var adjacent = ContextVocabulary.AdjacentTo(context.Daypart);

            var matched = new List<(Guid Id, int Score, int Rank)>();
            var nearMisses = new List<(Guid Id, int Score, int Rank)>();

            for (var rank = 0; rank < rankedIds.Count; rank++)
            {
                var id = rankedIds[rank];
                if (!affinities.TryGetValue(id, out var affinity))
                {
                    continue;
                }

                var score = 0;

                // Exact weather first; only if none of it lands does a near-enough
                // word count, so an item never scores for both at once.
                var exact = Overlap(affinity.Weather, context.Weather);
                score += exact > 0
                    ? exact * WeatherWeight
                    : Overlap(affinity.Weather, related) * StandInWeight;

                if (Contains(affinity.Dayparts, daypart))
                {
                    score += DaypartWeight;
                }

                if (score > 0)
                {
                    matched.Add((id, score, rank));
                    continue;
                }

                // Held back rather than scored. An item suiting the hour either side
                // of this one is a near miss, and near misses are only worth showing
                // when there is nothing better — see ComfortableRowLength.
                if (Overlap(affinity.Dayparts, adjacent) > 0)
                {
                    nearMisses.Add((id, 0, rank));
                }
            }

            matched.Sort(CompareMatches);

            // Topping up a thin row, and only a thin row. Some libraries have almost
            // nothing for a given hour — measured on a real one, six items in all
            // suited a morning — so a strict row there is three good answers beside
            // other rows of twenty, which reads as broken rather than selective.
            // Appended after sorting, so every genuine match still leads.
            if (matched.Count < ComfortableRowLength && nearMisses.Count > 0)
            {
                nearMisses.Sort(CompareMatches);
                matched.AddRange(nearMisses.Take(ComfortableRowLength - matched.Count));
            }

            if (matched.Count < MinimumRowLength)
            {
                return [];
            }

            IEnumerable<Guid> ordered = matched.Select(m => m.Id);
            if (maxItems > 0)
            {
                ordered = ordered.Take(maxItems);
            }

            return [.. ordered];
        }

        /// <summary>
        /// Orders by fit, then by the viewer's own ranking.
        /// </summary>
        /// <remarks>
        /// Score first, so an item suiting both the sky and the hour leads one
        /// suiting half of it. Then the viewer's own rank, which already carries
        /// unwatched-first and everything the recommendation pass decided.
        /// </remarks>
        private static int CompareMatches(
            (Guid Id, int Score, int Rank) a,
            (Guid Id, int Score, int Rank) b)
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.Rank.CompareTo(b.Rank);
        }

        /// <summary>
        /// The stand-in words for a reading, excluding the reading's own words.
        /// </summary>
        /// <remarks>
        /// The exclusion matters on a multi-word reading: a cold snowy evening wants
        /// <c>cold</c> as an exact match, not as snow's stand-in, or an item claiming
        /// only cold would score less than it should.
        /// </remarks>
        private static IReadOnlyList<string> RelatedWords(IReadOnlyList<string> wanted)
        {
            if (wanted.Count == 0)
            {
                return [];
            }

            var exact = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
            var related = new List<string>();

            foreach (var word in wanted)
            {
                foreach (var candidate in ContextVocabulary.RelatedTo(word))
                {
                    if (!exact.Contains(candidate) && !related.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        related.Add(candidate);
                    }
                }
            }

            return related;
        }

        private static bool Contains(IReadOnlyList<string> claimed, string word)
        {
            foreach (var value in claimed)
            {
                if (string.Equals(value, word, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// How many of the wanted words an item claims.
        /// </summary>
        private static int Overlap(IReadOnlyList<string> claimed, IReadOnlyList<string> wanted)
        {
            if (claimed.Count == 0 || wanted.Count == 0)
            {
                return 0;
            }

            var count = 0;
            foreach (var word in wanted)
            {
                if (Contains(claimed, word))
                {
                    count++;
                }
            }

            return count;
        }
    }
}
