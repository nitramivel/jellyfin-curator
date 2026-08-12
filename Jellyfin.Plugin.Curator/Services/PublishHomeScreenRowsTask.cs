using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Configuration;
using Jellyfin.Plugin.Curator.Services.Context;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services
{
    /// <summary>
    /// Re-publishes Curator's home screen rows, and does it on every server start.
    ///
    /// <para>
    /// This exists because of one fact about Home Screen Sections: a section
    /// registered with it lives in a dictionary in memory and is never written
    /// anywhere. Restart the server and every row Curator owns is gone — not
    /// broken, not empty, absent — until something registers it again. Under the
    /// old path that never mattered, because Collection Sections stored the rows in
    /// its own configuration and re-registered them for us. Owning the row means
    /// owning that job too, and it is a new way for the home screen to be wrong: if
    /// this task does not run, the rows do not come back.
    /// </para>
    ///
    /// <para>
    /// It is also the button to press when rows have drifted, which is why it is a
    /// visible task rather than a hidden startup hook. It costs nothing and calls
    /// no model.
    /// </para>
    /// </summary>
    public class PublishHomeScreenRowsTask : IScheduledTask
    {
        /// <summary>
        /// How many times to retry before giving up at startup.
        /// </summary>
        /// <remarks>
        /// Plugins start in no defined order, so Home Screen Sections may not have
        /// built its service provider by the time this fires — its entry point
        /// throws rather than queuing. Retrying is the whole mitigation.
        /// </remarks>
        private const int MaxAttempts = 6;

        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

        private readonly CuratorRunService _runService;
        private readonly ContextRowService _contextRows;
        private readonly ILogger<PublishHomeScreenRowsTask> _logger;

        public PublishHomeScreenRowsTask(
            CuratorRunService runService,
            ContextRowService contextRows,
            ILogger<PublishHomeScreenRowsTask> logger)
        {
            _runService = runService;
            _contextRows = contextRows;
            _logger = logger;
        }

        /// <summary>
        /// Republishes the two context rows, never fatally.
        /// </summary>
        /// <remarks>
        /// Their weather cache is in memory exactly like the registrations are, so
        /// after a restart both are empty. Left to the first home screen load, the
        /// first person to open Jellyfin would be the one who does not get the
        /// feature.
        /// </remarks>
        private async Task RefreshContextRowsAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _contextRows.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Curator: could not publish the context rows at startup");
            }
        }

        /// <inheritdoc />
        public string Name => "Publish Home Screen Rows";

        /// <inheritdoc />
        public string Key => "CuratorPublishHomeScreenRows";

        /// <inheritdoc />
        public string Description =>
            "Registers Curator's categories as home screen rows. Runs at every server start, because those registrations are held in memory and do not survive a restart.";

        /// <inheritdoc />
        public string Category => "Curator";

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);

            if (_runService.IsRunning)
            {
                // A run republishes the rows itself when it finishes, from newer
                // categories than these.
                _logger.LogInformation("Curator: a run is in progress; it will publish the home screen rows itself");
                progress.Report(100);
                return;
            }

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await _runService.SyncHomeScreenAsync(cancellationToken).ConfigureAwait(false);

                // A degraded sync published rows through Collection Sections, which
                // is a working home screen and not a reason to stop trying for the
                // one that was asked for. At startup it usually means the other
                // plugin had not finished starting on the previous attempt.
                if (result.Published && !result.Degraded)
                {
                    // The context rows are registered by their own task, which also
                    // carries a startup trigger — but plugins start in no defined
                    // order and there is no guarantee it fires after this one. Doing
                    // it here too costs nothing (the titles are cached against the
                    // conditions) and means the two rows come back with the rest.
                    await RefreshContextRowsAsync(cancellationToken).ConfigureAwait(false);
                    progress.Report(100);
                    return;
                }

                if (attempt < MaxAttempts)
                {
                    _logger.LogInformation(
                        "Curator: home screen rows not published yet (attempt {Attempt} of {Total}); the other plugins may still be starting",
                        attempt,
                        MaxAttempts);
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }

                progress.Report(attempt * 100d / MaxAttempts);
            }

            // Not an exception: playlists are intact and every other client shows
            // them. At worst the rows are missing, at best they are being served by
            // the fallback — and saying which, plainly, is more use than a failed
            // task with a stack trace.
            _logger.LogWarning(
                "Curator: could not publish home screen rows the configured way after {Attempts} attempts. Playlists are unaffected. "
                + "Check that Home Screen Sections is installed and enabled, then run this task again.",
                MaxAttempts);
            progress.Report(100);
        }

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.StartupTrigger,
            };
        }
    }
}
