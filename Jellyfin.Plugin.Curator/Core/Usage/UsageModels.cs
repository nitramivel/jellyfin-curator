using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Curator.Core.Usage
{
    /// <summary>
    /// The passes that can spend money, as they are named in a run log.
    /// </summary>
    /// <remarks>
    /// Kept as constants rather than an enum because these strings are already
    /// written into every run log on disk and are the join key between a call and
    /// its label. An unrecognised phase is shown under its own raw name rather than
    /// bucketed into "other" — a pass that starts spending without anyone noticing
    /// is the failure this whole breakdown exists to prevent.
    /// </remarks>
    public static class UsagePhase
    {
        /// <summary>The library-wide pass that coins shared rows.</summary>
        public const string Discovery = "discovery";

        /// <summary>One batch of a split discovery pass.</summary>
        public const string DiscoveryBatch = "discovery-batch";

        /// <summary>One viewer's own pass.</summary>
        public const string Personal = "personal";

        /// <summary>The condensing pass, which also consolidates tags.</summary>
        public const string Summaries = "summaries";

        /// <summary>Re-ordering one viewer's recommendation shortlist.</summary>
        public const string Rerank = "rerank";

        /// <summary>
        /// A readable label for a phase, and what it actually buys.
        /// </summary>
        /// <param name="phase">The phase as recorded.</param>
        /// <returns>The label, and a one-line description, or null for an unknown phase.</returns>
        public static (string Label, string Description)? Describe(string? phase)
        {
            return phase switch
            {
                Discovery or DiscoveryBatch => (
                    "Discovery",
                    "One pass over the whole library, coining the rows everyone shares. Usually one call, and the largest single prompt."),
                Personal => (
                    "Viewer passes",
                    "One call per viewer per run, inventing rows from their own watch history. Normally most of a run's calls."),
                Summaries => (
                    "Summaries and tags",
                    "Condensing long overviews, and consolidating tags in the same call — one model call produces both, so their cost cannot be split apart."),
                Rerank => (
                    "Recommendation re-rank",
                    "One call per viewer per refresh, ordering their spotlight row. Off by default, and runs six-hourly when on."),
                _ => null,
            };
        }
    }

    /// <summary>
    /// One billable call, reduced to what a cost breakdown needs.
    /// </summary>
    /// <param name="At">When the call was made (UTC).</param>
    /// <param name="Phase">Which pass it belonged to.</param>
    /// <param name="Model">The model it went to.</param>
    /// <param name="Provider">The provider it went to.</param>
    /// <param name="InputTokens">Uncached input tokens billed.</param>
    /// <param name="CachedTokens">Input tokens served from cache — discounted, never free.</param>
    /// <param name="OutputTokens">Output tokens billed.</param>
    /// <param name="CacheWriteTokens">Input tokens written to cache. Carries a premium that is not priced anywhere.</param>
    /// <param name="CostUsd">What it cost, or null when no rates were configured.</param>
    /// <param name="Outcome">"ok", "unparseable", or "error".</param>
    /// <param name="RunId">The run it belonged to.</param>
    public sealed record UsageCall(
        DateTime At,
        string Phase,
        string Model,
        string Provider,
        long InputTokens,
        long CachedTokens,
        long OutputTokens,
        long CacheWriteTokens,
        decimal? CostUsd,
        string Outcome,
        Guid RunId);

    /// <summary>
    /// Money and tokens for some slice of the record.
    /// </summary>
    /// <param name="CostUsd">What the priced calls in this slice cost.</param>
    /// <param name="Calls">How many calls, priced or not.</param>
    /// <param name="UnpricedCalls">
    /// How many carried no price. Reported separately and never as zero cost: a
    /// call made before the rates were typed in cost real money, and folding it in
    /// at zero would quietly understate the bill.
    /// </param>
    /// <param name="InputTokens">Uncached input tokens.</param>
    /// <param name="CachedTokens">Input tokens served from cache.</param>
    /// <param name="OutputTokens">Output tokens.</param>
    /// <param name="CacheWriteTokens">Input tokens written to cache.</param>
    /// <param name="WastedCostUsd">
    /// What the calls that produced nothing usable cost — errors and unparseable
    /// answers. Paid for in full, which is why it is called out rather than buried.
    /// </param>
    /// <param name="WastedCalls">How many of those there were.</param>
    public sealed record UsageTotals(
        decimal CostUsd,
        int Calls,
        int UnpricedCalls,
        long InputTokens,
        long CachedTokens,
        long OutputTokens,
        long CacheWriteTokens,
        decimal WastedCostUsd,
        int WastedCalls)
    {
        /// <summary>Gets every input token, cached or not.</summary>
        public long TotalInputTokens => InputTokens + CachedTokens;

        /// <summary>
        /// Gets the share of input tokens served from cache, 0 to 1, or null when
        /// there was no input to serve.
        /// </summary>
        public double? CacheHitRate => TotalInputTokens > 0
            ? (double)CachedTokens / TotalInputTokens
            : null;

        /// <summary>Gets an empty total.</summary>
        public static UsageTotals Empty { get; } = new(0m, 0, 0, 0, 0, 0, 0, 0m, 0);
    }

    /// <summary>
    /// What one model cost.
    /// </summary>
    /// <param name="Model">The model identifier.</param>
    /// <param name="Provider">The provider it was reached through.</param>
    /// <param name="Totals">Its spend.</param>
    /// <param name="Phases">Which passes used it, most expensive first.</param>
    public sealed record UsageByModel(
        string Model,
        string Provider,
        UsageTotals Totals,
        IReadOnlyList<string> Phases);

    /// <summary>
    /// What one pass cost.
    /// </summary>
    /// <param name="Phase">The phase as recorded.</param>
    /// <param name="Label">A readable name, or the raw phase when it is not one we know.</param>
    /// <param name="Description">What the pass buys, or empty for an unknown phase.</param>
    /// <param name="Known">Whether this is a phase Curator knows about.</param>
    /// <param name="Totals">Its spend.</param>
    public sealed record UsageByPhase(
        string Phase,
        string Label,
        string Description,
        bool Known,
        UsageTotals Totals);

    /// <summary>
    /// One day's spend, split by model so a chart can stack it.
    /// </summary>
    /// <param name="Date">The day (UTC), as yyyy-MM-dd.</param>
    /// <param name="CostUsd">Everything spent that day.</param>
    /// <param name="ByModel">Cost per model, keyed on model identifier.</param>
    /// <param name="Calls">How many calls were made.</param>
    public sealed record UsageDay(
        string Date,
        decimal CostUsd,
        IReadOnlyDictionary<string, decimal> ByModel,
        int Calls);

    /// <summary>
    /// One run's spend, for the history table.
    /// </summary>
    /// <param name="RunId">The run.</param>
    /// <param name="Trigger">What started it.</param>
    /// <param name="Status">How it ended.</param>
    /// <param name="StartedAt">When it started (UTC).</param>
    /// <param name="Models">Every model it used.</param>
    /// <param name="Totals">What it spent.</param>
    public sealed record UsageRun(
        Guid RunId,
        string Trigger,
        string Status,
        DateTime StartedAt,
        IReadOnlyList<string> Models,
        UsageTotals Totals);

    /// <summary>
    /// The whole cost picture.
    /// </summary>
    /// <param name="Overall">Everything, added up.</param>
    /// <param name="Models">Per model, most expensive first.</param>
    /// <param name="Phases">Per pass, most expensive first.</param>
    /// <param name="Daily">Per day, oldest first, with a row for every day in range even when nothing was spent.</param>
    /// <param name="Runs">Recent runs, newest first.</param>
    /// <param name="FirstCallAt">When the earliest recorded call happened, or null when there are none.</param>
    /// <param name="LastCallAt">When the most recent one happened, or null.</param>
    /// <param name="RunsCovered">How many run logs this was built from.</param>
    public sealed record UsageReport(
        UsageTotals Overall,
        IReadOnlyList<UsageByModel> Models,
        IReadOnlyList<UsageByPhase> Phases,
        IReadOnlyList<UsageDay> Daily,
        IReadOnlyList<UsageRun> Runs,
        DateTime? FirstCallAt,
        DateTime? LastCallAt,
        int RunsCovered)
    {
        /// <summary>Gets a report over nothing.</summary>
        public static UsageReport Empty { get; } = new(
            UsageTotals.Empty, [], [], [], [], null, null, 0);
    }
}
