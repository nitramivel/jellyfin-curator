using System;
using System.Collections.Generic;
using System.Linq;
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
    }
}
