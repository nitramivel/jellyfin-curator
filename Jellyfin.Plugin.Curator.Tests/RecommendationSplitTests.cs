using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Core.Recommendations;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Cutting one viewer's ranked recommendations into per-type lists.
    ///
    /// <para>
    /// Two properties carry this feature. The split must be a <em>filter over one
    /// ranking</em> rather than a second ranking, because an item's score comes from
    /// how many of the viewer's threads it appears in and those threads mix films
    /// and television by design. And the combined list must come out of it byte for
    /// byte unchanged, because that is the one a viewer already has and the one
    /// Media Bar is pointed at.
    /// </para>
    /// </summary>
    public class RecommendationSplitTests
    {
        private static Guid Id(int n)
        {
            var bytes = new byte[16];
            bytes[0] = (byte)n;
            return new Guid(bytes);
        }

        /// <summary>Alternating film, series, episode, film, ...</summary>
        private static (List<Guid> Ranked, Dictionary<Guid, MediaKind> Kinds) Library(int count)
        {
            var ranked = new List<Guid>();
            var kinds = new Dictionary<Guid, MediaKind>();
            for (var i = 0; i < count; i++)
            {
                var id = Id(i + 1);
                ranked.Add(id);
                kinds[id] = (MediaKind)(i % 3);
            }

            return (ranked, kinds);
        }

        [Fact]
        public void TheCombinedListIsTheRankingUntouched()
        {
            var (ranked, kinds) = Library(9);

            var combined = RecommendationSplit.Select(RecommendationScope.Combined, ranked, kinds, maxItems: 0);

            Assert.Equal(ranked, combined);
        }

        [Fact]
        public void TheCombinedListIgnoresKindsEntirely()
        {
            var (ranked, _) = Library(6);

            // Nothing is classified, which is what the free path hands in when the
            // split is off. The combined list must not be filtered down to nothing.
            var combined = RecommendationSplit.Select(
                RecommendationScope.Combined, ranked, new Dictionary<Guid, MediaKind>(), maxItems: 0);

            Assert.Equal(ranked, combined);
        }

        [Fact]
        public void FilmsAndTelevisionKeepTheOrderTheyWereRankedIn()
        {
            var (ranked, kinds) = Library(9);

            var movies = RecommendationSplit.Select(RecommendationScope.Movies, ranked, kinds, maxItems: 0);
            var shows = RecommendationSplit.Select(RecommendationScope.Shows, ranked, kinds, maxItems: 0);

            Assert.Equal([Id(1), Id(4), Id(7)], movies);
            Assert.Equal([Id(2), Id(3), Id(5), Id(6), Id(8), Id(9)], shows);

            // The relative order within each list is the ranking's, not a re-sort.
            Assert.Equal(movies, movies.OrderBy(id => ranked.IndexOf(id)).ToList());
            Assert.Equal(shows, shows.OrderBy(id => ranked.IndexOf(id)).ToList());
        }

        [Fact]
        public void EpisodesRideWithTheirSeries()
        {
            var series = Id(1);
            var episode = Id(2);
            var movie = Id(3);
            var kinds = new Dictionary<Guid, MediaKind>
            {
                [series] = MediaKind.Series,
                [episode] = MediaKind.Episode,
                [movie] = MediaKind.Movie,
            };

            var shows = RecommendationSplit.Select(
                RecommendationScope.Shows, [series, episode, movie], kinds, maxItems: 0);

            Assert.Equal([series, episode], shows);
        }

        [Fact]
        public void EveryItemLandsInExactlyOneOfTheTwo()
        {
            var (ranked, kinds) = Library(30);

            var movies = RecommendationSplit.Select(RecommendationScope.Movies, ranked, kinds, maxItems: 0);
            var shows = RecommendationSplit.Select(RecommendationScope.Shows, ranked, kinds, maxItems: 0);

            Assert.Empty(movies.Intersect(shows));
            Assert.Equal(ranked.Count, movies.Count + shows.Count);
        }

        [Fact]
        public void TheCapAppliesToEachListRatherThanToThePool()
        {
            // The reason the pool is ranked uncapped: a library that is mostly films
            // must not produce a television list of whatever few shows happened to
            // reach the top of the combined order.
            var ranked = new List<Guid>();
            var kinds = new Dictionary<Guid, MediaKind>();
            for (var i = 0; i < 100; i++)
            {
                var id = Id(i + 1);
                ranked.Add(id);
                kinds[id] = i < 90 ? MediaKind.Movie : MediaKind.Series;
            }

            var movies = RecommendationSplit.Select(RecommendationScope.Movies, ranked, kinds, maxItems: 20);
            var shows = RecommendationSplit.Select(RecommendationScope.Shows, ranked, kinds, maxItems: 20);

            Assert.Equal(20, movies.Count);
            Assert.Equal(10, shows.Count);
        }

        [Fact]
        public void AnItemThatHasLeftTheLibraryIsDroppedFromThePerTypeLists()
        {
            var here = Id(1);
            var gone = Id(2);
            var kinds = new Dictionary<Guid, MediaKind> { [here] = MediaKind.Movie };

            var movies = RecommendationSplit.Select(RecommendationScope.Movies, [here, gone], kinds, maxItems: 0);
            var shows = RecommendationSplit.Select(RecommendationScope.Shows, [here, gone], kinds, maxItems: 0);

            Assert.Equal([here], movies);
            Assert.Empty(shows);
        }

        /// <summary>
        /// The combined scope's identity must never be renumbered: it is the only
        /// record that an existing "Recommended for You" belongs to Curator, and a
        /// new seed would leave every one of them looking like an orphan to the
        /// sweep that deletes unclaimed Curator playlists.
        /// </summary>
        [Fact]
        public void TheCombinedIdentityIsUnchangedByTheSplitExisting()
        {
            var user = Guid.Parse("11111111-2222-3333-4444-555555555555");

            Assert.Equal(
                RecommendationRanker.IdentityFor(user),
                RecommendationRanker.IdentityFor(user, RecommendationScope.Combined));

            // Pinned as a literal, so a change to the seed fails here rather than on
            // somebody's server. Derived independently of this code from the seed the
            // combined scope has always used:
            //   SHA256("curator-recommendations:" + userId.ToString("N")), first 16
            //   bytes, read as a GUID little-endian.
            Assert.Equal(
                Guid.Parse("ca745a29-e380-79ae-077c-c44fc96bdcf8"),
                RecommendationRanker.IdentityFor(user));
        }

        [Fact]
        public void EachScopeGetsItsOwnIdentityPerViewer()
        {
            var alice = Guid.NewGuid();
            var bob = Guid.NewGuid();

            var identities = RecommendationRanker.AllScopes
                .SelectMany(scope => new[]
                {
                    RecommendationRanker.IdentityFor(alice, scope),
                    RecommendationRanker.IdentityFor(bob, scope),
                })
                .ToList();

            // Six playlists, six tethers. A collision would mean two of a viewer's
            // lists overwriting each other, or one viewer's list answering for
            // another's.
            Assert.Equal(6, identities.Distinct().Count());
        }

        [Fact]
        public void IdentitiesAreStableBetweenRuns()
        {
            var user = Guid.NewGuid();

            foreach (var scope in RecommendationRanker.AllScopes)
            {
                Assert.Equal(
                    RecommendationRanker.IdentityFor(user, scope),
                    RecommendationRanker.IdentityFor(user, scope));
            }
        }
    }
}
