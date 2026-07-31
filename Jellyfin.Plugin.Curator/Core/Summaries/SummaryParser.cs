using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Curator.Core.Llm;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Core.Summaries
{
    /// <summary>
    /// One accepted summary, already mapped back to its item.
    /// </summary>
    /// <param name="Item">The item the summary describes.</param>
    /// <param name="Text">The condensed text, trimmed to the configured budget.</param>
    /// <param name="Tags">
    /// Consolidated tags, however many the model judged applied. Empty both when
    /// tags were not requested and when the model correctly found none worth
    /// keeping — the caller distinguishes those by whether it asked.
    /// </param>
    public sealed record ParsedSummary(
        MediaItemRecord Item,
        string Text,
        IReadOnlyList<string> Tags);

    /// <summary>
    /// The outcome of parsing one distillation response.
    /// </summary>
    /// <param name="Summaries">Accepted summaries.</param>
    /// <param name="DiscardedCount">Entries dropped for a bad index, a duplicate, or empty text.</param>
    /// <param name="TrimmedCount">Summaries the model returned over budget, which were cut.</param>
    /// <param name="MissingCount">Items in the batch the model returned nothing for.</param>
    public sealed record SummaryParseResult(
        IReadOnlyList<ParsedSummary> Summaries,
        int DiscardedCount,
        int TrimmedCount,
        int MissingCount);

    /// <summary>
    /// Strict parser for distillation responses.
    ///
    /// <para>
    /// Enforces the same invariant as <see cref="ProposalParser"/>: the model works
    /// in batch-local integer indexes and never sees a Jellyfin ID, so an index
    /// outside the batch is discarded rather than resolved. A model cannot attach a
    /// summary to an item that was not in front of it.
    /// </para>
    /// </summary>
    public static class SummaryParser
    {
        /// <summary>
        /// Parses a distillation response against the batch that produced it.
        /// </summary>
        /// <param name="responseText">The model's text output.</param>
        /// <param name="batch">The batch the response describes; indexes map into it.</param>
        /// <param name="maxLength">The character budget; longer summaries are cut at a word boundary.</param>
        /// <param name="tagCeiling">
        /// The most consolidated tags to keep per item; 0 ignores tags entirely.
        /// A ceiling only — the model chooses how many below it to return, and
        /// clipping here is a guard against a runaway answer, not a target.
        /// </param>
        /// <returns>Accepted summaries and discard counts.</returns>
        /// <exception cref="FormatException">The response has no parseable object of the required shape.</exception>
        public static SummaryParseResult Parse(
            string responseText,
            IReadOnlyList<MediaItemRecord> batch,
            int maxLength,
            int tagCeiling = 0)
        {
            ArgumentNullException.ThrowIfNull(responseText);
            ArgumentNullException.ThrowIfNull(batch);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 20);

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
                    || !document.RootElement.TryGetProperty("summaries", out var summaries)
                    || summaries.ValueKind != JsonValueKind.Array)
                {
                    throw new FormatException("Model response lacks a top-level \"summaries\" array.");
                }

                var accepted = new List<ParsedSummary>();
                var seen = new HashSet<int>();
                var discarded = 0;
                var trimmed = 0;

                foreach (var entry in summaries.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object
                        || !entry.TryGetProperty("i", out var indexElement)
                        || indexElement.ValueKind != JsonValueKind.Number
                        || !indexElement.TryGetInt32(out var index))
                    {
                        discarded++;
                        continue;
                    }

                    // The invariant: outside the batch is not a summary we can place.
                    if (index < 0 || index >= batch.Count || !seen.Add(index))
                    {
                        discarded++;
                        continue;
                    }

                    var text = entry.TryGetProperty("s", out var textElement)
                        && textElement.ValueKind == JsonValueKind.String
                            ? textElement.GetString()?.Trim()
                            : null;

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        discarded++;
                        continue;
                    }

                    text = Clean(text);
                    if (text.Length > maxLength)
                    {
                        text = TrimToBudget(text, maxLength);
                        trimmed++;
                    }

                    accepted.Add(new ParsedSummary(batch[index], text, ReadTags(entry, tagCeiling)));
                }

                return new SummaryParseResult(accepted, discarded, trimmed, batch.Count - accepted.Count);
            }
        }

        /// <summary>
        /// Reads the consolidated tag list off one entry.
        /// </summary>
        /// <remarks>
        /// Deliberately tolerant of the model returning nothing: an item whose
        /// scraped tags are all production trivia genuinely has no tags worth
        /// keeping, and the prompt says so. An empty list here is a real answer,
        /// which is why nothing tops it up to a minimum.
        /// </remarks>
        private static IReadOnlyList<string> ReadTags(JsonElement entry, int ceiling)
        {
            if (ceiling <= 0
                || !entry.TryGetProperty("t", out var tags)
                || tags.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var kept = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var tag in tags.EnumerateArray())
            {
                if (kept.Count >= ceiling)
                {
                    break;
                }

                if (tag.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = tag.GetString()?.Trim().Trim('"', '\'').ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
                {
                    kept.Add(value);
                }
            }

            return kept;
        }

        /// <summary>
        /// Strips the wrappers models add despite being told not to.
        /// </summary>
        /// <remarks>
        /// Cheap to do and it protects the cache: a stray pair of quotes or a
        /// markdown bullet would be stored once and then sent on every run
        /// afterwards, so a cosmetic slip becomes permanent if it is not caught here.
        /// </remarks>
        private static string Clean(string text)
        {
            var cleaned = text.Trim();

            if (cleaned.StartsWith("- ", StringComparison.Ordinal)
                || cleaned.StartsWith("* ", StringComparison.Ordinal))
            {
                cleaned = cleaned[2..].TrimStart();
            }

            if (cleaned.Length >= 2
                && cleaned[0] == '"'
                && cleaned[^1] == '"')
            {
                cleaned = cleaned[1..^1].Trim();
            }

            return StripTrailingFieldFragment(cleaned);
        }

        /// <summary>
        /// Matches a summary that runs on into the start of the next JSON field.
        /// </summary>
        /// <remarks>
        /// Anchored to the end and deliberately narrow: a quote, a comma, a short
        /// bare key, a colon, and optionally the start of a value. It must not fire
        /// on ordinary prose, so the key is capped at 12 characters and the whole
        /// thing has to sit at the very end of the text.
        /// </remarks>
        private static readonly System.Text.RegularExpressions.Regex FieldFragment =
            new(
                """['"]?\s*,\s*['"]?[a-zA-Z_][a-zA-Z0-9_]{0,11}['"]?\s*:\s*[\[\{'"]?\s*$""",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// Whether a summary carries a trailing JSON field fragment.
        /// </summary>
        /// <remarks>
        /// Public because <see cref="SummaryPlan"/> needs it: summaries are cached on
        /// the source hash, so one already stored with a fragment is current by every
        /// other measure and would never be redistilled. Re-queueing it is what makes
        /// the fix reach the summaries that were corrupted before it existed, without
        /// anyone having to clear the store by hand.
        /// </remarks>
        /// <param name="text">The stored summary text.</param>
        /// <returns>Whether it ends in a leaked field fragment.</returns>
        public static bool CarriesFieldFragment(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var match = FieldFragment.Match(text);
            return match.Success && match.Index > 0;
        }

        /// <summary>
        /// Cuts a trailing JSON field fragment off a summary.
        /// </summary>
        /// <remarks>
        /// Observed on a live run: 17 of 232 stored summaries ended
        /// <c>…viciously sharp','t':[</c> — the model closing the prose and starting
        /// the tag field from inside the string it was still writing, so the fragment
        /// arrived as part of a perfectly valid <c>s</c> value and no parse error was
        /// ever raised. Worth catching here rather than anywhere downstream because
        /// summaries are a cache keyed on the source hash: an unnoticed one is stored
        /// once and then sent to the model on every run for the life of the overview,
        /// which is the same reasoning that put the quote-stripping above here.
        /// </remarks>
        private static string StripTrailingFieldFragment(string text)
        {
            var match = FieldFragment.Match(text);
            if (!match.Success || match.Index == 0)
            {
                return text;
            }

            // Leave it alone rather than return a stub: something has gone wrong
            // enough that a two-word "summary" would be worse than the mess.
            var kept = text[..match.Index].TrimEnd(' ', ',', ';', ':', '-', '—', '\'', '"');
            return kept.Length >= 20 ? kept : text;
        }

        /// <summary>
        /// Words a summary must never end on.
        /// </summary>
        /// <remarks>
        /// Cutting at any word boundary produced real stored summaries ending
        /// "…pure pop energy racing to a" and "…a secret that" — 11% of a measured
        /// 232-item pass. The reader of these is a model being asked to judge tone,
        /// and a sentence that stops mid-clause reads as a different, vaguer
        /// sentence rather than a shorter one. Backing off to the last word that can
        /// legitimately end a phrase costs a few characters and buys a whole thought.
        /// </remarks>
        private static readonly HashSet<string> DanglingWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "and", "or", "but", "to", "of", "in", "on", "at", "by", "for",
            "with", "from", "into", "onto", "over", "under", "as", "that", "which", "who",
            "whose", "while", "when", "where", "after", "before", "between", "through",
            "its", "it", "his", "her", "their", "this", "these", "those", "is", "are", "was",
            "were", "be", "been", "than", "then", "so", "yet", "via", "amid", "against", "about",
        };

        /// <summary>
        /// Cuts an over-long summary back to the last word that can end a phrase.
        /// </summary>
        private static string TrimToBudget(string text, int maxLength)
        {
            var cut = text.LastIndexOf(' ', maxLength - 1);
            if (cut <= 0)
            {
                // One enormous word. Nothing sensible to preserve, so cut it hard
                // rather than returning something over budget.
                return text[..(maxLength - 1)];
            }

            var words = text[..cut].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

            // Drop trailing connectives until the summary ends on a word that
            // carries meaning. Stop before emptying it: a stub is worse than a
            // slightly awkward ending.
            while (words.Count > 3
                && DanglingWords.Contains(words[^1].Trim(',', ';', ':', '-', '—', '.')))
            {
                words.RemoveAt(words.Count - 1);
            }

            return string.Join(' ', words).TrimEnd(' ', ',', ';', ':', '-', '—');
        }
    }
}
