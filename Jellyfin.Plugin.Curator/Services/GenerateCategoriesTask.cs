using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services
{
    /// <summary>
    /// The scheduled task entry point: "Curator: Generate Categories".
    /// Runs weekly by default — LLM runs cost money, so the default cadence is
    /// deliberately wide.
    /// </summary>
    public class GenerateCategoriesTask : IScheduledTask
    {
        private readonly CuratorRunService _runService;
        private readonly ILogger<GenerateCategoriesTask> _logger;

        public GenerateCategoriesTask(CuratorRunService runService, ILogger<GenerateCategoriesTask> logger)
        {
            _runService = runService;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Curator: Generate Categories";

        /// <inheritdoc />
        public string Key => "CuratorGenerateCategories";

        /// <inheritdoc />
        public string Description =>
            "Sends the library to the configured LLM, turns the categories it finds into ordered playlists, and publishes them as home screen rows.";

        /// <inheritdoc />
        public string Category => "Curator";

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            try
            {
                await _runService.RunAsync(progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Curator: run cancelled");
                throw;
            }
            catch (Exception ex)
            {
                // Surface the reason in the task's own log line rather than only
                // as a stack trace in the server log.
                _logger.LogError(ex, "Curator: run failed — {Message}", ex.Message);
                throw;
            }
        }

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromDays(7).Ticks,
            };
        }
    }
}
