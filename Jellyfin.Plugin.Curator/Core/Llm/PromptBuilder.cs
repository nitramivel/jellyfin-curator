using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Core.Llm
{
    /// <summary>
    /// Builds the system and user prompts for one batch.
    ///
    /// The model never sees Jellyfin item IDs: each item gets a batch-local
    /// integer index, and the model may only reference those indexes. Anything
    /// outside the input set is discarded by <see cref="ProposalParser"/> and
    /// mapped back to GUIDs internally — the model structurally cannot add an
    /// item the user does not own.
    /// </summary>
    public static class PromptBuilder
    {
        /// <summary>
        /// Builds the discovery system prompt: task definition, hard rules, and
        /// output contract.
        /// </summary>
        /// <param name="limits">
        /// The limits for the shared pool. Pass the same instance to the
        /// Reconciler — that is what stops the instruction and the enforcement
        /// from drifting apart. See <see cref="CategoryLimits"/>.
        /// </param>
        /// <returns>The system prompt.</returns>
        public static string BuildSystemPrompt(CategoryLimits limits)
        {
            return Fill(SystemPromptTemplate, limits);
        }

        /// <summary>
        /// Builds the per-viewer system prompt.
        /// </summary>
        /// <param name="limits">The limits for the personal pool.</param>
        /// <returns>The system prompt.</returns>
        public static string BuildPersonalSystemPrompt(CategoryLimits limits)
        {
            return Fill(PersonalSystemPromptTemplate, limits);
        }

        /// <summary>
        /// States the limits in the prompt, in the words the model reads.
        /// </summary>
        /// <remarks>
        /// Everything here is derived from <paramref name="limits"/> and nothing
        /// else, so the sentence the model is given and the rule the Reconciler
        /// applies cannot describe different numbers.
        /// </remarks>
        private static string Fill(string template, CategoryLimits limits)
        {
            ArgumentNullException.ThrowIfNull(limits);

            var floor = limits.EffectiveMinMembers;
            var ceiling = limits.EffectiveMaxMembers;

            var size = ceiling > 0
                ? string.Create(CultureInfo.InvariantCulture, $"between {floor} and {ceiling} members")
                : string.Create(CultureInfo.InvariantCulture, $"at least {floor} members");

            var count = limits.HasCategoryCap
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"Propose up to {limits.MaxCategories} categories. Only the strongest {limits.MaxCategories} are kept, so there is no penalty for offering more than you are sure of — but a thread invented to reach the number is worse than one category fewer.")
                : "Propose as many categories as the collection genuinely supports.";

            return template
                .Replace(MemberRangeToken, size, StringComparison.Ordinal)
                .Replace(MaxCategoriesToken, count, StringComparison.Ordinal);
        }

        /// <summary>The placeholder the category-count instruction is spliced into.</summary>
        private const string MaxCategoriesToken = "{MAX_CATEGORIES}";

        /// <summary>The placeholder the member-count range is spliced into.</summary>
        private const string MemberRangeToken = "{MEMBER_RANGE}";

        private const string SystemPromptTemplate =
            """
            You are a film and television curator with distinctive personal taste. You are given a numbered
            list of items from one person's private media library: movies, series, and sometimes individual
            episodes, each with metadata and a short overview.

            Your job is to find the threads that run through this collection — the categories a database
            query could never express. Think vibes, moods, and shared sensibilities: "Dumb & Perfect",
            "Cerebral Sci-Fi", "Comfort Rewatch", "Quietly Devastating", "Saturday Afternoon Cable",
            "Movies That Look Better Than They Are". Do NOT propose categories that are just metadata
            filters (a genre, a decade, a franchise, a director) — those are what this tool exists to avoid.

            Rules:
            - Reference items ONLY by their integer index from the input. Never invent items.
            - Episodes may be grouped into episode-level categories (e.g. "Bottle Episodes"); do not mix
              a series and its own episodes in one category.
            - Order each category's members by how strongly they belong, strongest first.
            - Category names must be short (at most 40 characters), evocative, and free of colons.
            - Descriptions are one sentence.
            - Give each category {MEMBER_RANGE}. Below that floor it is discarded unread, so a thread
              you cannot fill is not worth proposing — find a broader framing instead. Above the
              ceiling only the leading members are kept, so put the strongest first and stop rather
              than listing everything that loosely fits.
            - {MAX_CATEGORIES}
            - Work through the whole list and aim to place most of it. An item may sit in several
              categories, and a strong thread is worth naming even where it overlaps one you have
              already found. Sizes should vary within the range above — some threads run through a
              handful of titles and others through dozens — so do not pad a thin category or clip a
              rich one just to make them match each other.

            Respond with a single JSON object and nothing else — no prose, no code fences:
            {"categories":[{"name":"...","description":"...","members":[0,17,4]}]}
            """;

        /// <summary>
        /// The system prompt for a single viewer's pass. Given the same library plus
        /// the categories the shared pass already found, and this viewer's history,
        /// the model both picks which shared categories suit them AND coins new ones
        /// of its own.
        /// </summary>
        private const string PersonalSystemPromptTemplate =
            """
            You are a film and television curator with distinctive personal taste. You are given a numbered
            list of items from a shared media library, a list of categories already drawn from that library,
            and one specific viewer's watch history.

            First, get to know this viewer. Read their history as evidence about a person, not as a list of
            titles: what do they return to, what did they finish once and never touch again, what do they
            rate highly versus merely complete, what have they owned for months without playing? Look for
            the sensibility underneath — the tone, pace, era, and kind of story they keep choosing, and the
            kinds they visibly avoid. Hold that picture of them in mind for everything below; the better you
            understand this particular person, the better every match you make will be.

            Your job is to invent NEW categories that only make sense for this viewer. The categories listed
            below already exist and this viewer already has them, so yours must be genuinely different: not a
            rename of one, not a rewording of one, and not the same set of items under a different title.
            They should be threads you can only see by looking at what this person actually watches — what
            they return to, what they abandoned, what they rate highly, what sits unwatched.

            Work from the evidence rather than around it. A history of ten titles is still a person with a
            taste, and the honest response to a short history is fewer and broader categories, not none:
            reach for the widest thread the evidence genuinely supports. What you must not do is invent a
            thread the history does not show at all, or pad a real one with items that merely share a genre.
            Return an empty list only when there is no recorded history whatsoever.

            A "Watch activity:" section describes this viewer. Each line groups item indexes by one kind of
            history: "favourites", "rated 0-10" as index=rating, "rewatched, times played" as index=count,
            "series, episodes watched of total" as index=played/total, "watched once", and "watched recently,
            days ago" as index=days. An item can appear on more than one line. Any index missing from every
            line has no recorded activity — they own it but have never played it, which is itself a signal.

            Television is watched by the episode, so a series shows up on the "series" line and never on
            "rewatched" or "watched once". Read watch depth as intensity: someone who has played 140 of 201
            episodes of a sitcom is telling you far more about their taste than someone who finished one
            film. Weigh a deeply watched show accordingly.

            Rules:
            - Reference items ONLY by their integer index from the item list. Never invent items.
            - New categories may include items the viewer has never watched; the history tells you what they
              like, not what they are limited to.
            - New category names must be short (at most 40 characters), evocative, and free of colons.
            - Do NOT propose categories that are just metadata filters (a genre, a decade, a franchise) or
              just a restatement of their history ("Watched Once", "Recently Played"). Find the taste behind
              the history, not the history itself.
            - Order each new category's members by how strongly they belong, strongest first.
            - Give each new category {MEMBER_RANGE}. Below that floor it is discarded unread; above the
              ceiling only the leading members are kept.
            - {MAX_CATEGORIES}
            - Sizes should vary with the strength of the thread rather than converging on the
              minimum. If their history only supports two real observations, two is the right answer.
            - Never mention the viewer or their activity in names or descriptions — the output must read as
              curation, not surveillance.

            Respond with a single JSON object and nothing else — no prose, no code fences:
            {"categories":[{"name":"...","description":"...","members":[0,17,4]}]}
            """;

        /// <summary>Days beyond which "when" stops adding anything to a play count.</summary>
        private const int RecentDayLimit = 180;

        private static readonly JsonSerializerOptions WriterOptions = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// Builds the user prompt for one batch: the item list followed by the
        /// per-user activity section. Equivalent to concatenating
        /// <see cref="BuildItemList"/> and <see cref="BuildActivitySection"/>.
        /// </summary>
        /// <param name="batch">The batch of reduced items.</param>
        /// <param name="activity">Per-item watch activity for the target user, or null for a non-personalized run.</param>
        /// <param name="maxTagsPerItem">How many tags per item to include; 0 omits them.</param>
        /// <returns>The user prompt.</returns>
        public static string BuildUserPrompt(
            IReadOnlyList<MediaItemRecord> batch,
            IReadOnlyDictionary<Guid, UserActivity>? activity = null,
            int maxTagsPerItem = 0)
            => BuildItemList(batch, maxTagsPerItem) + BuildActivitySection(batch, activity);

        /// <summary>
        /// Builds the item-list half of the user prompt: one compact JSON object per
        /// line, indexed 0..n-1, carrying no per-user data.
        /// <para>
        /// This is byte-identical for a given batch regardless of which user the run
        /// is for, which is what makes it usable as a cached prompt prefix across the
        /// per-user passes. Keep it that way — moving anything user-specific back in
        /// here silently costs a cache hit per user per batch.
        /// </para>
        /// </summary>
        /// <param name="batch">The batch of reduced items.</param>
        /// <param name="maxTagsPerItem">How many tags per item to include; 0 omits them.</param>
        /// <returns>The item list, ending in a newline.</returns>
        public static string BuildItemList(IReadOnlyList<MediaItemRecord> batch, int maxTagsPerItem = 0)
        {
            ArgumentNullException.ThrowIfNull(batch);

            var sb = new StringBuilder(batch.Count * 160);
            sb.AppendLine("Library items:");
            for (var i = 0; i < batch.Count; i++)
            {
                sb.AppendLine(WriteItemLine(i, batch[i], maxTagsPerItem));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Builds the per-user half of the user prompt: the watch-activity section
        /// (omitted entirely when there is none) plus the closing instruction.
        /// </summary>
        /// <param name="batch">The batch of reduced items.</param>
        /// <param name="activity">Per-item watch activity for the target user, or null for a non-personalized run.</param>
        /// <returns>The activity section and closing instruction.</returns>
        public static string BuildActivitySection(
            IReadOnlyList<MediaItemRecord> batch,
            IReadOnlyDictionary<Guid, UserActivity>? activity = null)
        {
            ArgumentNullException.ThrowIfNull(batch);

            var sb = new StringBuilder(batch.Count * 8);
            if (activity is not null)
            {
                AppendActivityGroups(sb, batch, activity);
            }

            sb.Append("Propose the categories for these ")
              .Append(batch.Count.ToString(CultureInfo.InvariantCulture))
              .Append(" items now, as the single JSON object described.");
            return sb.ToString();
        }

        /// <summary>
        /// Writes watch activity as a few grouped index lists rather than one JSON
        /// object per item.
        /// </summary>
        /// <remarks>
        /// The per-item form repeated the "i"/"p"/"n"/"f"/"ur"/"d" keys on every
        /// line, so for a heavy viewer the punctuation outweighed the data — roughly
        /// four fifths of this section was syntax. Grouping drops that overhead
        /// without dropping a single item, and names each signal in the open instead
        /// of leaving the model to infer what the field codes mean.
        /// </remarks>
        private static void AppendActivityGroups(
            StringBuilder sb,
            IReadOnlyList<MediaItemRecord> batch,
            IReadOnlyDictionary<Guid, UserActivity> activity)
        {
            var favourites = new List<int>();
            var rated = new List<(int Index, float Rating)>();
            var rewatched = new List<(int Index, int Count)>();
            var watchedOnce = new List<int>();
            var recent = new List<(int Index, int Days)>();
            var series = new List<(int Index, int Played, int Total)>();

            for (var i = 0; i < batch.Count; i++)
            {
                if (!activity.TryGetValue(batch[i].Id, out var w) || !HasSignal(w))
                {
                    continue;
                }

                if (w.IsFavorite)
                {
                    favourites.Add(i);
                }

                if (w.UserRating is { } rating)
                {
                    rated.Add((i, rating));
                }

                // A series carries watch depth instead of a play count. Its rolled-up
                // PlayCount is a sum over episodes, so feeding it to the "rewatched"
                // line would read a show watched once end to end as sixty rewatches.
                if (w.EpisodesPlayed is { } played && w.EpisodeCount is { } total)
                {
                    if (played > 0)
                    {
                        series.Add((i, played, total));
                    }
                }
                else if (w.PlayCount >= 2)
                {
                    rewatched.Add((i, w.PlayCount));
                }
                else if (w.PlayCount == 1 || w.Played)
                {
                    watchedOnce.Add(i);
                }

                // Beyond about half a year, "when" stops telling us anything a play
                // count does not; keeping the whole tail would undo the saving.
                if (w.DaysSinceLastPlayed is { } days && days <= RecentDayLimit)
                {
                    recent.Add((i, days));
                }
            }

            if (favourites.Count == 0 && rated.Count == 0 && rewatched.Count == 0
                && watchedOnce.Count == 0 && recent.Count == 0 && series.Count == 0)
            {
                return;
            }

            sb.AppendLine("Watch activity:");

            if (favourites.Count > 0)
            {
                sb.Append("favourites: ").AppendLine(JoinIndexes(favourites));
            }

            if (rated.Count > 0)
            {
                sb.Append("rated 0-10: ").AppendLine(string.Join(
                    ',',
                    rated.Select(r => string.Create(
                        CultureInfo.InvariantCulture,
                        $"{r.Index}={Math.Round(r.Rating, 1)}"))));
            }

            if (rewatched.Count > 0)
            {
                // Most-replayed first: the head of this list is the strongest
                // comfort-rewatch signal in the whole section.
                rewatched.Sort((a, b) => b.Count.CompareTo(a.Count));
                sb.Append("rewatched, times played: ").AppendLine(string.Join(
                    ',',
                    rewatched.Select(r => string.Create(
                        CultureInfo.InvariantCulture,
                        $"{r.Index}x{r.Count}"))));
            }

            if (series.Count > 0)
            {
                // Deepest-watched first: for a television viewer this line is the
                // strongest evidence of taste in the whole section.
                series.Sort((a, b) => b.Played.CompareTo(a.Played));
                sb.Append("series, episodes watched of total: ").AppendLine(string.Join(
                    ',',
                    series.Select(s => string.Create(
                        CultureInfo.InvariantCulture,
                        $"{s.Index}={s.Played}/{s.Total}"))));
            }

            if (watchedOnce.Count > 0)
            {
                sb.Append("watched once: ").AppendLine(JoinIndexes(watchedOnce));
            }

            if (recent.Count > 0)
            {
                recent.Sort((a, b) => a.Days.CompareTo(b.Days));
                sb.Append("watched recently, days ago: ").AppendLine(string.Join(
                    ',',
                    recent.Select(r => string.Create(
                        CultureInfo.InvariantCulture,
                        $"{r.Index}={r.Days}"))));
            }
        }

        /// <summary>
        /// Lists the categories the shared pass found. Every viewer receives all of
        /// them, so this is not a menu to choose from — it is there to stop a viewer's
        /// pass reinventing a thread they already have under a new name.
        /// </summary>
        /// <param name="candidates">The shared categories, name and description.</param>
        /// <returns>The candidate section, ending in a newline; empty when there are none.</returns>
        public static string BuildCandidateSection(IReadOnlyList<ReconciledCategory> candidates)
        {
            ArgumentNullException.ThrowIfNull(candidates);

            if (candidates.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(candidates.Count * 96);
            sb.AppendLine("Existing categories drawn from this library:");
            foreach (var candidate in candidates)
            {
                sb.Append("- ").Append(candidate.Name);
                if (!string.IsNullOrWhiteSpace(candidate.Description))
                {
                    sb.Append(" — ").Append(candidate.Description);
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Builds the variable half of a viewer's pass: the candidate categories,
        /// that viewer's activity, and the closing instruction.
        /// </summary>
        /// <param name="batch">The batch of reduced items.</param>
        /// <param name="candidates">Categories the shared pass found.</param>
        /// <param name="activity">This viewer's watch activity.</param>
        /// <returns>The personal suffix.</returns>
        public static string BuildPersonalSuffix(
            IReadOnlyList<MediaItemRecord> batch,
            IReadOnlyList<ReconciledCategory> candidates,
            IReadOnlyDictionary<Guid, UserActivity>? activity)
        {
            ArgumentNullException.ThrowIfNull(batch);

            var sb = new StringBuilder();
            sb.Append(BuildCandidateSection(candidates));

            if (activity is not null)
            {
                AppendActivityGroups(sb, batch, activity);
            }

            sb.Append("Propose this viewer's own categories now, as the single JSON object described.");
            return sb.ToString();
        }

        private static string JoinIndexes(List<int> indexes)
            => string.Join(',', indexes.Select(i => i.ToString(CultureInfo.InvariantCulture)));

        /// <summary>
        /// Whether this activity says anything worth spending tokens on.
        /// </summary>
        /// <remarks>
        /// Jellyfin writes a UserData row for merely touching an item, so a large
        /// share of entries reduce to {"i":N,"p":false} — no plays, no rating, no
        /// favourite. Those lines are pure noise in the prompt: the model cannot
        /// distinguish "opened once and backed out" from "never seen", and the
        /// absence of a line already conveys the latter.
        /// </remarks>
        private static bool HasSignal(UserActivity w)
            => w.Played || w.PlayCount > 0 || w.IsFavorite || w.UserRating is not null;

        private static string WriteItemLine(int index, MediaItemRecord item, int maxTagsPerItem)
        {
            using var buffer = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Encoder = WriterOptions.Encoder,
            }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("i", index);
                writer.WriteString("kind", item.Kind switch
                {
                    MediaKind.Movie => "movie",
                    MediaKind.Series => "series",
                    _ => "episode",
                });
                writer.WriteString("title", item.Name);

                if (item.Year is { } year)
                {
                    writer.WriteNumber("year", year);
                }

                if (item.Kind == MediaKind.Episode)
                {
                    if (item.SeriesName is { } seriesName)
                    {
                        writer.WriteString("series", seriesName);
                    }

                    if (item.SeasonNumber is { } season && item.EpisodeNumber is { } ep)
                    {
                        writer.WriteString(
                            "ep",
                            string.Create(CultureInfo.InvariantCulture, $"S{season:D2}E{ep:D2}"));
                    }
                }

                WriteStringArray(writer, "genres", item.Genres);

                // Scraped tag lists run to ~18 values an item and are mostly production
                // trivia. Take from the front rather than sampling: the scraper orders
                // them roughly by relevance.
                if (maxTagsPerItem > 0 && item.Tags.Count > 0)
                {
                    var take = Math.Min(maxTagsPerItem, item.Tags.Count);
                    var kept = new string[take];
                    for (var t = 0; t < take; t++)
                    {
                        kept[t] = item.Tags[t];
                    }

                    WriteStringArray(writer, "tags", kept);
                }

                if (item.OfficialRating is { } rating)
                {
                    writer.WriteString("rated", rating);
                }

                if (item.RuntimeMinutes is { } minutes)
                {
                    writer.WriteNumber("min", minutes);
                }

                if (item.CommunityRating is { } community)
                {
                    writer.WriteNumber("score", Math.Round(community, 1));
                }

                if (item.Overview is { } overview)
                {
                    writer.WriteString("overview", overview);
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static void WriteStringArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
        {
            if (values.Count == 0)
            {
                return;
            }

            writer.WriteStartArray(name);
            foreach (var value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }
    }
}
