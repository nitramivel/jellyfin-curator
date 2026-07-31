using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Core.Summaries
{
    /// <summary>
    /// Builds the distillation prompt: a batch of overviews in, a batch of short
    /// rewrites out.
    ///
    /// <para>
    /// The instruction is deliberately about <em>tone</em> rather than plot. A
    /// summary compressed the obvious way — who does what to whom — produces
    /// exactly the metadata-shaped categories the curation prompt spends its whole
    /// length telling the model to avoid. What has to survive the squeeze is the
    /// thing that lets a film read as "quietly devastating" rather than "Drama".
    /// </para>
    /// </summary>
    public static class SummaryPromptBuilder
    {
        /// <summary>The placeholder the character budget is spliced into.</summary>
        private const string MaxLengthToken = "{MAX_LENGTH}";

        private static readonly JsonSerializerOptions WriterOptions = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// Builds the distillation system prompt.
        /// </summary>
        /// <param name="maxLength">The character budget for one summary.</param>
        /// <returns>The system prompt.</returns>
        public static string BuildSystemPrompt(int maxLength)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 20);

            return SystemPromptTemplate.Replace(
                MaxLengthToken,
                maxLength.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }

        private const string SystemPromptTemplate =
            """
            You compress film and television descriptions for a recommendation system. You are given a
            numbered list of titles, each with the description its metadata provider wrote.

            Rewrite each one in at most {MAX_LENGTH} characters.

            What matters is that the rewrite still carries the FEEL of the thing — its tone, its mood, its
            texture, the kind of experience watching it is. A later step reads only your version and has to
            be able to tell that one film is bleak and funny while another is warm and slight. Plot
            mechanics are the first thing to cut: who the characters are, what they are called, where they
            go, and how it ends almost never carry that feeling, and they are what a plain summary keeps.

            Rules:
            - Reference items ONLY by their integer index from the input. Return one entry per input item.
            - At most {MAX_LENGTH} characters each. Shorter is fine when the title is slight.
            - Write a description, not a review: no verdict on quality, no star ratings, no "a must-see".
            - Do not begin with the title, and do not begin every entry the same way.
            - Keep concrete texture over abstraction — "sun-bleached and cruel" beats "atmospheric".
            - No spoilers for endings, and no character names unless the name IS the point.
            - Plain prose. No markdown, no quotes around the summary, no trailing full stop needed.

            Respond with a single JSON object and nothing else — no prose, no code fences:
            {"summaries":[{"i":0,"s":"..."},{"i":1,"s":"..."}]}
            """;

        /// <summary>
        /// Builds the user prompt: one compact JSON object per line, indexed
        /// 0..n-1, carrying the untruncated overview.
        /// </summary>
        /// <remarks>
        /// The full overview goes out, not the reducer's 300-character cut. This is
        /// the one call that should see the whole thing — distilling a truncation
        /// would bake the truncation permanently into the cache.
        /// </remarks>
        /// <param name="batch">The items to distill.</param>
        /// <returns>The user prompt.</returns>
        public static string BuildUserPrompt(IReadOnlyList<MediaItemRecord> batch)
        {
            ArgumentNullException.ThrowIfNull(batch);

            var sb = new StringBuilder(batch.Count * 320);
            sb.AppendLine("Descriptions to compress:");
            for (var i = 0; i < batch.Count; i++)
            {
                sb.AppendLine(WriteItemLine(i, batch[i]));
            }

            sb.Append("Return the compressed version of all ")
              .Append(batch.Count.ToString(CultureInfo.InvariantCulture))
              .Append(" items now, as the single JSON object described.");
            return sb.ToString();
        }

        private static string WriteItemLine(int index, MediaItemRecord item)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Encoder = WriterOptions.Encoder,
            }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("i", index);
                writer.WriteString("title", item.Name);

                if (item.Year is { } year)
                {
                    writer.WriteNumber("year", year);
                }

                // Genre is cheap and stops the model inventing a tone that the
                // overview alone leaves ambiguous — a one-line synopsis of a horror
                // comedy reads very differently once you know which it is.
                if (item.Genres.Count > 0)
                {
                    writer.WriteStartArray("genres");
                    foreach (var genre in item.Genres)
                    {
                        writer.WriteStringValue(genre);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteString("overview", item.Overview ?? string.Empty);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }
}
