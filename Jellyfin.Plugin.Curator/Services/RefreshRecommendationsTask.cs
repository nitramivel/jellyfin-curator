using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services
{
    /// <summary>
    /// The scheduled task entry point: "Curator: Refresh Recommendations".
    /// </summary>
    /// <remarks>
    /// Every six hours by default, and free — no model call. It has its own cadence
    /// because it wants a very different one from everything else here: the
    /// per-viewer playlists are ranked by what somebody has <em>not</em> watched, so
    /// they go stale as soon as they watch something. A category run is weekly
    /// because it costs money; this is quarter-daily because it costs nothing and
    /// keeps a spotlight row honest between them.
    /// </remarks>
    public class RefreshRecommendationsTask : IScheduledTask
    {
        private readonly CuratorRunService _runService;
        private readonly ILogger<RefreshRecommendationsTask> _logger;

        public RefreshRecommendationsTask(
            CuratorRunService runService,
            ILogger<RefreshRecommendationsTask> logger)
        {
            _runService = runService;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Curator: Refresh Recommendations";

        /// <inheritdoc />
        public string Key => "CuratorRefreshRecommendations";

        /// <inheritdoc />
        public string Description =>
            "Rebuilds each viewer's recommendation playlist from the categories they already have, ranked against "
            + "their current watch activity so what they have just seen drops down the list. Calls no model and "
            + "costs nothing.";

        /// <inheritdoc />
        public string Category => "Curator";

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            progress?.Report(0);

            try
            {
                var built = await _runService.RefreshRecommendationsAsync(cancellationToken).ConfigureAwait(false);
                if (built < 0)
                {
                    _logger.LogInformation("Curator: recommendation refresh skipped — a run is in progress");
                }

                progress?.Report(100);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Curator: recommendation refresh cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Curator: recommendation refresh failed — {Message}", ex.Message);
                throw;
            }
        }

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(6).Ticks,
            };
        }
    }
}
