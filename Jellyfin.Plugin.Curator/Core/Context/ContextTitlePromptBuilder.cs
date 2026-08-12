using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.Curator.Core.Llm;

namespace Jellyfin.Plugin.Curator.Core.Context
{
    /// <summary>
    /// Asks a model for a few home screen row titles that fit the conditions.
    ///
    /// <para>
    /// The smallest paid call in the plugin, and the one most at risk of being
    /// worthless. A model asked for "a title for a rainy evening" hands back
    /// <i>Rainy Evening Picks</i> — which is the setting it replaced, spelled the
    /// same way, having cost money. So the prompt spends most of its length on what
    /// not to do: do not name the weather, do not say "picks" or "for you", do not
    /// write a sentence.
    /// </para>
    /// </summary>
    public static class ContextTitlePromptBuilder
    {
        /// <summary>The most characters a row title may run to.</summary>
        /// <remarks>
        /// Home screen rows are a single line and clip rather than wrap, so a title
        /// that does not fit is not a long title, it is a truncated one. Forty is
        /// about what the narrowest client shows.
        /// </remarks>
        public const int MaxTitleLength = 40;

        /// <summary>
        /// Builds the system prompt.
        /// </summary>
        /// <param name="count">How many titles to ask for.</param>
        /// <returns>The system prompt.</returns>
        public static string BuildSystemPrompt(int count)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

            return SystemPromptTemplate
                .Replace("{COUNT}", count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{MAX}", MaxTitleLength.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        /// <summary>
        /// Builds the user prompt for one moment.
        /// </summary>
        /// <param name="context">The conditions.</param>
        /// <param name="count">How many titles to ask for.</param>
        /// <returns>The user prompt.</returns>
        public static string BuildUserPrompt(ViewingContext context, int count)
        {
            ArgumentNullException.ThrowIfNull(context);

            return "Right now, where the viewer is: " + ContextTitles.Describe(context) + ".\n\nWrite "
                + count.ToString(CultureInfo.InvariantCulture)
                + " titles for the row of films and shows that suits it. Return the JSON object and nothing else.";
        }

        private const string SystemPromptTemplate =
            """
            You name a single row on a home screen full of films and television. The row holds things that
            suit this exact moment — both the weather outside and the hour of the day, together.

            Write {COUNT} different titles for it.

            A good one names the MOOD the two make between them. Weather and hour are not two facts to
            list, they are one feeling: rain at eleven at night is not rain at eight in the morning, and
            the title should be recognisable as one and not the other. Aim for something like "Rainy Night
            Cozy Vibes" or "Cloudy Morning Stories" — plain enough to read at a glance, specific enough
            that it could not describe a different sky or a different hour.

            The mistake to avoid is the bare label. "Rainy Evening Picks" is not a title — the reader can
            see it is raining, they are standing in it. Reach past the conditions to what they DO to a
            person: what they make someone want to sink into, hide from, stay up for, wake up gently with.

            Rules:
            - At most {MAX} characters. Rows clip rather than wrap, so a long title is a cut-off one.
            - Title Case. No full stops, no colons, no quotation marks, no emoji.
            - Never use the words "picks", "selection", "curated", "for you", "recommended", or "row".
            - Let BOTH the weather and the hour show, in the words or in the feeling. A title that would
              read the same at any hour has only done half the job.
            - Do not begin more than one of them the same way, and do not return near-duplicates —
              {COUNT} titles that all say the same thing is one title and a wasted call.
            - No second person. Name the mood, do not instruct the reader.

            Respond with a single JSON object and nothing else — no prose, no code fences:
            {"titles":["...","..."]}
            """;

        /// <summary>
        /// Reads the titles back.
        /// </summary>
        /// <remarks>
        /// Tolerant in one direction only. Anything unusable is dropped and whatever
        /// survives is kept, because the caller's fallback is the configured static
        /// name — so a partly-usable answer is worth more than none, and there is no
        /// downstream that a slightly short list can break. What it will not do is
        /// accept a title over the length budget: that renders as a cut-off phrase on
        /// somebody's home screen, which is worse than the plain name.
        /// </remarks>
        /// <param name="responseText">The model's text output.</param>
        /// <returns>The usable titles, in order, deduplicated.</returns>
        public static IReadOnlyList<string> Parse(string responseText)
        {
            ArgumentNullException.ThrowIfNull(responseText);

            // Both failures are swallowed, and the second is the one that matters:
            // ExtractObject throws when the response carries no JSON object at all,
            // which is what a model refusing, apologising or answering in prose looks
            // like. Every other parser here is right to throw on that — a lost batch
            // of summaries is worth a retry. This one is not: the fallback is the row
            // name the owner typed, so the honest outcome is no titles and no fuss.
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(JsonResponse.ExtractObject(responseText));
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                return [];
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("titles", out var titles)
                    || titles.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                var kept = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in titles.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var title = Clean(entry.GetString());
                    if (title is not null && seen.Add(title))
                    {
                        kept.Add(title);
                    }
                }

                return kept;
            }
        }

        /// <summary>
        /// Strips the wrappers models add despite being told not to, and rejects what
        /// cannot be repaired.
        /// </summary>
        private static string? Clean(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var title = value.Trim();

            if (title.Length >= 2
                && ((title[0] == '"' && title[^1] == '"') || (title[0] == '\'' && title[^1] == '\'')))
            {
                title = title[1..^1].Trim();
            }

            title = title.TrimEnd('.', ' ');

            // Length is a hard reject rather than a trim. Cutting a title at the
            // budget produces exactly the clipped phrase the budget exists to
            // prevent, and the fallback — the owner's own row name — is better than
            // a sentence that stops halfway.
            return title.Length is 0 or > MaxTitleLength ? null : title;
        }
    }
}
