using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Context;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Turning cached affinities plus "it is raining and it is evening" into one row.
    ///
    /// <para>
    /// The grading is the subject of most of these. A strict row demanding both the
    /// sky and the hour was measured against a real 202-item library and found to
    /// describe <b>one</b> film for "cloudy and morning" and none for "rain and
    /// morning" — so it would have been absent every morning. What the grading has
    /// to buy is drawability without dishonesty: the row may include an item that
    /// only suits the hour, but such an item must never lead over one that suits both.
    /// </para>
    ///
    /// <para>
    /// It also runs inside the request that draws the home screen, so everything
    /// here is arithmetic over data already bought — the bargain hard rule 15 makes
    /// for recommendations.
    /// </para>
    /// </summary>
    public class ContextRankerTests
    {
        private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        private static readonly Guid C = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
        private static readonly Guid D = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
        private static readonly Guid E = Guid.Parse("eeeeeeee-0000-0000-0000-000000000005");

        private static ItemContextAffinity Suits(string[] weather, string[] dayparts)
            => new(weather, dayparts);

        private static ItemContextAffinity Weather(params string[] words) => new(words, []);

        private static ItemContextAffinity Dayparts(params string[] words) => new([], words);

        private static ViewingContext RainyEvening() => new(["rain"], Daypart.Evening);

        [Fact]
        public void SomethingSuitingBothLeadsTheRow()
        {
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("rain"),
                [B] = Dayparts("evening"),
                [C] = Suits(["rain"], ["evening"]),
            };

            var row = ContextRanker.Rank([A, B, C], affinities, RainyEvening(), 0);

            Assert.Equal(C, row.First());
            Assert.Equal(3, row.Count);
        }

        [Fact]
        public void WeatherOutranksTheHourOnItsOwn()
        {
            // Four dayparts, and the busiest holds a third of a library — so suiting
            // the evening says less about the moment than suiting the rain does.
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Dayparts("evening"),
                [B] = Weather("rain"),
                [C] = Dayparts("evening"),
            };

            Assert.Equal(B, ContextRanker.Rank([A, B, C], affinities, RainyEvening(), 0).First());
        }

        [Fact]
        public void TheMorningRowStillDrawsWhenAlmostNothingSuitsAMorning()
        {
            // The measurement that forced the grading: on a real library "cloudy and
            // morning" described one film. A strict row would be absent every
            // morning, which is a time somebody plausibly opens Jellyfin.
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Suits(["cloudy"], ["morning"]),
                [B] = Weather("cloudy"),
                [C] = Weather("cloudy"),
                [D] = Dayparts("morning"),
            };

            var row = ContextRanker.Rank(
                [A, B, C, D], affinities, new ViewingContext(["cloudy"], Daypart.Morning), 0);

            Assert.Equal(A, row.First());
            Assert.Equal(4, row.Count);
        }

        [Fact]
        public void MoreWeatherWordsMatchedRanksHigher()
        {
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("rain"),
                [B] = Weather("rain", "cold"),
                [C] = Weather("cold"),
            };

            var row = ContextRanker.Rank(
                [A, B, C], affinities, new ViewingContext(["rain", "cold"], Daypart.Evening), 0);

            Assert.Equal(B, row.First());
        }

        [Fact]
        public void EqualFitFallsBackToTheViewersOwnOrder()
        {
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("rain"),
                [B] = Weather("rain"),
                [C] = Weather("rain"),
            };

            Assert.Equal([C, A, B], ContextRanker.Rank([C, A, B], affinities, RainyEvening(), 0));
            Assert.Equal([B, C, A], ContextRanker.Rank([B, C, A], affinities, RainyEvening(), 0));
        }

        [Fact]
        public void AnItemSuitingNeitherIsLeftOut()
        {
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("rain"),
                [B] = Weather("rain"),
                [C] = Dayparts("evening"),
                [D] = Suits(["snow"], ["morning"]),
                [E] = ItemContextAffinity.None,
            };

            var row = ContextRanker.Rank([A, B, C, D, E], affinities, RainyEvening(), 0);

            Assert.Equal([A, B, C], row);
        }

        [Fact]
        public void AnItemWithNoStoredAffinityIsNeverInvented()
        {
            // The library is only partly classified while the condensing pass works
            // through it. Unclassified is unknown, not a match.
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("rain"),
                [B] = Weather("rain"),
                [C] = Weather("rain"),
            };

            Assert.Equal([A, B, C], ContextRanker.Rank([D, E, A, B, C], affinities, RainyEvening(), 0));
        }

        // ---- the row survives a missing weather reading ----

        [Fact]
        public void WithNoReadingTheRowIsDrawnFromTheClockAlone()
        {
            // Unlike the weather row it replaced, this one has a second half to stand
            // on. A server that cannot reach Open-Meteo loses precision, not the row.
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Dayparts("evening"),
                [B] = Dayparts("evening"),
                [C] = Dayparts("evening"),
                [D] = Weather("rain"),
            };

            var row = ContextRanker.Rank(
                [A, B, C, D], affinities, ViewingContext.ClockOnly(Daypart.Evening), 0);

            Assert.Equal([A, B, C], row);
        }

        // ---- stand-ins, so a rare sky still contributes ----

        [Fact]
        public void ThunderFallsBackToRainSuitedTitles()
        {
            // "storm" is a word few films earn. Without a stand-in a thunderstorm
            // would contribute nothing at all to the row.
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("rain"),
                [B] = Weather("rain"),
                [C] = Weather("storm"),
            };

            var row = ContextRanker.Rank(
                [A, B, C], affinities, new ViewingContext(["storm"], Daypart.Evening), 0);

            Assert.Equal(C, row.First());
            Assert.Equal(3, row.Count);
        }

        [Fact]
        public void AStandInNeverOutranksTheHour()
        {
            // Rain standing in for thunder is worth less than genuinely suiting the
            // evening, because the stand-in is a guess and the daypart is not.
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("rain"),
                [B] = Dayparts("evening"),
                [C] = Dayparts("evening"),
            };

            var row = ContextRanker.Rank(
                [A, B, C], affinities, new ViewingContext(["storm"], Daypart.Evening), 0);

            Assert.Equal(A, row.Last());
        }

        [Fact]
        public void AWordAlreadyInTheReadingIsAnExactMatchNotAStandIn()
        {
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("cold"),
                [B] = Weather("snow"),
                [C] = Weather("snow", "cold"),
            };

            var row = ContextRanker.Rank(
                [A, B, C], affinities, new ViewingContext(["snow", "cold"], Daypart.Evening), 0);

            Assert.Equal(C, row.First());
            Assert.Equal(3, row.Count);
        }

        // ---- length ----

        [Fact]
        public void TooFewMatchesDrawNoRowRatherThanAStub()
        {
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("rain"),
                [B] = Weather("rain"),
            };

            Assert.Empty(ContextRanker.Rank([A, B], affinities, RainyEvening(), 0));
        }

        [Fact]
        public void ExactlyTheMinimumIsEnough()
        {
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("rain"),
                [B] = Weather("rain"),
                [C] = Weather("rain"),
            };

            Assert.Equal(
                ContextRanker.MinimumRowLength,
                ContextRanker.Rank([A, B, C], affinities, RainyEvening(), 0).Count);
        }

        [Fact]
        public void TheCapIsAppliedAfterOrdering()
        {
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("rain"),
                [B] = Dayparts("evening"),
                [C] = Suits(["rain"], ["evening"]),
                [D] = Weather("rain"),
            };

            var row = ContextRanker.Rank([A, B, C, D], affinities, RainyEvening(), maxItems: 2);

            // The best fit must survive the cut.
            Assert.Equal([C, A], row);
        }

        [Fact]
        public void MatchingIgnoresCase()
        {
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = new(["RAIN"], ["EVENING"]),
                [B] = Weather("Rain"),
                [C] = Weather("rain"),
            };

            Assert.Equal(3, ContextRanker.Rank([A, B, C], affinities, RainyEvening(), 0).Count);
        }

        [Fact]
        public void AnEmptyLibraryDrawsNothing()
        {
            Assert.Empty(ContextRanker.Rank(
                [], new Dictionary<Guid, ItemContextAffinity>(), RainyEvening(), 0));
        }
    }
}
