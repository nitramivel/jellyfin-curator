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
        public void TheWeatherKeyIgnoresTheOrderTheWordsArrivedIn()
        {
            // The order Open-Meteo happens to report them in is not a fact about the
            // weather, and treating it as one would double the conditions bought.
            Assert.Equal(
                ContextTitles.ConditionKey(ContextRowKind.Weather, Weather("rain", "cold")),
                ContextTitles.ConditionKey(ContextRowKind.Weather, Weather("cold", "rain")));
        }

        [Fact]
        public void TheDaypartKeyIgnoresTheWeatherEntirely()
        {
            // Combining the two would multiply the conditions eightfold to describe a
            // row that only ever claims one of them.
            var rainy = new ViewingContext(["rain"], Daypart.Evening);
            var clear = new ViewingContext(["clear", "hot"], Daypart.Evening);

            Assert.Equal(
                ContextTitles.ConditionKey(ContextRowKind.Daypart, rainy),
                ContextTitles.ConditionKey(ContextRowKind.Daypart, clear));
        }

        [Fact]
        public void TheTwoRowsNeverShareAKey()
        {
            var context = Weather("rain");

            Assert.NotEqual(
                ContextTitles.ConditionKey(ContextRowKind.Weather, context),
                ContextTitles.ConditionKey(ContextRowKind.Daypart, context));
        }

        [Fact]
        public void DifferentConditionsGetDifferentKeys()
        {
            var keys = new[]
            {
                ContextTitles.ConditionKey(ContextRowKind.Weather, Weather("rain")),
                ContextTitles.ConditionKey(ContextRowKind.Weather, Weather("rain", "cold")),
                ContextTitles.ConditionKey(ContextRowKind.Weather, Weather("snow")),
                ContextTitles.ConditionKey(ContextRowKind.Daypart, Weather("rain")),
            };

            Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        }

        // ---- rotation ----

        [Fact]
        public void SuccessiveDrawsMoveThroughTheSet()
        {
            var set = new ContextTitleSet("weather:rain", ["A", "B", "C"], 0, DateTime.UtcNow);
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
            var set = new ContextTitleSet("weather:rain", ["A", "B"], 7, DateTime.UtcNow);

            Assert.NotNull(ContextTitles.Draw(set, 0, DateTime.UtcNow));
        }

        [Fact]
        public void TwoViewersInOneConditionDoNotReadTheSameTitle()
        {
            // The moment per-viewer rows exist they share a condition and therefore a
            // set, so without the offset every household would read one title.
            var set = new ContextTitleSet("weather:rain", ["A", "B", "C", "D"], 0, DateTime.UtcNow);

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
            var set = new ContextTitleSet("weather:rain", [], 0, DateTime.UtcNow);

            Assert.Null(ContextTitles.Draw(set, 0, DateTime.UtcNow));
        }

        [Fact]
        public void ADrawStampsTheSetAsUsedSoCullingCannotTakeIt()
        {
            var stale = DateTime.UtcNow.AddYears(-5);
            var set = new ContextTitleSet("weather:rain", ["A"], 0, stale);
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
                new ContextTitleSet("weather:rain", ["A"], 0, now),
                new ContextTitleSet("weather:snow", ["B"], 0, now.AddDays(-400)),
            };

            var (kept, expired, obsolete) = ContextTitles.Prune(sets, now, retentionDays: 365);

            Assert.Equal("weather:rain", Assert.Single(kept).Condition);
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
            var sets = new[] { new ContextTitleSet("weather:cold,snow", ["A"], 0, now.AddDays(-200)) };

            var (kept, _, _) = ContextTitles.Prune(sets, now);

            Assert.Single(kept);
        }

        [Fact]
        public void ARetentionOfZeroKeepsThemForever()
        {
            var now = DateTime.UtcNow;
            var sets = new[] { new ContextTitleSet("weather:rain", ["A"], 0, now.AddYears(-20)) };

            var (kept, expired, _) = ContextTitles.Prune(sets, now, retentionDays: 0);

            Assert.Single(kept);
            Assert.Equal(0, expired);
        }

        [Fact]
        public void TitlesForAWordTheVocabularyLostGoImmediately()
        {
            // Not after a year: these can never match anything again, so waiting
            // serves nobody.
            var now = DateTime.UtcNow;
            var sets = new[]
            {
                new ContextTitleSet("weather:drizzly", ["A"], 0, now),
                new ContextTitleSet("daypart:dusk", ["B"], 0, now),
                new ContextTitleSet("weather:rain", ["C"], 0, now),
            };

            var (kept, expired, obsolete) = ContextTitles.Prune(sets, now);

            Assert.Equal("weather:rain", Assert.Single(kept).Condition);
            Assert.Equal(2, obsolete);
            Assert.Equal(0, expired);
        }

        [Fact]
        public void AnEmptySetIsCulledAsObsolete()
        {
            var now = DateTime.UtcNow;
            var (kept, _, obsolete) = ContextTitles.Prune(
                [new ContextTitleSet("weather:rain", [], 0, now)], now);

            Assert.Empty(kept);
            Assert.Equal(1, obsolete);
        }

        [Theory]
        [InlineData("weather:rain", true)]
        [InlineData("weather:cold,rain", true)]
        [InlineData("daypart:latenight", true)]
        [InlineData("weather:drizzly", false)]
        [InlineData("daypart:evening,morning", false)]
        [InlineData("weather:", false)]
        [InlineData("nonsense", false)]
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
            foreach (var word in ContextVocabulary.Weather)
            {
                Assert.True(ContextTitles.IsLiveCondition(
                    ContextTitles.ConditionKey(ContextRowKind.Weather, Weather(word))));
            }

            foreach (Daypart daypart in Enum.GetValues<Daypart>())
            {
                Assert.True(ContextTitles.IsLiveCondition(
                    ContextTitles.ConditionKey(ContextRowKind.Daypart, new ViewingContext([], daypart))));
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
        public void TheUserPromptNamesTheConditionsInProse()
        {
            var prompt = ContextTitlePromptBuilder.BuildUserPrompt(
                ContextRowKind.Weather, new ViewingContext(["rain", "cold"], Daypart.Evening), 5);

            Assert.Contains("rain", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cold", prompt, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheDaypartPromptDoesNotMentionTheWeather()
        {
            var prompt = ContextTitlePromptBuilder.BuildUserPrompt(
                ContextRowKind.Daypart, new ViewingContext(["snow"], Daypart.LateNight), 5);

            Assert.DoesNotContain("snow", prompt, StringComparison.OrdinalIgnoreCase);
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
