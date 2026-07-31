using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Summaries;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Recovering a distillation batch the model answered badly, and the bound that
    /// stops recovery costing more than the work.
    ///
    /// Measured on the pass that prompted this: 212 items, three batches cut off by
    /// the output cap and four more answered for a single item each. 185 items were
    /// written off, essentially all of them recoverable in a smaller request.
    /// </summary>
    public class SummaryRetryPlanTests
    {
        private static IReadOnlyList<int> Items(int count) => [.. Enumerable.Range(0, count)];

        [Fact]
        public void AnUnusableAnswerIsHalvedRatherThanWrittenOff()
        {
            var halves = SummaryRetryPlan.SplitForRetry(Items(30), attempt: 0);

            Assert.Equal(2, halves.Count);
            Assert.Equal(15, halves[0].Count);
            Assert.Equal(15, halves[1].Count);

            // Every item survives the split — that is the whole point.
            Assert.Equal(Items(30), halves.SelectMany(h => h).ToList());
        }

        [Fact]
        public void AnOddCountSplitsWithoutLosingTheOddItem()
        {
            var halves = SummaryRetryPlan.SplitForRetry(Items(31), attempt: 0);

            Assert.Equal(31, halves.Sum(h => h.Count));
            Assert.Equal(Items(31), halves.SelectMany(h => h).ToList());
        }

        [Fact]
        public void ASingleItemIsNotSplitAgain()
        {
            // Nothing left to halve; one item that still cannot be answered is the
            // only kind of item genuinely worth giving up on.
            Assert.Empty(SummaryRetryPlan.SplitForRetry(Items(1), attempt: 0));
            Assert.Empty(SummaryRetryPlan.SplitForRetry(Items(0), attempt: 0));
        }

        [Fact]
        public void SplittingStopsAtTheAttemptCeiling()
        {
            Assert.NotEmpty(SummaryRetryPlan.SplitForRetry(Items(30), attempt: SummaryRetryPlan.MaxAttempts - 1));
            Assert.Empty(SummaryRetryPlan.SplitForRetry(Items(30), attempt: SummaryRetryPlan.MaxAttempts));
            Assert.Empty(SummaryRetryPlan.SplitForRetry(Items(30), attempt: SummaryRetryPlan.MaxAttempts + 1));
        }

        [Fact]
        public void AHopelessBatchCostsABoundedNumberOfCalls()
        {
            // The guard that matters: every retry is a paid call, so a batch the model
            // will never answer must not walk itself down to one call per item. Drive
            // the real thing to exhaustion and count.
            var queue = new Queue<(IReadOnlyList<int> Items, int Attempt)>();
            queue.Enqueue((Items(30), 0));

            var calls = 0;
            var abandoned = 0;
            while (queue.Count > 0)
            {
                var (pending, attempt) = queue.Dequeue();
                calls++;

                var halves = SummaryRetryPlan.SplitForRetry(pending, attempt);
                if (halves.Count == 0)
                {
                    abandoned += pending.Count;
                    continue;
                }

                foreach (var half in halves)
                {
                    queue.Enqueue((half, attempt + 1));
                }
            }

            // 1 + 2 + 4 + 8: three halvings, then the leaves are given up on.
            Assert.Equal(15, calls);
            Assert.Equal(30, abandoned);
        }

        [Fact]
        public void UnansweredItemsAreAskedForAgain()
        {
            // The quiet half of the bug. A partial answer parses cleanly and stores
            // what it got, so nothing looked wrong while 29 of 30 items went missing.
            Assert.True(SummaryRetryPlan.ShouldRequeue(missingCount: 29, attempt: 0));
            Assert.True(SummaryRetryPlan.ShouldRequeue(missingCount: 1, attempt: SummaryRetryPlan.MaxAttempts - 1));
        }

        [Theory]
        [InlineData(1, 20, true)]    // the shape actually observed, four times over
        [InlineData(1, 30, true)]
        [InlineData(14, 30, true)]   // just under half
        [InlineData(15, 30, false)]  // exactly half is not "severely"
        [InlineData(28, 30, false)]  // a model that skipped two
        [InlineData(0, 1, false)]    // a single unanswered item cannot be split
        public void ASeverelyPartialAnswerIsDistinguishedFromAFewSkippedItems(int answered, int asked, bool severe)
        {
            Assert.Equal(severe, SummaryRetryPlan.AnswerWasSeverelyPartial(answered, asked));
        }

        [Fact]
        public void AOneOfTwentyAnswerRetriesTheRemainderInSmallerRequests()
        {
            // End to end on the real shape: 1 of 20 answered, so the other 19 must not
            // go back as a single 19-item request that will fail the same way.
            const int Answered = 1;
            var missing = Items(19);

            Assert.True(SummaryRetryPlan.AnswerWasSeverelyPartial(Answered, asked: 20));

            var parts = SummaryRetryPlan.SplitForRetry(missing, attempt: 0);
            Assert.Equal(2, parts.Count);
            Assert.Equal(19, parts.Sum(p => p.Count));
            Assert.All(parts, p => Assert.True(p.Count < missing.Count));
        }

        [Fact]
        public void AFullyAnsweredRequestIsNeverRequeued()
        {
            Assert.False(SummaryRetryPlan.ShouldRequeue(missingCount: 0, attempt: 0));
        }

        [Fact]
        public void RequeueingStopsAtTheAttemptCeiling()
        {
            // Otherwise an item the model simply refuses to summarize loops forever,
            // paying each time round.
            Assert.False(SummaryRetryPlan.ShouldRequeue(missingCount: 5, attempt: SummaryRetryPlan.MaxAttempts));
        }
    }
}
