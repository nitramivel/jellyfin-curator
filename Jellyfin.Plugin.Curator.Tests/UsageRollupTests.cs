using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Usage;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    public class UsageRollupTests
    {
        private static readonly DateTime Today = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        private static UsageCall Call(
            string phase = UsagePhase.Discovery,
            string model = "grok-4.5",
            decimal? cost = 0.10m,
            string outcome = "ok",
            int daysAgo = 0,
            long input = 1000,
            long cached = 0,
            long output = 100,
            string provider = "Grok")
            => new(
                Today.AddDays(-daysAgo),
                phase,
                model,
                provider,
                input,
                cached,
                output,
                0,
                cost,
                outcome,
                Guid.Empty);

        private static UsageReport Build(params UsageCall[] calls)
            => UsageRollup.Build(calls, [], days: 7, today: Today);

        // ---- the two rules that must never bend ----

        [Fact]
        public void AnUnpricedCallIsCountedButNeverCostedAtZero()
        {
            // A run made before the rates were typed in cost real money. Folding it
            // in at zero would understate the bill in the one direction that matters.
            var report = Build(Call(cost: 0.25m), Call(cost: null), Call(cost: null));

            Assert.Equal(0.25m, report.Overall.CostUsd);
            Assert.Equal(3, report.Overall.Calls);
            Assert.Equal(2, report.Overall.UnpricedCalls);
        }

        [Fact]
        public void AWastedCallIsStillChargedToTheTotal_AndCalledOutSeparately()
        {
            // An answer that would not parse was billed exactly like one that did.
            var report = Build(
                Call(cost: 0.10m),
                Call(cost: 0.30m, outcome: "unparseable"),
                Call(cost: 0.20m, outcome: "error"));

            Assert.Equal(0.60m, report.Overall.CostUsd);
            Assert.Equal(0.50m, report.Overall.WastedCostUsd);
            Assert.Equal(2, report.Overall.WastedCalls);
        }

        // ---- per model ----

        [Fact]
        public void ModelsAreSeparatedAndOrderedByWhatTheyCost()
        {
            var report = Build(
                Call(model: "grok-4.5", cost: 0.10m),
                Call(model: "claude-opus-5", cost: 2.00m, provider: "Anthropic"),
                Call(model: "grok-4.5", cost: 0.15m));

            Assert.Equal(["claude-opus-5", "grok-4.5"], report.Models.Select(m => m.Model).ToArray());
            Assert.Equal(2.00m, report.Models[0].Totals.CostUsd);
            Assert.Equal("Anthropic", report.Models[0].Provider);
            Assert.Equal(0.25m, report.Models[1].Totals.CostUsd);
            Assert.Equal(2, report.Models[1].Totals.Calls);
        }

        [Fact]
        public void AModelListsThePassesThatUsedIt()
        {
            var report = Build(
                Call(model: "grok-4.5", phase: UsagePhase.Discovery),
                Call(model: "grok-4.5", phase: UsagePhase.Summaries));

            Assert.Equal(
                [UsagePhase.Discovery, UsagePhase.Summaries],
                report.Models.Single().Phases.ToArray());
        }

        [Fact]
        public void ACallWithNoRecordedModelIsShownAsUnknown_NotAsBlank()
        {
            // Schema 1 run logs recorded no per-call model.
            var report = Build(Call(model: "", provider: ""));

            Assert.Equal("unknown", report.Models.Single().Model);
            Assert.Equal("unknown", report.Models.Single().Provider);
        }

        // ---- per pass ----

        [Fact]
        public void EveryPaidPassIsBrokenOutWithALabel()
        {
            var report = Build(
                Call(phase: UsagePhase.Discovery, cost: 1.00m),
                Call(phase: UsagePhase.Personal, cost: 0.50m),
                Call(phase: UsagePhase.Summaries, cost: 0.40m),
                Call(phase: UsagePhase.Rerank, cost: 0.05m));

            Assert.Equal(
                ["Discovery", "Viewer passes", "Summaries and tags", "Recommendation re-rank"],
                report.Phases.Select(p => p.Label).ToArray());
            Assert.All(report.Phases, p => Assert.True(p.Known));
            Assert.All(report.Phases, p => Assert.NotEqual(string.Empty, p.Description));
        }

        [Fact]
        public void TagConsolidationIsNotASeparateCost()
        {
            // Consolidation happens in the same model call as the summary and after
            // it, deliberately. There is no second call to attribute, so the label
            // has to say so rather than invent a split.
            var summaries = UsagePhase.Describe(UsagePhase.Summaries);

            Assert.NotNull(summaries);
            Assert.Contains("same call", summaries!.Value.Description, StringComparison.Ordinal);
        }

        [Fact]
        public void DiscoveryAndItsBatchesAreOneLine()
        {
            // A split discovery pass records each batch under its own phase name.
            // They are the same pass and the same money.
            Assert.Equal(
                UsagePhase.Describe(UsagePhase.Discovery),
                UsagePhase.Describe(UsagePhase.DiscoveryBatch));
        }

        [Fact]
        public void AnUnrecognisedPhaseIsShownUnderItsOwnName_NotSweptIntoOther()
        {
            // A pass that starts spending without anyone noticing is exactly what
            // this breakdown exists to catch, so an unknown phase must stay visible.
            var report = Build(Call(phase: "something-new", cost: 0.99m));

            var phase = report.Phases.Single();
            Assert.Equal("something-new", phase.Label);
            Assert.False(phase.Known);
            Assert.Equal(0.99m, phase.Totals.CostUsd);
        }

        // ---- the daily series ----

        [Fact]
        public void QuietDaysArePresentAsZeroes_SoAGapLooksLikeAGap()
        {
            // Runs are weekly and the summary pass daily. A series that only carried
            // the days with spend would make a weekly run look continuous.
            var report = Build(Call(daysAgo: 0, cost: 1m), Call(daysAgo: 3, cost: 2m));

            Assert.Equal(7, report.Daily.Count);
            Assert.Equal("2026-07-26", report.Daily[0].Date);
            Assert.Equal("2026-08-01", report.Daily[^1].Date);
            Assert.Equal(0m, report.Daily[0].CostUsd);
            Assert.Equal(2m, report.Daily[3].CostUsd);
            Assert.Equal(1m, report.Daily[^1].CostUsd);
        }

        [Fact]
        public void ADayIsSplitByModelSoTheChartCanStackIt()
        {
            var report = Build(
                Call(daysAgo: 1, model: "grok-4.5", cost: 0.30m),
                Call(daysAgo: 1, model: "claude-opus-5", cost: 0.70m),
                Call(daysAgo: 1, model: "grok-4.5", cost: 0.20m));

            var day = report.Daily.Single(d => d.Date == "2026-07-31");

            Assert.Equal(1.20m, day.CostUsd);
            Assert.Equal(0.50m, day.ByModel["grok-4.5"]);
            Assert.Equal(0.70m, day.ByModel["claude-opus-5"]);
            Assert.Equal(day.CostUsd, day.ByModel.Values.Sum());
        }

        [Fact]
        public void CallsOlderThanTheWindowAreLeftOutOfTheSeriesButNotTheTotal()
        {
            // The headline number describes everything on record; the chart
            // describes the window. Saying so is better than silently disagreeing.
            var report = Build(Call(daysAgo: 0, cost: 1m), Call(daysAgo: 30, cost: 5m));

            Assert.Equal(6m, report.Overall.CostUsd);
            Assert.Equal(1m, report.Daily.Sum(d => d.CostUsd));
        }

        // ---- tokens ----

        [Fact]
        public void CacheHitRateCountsCachedTokensAsInput()
        {
            var report = Build(Call(input: 30_000, cached: 70_000));

            Assert.Equal(100_000, report.Overall.TotalInputTokens);
            Assert.Equal(0.7, report.Overall.CacheHitRate!.Value, 3);
        }

        [Fact]
        public void NoInputMeansNoCacheHitRateRatherThanZero()
        {
            // Zero would read as "the cache never hit", which is a different claim
            // from "nothing was sent".
            Assert.Null(UsageTotals.Empty.CacheHitRate);
        }

        // ---- nothing recorded ----

        [Fact]
        public void NoCallsStillProducesAFullSeries()
        {
            var report = UsageRollup.Build([], [], days: 7, today: Today);

            Assert.Equal(7, report.Daily.Count);
            Assert.Equal(0m, report.Overall.CostUsd);
            Assert.Empty(report.Models);
            Assert.Empty(report.Phases);
            Assert.Null(report.FirstCallAt);
        }

        [Fact]
        public void TheCoveredRangeIsReadOffTheCalls()
        {
            var report = Build(Call(daysAgo: 5), Call(daysAgo: 0), Call(daysAgo: 2));

            Assert.Equal(Today.AddDays(-5), report.FirstCallAt);
            Assert.Equal(Today, report.LastCallAt);
        }
    }
}
