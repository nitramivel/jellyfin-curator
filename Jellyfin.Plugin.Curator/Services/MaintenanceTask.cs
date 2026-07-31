using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services
{
    /// <summary>
    /// The scheduled task entry point: "Clean Up and Sync", under the Curator category.
    /// </summary>
    /// <remarks>
    /// Daily by default, and free — nothing in here calls a model. Two reasons it
    /// wants to be far more frequent than the category run. Drift accumulates
    /// between runs: playlists deleted by hand, definitions pointing at playlists
    /// that are gone, items that have left the library. And the recommendation
    /// playlists rank by what a viewer has *not* watched, so they go stale as soon
    /// as somebody watches something — rebuilding them nightly keeps a spotlight
    /// row honest between the weekly runs that cost money.
    /// </remarks>
    public class MaintenanceTask : IScheduledTask
    {
        private readonly CuratorRunService _runService;
        private readonly ILogger<MaintenanceTask> _logger;

        public MaintenanceTask(CuratorRunService runService, ILogger<MaintenanceTask> logger)
        {
            _runService = runService;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Clean Up and Sync";

        /// <inheritdoc />
        public string Key => "CuratorMaintenance";

        /// <inheritdoc />
        public string Description =>
            "Reconciles categories against the playlists that actually exist, republishes home screen rows, "
            + "rebuilds each viewer's recommendation playlist against their current watch activity, and drops "
            + "condensed summaries for items that have left the library. Calls no model and costs nothing.";

        /// <inheritdoc />
        public string Category => "Curator";

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            progress?.Report(0);

            try
            {
                var result = await _runService.RunMaintenanceAsync(cancellationToken).ConfigureAwait(false);
                if (result.Skipped)
                {
                    _logger.LogInformation("Curator: maintenance skipped — a run is in progress");
                }

                progress?.Report(100);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Curator: maintenance cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Curator: maintenance failed — {Message}", ex.Message);
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
