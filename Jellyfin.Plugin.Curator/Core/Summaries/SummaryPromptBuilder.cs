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
        /// <param name="tagCeiling">
        /// The most consolidated tags an item may keep, or 0 to leave tags out of
        /// the pass entirely. This is a ceiling and never a target — the whole point
        /// is that the model keeps however many genuinely apply.
        /// </param>
        /// <param name="allowInventedTags">
        /// Whether the model may coin a tag the scraped list does not contain, as a
        /// last resort when nothing in it names what the rewrite just said.
        /// </param>
        /// <returns>The system prompt.</returns>
        public static string BuildSystemPrompt(int maxLength, int tagCeiling = 0, bool allowInventedTags = false)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 20);

            var body = tagCeiling > 0
                ? SystemPromptTemplate.Replace(TagSectionToken, TagSection, StringComparison.Ordinal)
                    .Replace(OutputToken, TagOutput, StringComparison.Ordinal)
                    .Replace(
                        VocabularyToken,
                        allowInventedTags ? InventedVocabulary : FixedVocabulary,
                        StringComparison.Ordinal)
                : SystemPromptTemplate.Replace(TagSectionToken, string.Empty, StringComparison.Ordinal)
                    .Replace(OutputToken, PlainOutput, StringComparison.Ordinal)
                    .Replace(VocabularyToken, string.Empty, StringComparison.Ordinal);

            return body
                .Replace(MaxLengthToken, maxLength.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace(TagCeilingToken, tagCeiling.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        /// <summary>The placeholder the tag instructions are spliced into.</summary>
        private const string TagSectionToken = "{TAG_SECTION}";

        /// <summary>The placeholder the output contract is spliced into.</summary>
        private const string OutputToken = "{OUTPUT}";

        /// <summary>The placeholder the tag ceiling is spliced into.</summary>
        private const string TagCeilingToken = "{TAG_CEILING}";

        /// <summary>The placeholder the tag vocabulary rule is spliced into.</summary>
        private const string VocabularyToken = "{VOCABULARY}";

        /// <summary>
        /// The default rule: the scraped list is the whole vocabulary.
        /// </summary>
        private const string FixedVocabulary =
            "Lower case, and keep each tag's own wording rather than inventing new vocabulary — the same tag\n"
            + "            has to mean the same thing across every item for these to be worth anything.";

        /// <summary>
        /// The rule when coinage is allowed. Written to make it a last resort rather
        /// than an invitation: the failure mode is not a bad word, it is four words
        /// for one texture across four items, which is worse than the scraped list.
        /// </summary>
        private const string InventedVocabulary =
            "Lower case. Prefer the scraped list's own wording — the same tag has to mean the same thing across\n"
            + "            every item for these to be worth anything, and a tag nothing else shares describes nothing.\n"
            + "            Where nothing in the list names what your rewrite just said, you MAY coin one word or a\n"
            + "            short plain phrase for it. Treat that as a last resort, not an opportunity: coin the most\n"
            + "            ordinary wording that fits rather than a vivid one, so that another item with the same\n"
            + "            texture would land on the same word. Never coin a second word for something the list, or a\n"
            + "            word you have already coined, already covers.";

        /// <summary>
        /// The tag half of the task, added only when tags are being consolidated.
        /// </summary>
        /// <remarks>
        /// Written to resist the obvious failure, which is the model returning
        /// exactly the ceiling every time. A fixed count is what the old
        /// take-the-first-N setting already did badly; the value here is that a
        /// film with one clear texture keeps one word and a dense one keeps six.
        /// </remarks>
        private const string TagSection =
            """

            Each item also carries a "tags" list scraped from a metadata provider — the whole list, unfiltered.
            Consolidate it.

            Do this SECOND, after you have written that item's rewrite, and let the rewrite decide. You have
            just committed to a reading of what watching the thing is like; the tags you keep are the same
            judgement in single words, so they must agree with it. If a tag pulls against the reading you
            just wrote, drop it — a scraped list is guesswork by a metadata provider that never watched
            anything, and you have. Where the list offers several words for the thing your rewrite is
            actually about, keep the one closest to it.

            Keep only tags that describe what watching the thing is LIKE — its mood, texture, or the kind of
            story it tells. Throw away production trivia (aftercreditsstinger, based on novel or book, sequel,
            remake, 3d animation), release facts, cast and crew facts, franchise names, place names, decades,
            and award labels. Where several tags say the same thing, keep the one that says it best rather
            than all of them.

            Keep HOWEVER MANY genuinely apply, up to {TAG_CEILING}. This is a ceiling, not a target, and
            returning {TAG_CEILING} for everything is the mistake to avoid: a title with one clear texture
            should come back with one tag, a dense one with several. An item whose tags are all trivia should
            come back with an empty list — that is a correct answer, not a failure.

            {VOCABULARY}
            """;

        private const string PlainOutput =
            """
            {"summaries":[{"i":0,"s":"..."},{"i":1,"s":"..."}]}
            """;

        private const string TagOutput =
            """
            {"summaries":[{"i":0,"s":"...","t":["...","..."]},{"i":1,"s":"...","t":[]}]}
            """;

        private const string SystemPromptTemplate =
            """
            You compress film and television descriptions for a recommendation system. You are given a
            numbered list of titles, each with the description its metadata provider wrote.

            Rewrite each one as a COMPLETE phrase of at most {MAX_LENGTH} characters.

            Complete is the part people get wrong. Do not write until you reach the limit and stop — compose
            something that already fits and finishes its thought. A rewrite that ends "...racing to a" or
            "...a secret that" is worse than one half the length, because the reader is judging tone and a
            sentence cut mid-clause reads as vaguer rather than shorter. If a thought will not fit, pick a
            smaller thought. Shorter and whole always beats longer and cut.

            What matters is that the rewrite still carries the FEEL of the thing — its tone, its mood, its
            texture, the kind of experience watching it is. A later step reads only your version and has to
            be able to tell that one film is bleak and funny while another is warm and slight. Plot
            mechanics are the first thing to cut: who the characters are, what they are called, where they
            go, and how it ends almost never carry that feeling, and they are what a plain summary keeps.

            Rules:
            - Reference items ONLY by their integer index from the input. Return one entry per input item.
            - At most {MAX_LENGTH} characters each, and never ending on a word like "a", "the", "to", "that"
              or "with" — that is the sign you ran out of room instead of choosing what to say.
            - Write a description, not a review: no verdict on quality, no star ratings, no "a must-see".
            - Do not begin with the title, and do not begin every entry the same way.
            - Keep concrete texture over abstraction — "sun-bleached and cruel" beats "atmospheric".
            - No spoilers for endings, and no character names unless the name IS the point.
            - Plain prose. No markdown, no quotes around the summary, no trailing full stop needed.
            {TAG_SECTION}
            Respond with a single JSON object and nothing else — no prose, no code fences:
            {OUTPUT}
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
        /// <param name="includeTags">Whether each item's scraped tag list is sent for consolidation.</param>
        /// <returns>The user prompt.</returns>
        public static string BuildUserPrompt(IReadOnlyList<MediaItemRecord> batch, bool includeTags = false)
        {
            ArgumentNullException.ThrowIfNull(batch);

            var sb = new StringBuilder(batch.Count * 320);
            sb.AppendLine("Descriptions to compress:");
            for (var i = 0; i < batch.Count; i++)
            {
                sb.AppendLine(WriteItemLine(i, batch[i], includeTags));
            }

            sb.Append("Return the compressed version of all ")
              .Append(batch.Count.ToString(CultureInfo.InvariantCulture))
              .Append(" items now, as the single JSON object described.");
            return sb.ToString();
        }

        private static string WriteItemLine(int index, MediaItemRecord item, bool includeTags)
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

                // The whole scraped list goes out, unfiltered and uncapped: deciding
                // which of these earn their place is exactly the judgement being
                // asked for, and pre-trimming would make it on the model's behalf.
                if (includeTags && item.Tags.Count > 0)
                {
                    writer.WriteStartArray("tags");
                    foreach (var tag in item.Tags)
                    {
                        writer.WriteStringValue(tag);
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
