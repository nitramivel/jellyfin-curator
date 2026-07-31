using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Services.Summaries;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services
{
    /// <summary>
    /// The scheduled task entry point: "Curator: Condense Summaries".
    /// </summary>
    /// <remarks>
    /// Daily by default, which is far more often than the weekly category run and
    /// still costs almost nothing: after the first pass this only touches items
    /// that are new to the library or whose overview a metadata refresh rewrote.
    /// Running it often is what keeps new films from arriving at a category run
    /// undistilled and quietly enlarging the prompt again.
    /// </remarks>
    public class DistillSummariesTask : IScheduledTask
    {
        private readonly SummaryDistillService _distillService;
        private readonly ILogger<DistillSummariesTask> _logger;

        public DistillSummariesTask(
            SummaryDistillService distillService,
            ILogger<DistillSummariesTask> logger)
        {
            _distillService = distillService;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Curator: Condense Summaries";

        /// <inheritdoc />
        public string Key => "CuratorCondenseSummaries";

        /// <inheritdoc />
        public string Description =>
            "Rewrites long library overviews into short, tone-carrying summaries and caches them, so category runs "
            + "send a much smaller prompt. Only new and changed items cost anything after the first pass. "
            + "Jellyfin's own overviews are never modified.";

        /// <inheritdoc />
        public string Category => "Curator";

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                _logger.LogWarning("Curator: plugin configuration unavailable; skipping the summary pass");
                return;
            }

            try
            {
                await _distillService
                    .DistillAsync(config, progress, force: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Curator: summary pass cancelled");
                throw;
            }
            catch (InvalidOperationException ex)
            {
                // Already running, or no model profile configured. Neither is a
                // defect worth a stack trace in the server log.
                _logger.LogWarning("Curator: summary pass skipped — {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Curator: summary pass failed — {Message}", ex.Message);
                throw;
            }
        }

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromDays(1).Ticks,
            };
        }
    }
}
