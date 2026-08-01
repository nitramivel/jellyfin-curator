using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Curator.Core.Usage
{
    /// <summary>
    /// Turns a flat list of billable calls into the cost breakdown the Usage tab
    /// draws. Pure: calls in, report out, no clock and no filesystem.
    ///
    /// <para>
    /// Two rules run through all of it, and both exist because the alternative
    /// misleads. <b>An unpriced call is never counted as free</b> — it is counted,
    /// and reported separately, so a total can never read low because nobody typed
    /// the rates in. And <b>a wasted call still costs</b>: an answer that would not
    /// parse was billed exactly like one that did, so errors and unparseable
    /// responses are priced into every total and then called out again on their own.
    /// </para>
    /// </summary>
    public static class UsageRollup
    {
        /// <summary>
        /// Builds the report.
        /// </summary>
        /// <param name="calls">Every billable call, in any order.</param>
        /// <param name="runs">The runs those calls came from, for the history table.</param>
        /// <param name="days">
        /// How many days the daily series covers, counted back from
        /// <paramref name="today"/>. Days with no spend are present with a zero, so a
        /// chart shows a gap as a gap rather than closing it up.
        /// </param>
        /// <param name="today">The last day of the series (UTC).</param>
        /// <returns>The report.</returns>
        public static UsageReport Build(
            IReadOnlyList<UsageCall> calls,
            IReadOnlyList<UsageRun> runs,
            int days,
            DateTime today)
        {
            ArgumentNullException.ThrowIfNull(calls);
            ArgumentNullException.ThrowIfNull(runs);

            if (calls.Count == 0)
            {
                return UsageReport.Empty with
                {
                    Daily = EmptySeries(days, today),
                    Runs = runs,
                    RunsCovered = runs.Count,
                };
            }

            var models = calls
                .GroupBy(c => c.Model, StringComparer.OrdinalIgnoreCase)
                .Select(group => new UsageByModel(
                    NameOrUnknown(group.Key),

                    // One model reached through two providers is not something the
                    // configuration allows, so the first is the answer rather than a
                    // list — but it is read off the calls rather than assumed.
                    NameOrUnknown(group.Select(c => c.Provider).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))),
                    SumCore(group),
                    group.Select(c => c.Phase).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList()))
                .OrderByDescending(m => m.Totals.CostUsd)
                .ThenByDescending(m => m.Totals.Calls)
                .ToList();

            var phases = calls
                .GroupBy(c => c.Phase, StringComparer.Ordinal)
                .Select(group =>
                {
                    var described = UsagePhase.Describe(group.Key);
                    return new UsageByPhase(
                        group.Key,
                        described?.Label ?? NameOrUnknown(group.Key),
                        described?.Description ?? string.Empty,
                        described is not null,
                        SumCore(group));
                })
                .OrderByDescending(p => p.Totals.CostUsd)
                .ThenByDescending(p => p.Totals.Calls)
                .ToList();

            return new UsageReport(
                SumCore(calls),
                models,
                phases,
                Daily(calls, days, today),
                runs,
                calls.Min(c => c.At),
                calls.Max(c => c.At),
                runs.Count);
        }

        /// <summary>
        /// Adds a set of calls up.
        /// </summary>
        /// <remarks>
        /// Public so a caller wanting one run's figures can ask for exactly that,
        /// rather than building a whole report and reading its total off the front.
        /// </remarks>
        /// <param name="calls">The calls to total.</param>
        /// <returns>Their combined spend.</returns>
        public static UsageTotals Sum(IEnumerable<UsageCall> calls)
        {
            ArgumentNullException.ThrowIfNull(calls);
            return SumCore(calls);
        }

        private static UsageTotals SumCore(IEnumerable<UsageCall> calls)
        {
            var cost = 0m;
            var wastedCost = 0m;
            var count = 0;
            var unpriced = 0;
            var wasted = 0;
            long input = 0, cached = 0, output = 0, cacheWrite = 0;

            foreach (var call in calls)
            {
                count++;
                input += call.InputTokens;
                cached += call.CachedTokens;
                output += call.OutputTokens;
                cacheWrite += call.CacheWriteTokens;

                if (call.CostUsd is { } priced)
                {
                    cost += priced;
                }
                else
                {
                    // Counted, never zeroed. A run made before the prices were
                    // entered still cost money, and a total that quietly absorbed it
                    // at zero would be wrong in the one direction that matters.
                    unpriced++;
                }

                if (!IsUsable(call.Outcome))
                {
                    wasted++;
                    wastedCost += call.CostUsd ?? 0m;
                }
            }

            return new UsageTotals(cost, count, unpriced, input, cached, output, cacheWrite, wastedCost, wasted);
        }

        /// <summary>
        /// Whether a call produced something worth having. A retry after a
        /// malformed answer appears as two calls and both were billed.
        /// </summary>
        private static bool IsUsable(string outcome)
            => string.Equals(outcome, "ok", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The per-day series, one row per day whether or not anything was spent.
        /// </summary>
        /// <remarks>
        /// Gaps are the point. A run happens weekly and the distillation pass daily,
        /// so a chart that only plotted the days with spend would compress six quiet
        /// days into nothing and make a weekly run look continuous.
        /// </remarks>
        private static List<UsageDay> Daily(IReadOnlyList<UsageCall> calls, int days, DateTime today)
        {
            var byDay = calls
                .GroupBy(c => c.At.Date)
                .ToDictionary(g => g.Key, g => g.ToList());

            var series = new List<UsageDay>(Math.Max(days, 0));
            var start = today.Date.AddDays(-(Math.Max(days, 1) - 1));

            for (var day = start; day <= today.Date; day = day.AddDays(1))
            {
                if (!byDay.TryGetValue(day, out var dayCalls))
                {
                    series.Add(new UsageDay(Format(day), 0m, new Dictionary<string, decimal>(), 0));
                    continue;
                }

                var byModel = dayCalls
                    .GroupBy(c => NameOrUnknown(c.Model), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Sum(c => c.CostUsd ?? 0m), StringComparer.OrdinalIgnoreCase);

                series.Add(new UsageDay(
                    Format(day),
                    dayCalls.Sum(c => c.CostUsd ?? 0m),
                    byModel,
                    dayCalls.Count));
            }

            return series;
        }

        private static List<UsageDay> EmptySeries(int days, DateTime today)
        {
            var series = new List<UsageDay>();
            var start = today.Date.AddDays(-(Math.Max(days, 1) - 1));
            for (var day = start; day <= today.Date; day = day.AddDays(1))
            {
                series.Add(new UsageDay(Format(day), 0m, new Dictionary<string, decimal>(), 0));
            }

            return series;
        }

        private static string Format(DateTime day)
            => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>
        /// A run log written before a model was recorded, or by a pass that never
        /// set one, leaves the field blank. Shown as unknown rather than as an empty
        /// row nobody can interpret.
        /// </summary>
        private static string NameOrUnknown(string? name)
            => string.IsNullOrWhiteSpace(name) ? "unknown" : name;
    }
}
