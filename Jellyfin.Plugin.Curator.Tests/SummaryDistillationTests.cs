using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Curator.Core.Context;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Core.Summaries;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Planning, prompting and parsing for the condensed-summary pass.
    ///
    /// The expensive mistakes here are both silent. Re-distilling items that have
    /// not changed re-buys the whole library on every pass, and failing to notice a
    /// rewritten overview leaves a summary describing the wrong film for as long as
    /// the cache lives. Both are pinned below.
    /// </summary>
    public class SummaryDistillationTests
    {
        private const string LongOverview =
            "A washed-up wrestler grinds through weekend shows in school gymnasiums, "
            + "trying to hold together a body and a life that have both been spent.";

        private static MediaItemRecord Item(string name, string? overview, Guid? id = null) => new()
        {
            Id = id ?? Guid.NewGuid(),
            Kind = MediaKind.Movie,
            Name = name,
            Overview = overview,
        };

        private static CondensedSummary Stored(MediaItemRecord item, string sourceOverview) => new()
        {
            ItemId = item.Id,
            Text = "spent body, spent life, fluorescent gymnasiums",
            SourceHash = SummaryPlan.HashOverview(sourceOverview),
            CreatedAt = DateTime.UtcNow,
        };

        // ---- planning ----

        [Fact]
        public void Plan_QueuesItemsWithNoStoredSummary()
        {
            var item = Item("The Wrestler", LongOverview);

            var plan = SummaryPlan.Create([item], new Dictionary<Guid, CondensedSummary>(), 140);

            var work = Assert.Single(plan.Work);
            Assert.Equal(SummaryPlan.SummaryReason.Missing, work.Reason);
        }

        [Fact]
        public void Plan_SkipsItemsWhoseStoredSummaryMatchesTheirOverview()
        {
            // The whole economics: a second pass over an unchanged library must be free.
            var item = Item("The Wrestler", LongOverview);
            var existing = new Dictionary<Guid, CondensedSummary> { [item.Id] = Stored(item, LongOverview) };

            var plan = SummaryPlan.Create([item], existing, 140);

            Assert.Empty(plan.Work);
            Assert.Equal(1, plan.UpToDate);
        }

        [Fact]
        public void Plan_RequeuesAnItemWhoseOverviewWasRewritten()
        {
            // A metadata refresh can replace the overview underneath a stored
            // summary. Missing that leaves the cache describing the wrong film.
            var item = Item("The Wrestler", LongOverview + " Now with a different ending entirely.");
            var existing = new Dictionary<Guid, CondensedSummary> { [item.Id] = Stored(item, LongOverview) };

            var plan = SummaryPlan.Create([item], existing, 140);

            var work = Assert.Single(plan.Work);
            Assert.Equal(SummaryPlan.SummaryReason.Stale, work.Reason);
        }

        [Fact]
        public void Plan_LeavesShortOverviewsAlone()
        {
            var plan = SummaryPlan.Create(
                [Item("Short", "A brief note.")],
                new Dictionary<Guid, CondensedSummary>(),
                140);

            Assert.Empty(plan.Work);
            Assert.Equal(1, plan.TooShort);
        }

        [Fact]
        public void Plan_CountsItemsWithNoOverviewSeparately()
        {
            var plan = SummaryPlan.Create(
                [Item("Blank", null), Item("Empty", "   ")],
                new Dictionary<Guid, CondensedSummary>(),
                140);

            Assert.Empty(plan.Work);
            Assert.Equal(2, plan.NoOverview);
        }

        [Fact]
        public void Plan_ForceRedoesEverythingWithAnOverview()
        {
            var item = Item("The Wrestler", LongOverview);
            var existing = new Dictionary<Guid, CondensedSummary> { [item.Id] = Stored(item, LongOverview) };

            var plan = SummaryPlan.Create([item], existing, 140, force: true);

            Assert.Equal(SummaryPlan.SummaryReason.Forced, Assert.Single(plan.Work).Reason);
        }

        [Fact]
        public void Plan_ForceStillRespectsTheShortOverviewFloor()
        {
            // Force means "redo the work", not "do work that was never worth doing".
            var plan = SummaryPlan.Create(
                [Item("Short", "A brief note.")],
                new Dictionary<Guid, CondensedSummary>(),
                140,
                force: true);

            Assert.Empty(plan.Work);
        }

        [Fact]
        public void HashOverview_IgnoresSurroundingWhitespaceOnly()
        {
            Assert.Equal(SummaryPlan.HashOverview(LongOverview), SummaryPlan.HashOverview("  " + LongOverview + "\n"));
            Assert.NotEqual(SummaryPlan.HashOverview(LongOverview), SummaryPlan.HashOverview(LongOverview + " More."));
        }

        // ---- prompting ----

        [Fact]
        public void Prompt_StatesTheBudgetTheParserEnforces()
        {
            // Same contract CategoryLimits keeps for categories: the number the model
            // is told must be the number its answer is judged by.
            var system = SummaryPromptBuilder.BuildSystemPrompt(90);

            Assert.Contains("90 characters", system, StringComparison.Ordinal);
            Assert.DoesNotContain("{MAX_LENGTH}", system, StringComparison.Ordinal);
        }

        [Fact]
        public void Prompt_SendsTheFullOverviewNotATruncation()
        {
            // Distilling a truncation would bake the cut permanently into the cache.
            var overview = new string('x', 900);
            var prompt = SummaryPromptBuilder.BuildUserPrompt([Item("Long", overview)]);

            Assert.Contains(overview, prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void Prompt_NumbersItemsFromZeroAndNeverSendsIds()
        {
            var id = Guid.NewGuid();
            var prompt = SummaryPromptBuilder.BuildUserPrompt([Item("A", LongOverview, id), Item("B", LongOverview)]);

            Assert.Contains("\"i\":0", prompt, StringComparison.Ordinal);
            Assert.Contains("\"i\":1", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain(id.ToString(), prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(id.ToString("N"), prompt, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Plan_RequeuesACorruptSummaryEvenThoughItsHashStillMatches()
        {
            // The repair path for summaries stored before the parser learned to strip
            // the fragment. Their hash matches by construction, so every other check
            // in Create() calls them current and they would be sent to the model,
            // broken, on every run for the life of the overview.
            var item = Item("A", LongOverview);
            var stored = Stored(item, LongOverview) with
            {
                Text = "Darkly comic class infiltration, ever more tense and viciously sharp\u0027,\u0027t\u0027:[",
            };

            var plan = SummaryPlan.Create(
                [item],
                new Dictionary<Guid, CondensedSummary> { [item.Id] = stored },
                minSourceLength: 20);

            var task = Assert.Single(plan.Work);
            Assert.Equal(SummaryPlan.SummaryReason.Corrupt, task.Reason);
            Assert.Equal(0, plan.UpToDate);
        }

        [Fact]
        public void Plan_LeavesACleanCurrentSummaryAlone()
        {
            // The other half of the same guard: the corruption check must not drag
            // the whole library back into a paid pass.
            var item = Item("A", LongOverview);
            var stored = Stored(item, LongOverview) with
            {
                Text = "Darkly comic class infiltration, ever more tense and viciously sharp",
            };

            var plan = SummaryPlan.Create(
                [item],
                new Dictionary<Guid, CondensedSummary> { [item.Id] = stored },
                minSourceLength: 20);

            Assert.Empty(plan.Work);
            Assert.Equal(1, plan.UpToDate);
        }

        // ---- parsing ----

        private static readonly IReadOnlyList<MediaItemRecord> Batch =
            [Item("A", LongOverview), Item("B", LongOverview)];

        [Fact]
        public void Parse_MapsIndexesBackToItems()
        {
            var result = SummaryParser.Parse(
                """{"summaries":[{"i":1,"s":"bleak and funny"},{"i":0,"s":"warm and slight"}]}""",
                Batch,
                90);

            Assert.Equal(2, result.Summaries.Count);
            Assert.Equal("bleak and funny", result.Summaries.First(s => s.Item.Name == "B").Text);
            Assert.Equal("warm and slight", result.Summaries.First(s => s.Item.Name == "A").Text);
            Assert.Equal(0, result.MissingCount);
        }

        /// <summary>
        /// Real corruption from a live 232-item pass: 17 summaries were stored ending
        /// <c>…viciously sharp','t':[</c>. The model closed the prose and began the
        /// tag field from inside the string it was still writing, so the fragment came
        /// back as part of a valid "s" value and nothing ever raised a parse error.
        /// These are cached on the source hash, so an uncaught one is sent to the
        /// model on every subsequent run for the life of the overview.
        /// </summary>
        [Theory]
        [InlineData(
            "Darkly comic class infiltration that grows ever more tense, shocking and viciously sharp\u0027,\u0027t\u0027:[",
            "Darkly comic class infiltration that grows ever more tense, shocking and viciously sharp")]
        [InlineData(
            "Awkward, candid how-to films that wander into the contradictions of anxious city life\u0027,\u0027t\u0027:[",
            "Awkward, candid how-to films that wander into the contradictions of anxious city life")]
        [InlineData(
            "Warm workplace sitcom of an earnest bureaucrat cheerfully battling small-town red tape\u0027,\u0027t\u0027:[",
            "Warm workplace sitcom of an earnest bureaucrat cheerfully battling small-town red tape")]
        public void Parse_StripsATrailingJsonFieldFragmentTheModelWroteIntoTheProse(string stored, string expected)
        {
            var result = SummaryParser.Parse(
                JsonSerializer.Serialize(new
                {
                    summaries = new[] { new { i = 0, s = stored } },
                }),
                Batch,
                200);

            Assert.Equal(expected, Assert.Single(result.Summaries).Text);
        }

        [Theory]
        [InlineData("ends on a double-quoted key\", \"t\":[")]
        [InlineData("ends on an object open\u0027,\u0027tags\u0027:{")]
        [InlineData("ends on a bare value\u0027,\u0027t\u0027:")]
        public void Parse_StripsTheFragmentWhicheverQuotingTheModelUsed(string stored)
        {
            var result = SummaryParser.Parse(
                JsonSerializer.Serialize(new { summaries = new[] { new { i = 0, s = stored } } }),
                Batch,
                200);

            var text = Assert.Single(result.Summaries).Text;
            Assert.DoesNotContain(":", text, StringComparison.Ordinal);
            Assert.StartsWith("ends on a", text, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("A ratio of 3:1 drives the whole thing")]
        [InlineData("Two men, one long night: nothing goes to plan")]
        [InlineData("Chapter 4: the reckoning, told sideways and cold")]
        [InlineData("Grief, guilt, and a house that will not let go")]
        public void Parse_LeavesOrdinaryProseWithPunctuationAlone(string stored)
        {
            // The strip is anchored to the end and narrow on purpose: a colon mid
            // sentence is normal writing, not a leaked field.
            var result = SummaryParser.Parse(
                JsonSerializer.Serialize(new { summaries = new[] { new { i = 0, s = stored } } }),
                Batch,
                200);

            Assert.Equal(stored, Assert.Single(result.Summaries).Text);
        }

        [Fact]
        public void Parse_KeepsTheMessRatherThanReturningAStub()
        {
            // Stripping back to almost nothing would replace a visible problem with
            // an invisible one — a summary too short to carry any tone at all.
            const string Stored = "bleak\u0027,\u0027t\u0027:[";

            var result = SummaryParser.Parse(
                JsonSerializer.Serialize(new { summaries = new[] { new { i = 0, s = Stored } } }),
                Batch,
                200);

            Assert.Equal(Stored, Assert.Single(result.Summaries).Text);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(-1)]
        [InlineData(99)]
        public void Parse_DiscardsIndexesOutsideTheBatch(int index)
        {
            // Hard rule 1: the model cannot attach a summary to an item it never saw.
            var result = SummaryParser.Parse(
                $$"""{"summaries":[{"i":{{index}},"s":"nope"}]}""",
                Batch,
                90);

            Assert.Empty(result.Summaries);
            Assert.Equal(1, result.DiscardedCount);
        }

        [Fact]
        public void Parse_DiscardsADuplicateIndex()
        {
            var result = SummaryParser.Parse(
                """{"summaries":[{"i":0,"s":"first"},{"i":0,"s":"second"}]}""",
                Batch,
                90);

            Assert.Equal("first", Assert.Single(result.Summaries).Text);
            Assert.Equal(1, result.DiscardedCount);
        }

        [Fact]
        public void Parse_TrimsAnOverLongSummaryAtAWordBoundary()
        {
            var result = SummaryParser.Parse(
                """{"summaries":[{"i":0,"s":"aaaa bbbb cccc dddd eeee ffff gggg hhhh"}]}""",
                Batch,
                20);

            var text = Assert.Single(result.Summaries).Text;
            Assert.True(text.Length <= 20, $"'{text}' is {text.Length} chars");
            Assert.DoesNotContain("  ", text, StringComparison.Ordinal);
            Assert.Equal(1, result.TrimmedCount);
        }

        [Fact]
        public void Parse_StripsQuotesAndBulletsTheModelAdded()
        {
            // Stored once, then sent on every run forever — a cosmetic slip becomes
            // permanent if it is not caught at the boundary.
            var result = SummaryParser.Parse(
                """{"summaries":[{"i":0,"s":"- \"bleak and funny\""}]}""",
                Batch,
                90);

            Assert.Equal("bleak and funny", Assert.Single(result.Summaries).Text);
        }

        [Fact]
        public void Parse_ReportsItemsTheModelSkipped()
        {
            var result = SummaryParser.Parse(
                """{"summaries":[{"i":0,"s":"only one"}]}""",
                Batch,
                90);

            Assert.Single(result.Summaries);
            Assert.Equal(1, result.MissingCount);
        }

        [Fact]
        public void Parse_ToleratesCodeFencesAndTrailingProse()
        {
            var result = SummaryParser.Parse(
                "```json\n{\"summaries\":[{\"i\":0,\"s\":\"fenced\"}]}\n```\nHope that helps!",
                Batch,
                90);

            Assert.Equal("fenced", Assert.Single(result.Summaries).Text);
        }

        // ---- not ending mid-phrase ----

        [Theory]
        [InlineData("Black-and-white burst of Beatlemania chaos: wit, chase, and pure pop energy racing to a")]
        [InlineData("Glossy bee courtroom farce and city adventure after one graduate sues humanity for")]
        [InlineData("Neon-rain noir; a weary hunter retires replicants in a smog future that questions who")]
        [InlineData("Bleached vast silence and holographic loneliness as a new hunter chases a secret that")]
        public void Parse_DoesNotLeaveASummaryEndingOnADanglingWord(string overLong)
        {
            // Verbatim from a real 232-item pass, where 11% ended like this. The
            // reader is a model judging tone, and a sentence cut mid-clause reads as
            // vaguer rather than shorter.
            var result = SummaryParser.Parse(
                JsonSerializer.Serialize(new { summaries = new[] { new { i = 0, s = overLong } } }),
                Batch,
                60);

            var text = Assert.Single(result.Summaries).Text;
            var last = text.Split(' ')[^1].ToLowerInvariant();

            Assert.DoesNotContain(last, new[] { "a", "an", "the", "to", "for", "that", "who", "with", "of", "and" });
            Assert.True(text.Length <= 60, $"'{text}' is {text.Length} chars");
        }

        [Fact]
        public void Parse_KeepsAShortSummaryEvenIfItEndsAwkwardly()
        {
            // Backing off must not eat a summary down to a stub; under budget it is
            // left exactly as written.
            const string text = "Warm mockumentary of underfunded teachers outshining the";

            var result = SummaryParser.Parse(
                JsonSerializer.Serialize(new { summaries = new[] { new { i = 0, s = text } } }),
                Batch,
                200);

            Assert.Equal(text, Assert.Single(result.Summaries).Text);
        }

        [Fact]
        public void Prompt_TellsTheModelToFinishItsThought()
        {
            // The bigger cause of the 11%: only 7% were actually at the cap, so the
            // model was writing to the limit and stopping rather than composing to fit.
            var system = SummaryPromptBuilder.BuildSystemPrompt(90);

            Assert.Contains("COMPLETE", system, StringComparison.Ordinal);
            Assert.Contains("Shorter and whole always beats longer and cut", system, StringComparison.Ordinal);
        }

        // ---- tag consolidation ----

        private static MediaItemRecord Tagged(params string[] tags) => new()
        {
            Id = Guid.NewGuid(),
            Kind = MediaKind.Movie,
            Name = "Tagged",
            Overview = LongOverview,
            Tags = tags,
        };

        [Fact]
        public void Prompt_LeavesTagsOutEntirelyWhenTheCeilingIsZero()
        {
            var system = SummaryPromptBuilder.BuildSystemPrompt(90, tagCeiling: 0);

            Assert.DoesNotContain("Consolidate", system, StringComparison.Ordinal);
            Assert.DoesNotContain("\"t\"", system, StringComparison.Ordinal);
        }

        [Fact]
        public void Prompt_AsksForHoweverManyTagsApplyRatherThanAFixedCount()
        {
            // The whole point over the old take-the-first-N setting.
            var system = SummaryPromptBuilder.BuildSystemPrompt(90, tagCeiling: 6);

            Assert.Contains("HOWEVER MANY genuinely apply, up to 6", system, StringComparison.Ordinal);
            Assert.Contains("ceiling, not a target", system, StringComparison.Ordinal);
            Assert.Contains("\"t\":[", system, StringComparison.Ordinal);
        }

        /// <summary>
        /// The point of doing both halves in one call: the model writes the rewrite,
        /// then picks the tags that agree with the reading it just committed to.
        /// Without this the tag half is an independent filtering job that happens to
        /// share a request, and the vibe never reaches the tags.
        /// </summary>
        [Fact]
        public void Prompt_TiesTagChoiceToTheRewriteJustWritten()
        {
            var system = SummaryPromptBuilder.BuildSystemPrompt(90, tagCeiling: 6);

            Assert.Contains("Do this SECOND", system, StringComparison.Ordinal);
            Assert.Contains("let the rewrite decide", system, StringComparison.Ordinal);
            Assert.Contains("must agree with it", system, StringComparison.Ordinal);
            Assert.Contains("pulls against the reading", system, StringComparison.Ordinal);
        }

        [Fact]
        public void Prompt_ShowsTheSummaryBeforeTheTagsInTheOutputContract()
        {
            // The instruction above is only honoured if "s" is generated before "t",
            // so the example must not teach the opposite order.
            var system = SummaryPromptBuilder.BuildSystemPrompt(90, tagCeiling: 6);

            var summaryField = system.IndexOf("\"s\":", StringComparison.Ordinal);
            var tagField = system.IndexOf("\"t\":", StringComparison.Ordinal);

            Assert.True(summaryField >= 0 && tagField > summaryField);
        }

        [Fact]
        public void Prompt_KeepsTheScrapedVocabularyByDefault()
        {
            // A tag is worth something only if the same tag means the same thing
            // across items, and the scraped list is a shared vocabulary for free.
            var system = SummaryPromptBuilder.BuildSystemPrompt(90, tagCeiling: 6);

            Assert.Contains("rather than inventing new vocabulary", system, StringComparison.Ordinal);
            Assert.DoesNotContain("you MAY coin", system, StringComparison.Ordinal);
        }

        [Fact]
        public void Prompt_AllowsCoiningAWordButOnlyAsALastResort()
        {
            // The failure mode being guarded against is not a bad word — it is four
            // words for one texture across four items, which is worse than the
            // scraped list. So the permission comes with the constraint attached.
            var system = SummaryPromptBuilder.BuildSystemPrompt(90, tagCeiling: 6, allowInventedTags: true);

            Assert.Contains("you MAY coin", system, StringComparison.Ordinal);
            Assert.Contains("last resort", system, StringComparison.Ordinal);
            Assert.Contains("most\n            ordinary wording", system, StringComparison.Ordinal);
            Assert.Contains("Never coin a second word", system, StringComparison.Ordinal);

            // Still anchored to the scraped list first.
            Assert.Contains("Prefer the scraped list's own wording", system, StringComparison.Ordinal);
        }

        [Fact]
        public void Prompt_SaysNothingAboutVocabularyWhenTagsAreOff()
        {
            var system = SummaryPromptBuilder.BuildSystemPrompt(90, tagCeiling: 0, allowInventedTags: true);

            Assert.DoesNotContain("vocabulary", system, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("coin", system, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Prompt_SendsTheWholeScrapedTagListUnfiltered()
        {
            // Pre-trimming would make the judgement being asked for on the model's behalf.
            var item = Tagged("aftercreditsstinger", "bleak", "based on novel or book", "sardonic");

            var prompt = SummaryPromptBuilder.BuildUserPrompt([item], includeTags: true);

            Assert.Contains("aftercreditsstinger", prompt, StringComparison.Ordinal);
            Assert.Contains("sardonic", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void Prompt_OmitsTagsWhenNotConsolidatingThem()
        {
            var prompt = SummaryPromptBuilder.BuildUserPrompt([Tagged("bleak")], includeTags: false);

            Assert.DoesNotContain("bleak", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_KeepsAVaryingNumberOfTagsPerItem()
        {
            var json = JsonSerializer.Serialize(new
            {
                summaries = new object[]
                {
                    new { i = 0, s = "dense", t = new[] { "bleak", "sardonic", "slow-burn" } },
                    new { i = 1, s = "thin", t = new[] { "cheerful" } },
                },
            });

            var result = SummaryParser.Parse(json, Batch, 90, tagCeiling: 6);

            Assert.Equal(3, result.Summaries.First(s => s.Text == "dense").Tags.Count);
            Assert.Single(result.Summaries.First(s => s.Text == "thin").Tags);
        }

        [Fact]
        public void Parse_AcceptsAnEmptyTagListAsARealAnswer()
        {
            // An item whose scraped tags are all production trivia genuinely has
            // none worth keeping. Nothing may top it up to a minimum.
            var result = SummaryParser.Parse(
                """{"summaries":[{"i":0,"s":"only trivia","t":[]}]}""",
                Batch,
                90,
                tagCeiling: 6);

            Assert.Empty(Assert.Single(result.Summaries).Tags);
        }

        [Fact]
        public void Parse_ClipsARunawayTagListToTheCeiling()
        {
            var many = Enumerable.Range(0, 40).Select(i => "tag" + i).ToArray();
            var json = JsonSerializer.Serialize(new
            {
                summaries = new[] { new { i = 0, s = "x", t = many } },
            });

            var result = SummaryParser.Parse(json, Batch, 90, tagCeiling: 6);

            Assert.Equal(6, Assert.Single(result.Summaries).Tags.Count);
        }

        [Fact]
        public void Parse_NormalisesAndDeduplicatesTags()
        {
            var result = SummaryParser.Parse(
                """{"summaries":[{"i":0,"s":"x","t":["Bleak"," bleak ","SARDONIC",""]}]}""",
                Batch,
                90,
                tagCeiling: 6);

            Assert.Equal(["bleak", "sardonic"], Assert.Single(result.Summaries).Tags);
        }

        [Fact]
        public void Parse_IgnoresTagsWhenTheyWereNotAskedFor()
        {
            var result = SummaryParser.Parse(
                """{"summaries":[{"i":0,"s":"x","t":["bleak"]}]}""",
                Batch,
                90,
                tagCeiling: 0);

            Assert.Empty(Assert.Single(result.Summaries).Tags);
        }

        [Fact]
        public void Plan_QueuesAnItemWhoseSummaryIsCurrentButHasNoTagsYet()
        {
            // The state every stored summary is in the first time tags are switched
            // on. Without this, enabling tags would need a full Redo all.
            var item = Tagged("bleak", "sardonic");
            var existing = new Dictionary<Guid, CondensedSummary>
            {
                [item.Id] = Stored(item, LongOverview),
            };

            var plan = SummaryPlan.Create([item], existing, 140, force: false, consolidateTags: true);

            Assert.Equal(SummaryPlan.SummaryReason.TagsMissing, Assert.Single(plan.Work).Reason);
        }

        [Fact]
        public void Plan_LeavesThatItemAloneWhenTagsAreNotWanted()
        {
            var item = Tagged("bleak");
            var existing = new Dictionary<Guid, CondensedSummary>
            {
                [item.Id] = Stored(item, LongOverview),
            };

            Assert.Empty(SummaryPlan.Create([item], existing, 140, force: false, consolidateTags: false).Work);
        }

        [Fact]
        public void Plan_NeverQueuesAnItemThatHasNoScrapedTagsToConsolidate()
        {
            // Queueing it would re-buy a summary on every pass for an answer that can
            // only ever be empty.
            var item = Item("No tags", LongOverview);
            var existing = new Dictionary<Guid, CondensedSummary>
            {
                [item.Id] = Stored(item, LongOverview),
            };

            Assert.Empty(SummaryPlan.Create([item], existing, 140, force: false, consolidateTags: true).Work);
        }

        [Fact]
        public void Plan_RequeuesWhenTheScrapedTagsThemselvesChanged()
        {
            var item = Tagged("bleak", "sardonic");
            var stored = Stored(item, LongOverview) with
            {
                TagSourceHash = SummaryPlan.HashTags(["something", "else"]),
            };

            var plan = SummaryPlan.Create(
                [item],
                new Dictionary<Guid, CondensedSummary> { [item.Id] = stored },
                140,
                force: false,
                consolidateTags: true);

            Assert.Equal(SummaryPlan.SummaryReason.TagsMissing, Assert.Single(plan.Work).Reason);
        }

        [Fact]
        public void HashTags_IgnoresOrderAndCase()
        {
            // Metadata providers do not promise a stable order, and a reshuffle is not
            // worth paying a model to re-read.
            Assert.Equal(SummaryPlan.HashTags(["a", "b"]), SummaryPlan.HashTags(["B", "A"]));
            Assert.NotEqual(SummaryPlan.HashTags(["a", "b"]), SummaryPlan.HashTags(["a", "b", "c"]));
        }

        [Fact]
        public void HashTags_DoesNotConfuseDifferentSplits()
        {
            Assert.NotEqual(SummaryPlan.HashTags(["ab", "c"]), SummaryPlan.HashTags(["a", "bc"]));
        }

        [Fact]
        public void Parse_ThrowsOnTheWrongShape()
        {
            Assert.Throws<FormatException>(
                () => SummaryParser.Parse("""{"categories":[]}""", Batch, 90));
        }

        [Fact]
        public void Parse_ThrowsOnAResponseCutOffMidObject()
        {
            // What a truncated response looks like when thinking ate the output cap.
            Assert.Throws<FormatException>(
                () => SummaryParser.Parse("""{"summaries":[{"i":0,"s":"unter""", Batch, 90));
        }

        // ---- when an item suits watching ----

        [Fact]
        public void Parse_ReadsBothContextLists()
        {
            var result = SummaryParser.Parse(
                """{"summaries":[{"i":0,"s":"bleak","w":["rain","cold"],"d":["evening","latenight"]}]}""",
                Batch,
                90,
                classifyContext: true);

            var context = Assert.Single(result.Summaries).Context;
            Assert.Equal(["rain", "cold"], context.Weather);
            Assert.Equal(["evening", "latenight"], context.Dayparts);
        }

        [Fact]
        public void Parse_DropsAWordOutsideTheVocabularyRatherThanMappingIt()
        {
            // "drizzly" is not a more precise "rain" — a row asking for rain matches
            // it never, so guessing what it meant is how a closed vocabulary reopens.
            var result = SummaryParser.Parse(
                """{"summaries":[{"i":0,"s":"bleak","w":["drizzly","rain","overcast"],"d":["dusk"]}]}""",
                Batch,
                90,
                classifyContext: true);

            var context = Assert.Single(result.Summaries).Context;
            Assert.Equal(["rain"], context.Weather);
            Assert.Empty(context.Dayparts);
        }

        [Fact]
        public void Parse_KeepsTheSummaryWhenEveryContextWordIsRejected()
        {
            // The summary is the expensive half of the answer. Losing it over a
            // stray word would throw away the costly part to protect the cheap one.
            var result = SummaryParser.Parse(
                """{"summaries":[{"i":0,"s":"a real summary","w":["drizzly"],"d":["dusk"]}]}""",
                Batch,
                90,
                classifyContext: true);

            var summary = Assert.Single(result.Summaries);
            Assert.Equal("a real summary", summary.Text);
            Assert.Equal(0, result.DiscardedCount);
        }

        [Fact]
        public void Parse_TreatsAnEmptyContextAnswerAsReal()
        {
            // Most of the library is expected to land here: a broad comedy suits any
            // hour and any sky, and two empty lists is the correct answer for it.
            var result = SummaryParser.Parse(
                """{"summaries":[{"i":0,"s":"broad comedy","w":[],"d":[]}]}""",
                Batch,
                90,
                classifyContext: true);

            var context = Assert.Single(result.Summaries).Context;
            Assert.Empty(context.Weather);
            Assert.Empty(context.Dayparts);
        }

        [Fact]
        public void Parse_IgnoresContextForAPassThatNeverAskedForIt()
        {
            // A model volunteering fields a pass did not request must not write
            // anything into the store.
            var result = SummaryParser.Parse(
                """{"summaries":[{"i":0,"s":"bleak","w":["rain"],"d":["evening"]}]}""",
                Batch,
                90);

            Assert.Empty(Assert.Single(result.Summaries).Context.Weather);
        }

        [Fact]
        public void Parse_DeduplicatesContextWordsCaseInsensitively()
        {
            var result = SummaryParser.Parse(
                """{"summaries":[{"i":0,"s":"bleak","w":["Rain","rain","RAIN"],"d":[]}]}""",
                Batch,
                90,
                classifyContext: true);

            Assert.Equal(["rain"], Assert.Single(result.Summaries).Context.Weather);
        }

        [Fact]
        public void Plan_QueuesAnItemWhoseSummaryIsCurrentButWasNeverJudgedForContext()
        {
            // The state every stored summary is in the first time the setting is
            // switched on. Without this, it would appear to do nothing.
            var item = Item("The Wrestler", LongOverview);
            var stored = Stored(item, LongOverview);

            var plan = SummaryPlan.Create([item], new Dictionary<Guid, CondensedSummary> { [item.Id] = stored }, 140, classifyContext: true);

            Assert.Equal(SummaryPlan.SummaryReason.ContextMissing, Assert.Single(plan.Work).Reason);
        }

        [Fact]
        public void Plan_LeavesAJudgedItemAloneEvenWhenItSuitsNothing()
        {
            // An empty answer is a judgement that was paid for. Re-queueing it would
            // re-buy most of the library on every pass, since most of it lands empty.
            var item = Item("The Wrestler", LongOverview);
            var stored = Stored(item, LongOverview) with
            {
                ContextSourceHash = SummaryPlan.HashOverview(LongOverview),
            };

            var plan = SummaryPlan.Create([item], new Dictionary<Guid, CondensedSummary> { [item.Id] = stored }, 140, classifyContext: true);

            Assert.Empty(plan.Work);
            Assert.Equal(1, plan.UpToDate);
        }

        [Fact]
        public void Plan_ReJudgesAnItemWhoseOverviewWasRewrittenUnderneathIt()
        {
            var item = Item("The Wrestler", LongOverview);
            var stored = Stored(item, LongOverview) with
            {
                ContextSourceHash = SummaryPlan.HashOverview("something else entirely"),
            };

            var plan = SummaryPlan.Create([item], new Dictionary<Guid, CondensedSummary> { [item.Id] = stored }, 140, classifyContext: true);

            // Stale wins: the summary itself is fine, so this is the context hash
            // catching a rewrite the summary hash agreed with.
            Assert.Equal(SummaryPlan.SummaryReason.ContextMissing, Assert.Single(plan.Work).Reason);
        }

        [Fact]
        public void Plan_IgnoresContextEntirelyWhenTheSettingIsOff()
        {
            var item = Item("The Wrestler", LongOverview);

            var plan = SummaryPlan.Create([item], new Dictionary<Guid, CondensedSummary> { [item.Id] = Stored(item, LongOverview) }, 140);

            Assert.Empty(plan.Work);
        }

        // ---- the prompt half of the contract ----

        [Fact]
        public void Prompt_AsksForTheContextFieldsOnlyWhenClassifying()
        {
            var without = SummaryPromptBuilder.BuildSystemPrompt(90);
            var with = SummaryPromptBuilder.BuildSystemPrompt(90, classifyContext: true);

            Assert.DoesNotContain("\"w\"", without, StringComparison.Ordinal);
            Assert.Contains("\"w\"", with, StringComparison.Ordinal);
            Assert.Contains("\"d\"", with, StringComparison.Ordinal);
        }

        [Fact]
        public void Prompt_ListsTheWholeVocabularyAndNothingElse()
        {
            var prompt = SummaryPromptBuilder.BuildSystemPrompt(90, classifyContext: true);

            foreach (var word in ContextVocabulary.Weather)
            {
                Assert.Contains(word, prompt, StringComparison.Ordinal);
            }

            foreach (var word in ContextVocabulary.Dayparts)
            {
                Assert.Contains(word, prompt, StringComparison.Ordinal);
            }
        }

        [Theory]
        [InlineData(false, false, """{"summaries":[{"i":0,"s":"..."},{"i":1,"s":"..."}]}""")]
        [InlineData(true, false, "\"t\":[")]
        [InlineData(false, true, "\"w\":[")]
        [InlineData(true, true, "\"t\":[")]
        public void Prompt_ExampleObjectMatchesTheFieldsBeingAskedFor(
            bool tags,
            bool context,
            string expected)
        {
            // The example is the prompt's half of the schema contract. When the two
            // disagree the model writes the missing field into the previous string.
            var prompt = SummaryPromptBuilder.BuildSystemPrompt(
                90, tagCeiling: tags ? 6 : 0, allowInventedTags: false, classifyContext: context);

            Assert.Contains(expected, prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void Prompt_NeverAsksForTagsOrContextItDidNotEnable()
        {
            var plain = SummaryPromptBuilder.BuildSystemPrompt(90);

            Assert.DoesNotContain("\"t\":[", plain, StringComparison.Ordinal);
            Assert.DoesNotContain("\"w\":[", plain, StringComparison.Ordinal);
            Assert.DoesNotContain("\"d\":[", plain, StringComparison.Ordinal);
        }

        [Fact]
        public void Prompt_HoldsWeatherAndTimeOfDayToDifferentBars()
        {
            // They fail in opposite directions. Weather applied to everything stops
            // selecting anything; time of day applied to almost nothing starves the
            // row — measured on a real library, six items in all suited a morning.
            var prompt = SummaryPromptBuilder.BuildSystemPrompt(90, classifyContext: true);

            Assert.Contains("Be sparing", prompt, StringComparison.Ordinal);
            Assert.Contains("empty \"w\" is the correct answer", prompt, StringComparison.Ordinal);
            Assert.Contains("give MOST", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void Prompt_NamesTheEveningDefaultAsTheTrapItIs()
        {
            // The specific bias to resist: asked what hour a film suits, a model
            // reaches for "evening" far more often than is true, which leaves one
            // useful daypart and three empty ones.
            var prompt = SummaryPromptBuilder.BuildSystemPrompt(90, classifyContext: true);

            Assert.Contains("not everything is an evening film", prompt, StringComparison.Ordinal);
        }
    }
}
