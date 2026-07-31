using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Core.Recommendations
{
    /// <summary>
    /// Builds the prompt that asks a model to re-order one viewer's shortlist.
    ///
    /// <para>
    /// The weighted ranker decides <em>which</em> items are in play, and this decides
    /// what order they go in. That division is deliberate: selection is arithmetic
    /// over data already bought — how many of a viewer's threads an item appears in,
    /// how high it sits in each, whether they have watched it — and a model adds
    /// nothing to it. Ordering is the part arithmetic is bad at, because "what should
    /// this person be shown first tonight" is a judgement about a spread of moods
    /// rather than a sum of weights.
    /// </para>
    ///
    /// <para>
    /// Indexes only, never GUIDs — hard rule 1. The shortlist is numbered 0..n-1 and
    /// <see cref="RecommendationParser"/> discards anything outside that range, so the
    /// model cannot promote an item this viewer does not have.
    /// </para>
    /// </summary>
    public static class RecommendationPromptBuilder
    {
        private static readonly JsonWriterOptions WriterOptions = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>The placeholder the list length is spliced into.</summary>
        private const string CountToken = "{COUNT}";

        /// <summary>
        /// Builds the re-ranking system prompt.
        /// </summary>
        /// <param name="count">How many items the shortlist holds.</param>
        /// <returns>The system prompt.</returns>
        public static string BuildSystemPrompt(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            return SystemPromptTemplate.Replace(
                CountToken,
                count.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Builds the user prompt: the shortlist, in the weighted ranker's order.
        /// </summary>
        /// <remarks>
        /// Sent in the ranker's order rather than shuffled. The order already carries
        /// real information — it is what the viewer's own categories say — and the
        /// model is being asked to improve on a starting point, not to sort from
        /// nothing. It is also what makes "leave it alone" a cheap correct answer.
        /// </remarks>
        /// <param name="shortlist">The candidates, in the weighted ranker's order.</param>
        /// <param name="watched">Item IDs the viewer has already played.</param>
        /// <returns>The user prompt.</returns>
        public static string BuildUserPrompt(
            IReadOnlyList<MediaItemRecord> shortlist,
            IReadOnlyCollection<Guid> watched)
        {
            ArgumentNullException.ThrowIfNull(shortlist);
            ArgumentNullException.ThrowIfNull(watched);

            var seen = watched as HashSet<Guid> ?? [.. watched];
            var sb = new StringBuilder(shortlist.Count * 220);
            sb.AppendLine("Shortlist, already in a reasonable order:");

            for (var i = 0; i < shortlist.Count; i++)
            {
                sb.AppendLine(WriteItemLine(i, shortlist[i], seen.Contains(shortlist[i].Id)));
            }

            sb.Append("Return all ")
              .Append(shortlist.Count.ToString(CultureInfo.InvariantCulture))
              .Append(" indexes in your order, as the single JSON object described.");
            return sb.ToString();
        }

        private static string WriteItemLine(int index, MediaItemRecord item, bool watched)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                writer.WriteStartObject();
                writer.WriteNumber("i", index);
                writer.WriteString("title", item.Name);

                if (item.Year is { } year)
                {
                    writer.WriteNumber("year", year);
                }

                if (item.Genres.Count > 0)
                {
                    writer.WriteStartArray("genres");
                    foreach (var genre in item.Genres)
                    {
                        writer.WriteStringValue(genre);
                    }

                    writer.WriteEndArray();
                }

                // Only when true. Most of a shortlist is unwatched, and a false on
                // every line is pure length across a per-viewer prompt.
                if (watched)
                {
                    writer.WriteBoolean("watched", true);
                }

                writer.WriteString("overview", item.Overview ?? string.Empty);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private const string SystemPromptTemplate =
            """
            You are ordering one viewer's recommendation row. You are given a shortlist of {COUNT} items
            drawn from the categories that viewer already has, numbered from 0, already in a reasonable
            order — the items they are most connected to come first.

            Reorder it for what this person should actually be shown first. You are not filtering: every
            index you are given comes back, exactly once, in the order you choose.

            The row is a spotlight that people see a few of at a time, so what matters is the top of it and
            the shape of the whole:

            - Lead with the strongest fit, not the safest one. The first few decide whether the row gets
              looked at at all.
            - Vary the mood as it goes. Six bleak films in a row reads as one recommendation, however well
              each of them fits — spread the registers so consecutive entries feel different.
            - An item already watched belongs lower than a comparable one they have not seen, but a rewatch
              they clearly love beats a weak new suggestion. "watched": true marks those.
            - Do not simply sort by how good things are. A row is a sequence, and a great film in the wrong
              place is a worse recommendation than a good one in the right place.
            - Where you have no real reason to move something, leave it. The order you were given is not
              arbitrary and rearranging for its own sake loses information.

            Reference items ONLY by their integer index. Never invent an index, never repeat one, and never
            drop one — the answer must be a permutation of 0..{COUNT}-1 and nothing else.

            Respond with a single JSON object and nothing else — no prose, no code fences:
            {"order":[0,1,2]}
            """;
    }
}
