using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Context;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Turning cached affinities plus "it is raining and it is evening" into a row.
    ///
    /// This runs inside the request that draws the home screen, so everything here
    /// is arithmetic over data already bought — the same bargain hard rule 15 makes
    /// for recommendations.
    /// </summary>
    public class ContextRankerTests
    {
        private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        private static readonly Guid C = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
        private static readonly Guid D = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
        private static readonly Guid E = Guid.Parse("eeeeeeee-0000-0000-0000-000000000005");

        private static ItemContextAffinity Weather(params string[] words) => new(words, []);

        private static ItemContextAffinity Dayparts(params string[] words) => new([], words);

        private static ViewingContext RainyEvening()
            => new(["rain"], Daypart.Evening);

        [Fact]
        public void OnlyItemsClaimingTheCurrentWeatherAppear()
        {
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("rain"),
                [B] = Weather("clear"),
                [C] = Weather("rain", "cold"),
                [D] = Weather("rain"),
                [E] = ItemContextAffinity.None,
            };

            var row = ContextRanker.Rank([A, B, C, D, E], affinities, RainyEvening(), ContextRowKind.Weather, 0);

            Assert.Equal([A, C, D], row);
        }

        [Fact]
        public void AStrongerMatchLeadsTheRowHoweverTheViewerRanksIt()
        {
            // Cold AND snowy beats merely snowy, even from further down the list.
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("snow"),
                [B] = Weather("snow"),
                [C] = Weather("snow", "cold"),
            };

            var row = ContextRanker.Rank(
                [A, B, C],
                affinities,
                new ViewingContext(["snow", "cold"], Daypart.LateNight),
                ContextRowKind.Weather,
                0);

            Assert.Equal([C, A, B], row);
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

            Assert.Equal([C, A, B], ContextRanker.Rank([C, A, B], affinities, RainyEvening(), ContextRowKind.Weather, 0));
            Assert.Equal([B, C, A], ContextRanker.Rank([B, C, A], affinities, RainyEvening(), ContextRowKind.Weather, 0));
        }

        [Fact]
        public void TheDaypartRowMatchesTheHourAndIgnoresTheWeather()
        {
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Dayparts("evening", "latenight"),
                [B] = Dayparts("morning"),
                [C] = Dayparts("evening"),
                [D] = Dayparts("evening"),
                [E] = Weather("rain"),
            };

            var row = ContextRanker.Rank([A, B, C, D, E], affinities, RainyEvening(), ContextRowKind.Daypart, 0);

            Assert.Equal([A, C, D], row);
        }

        [Fact]
        public void AWeatherRowWithNoReadingIsNotDrawnAtAll()
        {
            // It must not quietly fall back to the clock. A row claiming to answer
            // the weather while answering something else is worse than no row.
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("rain"),
                [B] = Weather("rain"),
                [C] = Weather("rain"),
            };

            var row = ContextRanker.Rank(
                [A, B, C],
                affinities,
                ViewingContext.ClockOnly(Daypart.Evening),
                ContextRowKind.Weather,
                0);

            Assert.Empty(row);
        }

        [Fact]
        public void TheDaypartRowStillWorksWithNoWeatherReading()
        {
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Dayparts("evening"),
                [B] = Dayparts("evening"),
                [C] = Dayparts("evening"),
            };

            var row = ContextRanker.Rank(
                [A, B, C],
                affinities,
                ViewingContext.ClockOnly(Daypart.Evening),
                ContextRowKind.Daypart,
                0);

            Assert.Equal(3, row.Count);
        }

        [Fact]
        public void TooFewMatchesDrawNoRowRatherThanAStub()
        {
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("rain"),
                [B] = Weather("rain"),
            };

            Assert.Empty(ContextRanker.Rank([A, B], affinities, RainyEvening(), ContextRowKind.Weather, 0));
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
                ContextRanker.Rank([A, B, C], affinities, RainyEvening(), ContextRowKind.Weather, 0).Count);
        }

        [Fact]
        public void TheCapIsAppliedAfterOrdering()
        {
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("rain"),
                [B] = Weather("rain"),
                [C] = Weather("rain", "cold"),
                [D] = Weather("rain"),
            };

            var row = ContextRanker.Rank(
                [A, B, C, D],
                affinities,
                new ViewingContext(["rain", "cold"], Daypart.Evening),
                ContextRowKind.Weather,
                maxItems: 2);

            // C is the strongest fit, so the cap must not cut it in favour of A and B.
            Assert.Equal([C, A], row);
        }

        [Fact]
        public void AnItemWithNoStoredAffinityIsNeverInvented()
        {
            // The library is only partly classified while the condensing pass works
            // through it. An unclassified item is unknown, not a match.
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("rain"),
                [B] = Weather("rain"),
                [C] = Weather("rain"),
            };

            var row = ContextRanker.Rank([D, E, A, B, C], affinities, RainyEvening(), ContextRowKind.Weather, 0);

            Assert.Equal([A, B, C], row);
        }

        [Fact]
        public void MatchingIgnoresCase()
        {
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("RAIN"),
                [B] = Weather("Rain"),
                [C] = Weather("rain"),
            };

            Assert.Equal(3, ContextRanker.Rank([A, B, C], affinities, RainyEvening(), ContextRowKind.Weather, 0).Count);
        }

        [Fact]
        public void AnItemIsNeverCountedTwiceForOneWord()
        {
            // A duplicated word in a stored affinity must not outrank a genuine
            // two-word match.
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("rain", "rain", "rain"),
                [B] = Weather("rain", "cold"),
                [C] = Weather("rain"),
            };

            var row = ContextRanker.Rank(
                [A, B, C],
                affinities,
                new ViewingContext(["rain", "cold"], Daypart.Evening),
                ContextRowKind.Weather,
                0);

            Assert.Equal(B, row.First());
        }

        [Fact]
        public void AnEmptyLibraryDrawsNothing()
        {
            Assert.Empty(ContextRanker.Rank(
                [],
                new Dictionary<Guid, ItemContextAffinity>(),
                RainyEvening(),
                ContextRowKind.Weather,
                0));
        }

        // ---- stand-ins, so a rare condition still draws a row ----

        [Fact]
        public void AThunderstormFallsBackToRainWhenTooFewFilmsSuitThunder()
        {
            // The case this exists for. "storm" is a word few films earn, so a strict
            // row would be empty exactly when the weather is most dramatic.
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("storm"),
                [B] = Weather("rain"),
                [C] = Weather("rain"),
                [D] = Weather("clear"),
            };

            var row = ContextRanker.Rank(
                [A, B, C, D],
                affinities,
                new ViewingContext(["storm"], Daypart.Evening),
                ContextRowKind.Weather,
                0);

            // Thunder leads; rain stands in behind it; a clear-sky film is not weather.
            Assert.Equal([A, B, C], row);
        }

        [Fact]
        public void StandInsNeverOutrankAGenuineMatch()
        {
            // Rain may stand in for thunder. It may not lead a thunderstorm row.
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("rain"),
                [B] = Weather("rain"),
                [C] = Weather("storm"),
            };

            var row = ContextRanker.Rank(
                [A, B, C],
                affinities,
                new ViewingContext(["storm"], Daypart.Evening),
                ContextRowKind.Weather,
                0);

            Assert.Equal(C, row.First());
        }

        [Fact]
        public void AWellStockedConditionIsNeverDiluted()
        {
            // Stand-ins are consulted only when the exact matches cannot fill a row.
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("rain"),
                [B] = Weather("rain"),
                [C] = Weather("rain"),
                [D] = Weather("cloudy"),
                [E] = Weather("cloudy"),
            };

            var row = ContextRanker.Rank(
                [A, B, C, D, E],
                affinities,
                new ViewingContext(["rain"], Daypart.Evening),
                ContextRowKind.Weather,
                0);

            Assert.Equal([A, B, C], row);
        }

        [Fact]
        public void AStandInCannotRescueARowThatIsStillTooShort()
        {
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("storm"),
                [B] = Weather("rain"),
            };

            Assert.Empty(ContextRanker.Rank(
                [A, B],
                affinities,
                new ViewingContext(["storm"], Daypart.Evening),
                ContextRowKind.Weather,
                0));
        }

        [Fact]
        public void AWordAlreadyInTheReadingIsAnExactMatchNotAStandIn()
        {
            // A cold snowy evening wants "cold" as a match in its own right, not as
            // snow's stand-in, or an item claiming only cold would rank below one
            // claiming nothing relevant at all.
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Weather("cold"),
                [B] = Weather("snow"),
                [C] = Weather("snow", "cold"),
            };

            var row = ContextRanker.Rank(
                [A, B, C],
                affinities,
                new ViewingContext(["snow", "cold"], Daypart.Evening),
                ContextRowKind.Weather,
                0);

            Assert.Equal(C, row.First());
            Assert.Equal(3, row.Count);
        }

        [Fact]
        public void TheDaypartRowNeverUsesStandIns()
        {
            // There is no "nearly evening". The four dayparts are exhaustive and
            // adjacent ones mean genuinely different things.
            var affinities = new Dictionary<Guid, ItemContextAffinity>
            {
                [A] = Dayparts("morning"),
                [B] = Dayparts("morning"),
                [C] = Dayparts("morning"),
            };

            Assert.Empty(ContextRanker.Rank(
                [A, B, C],
                affinities,
                ViewingContext.ClockOnly(Daypart.LateNight),
                ContextRowKind.Daypart,
                0));
        }

    }
}
