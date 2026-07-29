using System;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    public class ItemReducerTests
    {
        private static Movie FullMovie() => new()
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Blade Runner",
            ProductionYear = 1982,
            Genres = ["Science Fiction", "Thriller"],
            Tags = ["neo-noir", "dystopia"],
            OfficialRating = "R",
            RunTimeTicks = TimeSpan.FromMinutes(117).Ticks,
            CommunityRating = 8.1f,
            Overview = "A blade runner must pursue and terminate four replicants who stole a ship in space and have returned to Earth to find their creator.",
        };

        [Fact]
        public void Reduce_Movie_MapsAllFields()
        {
            var record = ItemReducer.Reduce(FullMovie());

            Assert.NotNull(record);
            Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), record.Id);
            Assert.Equal(MediaKind.Movie, record.Kind);
            Assert.Equal("Blade Runner", record.Name);
            Assert.Equal(1982, record.Year);
            Assert.Equal(["Science Fiction", "Thriller"], record.Genres);
            Assert.Equal(["neo-noir", "dystopia"], record.Tags);
            Assert.Equal("R", record.OfficialRating);
            Assert.Equal(117, record.RuntimeMinutes);
            Assert.Equal(8.1f, record.CommunityRating);
            Assert.NotNull(record.Overview);
            Assert.Null(record.SeriesName);
            Assert.Null(record.SeasonNumber);
        }

        [Fact]
        public void Reduce_Movie_WithOnlyName_MapsToNulls()
        {
            var movie = new Movie { Id = Guid.NewGuid(), Name = "Bare" };

            var record = ItemReducer.Reduce(movie);

            Assert.NotNull(record);
            Assert.Null(record.Year);
            Assert.Empty(record.Genres);
            Assert.Empty(record.Tags);
            Assert.Null(record.OfficialRating);
            Assert.Null(record.RuntimeMinutes);
            Assert.Null(record.CommunityRating);
            Assert.Null(record.Overview);
        }

        [Fact]
        public void Reduce_Series_MapsKind()
        {
            var series = new Series
            {
                Id = Guid.NewGuid(),
                Name = "The Wire",
                ProductionYear = 2002,
                Genres = ["Crime", "Drama"],
            };

            var record = ItemReducer.Reduce(series);

            Assert.NotNull(record);
            Assert.Equal(MediaKind.Series, record.Kind);
            Assert.Equal("The Wire", record.Name);
        }

        [Fact]
        public void Reduce_Episode_MapsSeriesLinkage()
        {
            var seriesId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var episode = new Episode
            {
                Id = Guid.NewGuid(),
                Name = "Fly",
                SeriesName = "Breaking Bad",
                SeriesId = seriesId,
                ParentIndexNumber = 3,
                IndexNumber = 10,
                Overview = "A fly loose in the lab drives Walt to obsession.",
            };

            var record = ItemReducer.Reduce(episode);

            Assert.NotNull(record);
            Assert.Equal(MediaKind.Episode, record.Kind);
            Assert.Equal("Fly", record.Name);
            Assert.Equal("Breaking Bad", record.SeriesName);
            Assert.Equal(seriesId, record.SeriesId);
            Assert.Equal(3, record.SeasonNumber);
            Assert.Equal(10, record.EpisodeNumber);
        }

        [Fact]
        public void Reduce_Episode_EmptySeriesId_BecomesNull()
        {
            var episode = new Episode { Id = Guid.NewGuid(), Name = "Orphan", SeriesId = Guid.Empty };

            var record = ItemReducer.Reduce(episode);

            Assert.NotNull(record);
            Assert.Null(record.SeriesId);
            Assert.Null(record.SeriesName);
        }

        [Fact]
        public void Reduce_NamelessItem_IsSkipped()
        {
            Assert.Null(ItemReducer.Reduce(new Movie { Id = Guid.NewGuid(), Name = "   " }));
            Assert.Null(ItemReducer.Reduce(new Movie { Id = Guid.NewGuid() }));
        }

        [Fact]
        public void Reduce_UnsupportedKind_IsSkipped()
        {
            var video = new Video { Id = Guid.NewGuid(), Name = "Home Video" };

            Assert.Null(ItemReducer.Reduce(video));
        }

        [Fact]
        public void Reduce_TrimsNameAndFiltersBlankGenresAndTags()
        {
            var movie = new Movie
            {
                Id = Guid.NewGuid(),
                Name = "  Heat  ",
                Genres = ["Crime", "", "  "],
                Tags = ["", "heist"],
            };

            var record = ItemReducer.Reduce(movie);

            Assert.NotNull(record);
            Assert.Equal("Heat", record.Name);
            Assert.Equal(["Crime"], record.Genres);
            Assert.Equal(["heist"], record.Tags);
        }

        [Fact]
        public void Reduce_LongOverview_IsTruncatedAtWordBoundary()
        {
            var movie = FullMovie();
            movie.Overview = string.Join(" ", new string('x', 50), new string('y', 300), new string('z', 50));

            var record = ItemReducer.Reduce(movie, maxOverviewLength: 100);

            Assert.NotNull(record);
            Assert.NotNull(record.Overview);
            Assert.True(record.Overview.Length <= 100, $"overview was {record.Overview.Length} chars");
            Assert.EndsWith("…", record.Overview, StringComparison.Ordinal);
            Assert.Equal(new string('x', 50) + "…", record.Overview);
        }

        [Fact]
        public void TruncateOverview_ShortText_IsUnchanged()
        {
            Assert.Equal("Short.", ItemReducer.TruncateOverview("Short.", 300));
        }

        [Fact]
        public void TruncateOverview_ExactLimit_IsUnchanged()
        {
            var text = new string('a', 300);
            Assert.Equal(text, ItemReducer.TruncateOverview(text, 300));
        }

        [Fact]
        public void TruncateOverview_NoSpaces_HardCutsWithEllipsis()
        {
            var result = ItemReducer.TruncateOverview(new string('a', 400), 100);

            Assert.NotNull(result);
            Assert.Equal(100, result.Length);
            Assert.EndsWith("…", result, StringComparison.Ordinal);
        }

        [Fact]
        public void TruncateOverview_Whitespace_ReturnsNull()
        {
            Assert.Null(ItemReducer.TruncateOverview(null, 300));
            Assert.Null(ItemReducer.TruncateOverview("   ", 300));
        }

        [Fact]
        public void Reduce_ZeroRuntimeTicks_BecomesNull()
        {
            var movie = FullMovie();
            movie.RunTimeTicks = 0;

            var record = ItemReducer.Reduce(movie);

            Assert.NotNull(record);
            Assert.Null(record.RuntimeMinutes);
        }
    }
}
