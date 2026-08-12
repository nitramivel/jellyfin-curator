using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Curator.Core.Context
{
    /// <summary>
    /// Which of the two context rows is being drawn.
    /// </summary>
    public enum ContextRowKind
    {
        /// <summary>Matches items against what the weather is doing outside.</summary>
        Weather = 0,

        /// <summary>Matches items against what part of the day it is.</summary>
        Daypart = 1,
    }

    /// <summary>
    /// Picks and orders the items for one context row.
    ///
    /// <para>
    /// This spends no model call, and that is the design rather than an optimisation.
    /// The judgement — does this film suit a rainy evening — was bought once during
    /// the condensing pass and cached against the item; what is left is matching two
    /// small sets of words and breaking ties. Hard rule 15 says the same thing about
    /// recommendations, and it applies with more force here: this runs inside the
    /// request that draws the home screen, so anything slow or billable on this path
    /// is paid for on every page load by every viewer.
    /// </para>
    ///
    /// <para>
    /// The viewer's own ordering arrives already computed, as the ranked list
    /// <c>RecommendationRanker</c> produced. This never reorders on taste, only on
    /// fit, and uses that rank to break ties — so a row for a rainy night is the
    /// viewer's own recommendations filtered to what suits a rainy night, rather
    /// than a second opinion about what they like.
    /// </para>
    /// </summary>
    public static class ContextRanker
    {
        /// <summary>
        /// How few matching items make a row not worth drawing.
        /// </summary>
        /// <remarks>
        /// A home screen row holding one card does not read as a thin row, it reads
        /// as a broken one — and a context row is a claim ("this suits the weather")
        /// that a single card cannot support. Below this the row returns nothing and
        /// is simply not drawn, which is the honest outcome for a library whose
        /// classified items happen to have nothing for today.
        /// </remarks>
        public const int MinimumRowLength = 3;

        /// <summary>
        /// Builds one context row.
        /// </summary>
        /// <param name="rankedIds">
        /// The viewer's items, best first, as <c>RecommendationRanker</c> ordered
        /// them. Position in this list is the tie-break, so it must be the viewer's
        /// full ordering rather than a shortlist — an item that suits tonight exactly
        /// should not be missing because it sat 90th on a list cut at 75.
        /// </param>
        /// <param name="affinities">Context affinities by item ID; a missing item has none.</param>
        /// <param name="context">The conditions right now.</param>
        /// <param name="kind">Which row is being drawn.</param>
        /// <param name="maxItems">How many items the row may hold. 0 means no cap.</param>
        /// <returns>
        /// The row's items, best fit first, or empty when too few items match to make
        /// a row worth drawing.
        /// </returns>
        public static IReadOnlyList<Guid> Rank(
            IReadOnlyList<Guid> rankedIds,
            IReadOnlyDictionary<Guid, ItemContextAffinity> affinities,
            ViewingContext context,
            ContextRowKind kind,
            int maxItems)
        {
            ArgumentNullException.ThrowIfNull(rankedIds);
            ArgumentNullException.ThrowIfNull(affinities);
            ArgumentNullException.ThrowIfNull(context);

            // A weather row with nothing to match against is not a weather row. It
            // returns empty rather than falling back to the clock: the viewer would
            // be shown a row claiming to answer the weather while answering something
            // else, and a row that is quietly not what it says is worse than absent.
            if (kind == ContextRowKind.Weather && !context.HasWeather)
            {
                return [];
            }

            IReadOnlyList<string> wanted = kind == ContextRowKind.Weather
                ? context.Weather
                : [ContextVocabulary.WordFor(context.Daypart)];

            var matched = new List<(Guid Id, int Strength, int Rank)>();
            var standIns = new List<(Guid Id, int Strength, int Rank)>();

            // Words that may stand in when the exact condition is too thin to fill a
            // row. Never mixed with the exact matches — collected separately so they
            // can be appended below them, and only if they are needed at all.
            var related = kind == ContextRowKind.Weather ? RelatedWords(wanted) : [];

            for (var rank = 0; rank < rankedIds.Count; rank++)
            {
                var id = rankedIds[rank];
                if (!affinities.TryGetValue(id, out var affinity))
                {
                    continue;
                }

                var claimed = kind == ContextRowKind.Weather ? affinity.Weather : affinity.Dayparts;

                var strength = Overlap(claimed, wanted);
                if (strength > 0)
                {
                    matched.Add((id, strength, rank));
                    continue;
                }

                var stand = Overlap(claimed, related);
                if (stand > 0)
                {
                    standIns.Add((id, stand, rank));
                }
            }

            // Strength first, so a film the model called both cold and snowy leads a
            // snowy cold evening over one that is merely snowy. Then the viewer's own
            // rank, which already carries unwatched-first and everything else the
            // recommendation pass decided.
            matched.Sort(CompareMatches);

            // The rescue, and the reason it is conditional. The rarer a condition is,
            // the fewer items suit it — and the rarest conditions are the ones a
            // viewer most wants a row for. A thunderstorm would otherwise draw nothing
            // at all on the one evening the feature should shine. When the exact
            // matches can fill a row they are used alone, so a well-stocked condition
            // is never diluted; and stand-ins are APPENDED to the sorted exact list
            // rather than sorted in with it, so rain may stand in for thunder without
            // ever outranking it.
            if (matched.Count < MinimumRowLength && standIns.Count > 0)
            {
                standIns.Sort(CompareMatches);
                matched.AddRange(standIns);
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
        private static int CompareMatches(
            (Guid Id, int Strength, int Rank) a,
            (Guid Id, int Strength, int Rank) b)
        {
            var byStrength = b.Strength.CompareTo(a.Strength);
            return byStrength != 0 ? byStrength : a.Rank.CompareTo(b.Rank);
        }

        /// <summary>
        /// The stand-in words for a set of conditions, excluding the conditions
        /// themselves.
        /// </summary>
        /// <remarks>
        /// The exclusion matters on a multi-word reading: a cold snowy evening wants
        /// <c>cold</c> as an exact match, not as snow's stand-in, or an item claiming
        /// only cold would be filed below one claiming nothing relevant at all.
        /// </remarks>
        private static IReadOnlyList<string> RelatedWords(IReadOnlyList<string> wanted)
        {
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
                foreach (var value in claimed)
                {
                    if (string.Equals(value, word, StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                        break;
                    }
                }
            }

            return count;
        }
    }
}
