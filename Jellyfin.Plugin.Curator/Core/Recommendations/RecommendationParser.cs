using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Curator.Core.Llm;

namespace Jellyfin.Plugin.Curator.Core.Recommendations
{
    /// <summary>
    /// Reads a re-ranked order back, and refuses to lose anything doing it.
    ///
    /// <para>
    /// Enforces the same invariant as every other parser here: the model works in
    /// list-local indexes and never sees a Jellyfin ID, so an index outside the
    /// shortlist is discarded rather than resolved (hard rule 1).
    /// </para>
    ///
    /// <para>
    /// The difference from the other parsers is what happens to a partial answer. A
    /// missing summary is one item without a summary; a missing index here would be
    /// an item silently deleted from somebody's row. So the model's order is treated
    /// as a *preference over* the shortlist rather than a replacement for it —
    /// anything it omits keeps its original relative position at the end. A model
    /// that returns nothing usable therefore costs the row nothing at all.
    /// </para>
    /// </summary>
    public static class RecommendationParser
    {
        /// <summary>
        /// Applies a model's ordering to a shortlist.
        /// </summary>
        /// <typeparam name="T">The item type.</typeparam>
        /// <param name="responseText">The model's text output.</param>
        /// <param name="shortlist">The shortlist the indexes refer to.</param>
        /// <returns>
        /// The reordered shortlist and how much of the answer was unusable. Always
        /// the same items in the same count as <paramref name="shortlist"/>.
        /// </returns>
        /// <exception cref="FormatException">The response has no parseable object of the required shape.</exception>
        public static RecommendationOrderResult<T> Reorder<T>(
            string responseText,
            IReadOnlyList<T> shortlist)
        {
            ArgumentNullException.ThrowIfNull(responseText);
            ArgumentNullException.ThrowIfNull(shortlist);

            var json = JsonResponse.ExtractObject(responseText);

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new FormatException("Model response is not valid JSON.", ex);
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("order", out var order)
                    || order.ValueKind != JsonValueKind.Array)
                {
                    throw new FormatException("Model response lacks a top-level \"order\" array.");
                }

                var ordered = new List<T>(shortlist.Count);
                var taken = new bool[shortlist.Count];
                var discarded = 0;

                foreach (var element in order.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Number
                        || !element.TryGetInt32(out var index)
                        || index < 0
                        || index >= shortlist.Count
                        || taken[index])
                    {
                        // Out of range, repeated, or not a number. A repeat is the
                        // interesting one: honouring it would put one item on the row
                        // twice and push another off the end.
                        discarded++;
                        continue;
                    }

                    taken[index] = true;
                    ordered.Add(shortlist[index]);
                }

                // Whatever the model left out keeps the order it already had. This is
                // what makes a bad answer harmless rather than destructive.
                var missing = 0;
                for (var i = 0; i < shortlist.Count; i++)
                {
                    if (!taken[i])
                    {
                        ordered.Add(shortlist[i]);
                        missing++;
                    }
                }

                return new RecommendationOrderResult<T>(ordered, discarded, missing);
            }
        }
    }

    /// <summary>
    /// The outcome of applying a model's ordering.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="Ordered">The shortlist in the model's order; never shorter than the input.</param>
    /// <param name="DiscardedCount">Indexes dropped for being out of range, repeated, or not a number.</param>
    /// <param name="MissingCount">Items the model never mentioned, appended in their original order.</param>
    public sealed record RecommendationOrderResult<T>(
        IReadOnlyList<T> Ordered,
        int DiscardedCount,
        int MissingCount);
}
