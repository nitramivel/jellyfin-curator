using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Curator.Core.Summaries
{
    /// <summary>
    /// What a distillation pass does with a request the model did not answer
    /// properly.
    ///
    /// <para>
    /// Before this existed, it did nothing: a batch whose answer would not parse
    /// counted every item in it as failed, and a batch answered for one item out of
    /// thirty counted the other twenty-nine as failed without a word. Measured on a
    /// 212-item pass, 185 items were written off that way — the overwhelming
    /// majority of them recoverable, because the cause was an output cap that a
    /// smaller request simply does not hit.
    /// </para>
    ///
    /// <para>
    /// Pure so the bound is pinned by tests rather than discovered on a bill: every
    /// retry is a paid call, and an unbounded split of a request the model will
    /// never answer would walk the whole batch down to single items and charge for
    /// each one.
    /// </para>
    /// </summary>
    public static class SummaryRetryPlan
    {
        /// <summary>
        /// How many times a set of items may be split or requeued before the pass
        /// gives up on it.
        /// </summary>
        /// <remarks>
        /// Three halvings take a 30-item batch to under four items a request, which
        /// is inside any sane output cap. If that still cannot be answered, the batch
        /// size was never the problem and more calls will not discover that.
        /// </remarks>
        public const int MaxAttempts = 3;

        /// <summary>
        /// Splits a request whose answer was unusable into two smaller ones.
        /// </summary>
        /// <remarks>
        /// Halving rather than dropping to single items on the first failure: the
        /// usual cause is the response being cut off by the output cap, and half as
        /// many items in a request is usually already under it. Going straight to one
        /// item per call would recover the same summaries for many times the money.
        /// </remarks>
        /// <typeparam name="T">The item type.</typeparam>
        /// <param name="pending">The items the failed request covered.</param>
        /// <param name="attempt">How many times this set has already been retried.</param>
        /// <returns>
        /// The two halves to retry, or empty when the set must be given up — either
        /// because it is down to a single item or because it is out of attempts.
        /// </returns>
        public static IReadOnlyList<IReadOnlyList<T>> SplitForRetry<T>(IReadOnlyList<T> pending, int attempt)
        {
            ArgumentNullException.ThrowIfNull(pending);

            if (pending.Count <= 1 || attempt >= MaxAttempts)
            {
                return [];
            }

            var half = pending.Count / 2;
            return [[.. pending.Take(half)], [.. pending.Skip(half)]];
        }

        /// <summary>
        /// Whether items the model returned nothing for are worth asking again.
        /// </summary>
        /// <remarks>
        /// A partial answer is not an error — it parses, it is stored, and the run
        /// carries on. That is exactly why it went unnoticed: the missing items were
        /// counted as failures and never retried, so a model that answered for one
        /// item in thirty looked like twenty-nine items that could not be summarized.
        /// </remarks>
        /// <param name="missingCount">How many items came back unanswered.</param>
        /// <param name="attempt">How many times this set has already been retried.</param>
        /// <returns>Whether to ask again for the missing items.</returns>
        public static bool ShouldRequeue(int missingCount, int attempt)
            => missingCount > 0 && attempt < MaxAttempts;

        /// <summary>
        /// Whether an answer was so incomplete that the request itself is the
        /// problem, rather than the model having skipped a couple of items.
        /// </summary>
        /// <remarks>
        /// Observed: four separate batches came back with a summary for exactly one
        /// item out of roughly twenty, and the answer parsed cleanly every time.
        /// Asking again for the nineteen that were missed would be very nearly the
        /// same request that just failed; the fix is to halve it. Two items answered
        /// out of thirty is a request that was too big, while twenty-eight out of
        /// thirty is a model that skipped two, and those want opposite treatment.
        /// </remarks>
        /// <param name="answered">How many items the model returned a summary for.</param>
        /// <param name="asked">How many items the request covered.</param>
        /// <returns>Whether the remainder should be split rather than simply retried.</returns>
        public static bool AnswerWasSeverelyPartial(int answered, int asked)
            => asked > 1 && answered * 2 < asked;
    }
}
