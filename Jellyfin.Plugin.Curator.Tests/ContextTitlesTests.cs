using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Context;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Naming a context row, and keeping the store of names bounded.
    ///
    /// The economics are what these pin. A title is bought once per set of
    /// conditions and rotated afterwards, so the cost is the number of distinct
    /// conditions a place produces rather than the number of refreshes — and the
    /// culling rules exist to keep that store finite without re-buying every winter
    /// what was culled every summer.
    /// </summary>
    public class ContextTitlesTests
    {
        private static ViewingContext Weather(params string[] words)
            => new(words, Daypart.Evening);

        [Fact]
        public void TheWeatherWordsAreSortedSoOneMomentIsOneKey()
        {
            // The order Open-Meteo happens to report them in is not a fact about the
            // weather, and treating it as one would double the conditions bought.
            Assert.Equal(
                ContextTitles.ConditionKey(new ViewingContext(["rain", "cold"], Daypart.Evening)),
                ContextTitles.ConditionKey(new ViewingContext(["cold", "rain"], Daypart.Evening)));
        }

        [Fact]
        public void TheHourIsPartOfTheKeyBecauseItIsPartOfTheTitle()
        {
            // "Rainy Night Cozy Vibes" cannot be reused at eleven in the morning.
            Assert.NotEqual(
                ContextTitles.ConditionKey(new ViewingContext(["rain"], Daypart.Evening)),
                ContextTitles.ConditionKey(new ViewingContext(["rain"], Daypart.Morning)));
        }

        [Fact]
        public void DifferentMomentsGetDifferentKeys()
        {
            var keys = new[]
            {
                ContextTitles.ConditionKey(new ViewingContext(["rain"], Daypart.Evening)),
                ContextTitles.ConditionKey(new ViewingContext(["rain", "cold"], Daypart.Evening)),
                ContextTitles.ConditionKey(new ViewingContext(["snow"], Daypart.Evening)),
                ContextTitles.ConditionKey(new ViewingContext(["rain"], Daypart.LateNight)),
                ContextTitles.ConditionKey(ViewingContext.ClockOnly(Daypart.Evening)),
            };

            Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void AMomentWithNoReadingStillHasAKeyOfItsOwn()
        {
            // The row is drawn from the clock alone when the weather cannot be read,
            // and that moment deserves its own title rather than borrowing a rainy one.
            var key = ContextTitles.ConditionKey(ViewingContext.ClockOnly(Daypart.Morning));

            Assert.True(ContextTitles.IsLiveCondition(key));
            Assert.NotEqual(
                ContextTitles.ConditionKey(new ViewingContext(["clear"], Daypart.Morning)),
                key);
        }

        // ---- rotation ----

        [Fact]
        public void SuccessiveDrawsMoveThroughTheSet()
        {
            var set = new ContextTitleSet("rain|evening", ["A", "B", "C"], 0, DateTime.UtcNow);
            var seen = new List<string>();

            for (var i = 0; i < 3; i++)
            {
                var drawn = ContextTitles.Draw(set, 0, DateTime.UtcNow);
                Assert.NotNull(drawn);
                seen.Add(drawn!.Value.Title);
                set = drawn.Value.Updated;
            }

            Assert.Equal(["A", "B", "C"], seen);
        }

        [Fact]
        public void TheRotationWrapsRatherThanRunningOff()
        {
            var set = new ContextTitleSet("rain|evening", ["A", "B"], 7, DateTime.UtcNow);

            Assert.NotNull(ContextTitles.Draw(set, 0, DateTime.UtcNow));
        }

        [Fact]
        public void TwoViewersInOneConditionDoNotReadTheSameTitle()
        {
            // The moment per-viewer rows exist they share a condition and therefore a
            // set, so without the offset every household would read one title.
            var set = new ContextTitleSet("rain|evening", ["A", "B", "C", "D"], 0, DateTime.UtcNow);

            var first = ContextTitles.Draw(set, 0, DateTime.UtcNow)!.Value.Title;
            var second = ContextTitles.Draw(set, 1, DateTime.UtcNow)!.Value.Title;

            Assert.NotEqual(first, second);
        }

        [Fact]
        public void AViewersOffsetIsStableAcrossProcesses()
        {
            // Not GetHashCode: string and object hashing is randomized per process in
            // .NET, so a viewer's title would change on every server restart.
            var id = Guid.Parse("8a1f0b2c-3d4e-4f50-9617-2b3c4d5e6f70");

            Assert.Equal(ContextTitles.OffsetFor(id), ContextTitles.OffsetFor(id));
            Assert.True(ContextTitles.OffsetFor(id) >= 0);
        }

        [Fact]
        public void AnEmptySetDrawsNothingRatherThanThrowing()
        {
            var set = new ContextTitleSet("rain|evening", [], 0, DateTime.UtcNow);

            Assert.Null(ContextTitles.Draw(set, 0, DateTime.UtcNow));
        }

        [Fact]
        public void ADrawStampsTheSetAsUsedSoCullingCannotTakeIt()
        {
            var stale = DateTime.UtcNow.AddYears(-5);
            var set = new ContextTitleSet("rain|evening", ["A"], 0, stale);
            var now = DateTime.UtcNow;

            var drawn = ContextTitles.Draw(set, 0, now);

            Assert.Equal(now, drawn!.Value.Updated.LastUsedUtc);
        }

        // ---- culling ----

        [Fact]
        public void UnusedTitlesAreCulledAfterTheRetentionWindow()
        {
            var now = DateTime.UtcNow;
            var sets = new[]
            {
                new ContextTitleSet("rain|evening", ["A"], 0, now),
                new ContextTitleSet("snow|evening", ["B"], 0, now.AddDays(-400)),
            };

            var (kept, expired, obsolete) = ContextTitles.Prune(sets, now, retentionDays: 365);

            Assert.Equal("rain|evening", Assert.Single(kept).Condition);
            Assert.Equal(1, expired);
            Assert.Equal(0, obsolete);
        }

        [Fact]
        public void SeasonalTitlesSurviveAnOffSeason()
        {
            // The failure this guards: culling the snowy-evening set in July for
            // going six months unused would re-buy it every winter, which is exactly
            // what the cache exists to prevent.
            var now = DateTime.UtcNow;
            var sets = new[] { new ContextTitleSet("cold,snow|evening", ["A"], 0, now.AddDays(-200)) };

            var (kept, _, _) = ContextTitles.Prune(sets, now);

            Assert.Single(kept);
        }

        [Fact]
        public void ARetentionOfZeroKeepsThemForever()
        {
            var now = DateTime.UtcNow;
            var sets = new[] { new ContextTitleSet("rain|evening", ["A"], 0, now.AddYears(-20)) };

            var (kept, expired, _) = ContextTitles.Prune(sets, now, retentionDays: 0);

            Assert.Single(kept);
            Assert.Equal(0, expired);
        }

        [Fact]
        public void TitlesForADeadConditionOrTheOldTwoRowShapeGoImmediately()
        {
            // Not after a year: these can never match anything again, so waiting
            // serves nobody.
            var now = DateTime.UtcNow;
            var sets = new[]
            {
                new ContextTitleSet("drizzly|evening", ["A"], 0, now),
                new ContextTitleSet("rain|dusk", ["B"], 0, now),
                new ContextTitleSet("weather:rain", ["C"], 0, now),
                new ContextTitleSet("rain|evening", ["D"], 0, now),
            };

            var (kept, expired, obsolete) = ContextTitles.Prune(sets, now);

            Assert.Equal("rain|evening", Assert.Single(kept).Condition);
            Assert.Equal(3, obsolete);
            Assert.Equal(0, expired);
        }

        [Fact]
        public void AnEmptySetIsCulledAsObsolete()
        {
            var now = DateTime.UtcNow;
            var (kept, _, obsolete) = ContextTitles.Prune(
                [new ContextTitleSet("rain|evening", [], 0, now)], now);

            Assert.Empty(kept);
            Assert.Equal(1, obsolete);
        }

        [Theory]
        [InlineData("rain|evening", true)]
        [InlineData("cold,rain|latenight", true)]
        [InlineData("|morning", true)]
        [InlineData("drizzly|evening", false)]
        [InlineData("rain|dusk", false)]
        [InlineData("weather:rain", false)]
        [InlineData("daypart:evening", false)]
        [InlineData("rain", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void OnlyKeysAliveConditionCouldProduceAreLive(string? condition, bool expected)
        {
            Assert.Equal(expected, ContextTitles.IsLiveCondition(condition));
        }

        [Fact]
        public void EveryKeyTheBuilderProducesIsALiveOne()
        {
            // The two halves of the cache have to agree, or every entry written is
            // culled on the next pass.
            foreach (Daypart daypart in Enum.GetValues<Daypart>())
            {
                Assert.True(ContextTitles.IsLiveCondition(
                    ContextTitles.ConditionKey(ViewingContext.ClockOnly(daypart))));

                foreach (var word in ContextVocabulary.Weather)
                {
                    Assert.True(ContextTitles.IsLiveCondition(
                        ContextTitles.ConditionKey(new ViewingContext([word], daypart))));
                }
            }
        }

        // ---- the prompt and what comes back ----

        [Fact]
        public void ThePromptForbidsTheLabelItIsReplacing()
        {
            // A model asked for "a title for a rainy evening" hands back "Rainy
            // Evening Picks" — the setting it replaced, spelled the same, having
            // cost money.
            var prompt = ContextTitlePromptBuilder.BuildSystemPrompt(5);

            Assert.Contains("picks", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("40 characters", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void TheUserPromptNamesBothTheSkyAndTheHour()
        {
            var prompt = ContextTitlePromptBuilder.BuildUserPrompt(
                new ViewingContext(["rain", "cold"], Daypart.LateNight), 5);

            Assert.Contains("rain", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cold", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("night", prompt, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheSameSkyAtADifferentHourAsksADifferentQuestion()
        {
            // Rain at eleven at night is not rain at eight in the morning, and the
            // titles should be recognisable as one and not the other.
            Assert.NotEqual(
                ContextTitlePromptBuilder.BuildUserPrompt(new ViewingContext(["rain"], Daypart.Morning), 5),
                ContextTitlePromptBuilder.BuildUserPrompt(new ViewingContext(["rain"], Daypart.LateNight), 5));
        }

        [Fact]
        public void TheSystemPromptAsksForBothHalvesToShow()
        {
            var prompt = ContextTitlePromptBuilder.BuildSystemPrompt(5);

            Assert.Contains("weather and the hour", prompt, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void APromptWithNoReadingSaysSoRatherThanInventingWeather()
        {
            var prompt = ContextTitlePromptBuilder.BuildUserPrompt(
                ViewingContext.ClockOnly(Daypart.Afternoon), 5);

            Assert.Contains("afternoon", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("unknown", prompt, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TitlesAreReadBackInOrder()
        {
            var titles = ContextTitlePromptBuilder.Parse(
                """{"titles":["Small Hours Cinema","Rain-Soaked and Restless"]}""");

            Assert.Equal(["Small Hours Cinema", "Rain-Soaked and Restless"], titles);
        }

        [Fact]
        public void AnOverLongTitleIsRejectedRatherThanCut()
        {
            // Cutting it produces exactly the clipped phrase the budget exists to
            // prevent, and the fallback — the owner's own row name — is better.
            var long_ = new string('x', ContextTitlePromptBuilder.MaxTitleLength + 1);
            var titles = ContextTitlePromptBuilder.Parse(
                $$"""{"titles":["{{long_}}","Fine"]}""");

            Assert.Equal(["Fine"], titles);
        }

        [Fact]
        public void WrappersAndTrailingStopsAreStripped()
        {
            var titles = ContextTitlePromptBuilder.Parse(
                """{"titles":["\"Quoted Title\"","Trailing Stop."]}""");

            Assert.Equal(["Quoted Title", "Trailing Stop"], titles);
        }

        [Fact]
        public void DuplicatesAreDroppedCaseInsensitively()
        {
            var titles = ContextTitlePromptBuilder.Parse(
                """{"titles":["Grey Hours","grey hours","Other"]}""");

            Assert.Equal(["Grey Hours", "Other"], titles);
        }

        [Fact]
        public void AnUnusableResponseYieldsNothingRatherThanThrowing()
        {
            // The caller's fallback is the configured name, so a bad answer must cost
            // the call and nothing else.
            Assert.Empty(ContextTitlePromptBuilder.Parse("not json at all"));
            Assert.Empty(ContextTitlePromptBuilder.Parse("""{"wrong":[]}"""));
            Assert.Empty(ContextTitlePromptBuilder.Parse("""{"titles":[1,2,3]}"""));
            Assert.Empty(ContextTitlePromptBuilder.Parse("""{"titles":["","   "]}"""));
        }
    }
}
