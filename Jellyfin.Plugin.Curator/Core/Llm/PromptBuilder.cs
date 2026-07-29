using System;
using System.Collections.Generic;
using System.Globalization;
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
        /// The system prompt: task definition, hard rules, and output contract.
        /// </summary>
        public const string SystemPrompt =
            """
            You are a film and television curator with distinctive personal taste. You are given a numbered
            list of items from one person's private media library: movies, series, and sometimes individual
            episodes, each with metadata and a short overview.

            Your job is to find the threads that run through this collection — the categories a database
            query could never express. Think vibes, moods, and shared sensibilities: "Dumb & Perfect",
            "Cerebral Sci-Fi", "Comfort Rewatch", "Quietly Devastating", "Saturday Afternoon Cable",
            "Movies That Look Better Than They Are". Do NOT propose categories that are just metadata
            filters (a genre, a decade, a franchise, a director) — those are what this tool exists to avoid.

            When watch-activity data is present (the "w" field: p=played, n=play count, f=favorite,
            ur=user rating 0-10, d=days since last played), the list belongs to one specific viewer.
            Use it: high play counts suggest comfort rewatches, favorites reveal taste, unwatched items
            can seed "you own this for a reason" categories. Never mention the viewer or their activity
            in category names or descriptions — the output must read as curation, not surveillance.

            Rules:
            - Reference items ONLY by their integer index from the input. Never invent items.
            - Episodes may be grouped into episode-level categories (e.g. "Bottle Episodes"); do not mix
              a series and its own episodes in one category.
            - Order each category's members by how strongly they belong, strongest first.
            - Category names must be short (at most 40 characters), evocative, and free of colons.
            - Descriptions are one sentence.
            - Propose only categories with at least 3 members from this batch.

            Respond with a single JSON object and nothing else — no prose, no code fences:
            {"categories":[{"name":"...","description":"...","members":[0,17,4]}]}
            """;

        private static readonly JsonSerializerOptions WriterOptions = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// Builds the user prompt for one batch: one compact JSON object per line,
        /// indexed 0..n-1, with optional per-item watch activity.
        /// </summary>
        /// <param name="batch">The batch of reduced items.</param>
        /// <param name="activity">Per-item watch activity for the target user, or null for a non-personalized run.</param>
        /// <returns>The user prompt.</returns>
        public static string BuildUserPrompt(
            IReadOnlyList<MediaItemRecord> batch,
            IReadOnlyDictionary<Guid, UserActivity>? activity = null)
        {
            ArgumentNullException.ThrowIfNull(batch);

            var sb = new StringBuilder(batch.Count * 160);
            sb.AppendLine("Library items:");
            for (var i = 0; i < batch.Count; i++)
            {
                sb.AppendLine(WriteItemLine(i, batch[i], activity));
            }

            sb.Append("Propose the categories for these ")
              .Append(batch.Count.ToString(CultureInfo.InvariantCulture))
              .Append(" items now, as the single JSON object described.");
            return sb.ToString();
        }

        private static string WriteItemLine(
            int index,
            MediaItemRecord item,
            IReadOnlyDictionary<Guid, UserActivity>? activity)
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
                WriteStringArray(writer, "tags", item.Tags);

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

                if (activity is not null && activity.TryGetValue(item.Id, out var w))
                {
                    writer.WriteStartObject("w");
                    writer.WriteBoolean("p", w.Played);
                    if (w.PlayCount > 0)
                    {
                        writer.WriteNumber("n", w.PlayCount);
                    }

                    if (w.IsFavorite)
                    {
                        writer.WriteBoolean("f", true);
                    }

                    if (w.UserRating is { } userRating)
                    {
                        writer.WriteNumber("ur", Math.Round(userRating, 1));
                    }

                    if (w.DaysSinceLastPlayed is { } days)
                    {
                        writer.WriteNumber("d", days);
                    }

                    writer.WriteEndObject();
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
