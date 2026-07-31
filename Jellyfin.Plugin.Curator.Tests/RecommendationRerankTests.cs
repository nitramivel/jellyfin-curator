using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Curator.Configuration;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Core.Recommendations;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Re-ordering a viewer's shortlist with a model.
    ///
    /// The property that matters most here is not that a good answer is honoured —
    /// it is that a bad one cannot hurt. Every other parser in this plugin drops
    /// what it cannot read; dropping an index here would silently delete an item
    /// from somebody's home screen row, so the answer is treated as a preference
    /// over the shortlist rather than a replacement for it.
    /// </summary>
    public class RecommendationRerankTests
    {
        private static string Response(params int[] order)
            => JsonSerializer.Serialize(new { order });

        private static IReadOnlyList<string> List(int count)
            => [.. Enumerable.Range(0, count).Select(i => "item" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))];

        [Fact]
        public void AGoodAnswerIsHonouredExactly()
        {
            var result = RecommendationParser.Reorder(Response(2, 0, 1), List(3));

            Assert.Equal(["item2", "item0", "item1"], result.Ordered);
            Assert.Equal(0, result.DiscardedCount);
            Assert.Equal(0, result.MissingCount);
        }

        [Fact]
        public void ItemsTheModelOmitsKeepTheirPlaceInsteadOfVanishing()
        {
            // The whole safety property. A model that answers for the first two of
            // five must not cost the viewer the other three.
            var result = RecommendationParser.Reorder(Response(4, 3), List(5));

            Assert.Equal(["item4", "item3", "item0", "item1", "item2"], result.Ordered);
            Assert.Equal(3, result.MissingCount);
        }

        [Fact]
        public void ARepeatedIndexIsNotHonouredTwice()
        {
            // Honouring it would put one item on the row twice and push another off
            // the end of a capped playlist.
            var result = RecommendationParser.Reorder(Response(1, 1, 1, 0), List(3));

            Assert.Equal(["item1", "item0", "item2"], result.Ordered);
            Assert.Equal(3, result.Ordered.Count);
            Assert.Equal(2, result.DiscardedCount);
        }

        [Theory]
        [InlineData(9)]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        public void AnIndexOutsideTheShortlistIsDiscarded(int rogue)
        {
            // Hard rule 1: the model works in list-local indexes and cannot reach an
            // item that was not put in front of it.
            var result = RecommendationParser.Reorder(Response(rogue, 0), List(3));

            Assert.Equal(3, result.Ordered.Count);
            Assert.Equal(1, result.DiscardedCount);
            Assert.Equal("item0", result.Ordered[0]);
        }

        [Fact]
        public void AnEmptyAnswerLeavesTheWeightedOrderUntouched()
        {
            var result = RecommendationParser.Reorder(Response(), List(4));

            Assert.Equal(List(4), result.Ordered);
            Assert.Equal(4, result.MissingCount);
        }

        [Fact]
        public void NoAnswerAtAllIsAFormatErrorRatherThanAnEmptyRow()
        {
            // The caller catches this and keeps the weighted order. It must never be
            // mistaken for "the model wants an empty list".
            Assert.Throws<FormatException>(() => RecommendationParser.Reorder("no json here", List(3)));
            Assert.Throws<FormatException>(() => RecommendationParser.Reorder("""{"nope":[]}""", List(3)));
        }

        [Fact]
        public void EveryItemSurvivesWhateverTheModelSays()
        {
            // Property check over the failure shapes together: garbage in, same items
            // out, every time.
            var shortlist = List(6);
            foreach (var answer in new[]
            {
                Response(),
                Response(0),
                Response(5, 5, 5),
                Response(-3, 99, 2),
                Response(5, 4, 3, 2, 1, 0),
            })
            {
                var result = RecommendationParser.Reorder(answer, shortlist);
                Assert.Equal(shortlist.Count, result.Ordered.Count);
                Assert.Equal([.. shortlist.OrderBy(x => x, StringComparer.Ordinal)],
                    [.. result.Ordered.OrderBy(x => x, StringComparer.Ordinal)]);
            }
        }

        // ---- the prompt ----

        private static MediaItemRecord Item(string name, bool watched = false) => new()
        {
            Id = Guid.NewGuid(),
            Kind = MediaKind.Movie,
            Name = name,
            Overview = "an overview of " + name,
            Genres = ["Drama"],
        };

        [Fact]
        public void ThePromptStatesTheListLengthAndThePermutationRule()
        {
            var system = RecommendationPromptBuilder.BuildSystemPrompt(30);

            Assert.Contains("shortlist of 30 items", system, StringComparison.Ordinal);
            Assert.Contains("permutation of 0..30-1", system, StringComparison.Ordinal);
            Assert.Contains("never repeat one", system, StringComparison.Ordinal);
        }

        [Fact]
        public void ThePromptAsksForASpreadOfMoodsRatherThanASort()
        {
            // The reason to spend a call at all. Sorting by fit is what the weighted
            // ranker already did for free.
            var system = RecommendationPromptBuilder.BuildSystemPrompt(30);

            Assert.Contains("Vary the mood", system, StringComparison.Ordinal);
            Assert.Contains("Do not simply sort", system, StringComparison.Ordinal);
            Assert.Contains("leave it", system, StringComparison.Ordinal);
        }

        [Fact]
        public void TheUserPromptNumbersFromZeroAndMarksOnlyWatchedItems()
        {
            var a = Item("Alpha");
            var b = Item("Beta");

            var prompt = RecommendationPromptBuilder.BuildUserPrompt([a, b], [b.Id]);

            Assert.Contains("\"i\":0", prompt, StringComparison.Ordinal);
            Assert.Contains("\"i\":1", prompt, StringComparison.Ordinal);

            // Only the watched one carries the flag; a false on every line is pure
            // length across a per-viewer prompt.
            Assert.Equal(1, prompt.Split("\"watched\":true").Length - 1);
            Assert.DoesNotContain("\"watched\":false", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void TheUserPromptNeverCarriesAJellyfinId()
        {
            // Hard rule 1, checked directly.
            var a = Item("Alpha");

            var prompt = RecommendationPromptBuilder.BuildUserPrompt([a], []);

            Assert.DoesNotContain(a.Id.ToString(), prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(a.Id.ToString("N"), prompt, StringComparison.OrdinalIgnoreCase);
        }

        // ---- the settings ----

        [Fact]
        public void ModelRankingIsOffByDefaultBecauseItIsTheOnlyPartThatCosts()
        {
            var config = new PluginConfiguration();

            Assert.False(config.ModelRankedRecommendations);
            Assert.Equal(string.Empty, config.RecommendationModelProfileId);

            // And the head sent is bounded by default, so switching it on cannot
            // quietly send a 200-item playlist per viewer per refresh.
            Assert.Equal(30, config.MaxRecommendationsToRank);
        }
    }
}
