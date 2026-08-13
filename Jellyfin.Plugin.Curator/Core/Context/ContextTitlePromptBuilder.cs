using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Curator.Core.Llm;

namespace Jellyfin.Plugin.Curator.Core.Context
{
    /// <summary>
    /// Asks a model for a few home screen row titles that fit the conditions.
    ///
    /// <para>
    /// The smallest paid call in the plugin, and the one most at risk of being
    /// worthless — in two opposite directions, and this prompt has now been wrong
    /// in both. Asked plainly for "a title for a rainy evening" a model hands back
    /// <i>Rainy Evening Picks</i>, which is the setting it replaced, spelled the
    /// same way, having cost money. So the prompt was written to push away from the
    /// bare label and towards the mood the conditions make — and it pushed far too
    /// hard. Measured on the owner's server: <i>Slate Sky Slow Burns</i>, which
    /// names neither the weather nor the hour in any word a reader would recognise
    /// as either, and reads as a riddle on a row nobody has time to solve.
    /// </para>
    /// <para>
    /// So the target is the middle and it has to be described as the middle, not as
    /// a direction to travel in: say plainly what the row suits, in the words a
    /// person would use out loud — <i>Good for an Overcast Afternoon</i>. The
    /// merchandising vocabulary stays banned, which is what keeps plain from
    /// collapsing back into <i>Picks</i>. The one thing plainness costs is variety,
    /// because the plainest phrasing is the same phrasing every time, so the prompt
    /// spends its remaining length insisting the openings differ.
    /// </para>
    /// <para>
    /// Naming the conditions plainly then raises the question of which conditions are
    /// worth naming, and one is not. The temperature words already carry a notability
    /// bar in their thresholds — <see cref="WeatherCodes.HotCelsius"/> is set where
    /// heat becomes the thing you notice about the day — but the sky words have none,
    /// so <c>clear</c> earns a word for the same reason <c>storm</c> does. It should
    /// not: a clear sky is the ordinary state of the sky, "a clear evening" says
    /// barely more than "an evening", and on a 40-character budget it is the word to
    /// spend elsewhere. So a bare <c>clear</c> is not named. It stays nameable
    /// <b>with</b> heat or cold, because a cold bright morning is a specific thing in
    /// a way a clear afternoon is not — which is why this is a rule in the prompt
    /// rather than a filter over the vocabulary. Selection is untouched either way:
    /// <c>ContextRanker</c> still chooses on <c>clear</c>, and the title simply stops
    /// announcing the least interesting thing about the moment.
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
        /// Which generation of this prompt is current.
        /// </summary>
        /// <remarks>
        /// <b>Bump this whenever the prompt changes what a title should sound
        /// like</b> — not for a typo, but for anything a reader would notice.
        /// Titles are bought per set of conditions and kept for a year, so a
        /// rewritten prompt otherwise applies only to weather the server has not
        /// seen yet: the owner rewrites the instruction, pays nothing, and watches
        /// the old titles carry on appearing until the seasons turn. Stamping the
        /// generation on each set makes <see cref="ContextTitles.Prune"/> cull the
        /// stale ones on the next hourly pass, which re-buys them at the usual one
        /// call per condition.
        /// <para>
        /// 0 is every set written before this existed. 1 was the mood prompt, which
        /// pushed so hard away from naming the conditions that it produced titles
        /// like <i>Slate Sky Slow Burns</i>. 2 names them plainly. 3 stops naming a
        /// clear sky that comes with nothing else. 4 is Title Case, to match the rows
        /// Jellyfin draws beside it.
        /// </para>
        /// </remarks>
        public const int StyleVersion = 4;

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
            suit this exact moment — the weather outside and the hour of the day, together.

            Write {COUNT} different titles for it.

            Say plainly what the row is for. Name the weather and the hour in ordinary words, the way
            someone would say them out loud:

              Good for an Overcast Afternoon
              Made for a Grey Afternoon
              Suits a Rainy Evening
              When the Rain Sets In
              Something for a Slow Morning
              Cold Night, Warm Film

            Straightforward beats clever here, and by some distance. "Slate Sky Slow Burns" is the
            failure to avoid: it is a riddle, the reader has to work out what is being offered, and on a
            home screen nobody stops to. If someone glancing at the row cannot tell within a second what
            weather and what time of day it is meant for, the title has not worked.

            Vary how they open. {COUNT} titles that all begin "Good for" is one title written {COUNT}
            times, and these are shown in rotation, so the reader sees the repetition. At most ONE may
            open that way. Reach for other shapes: put the hour first, put the sky first, name the
            conditions and stop, say what it suits without using the word "for".

            Rules:
            - At most {MAX} characters. Rows clip rather than wrap, so a long title is a cut-off one.
            - Title Case, the way a heading is written: capitalise the first word, the last word, and every
              important word between them. Leave short joining words lowercase — a, an, the, and, or, for,
              of, in, on, at, to. "Good for an Overcast Afternoon", not "Good For An Overcast Afternoon"
              and not "Good for an overcast afternoon". This row sits beside Jellyfin's own, which are
              titled that way, and one row in a different case is the one that looks broken.
            - No full stops, no colons, no quotation marks, no emoji.
            - Never use the words "picks", "selection", "curated", "for you", "recommended", or "row".
              Plain is the point; advertising is not.
            - Both the weather and the hour must be readable in the words. One without the other names
              half a moment. Two exceptions, and only these two:
                - When the weather is given as unknown, name only the hour and do not guess at a sky.
                - A clear bright sky and nothing else is not worth naming. It is the ordinary state of
                  the sky, so "a clear evening" says barely more than "an evening" and spends
                  characters doing it — name the hour and let the sky go. This does NOT apply when the
                  clear sky comes with heat or cold: a cold bright morning is a specific thing and
                  worth naming as one.
            - Do not begin more than one of them the same way, and do not return near-duplicates —
              {COUNT} titles that all say the same thing is one title and a wasted call.
            - No second person. Describe the moment, do not address or instruct the reader.

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
            return title.Length is 0 or > MaxTitleLength ? null : TitleCase(title);
        }

        /// <summary>
        /// Words left lowercase inside a title.
        /// </summary>
        /// <remarks>
        /// Articles, coordinating conjunctions and the short prepositions. Kept
        /// deliberately small: the failure worth avoiding is a title that reads as
        /// shouting, and the way to get there is lowercasing too little, not too
        /// much.
        /// </remarks>
        private static readonly HashSet<string> SmallWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the",
            "and", "as", "but", "for", "if", "nor", "or", "so", "yet",
            "at", "by", "down", "from", "in", "into", "near", "of", "off", "on",
            "onto", "out", "over", "per", "to", "up", "upon", "via", "with",
        };

        /// <summary>
        /// Puts a title into Title Case.
        /// </summary>
        /// <remarks>
        /// A backstop, not the mechanism — the prompt asks for this case and usually
        /// gets it. It exists because the row sits beside Jellyfin's own
        /// ("Continue Watching", "Next Up"), and a single row in a different case is
        /// the one that looks broken; leaving that to a model that is right most of
        /// the time means it is visibly wrong some of the time.
        /// <para>
        /// Only ever changes the FIRST letter of a word. <c>ToTitleCase</c> from
        /// <c>TextInfo</c> was the obvious tool and is wrong twice over: it
        /// capitalises articles ("Good For An Overcast Afternoon") and it lowercases
        /// the rest of a word, which turns "TV" into "Tv". First and last words are
        /// always capitalised however small, so "Next Up" keeps its capital.
        /// </para>
        /// </remarks>
        /// <param name="title">The title as written.</param>
        /// <returns>The same words, cased as a heading.</returns>
        public static string TitleCase(string title)
        {
            ArgumentNullException.ThrowIfNull(title);

            // A title with no lowercase letter anywhere is shouting rather than
            // cased, and preserving its capitals would produce "MADE for a COLD
            // NIGHT" — worse than what came in. Flattened first, then cased
            // normally. Anything with even one lowercase letter is left to the
            // per-word rule below, so "A Night of TV" keeps its TV.
            var source = title.Any(char.IsLower) ? title : title.ToLowerInvariant();

            var words = source.Split(' ');
            var last = words.Length - 1;

            for (var i = 0; i <= last; i++)
            {
                var word = words[i];
                if (word.Length == 0)
                {
                    continue;
                }

                // Small words are lowercased WHOLE. They are all plain function
                // words with no legitimate internal capitals, and touching only the
                // first letter leaves "FOR" as "fOR".
                if (i != 0 && i != last && SmallWords.Contains(word.Trim(',', ';', '-')))
                {
                    words[i] = word.ToLowerInvariant();
                    continue;
                }

                // Everything else has its first letter raised and the rest left
                // exactly as it is, so capitals a word carries of its own — "TV",
                // "IMAX", "McCarthy" — survive. This can add case but never destroy
                // it.
                words[i] = char.ToUpperInvariant(word[0]) + word[1..];
            }

            return string.Join(' ', words);
        }
    }
}
