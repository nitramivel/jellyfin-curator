using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Models;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Pins the fix for the bug that made every television viewer look like a viewer
    /// of nothing. Jellyfin records playback against episode rows; a series row's own
    /// user data never carries watch depth, so a scan of movies and series handed the
    /// model a household in which nobody watched television.
    /// </summary>
    public class SeriesActivityRollupTests
    {
        private static readonly Guid Office = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private static readonly Guid Friends = Guid.Parse("22222222-2222-2222-2222-222222222222");
        private static readonly Guid Movie = Guid.Parse("33333333-3333-3333-3333-333333333333");

        private static SeriesActivityRollup.EpisodeActivity Episode(
            Guid seriesId, bool played = true, int playCount = 1, bool favorite = false, int? days = null)
            => new(seriesId, played, playCount, favorite, days);

        [Fact]
        public void SeriesWithNoOwnData_GainsWatchDepthFromItsEpisodes()
        {
            var result = SeriesActivityRollup.Apply(
                new Dictionary<Guid, UserActivity>(),
                [Office],
                [Episode(Office), Episode(Office), Episode(Office, played: false, playCount: 0)]);

            var office = result[Office];
            Assert.True(office.Played);
            Assert.Equal(2, office.EpisodesPlayed);
            Assert.Equal(3, office.EpisodeCount);
        }

        /// <summary>
        /// The whole point: a series the user has watched deeply must arrive with a
        /// signal at all. Before the rollup this entry did not exist.
        /// </summary>
        [Fact]
        public void ASeriesWatchedDeeply_IsCountedAsWatched()
        {
            var episodes = new List<SeriesActivityRollup.EpisodeActivity>();
            for (var i = 0; i < 201; i++)
            {
                episodes.Add(Episode(Office, played: i < 140, playCount: i < 140 ? 1 : 0));
            }

            var result = SeriesActivityRollup.Apply(new Dictionary<Guid, UserActivity>(), [Office], episodes);

            Assert.Equal(140, result[Office].EpisodesPlayed);
            Assert.Equal(201, result[Office].EpisodeCount);
            Assert.Equal(1, PersonalizationEligibility.CountWatched(result));
        }

        [Fact]
        public void MostRecentlyPlayedEpisodeDatesTheSeries()
        {
            var result = SeriesActivityRollup.Apply(
                new Dictionary<Guid, UserActivity>(),
                [Office],
                [Episode(Office, days: 400), Episode(Office, days: 3), Episode(Office, days: 90)]);

            Assert.Equal(3, result[Office].DaysSinceLastPlayed);
        }

        /// <summary>
        /// An unplayed episode must not drag the series' date backwards — it has no
        /// date of its own to contribute.
        /// </summary>
        [Fact]
        public void UnplayedEpisodesDoNotDateTheSeries()
        {
            var result = SeriesActivityRollup.Apply(
                new Dictionary<Guid, UserActivity>(),
                [Office],
                [Episode(Office, days: 30), Episode(Office, played: false, playCount: 0, days: 0)]);

            Assert.Equal(30, result[Office].DaysSinceLastPlayed);
        }

        /// <summary>
        /// Favourite and rating are set deliberately on the show itself, so the
        /// series' own entry keeps them; only the watch half is replaced.
        /// </summary>
        [Fact]
        public void TheSeriesOwnFavouriteAndRatingSurviveTheRollup()
        {
            var own = new Dictionary<Guid, UserActivity>
            {
                [Office] = new() { IsFavorite = true, UserRating = 9f },
            };

            var result = SeriesActivityRollup.Apply(own, [Office], [Episode(Office)]);

            Assert.True(result[Office].IsFavorite);
            Assert.Equal(9f, result[Office].UserRating);
            Assert.Equal(1, result[Office].EpisodesPlayed);
        }

        [Fact]
        public void AFavouritedEpisodeMarksTheSeriesAFavourite()
        {
            var result = SeriesActivityRollup.Apply(
                new Dictionary<Guid, UserActivity>(),
                [Office],
                [Episode(Office, favorite: true)]);

            Assert.True(result[Office].IsFavorite);
        }

        /// <summary>
        /// A show sitting unplayed in the library is the default state, exactly as an
        /// unwatched movie is, and must not become an entry the prompt renders.
        /// </summary>
        [Fact]
        public void ASeriesWithNoEpisodePlayed_GetsNoEntry()
        {
            var result = SeriesActivityRollup.Apply(
                new Dictionary<Guid, UserActivity>(),
                [Office],
                [Episode(Office, played: false, playCount: 0), Episode(Office, played: false, playCount: 0)]);

            Assert.False(result.ContainsKey(Office));
        }

        [Fact]
        public void EpisodesOfASeriesOutsideTheScan_AreIgnored()
        {
            var result = SeriesActivityRollup.Apply(
                new Dictionary<Guid, UserActivity>(),
                [Office],
                [Episode(Office), Episode(Friends), Episode(Guid.Empty)]);

            Assert.True(result.ContainsKey(Office));
            Assert.False(result.ContainsKey(Friends));
            Assert.False(result.ContainsKey(Guid.Empty));
        }

        [Fact]
        public void MovieEntriesAreLeftExactlyAsTheyWere()
        {
            var own = new Dictionary<Guid, UserActivity>
            {
                [Movie] = new() { Played = true, PlayCount = 4, DaysSinceLastPlayed = 12 },
            };

            var result = SeriesActivityRollup.Apply(own, [Office], [Episode(Office)]);

            Assert.Equal(own[Movie], result[Movie]);
            Assert.Null(result[Movie].EpisodesPlayed);
        }

        [Fact]
        public void EachSeriesIsAggregatedSeparately()
        {
            var result = SeriesActivityRollup.Apply(
                new Dictionary<Guid, UserActivity>(),
                [Office, Friends],
                [Episode(Office), Episode(Office), Episode(Friends)]);

            Assert.Equal(2, result[Office].EpisodesPlayed);
            Assert.Equal(1, result[Friends].EpisodesPlayed);
        }
    }
}
