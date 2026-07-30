using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Core
{
    /// <summary>
    /// Folds episode-level watch data up onto the parent series.
    ///
    /// <para>
    /// Jellyfin records playback against the <c>Episode</c> row. A <c>Series</c> row's
    /// own user data carries the favorite flag and a manual rating, but its
    /// <c>PlayCount</c> and <c>LastPlayedDate</c> stay empty no matter how much of the
    /// show has been watched — "played" for a series is derived at DTO-build time from
    /// child counts and is never persisted where <c>GetUserData</c> can see it.
    /// </para>
    ///
    /// <para>
    /// Curator scans movies and series, so without this rollup a viewer who only
    /// watches television reads as a viewer who has watched nothing. Measured on the
    /// owner's library: 86 series, and across all six users the activity map held
    /// three series entries against 210 movie entries. Two users were handed to the
    /// model with 0 and 3 items of history, one of whom got an empty response back in
    /// a second flat, and a category of thirteen sitcoms was correctly withheld from a
    /// viewer whose recorded history contained no television at all.
    /// </para>
    ///
    /// <para>
    /// This is a pure function so the aggregation is pinned by tests; the Jellyfin
    /// query that feeds it lives in <c>Services/Library/UserActivityProvider</c>.
    /// </para>
    /// </summary>
    public static class SeriesActivityRollup
    {
        /// <summary>
        /// One episode's user data, already flattened out of Jellyfin's types.
        /// </summary>
        /// <param name="SeriesId">The parent series ID, from the episode's persisted property.</param>
        /// <param name="Played">Whether the episode is marked played.</param>
        /// <param name="PlayCount">How many times the episode was played.</param>
        /// <param name="IsFavorite">Whether the episode itself was favorited.</param>
        /// <param name="DaysSinceLastPlayed">Whole days since this episode last played, when known.</param>
        public readonly record struct EpisodeActivity(
            Guid SeriesId,
            bool Played,
            int PlayCount,
            bool IsFavorite,
            int? DaysSinceLastPlayed);

        /// <summary>
        /// Merges episode activity into the series entries of an activity map.
        /// </summary>
        /// <remarks>
        /// The series' own entry wins for the two signals a viewer sets directly on a
        /// show — favorite and personal rating — because those are real and deliberate.
        /// Everything about *watching* is replaced by the rollup, since the series row
        /// never held it in the first place.
        /// </remarks>
        /// <param name="seriesActivity">
        /// Activity already gathered for the scanned items, keyed by item ID. Not mutated.
        /// </param>
        /// <param name="seriesIds">
        /// The series in the scan. Episodes of anything outside this set are ignored, so
        /// a show the scan excluded cannot conjure an entry.
        /// </param>
        /// <param name="episodes">Episode activity for every episode in the library.</param>
        /// <returns>A new map with series entries carrying watch depth.</returns>
        public static IReadOnlyDictionary<Guid, UserActivity> Apply(
            IReadOnlyDictionary<Guid, UserActivity> seriesActivity,
            IReadOnlyCollection<Guid> seriesIds,
            IEnumerable<EpisodeActivity> episodes)
        {
            ArgumentNullException.ThrowIfNull(seriesActivity);
            ArgumentNullException.ThrowIfNull(seriesIds);
            ArgumentNullException.ThrowIfNull(episodes);

            var wanted = seriesIds as HashSet<Guid> ?? new HashSet<Guid>(seriesIds);
            var totals = new Dictionary<Guid, Accumulator>();

            foreach (var episode in episodes)
            {
                if (episode.SeriesId == Guid.Empty || !wanted.Contains(episode.SeriesId))
                {
                    continue;
                }

                var acc = totals.TryGetValue(episode.SeriesId, out var existing) ? existing : default;
                acc.EpisodeCount++;

                if (episode.Played || episode.PlayCount > 0)
                {
                    acc.EpisodesPlayed++;
                    acc.TotalPlays += Math.Max(1, episode.PlayCount);
                }

                acc.AnyFavorite |= episode.IsFavorite;

                // The most recent episode is what dates the show. An episode watched
                // two years ago tells us nothing once a later one exists.
                if (episode.DaysSinceLastPlayed is { } days
                    && (episode.Played || episode.PlayCount > 0)
                    && (acc.DaysSinceLastPlayed is null || days < acc.DaysSinceLastPlayed))
                {
                    acc.DaysSinceLastPlayed = days;
                }

                totals[episode.SeriesId] = acc;
            }

            var result = new Dictionary<Guid, UserActivity>(seriesActivity);

            foreach (var (seriesId, acc) in totals)
            {
                if (acc.EpisodeCount == 0)
                {
                    continue;
                }

                result.TryGetValue(seriesId, out var own);

                // A show sitting in the library with no episode ever played is the
                // default state, exactly as for an unplayed movie. Carry it only when
                // the series row itself holds a deliberate signal.
                if (acc.EpisodesPlayed == 0)
                {
                    if (own is not null)
                    {
                        result[seriesId] = own with
                        {
                            EpisodeCount = acc.EpisodeCount,
                            EpisodesPlayed = 0,
                        };
                    }

                    continue;
                }

                result[seriesId] = new UserActivity
                {
                    Played = true,
                    PlayCount = acc.TotalPlays,
                    IsFavorite = (own?.IsFavorite ?? false) || acc.AnyFavorite,
                    UserRating = own?.UserRating,
                    DaysSinceLastPlayed = acc.DaysSinceLastPlayed,
                    EpisodeCount = acc.EpisodeCount,
                    EpisodesPlayed = acc.EpisodesPlayed,
                };
            }

            return result;
        }

        private struct Accumulator
        {
            public int EpisodeCount;
            public int EpisodesPlayed;
            public int TotalPlays;
            public bool AnyFavorite;
            public int? DaysSinceLastPlayed;
        }
    }
}
