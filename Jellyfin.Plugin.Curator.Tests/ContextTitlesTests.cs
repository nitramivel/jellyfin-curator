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
        public void TitlesWrittenByAnOlderPromptAreCulledAtOnce()
        {
            // Without this a rewritten prompt changes nothing anybody sees. A set is
            // bought per condition and kept for a year, so the old wording would go
            // on appearing on every sky the server has already met — the owner pays
            // nothing and watches the titles they asked to be changed carry on.
            var now = DateTime.UtcNow;
            var sets = new[]
            {
                new ContextTitleSet("rain|evening", ["Slate Sky Slow Burns"], 0, now, "m", Style: 1),
                new ContextTitleSet("cold|morning", ["Older still"], 0, now, "m", Style: 0),
                new ContextTitleSet("clear|afternoon", ["Good for a bright afternoon"], 0, now, "m", Style: 2),
            };

            var (kept, expired, obsolete) = ContextTitles.Prune(
                sets, now, ContextTitles.DefaultRetentionDays, style: 2);

            Assert.Equal("clear|afternoon", Assert.Single(kept).Condition);
            Assert.Equal(2, obsolete);

            // Obsolete, never merely expired: these are wrong rather than stale, and
            // waiting out the retention window is the failure being fixed.
            Assert.Equal(0, expired);
        }

        [Fact]
        public void ASetKeepsItsStyleStampAsItRotates()
        {
            // Draw rewrites the set on every draw. Losing the stamp there would make
            // a current set look legacy the first time it was used and be culled on
            // the same pass, re-buying every condition every hour — a free feature
            // turned into a per-refresh one.
            var set = new ContextTitleSet("rain|evening", ["A", "B"], 0, DateTime.UtcNow, "m", Style: 2);

            var drawn = ContextTitles.Draw(set, 0, DateTime.UtcNow);

            Assert.NotNull(drawn);
            Assert.Equal(2, drawn!.Value.Updated.Style);
        }

        [Fact]
        public void ASetWrittenBeforeStylesExistedReadsAsLegacy()
        {
            // Existing stores have no such property, so the constructor default is
            // what every set already on disk deserializes to. It must not collide
            // with a real generation.
            var set = new ContextTitleSet("rain|evening", ["A"], 0, DateTime.UtcNow);

            Assert.Equal(0, set.Style);
            Assert.NotEqual(ContextTitlePromptBuilder.StyleVersion, set.Style);
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
            // cost money. Naming the conditions is now wanted; the merchandising
            // vocabulary is what still has to go, and it is the only thing keeping
            // "plain" from collapsing back into "Picks".
            var prompt = ContextTitlePromptBuilder.BuildSystemPrompt(5);

            Assert.Contains("picks", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("40 characters", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void ThePromptAsksForTheConditionsInPlainWords()
        {
            // The opposite failure, and the measured one: pushed away from the bare
            // label, a model returns "Slate Sky Slow Burns" — a title naming neither
            // the sky nor the hour in any word a reader recognises as either. The
            // prompt now carries worked examples rather than a direction to travel
            // in, because "less oblique" is not a target a model can aim at.
            var prompt = ContextTitlePromptBuilder.BuildSystemPrompt(5);

            Assert.Contains("Good for an Overcast Afternoon", prompt, StringComparison.Ordinal);
            Assert.Contains("Slate Sky Slow Burns", prompt, StringComparison.Ordinal);
            Assert.Contains("Title Case", prompt, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("good for an overcast afternoon", "Good for an Overcast Afternoon")]
        [InlineData("Good For An Overcast Afternoon", "Good for an Overcast Afternoon")]
        [InlineData("when the rain sets in", "When the Rain Sets In")]
        [InlineData("cold night, warm film", "Cold Night, Warm Film")]
        public void TitlesAreCasedLikeAHeading(string written, string expected)
        {
            // The row sits beside Jellyfin's own — "Continue Watching", "Next Up" —
            // and a single row in a different case is the one that looks broken. The
            // prompt asks for this and usually gets it; this makes it certain.
            Assert.Equal(expected, ContextTitlePromptBuilder.TitleCase(written));
        }

        [Fact]
        public void ASmallWordAtEitherEndKeepsItsCapital()
        {
            // "Next Up" is Jellyfin's own row and would read as a typo lowercased.
            Assert.Equal("Up for a Quiet One", ContextTitlePromptBuilder.TitleCase("up for a quiet one"));
            Assert.Equal("Something to Curl Up", ContextTitlePromptBuilder.TitleCase("something to curl up"));
        }

        [Fact]
        public void CapitalsAlreadyInAWordAreNeverFlattened()
        {
            // TextInfo.ToTitleCase was the obvious tool and does exactly this wrong:
            // it lowercases the rest of a word, turning "TV" into "Tv".
            Assert.Equal("A Night of TV", ContextTitlePromptBuilder.TitleCase("a night of TV"));
            Assert.Equal("McCarthy Weather", ContextTitlePromptBuilder.TitleCase("McCarthy weather"));
        }

        [Fact]
        public void ParsedTitlesComeBackCasedWithoutTheCallerAskingTwice()
        {
            // The normaliser has to sit inside Parse, not beside it: every caller
            // that forgot would publish a row in the wrong case.
            var titles = ContextTitlePromptBuilder.Parse(
                """{"titles":["good for a rainy evening","MADE FOR A COLD NIGHT"]}""");

            Assert.Equal(["Good for a Rainy Evening", "Made for a Cold Night"], titles);
        }

        [Fact]
        public void ThePromptStopsEveryTitleOpeningTheSameWay()
        {
            // What plainness costs: the plainest phrasing is the same phrasing every
            // time. A set is bought once and rotated, so the reader sees all of them
            // — five titles that all begin "Good for" is one title shown five times.
            var prompt = ContextTitlePromptBuilder.BuildSystemPrompt(5);

            Assert.Contains("Good for", prompt, StringComparison.Ordinal);
            Assert.Contains("At most ONE may", prompt, StringComparison.Ordinal);
            Assert.Contains("Vary how they open", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void ThePromptLeavesABareClearSkyUnnamed()
        {
            // The temperature words carry a notability bar in their thresholds — hot
            // fires where heat becomes the thing you notice about the day — but the
            // sky words have none, so "clear" earns a word for the same reason
            // "storm" does. It should not: a clear sky is the ordinary state of the
            // sky, and "a clear evening" says barely more than "an evening" while
            // spending characters from a 40-character budget to do it.
            var prompt = ContextTitlePromptBuilder.BuildSystemPrompt(5);

            Assert.Contains("clear bright sky and nothing else is not worth naming", prompt, StringComparison.Ordinal);

            // But only when it is alone. A cold bright morning is a specific thing in
            // a way a clear afternoon is not, which is why this is a rule the model
            // applies rather than a filter over the vocabulary.
            Assert.Contains("does NOT apply when the", prompt, StringComparison.Ordinal);
            Assert.Contains("heat or cold", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void NoWorkedExampleContradictsTheRules()
        {
            // The examples are the strongest instruction in this prompt — a model
            // copies a shown title over a stated rule. An example naming a bare clear
            // sky would teach exactly what the rule below it forbids.
            var prompt = ContextTitlePromptBuilder.BuildSystemPrompt(5);

            // The example block is the indented run between "out loud:" and the
            // paragraph after it. Anchored on both ends so this cannot quietly start
            // matching nothing and passing for that reason.
            var block = prompt.Split("out loud:", StringSplitOptions.None)[1]
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)[0];
            var examples = block
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();

            Assert.Equal(6, examples.Count);
            Assert.Contains("Good for an Overcast Afternoon", examples);
            Assert.Contains("Cold Night, Warm Film", examples);

            // "Clear morning watching" was a worked example until the rule arrived,
            // and a shown title beats a stated rule every time.
            Assert.DoesNotContain(examples, line => line.StartsWith("Clear", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ThePromptSaysNotToInventASkyThatCouldNotBeRead()
        {
            // Naming both halves is now a rule, so the case where there is no
            // weather reading needs an explicit exemption — otherwise the rule reads
            // as an instruction to guess one, and the row claims a sky the server
            // never saw.
            var prompt = ContextTitlePromptBuilder.BuildSystemPrompt(5);

            Assert.Contains("weather is given as unknown", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("do not guess at a sky", prompt, StringComparison.OrdinalIgnoreCase);
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
