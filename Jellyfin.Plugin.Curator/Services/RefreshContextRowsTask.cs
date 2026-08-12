using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Services.Context;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services
{
    /// <summary>
    /// Re-reads the weather, re-titles the two context rows, and republishes them.
    ///
    /// <para>
    /// The seventh task, and it exists because of one fact about Home Screen
    /// Sections: a row's display text belongs to the <b>registration</b>, so a title
    /// that tracks the weather is a title that has to be re-registered when the
    /// weather turns over. Nothing on the render path may do that — it is a write
    /// into another plugin, and possibly a paid model call — so it lands here.
    /// </para>
    ///
    /// <para>
    /// Almost always free. Titles are cached against the conditions rather than the
    /// clock, so a run that finds every condition already known buys nothing at all;
    /// only the first cold snowy evening of the year costs a call. When the titles
    /// are set to Fixed it is free by construction.
    /// </para>
    /// </summary>
    public class RefreshContextRowsTask : IScheduledTask
    {
        private readonly ContextRowService _contextRows;
        private readonly CuratorRunService _runService;
        private readonly ILogger<RefreshContextRowsTask> _logger;

        public RefreshContextRowsTask(
            ContextRowService contextRows,
            CuratorRunService runService,
            ILogger<RefreshContextRowsTask> logger)
        {
            _contextRows = contextRows;
            _runService = runService;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Refresh Context Rows";

        /// <inheritdoc />
        public string Key => "CuratorRefreshContextRows";

        /// <inheritdoc />
        public string Description =>
            "Re-reads the weather, renames the weather and time-of-day rows for the conditions, and republishes them. "
            + "Free unless a set of conditions has never been seen before.";

        /// <inheritdoc />
        public string Category => "Curator";

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);

            if (_runService.IsRunning)
            {
                // A run rewrites the categories these rows draw from and republishes
                // the home screen when it finishes. Racing it loses work.
                _logger.LogInformation("Curator: a run is in progress; the context rows will be refreshed after it");
                progress.Report(100);
                return;
            }

            progress.Report(5);

            var result = await _contextRows.RefreshAsync(cancellationToken).ConfigureAwait(false);

            if (result.Error is not null)
            {
                // Not thrown. The rows are cosmetic against the playlists, and a task
                // that fails loudly over a home screen label teaches its owner to
                // ignore the task list.
                _logger.LogWarning(
                    "Curator: the context rows could not be published — {Error}. Everything else is unaffected.",
                    result.Error);
            }

            progress.Report(100);
        }

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Hourly, because the weather turns over on no schedule and the daypart
            // boundaries are at 05:00, 12:00, 17:00 and 22:00 — a title an hour stale
            // is a title that was true recently, which is the most a registered name
            // can promise.
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(1).Ticks,
            };

            // The startup trigger is what makes the interval safe. Jellyfin arms an
            // interval at max(lastEnd, lastStart, now + 1min) + interval and does not
            // catch up, so a server restarted more often than the interval runs that
            // task never — measured on this owner's server, four tasks on 12h and 48h
            // intervals went three days without firing. An hour is short enough to
            // survive normal restarts, and this guarantees the first refresh happens
            // immediately after every one of them regardless.
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.StartupTrigger,
            };
        }
    }
}
