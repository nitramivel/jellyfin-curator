using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Core.Recommendations;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Ordering for the per-viewer recommendation playlist.
    ///
    /// The list feeds a spotlight row, so the head of it is the whole product: the
    /// first few entries are all most viewers will ever see. The properties worth
    /// pinning are that unseen content leads, that turning up in several of
    /// someone's threads outranks topping one of them, and that the order does not
    /// reshuffle between runs for no reason.
    /// </summary>
    public class RecommendationRankerTests
    {
        private static readonly Guid A = new("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid B = new("bbbbbbbb-0000-0000-0000-000000000002");
        private static readonly Guid C = new("cccccccc-0000-0000-0000-000000000003");
        private static readonly Guid D = new("dddddddd-0000-0000-0000-000000000004");

        private static readonly RecommendationOptions Default = new(MaxItems: 0, IncludeWatched: true);

        private static Dictionary<Guid, UserActivity> NoActivity() => [];

        private static Dictionary<Guid, UserActivity> Watched(params Guid[] ids)
            => ids.ToDictionary(id => id, _ => new UserActivity { Played = true, PlayCount = 1 });

        [Fact]
        public void Rank_PutsUnwatchedAheadOfWatchedWhateverTheScore()
        {
            // The tier is absolute. A watched item at the top of three personal
            // categories must still sit below something unseen, or the row stops
            // being a recommendation and becomes a history.
            var categories = new[]
            {
                new RankedCategory([A], IsPersonal: true),
                new RankedCategory([A], IsPersonal: true),
                new RankedCategory([A], IsPersonal: true),
                new RankedCategory([B], IsPersonal: false),
            };

            var ranked = RecommendationRanker.Rank(categories, Watched(A), Default);

            Assert.Equal([B, A], ranked);
        }

        [Fact]
        public void Rank_RewardsAppearingInSeveralCategories()
        {
            // The signal a single category cannot express.
            var categories = new[]
            {
                new RankedCategory([A, B], IsPersonal: false),
                new RankedCategory([B], IsPersonal: false),
                new RankedCategory([B], IsPersonal: false),
            };

            var ranked = RecommendationRanker.Rank(categories, NoActivity(), Default);

            Assert.Equal(B, ranked[0]);
        }

        [Fact]
        public void Rank_WeightsAPersonalCategoryAboveASharedOne()
        {
            var categories = new[]
            {
                new RankedCategory([A], IsPersonal: false),
                new RankedCategory([B], IsPersonal: true),
            };

            var ranked = RecommendationRanker.Rank(categories, NoActivity(), Default);

            Assert.Equal([B, A], ranked);
        }

        [Fact]
        public void Rank_RespectsPositionWithinACategory()
        {
            var categories = new[] { new RankedCategory([A, B, C], IsPersonal: false) };

            var ranked = RecommendationRanker.Rank(categories, NoActivity(), Default);

            Assert.Equal([A, B, C], ranked);
        }

        [Fact]
        public void Rank_DoesNotLetOneLongCategoryOutrankRealOverlap()
        {
            // Position falls off gently on purpose. An item in two threads should
            // beat the head of a single long one; if the falloff were steep, one
            // twenty-item category would own the entire head of the list.
            var longCategory = new RankedCategory([A, .. Enumerable.Range(0, 19).Select(_ => Guid.NewGuid())], IsPersonal: false);
            var categories = new[]
            {
                longCategory,
                new RankedCategory([B], IsPersonal: false),
                new RankedCategory([B], IsPersonal: false),
            };

            var ranked = RecommendationRanker.Rank(categories, NoActivity(), Default);

            Assert.Equal(B, ranked[0]);
            Assert.Equal(A, ranked[1]);
        }

        [Fact]
        public void Rank_CountsASeriesAsWatchedByItsEpisodes()
        {
            // A series' own user data never carries watch history. Reading only
            // PlayCount would recommend every show the viewer has worked through.
            var activity = new Dictionary<Guid, UserActivity>
            {
                [A] = new UserActivity { EpisodesPlayed = 140, EpisodeCount = 201 },
            };

            var categories = new[]
            {
                new RankedCategory([A], IsPersonal: true),
                new RankedCategory([B], IsPersonal: false),
            };

            var ranked = RecommendationRanker.Rank(categories, activity, Default);

            Assert.Equal([B, A], ranked);
        }

        [Fact]
        public void Rank_TreatsAnUnstartedSeriesAsUnwatched()
        {
            var activity = new Dictionary<Guid, UserActivity>
            {
                [A] = new UserActivity { EpisodesPlayed = 0, EpisodeCount = 60 },
            };

            var ranked = RecommendationRanker.Rank(
                [new RankedCategory([A], IsPersonal: false)],
                activity,
                Default);

            Assert.Equal([A], ranked);
        }

        [Fact]
        public void Rank_LeadsTheWatchedTailWithFavourites()
        {
            var activity = new Dictionary<Guid, UserActivity>
            {
                [A] = new UserActivity { Played = true, PlayCount = 1 },
                [B] = new UserActivity { Played = true, PlayCount = 1, IsFavorite = true },
            };

            var categories = new[]
            {
                new RankedCategory([A], IsPersonal: false),
                new RankedCategory([B], IsPersonal: false),
                new RankedCategory([C], IsPersonal: false),
            };

            var ranked = RecommendationRanker.Rank(categories, activity, Default);

            Assert.Equal([C, B, A], ranked);
        }

        [Fact]
        public void Rank_CanExcludeWatchedEntirely()
        {
            var categories = new[]
            {
                new RankedCategory([A, B], IsPersonal: false),
            };

            var ranked = RecommendationRanker.Rank(
                categories,
                Watched(A),
                new RecommendationOptions(MaxItems: 0, IncludeWatched: false));

            Assert.Equal([B], ranked);
        }

        [Fact]
        public void Rank_AppliesTheLengthCap()
        {
            var categories = new[] { new RankedCategory([A, B, C, D], IsPersonal: false) };

            var ranked = RecommendationRanker.Rank(
                categories,
                NoActivity(),
                new RecommendationOptions(MaxItems: 2, IncludeWatched: true));

            Assert.Equal([A, B], ranked);
        }

        [Fact]
        public void Rank_NeverRepeatsAnItemThatIsInSeveralCategories()
        {
            var categories = new[]
            {
                new RankedCategory([A, B], IsPersonal: false),
                new RankedCategory([B, A], IsPersonal: true),
            };

            var ranked = RecommendationRanker.Rank(categories, NoActivity(), Default);

            Assert.Equal(2, ranked.Count);
            Assert.Equal(ranked.Count, ranked.Distinct().Count());
        }

        [Fact]
        public void Rank_IsStableBetweenIdenticalRuns()
        {
            // Two items on an identical score must not swap places run to run, or
            // the spotlight row visibly reshuffles itself for no reason.
            var categories = new[] { new RankedCategory([A, B, C, D], IsPersonal: false) };

            var first = RecommendationRanker.Rank(categories, NoActivity(), Default);
            var second = RecommendationRanker.Rank(categories, NoActivity(), Default);

            Assert.Equal(first, second);
        }

        [Fact]
        public void Rank_ReturnsNothingForAViewerWithNoCategories()
        {
            Assert.Empty(RecommendationRanker.Rank([], NoActivity(), Default));
            Assert.Empty(RecommendationRanker.Rank(
                [new RankedCategory([], IsPersonal: true)],
                NoActivity(),
                Default));
        }

        [Fact]
        public void IdentityFor_IsStableAndPerUser()
        {
            // This is the playlist's identity across runs, and nothing stores it —
            // so if it were not deterministic, every run would orphan the previous
            // playlist and build a duplicate.
            var user = Guid.NewGuid();
            var other = Guid.NewGuid();

            Assert.Equal(RecommendationRanker.IdentityFor(user), RecommendationRanker.IdentityFor(user));
            Assert.NotEqual(RecommendationRanker.IdentityFor(user), RecommendationRanker.IdentityFor(other));
            Assert.NotEqual(Guid.Empty, RecommendationRanker.IdentityFor(user));
        }
    }
}
