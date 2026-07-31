using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Core.Health;
using Jellyfin.Plugin.Curator.Services.Health;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services
{
    /// <summary>
    /// The scheduled task entry point: "Curator: Health Check".
    /// </summary>
    /// <remarks>
    /// Daily, free, and read-only. It exists because every way this plugin goes
    /// wrong in practice is quiet: a run dies mid-flight when installing any plugin
    /// tears the host down, a prerequisite plugin gets uninstalled and its rows
    /// simply stop appearing, library rows outlive the folder they came from. None
    /// of those throw, and from the outside all of them look identical to Curator
    /// having stopped working — which is how a real library ran for weeks with 12%
    /// of it unplayable and nobody the wiser.
    /// <para>
    /// Writes nothing. Its whole output is log lines and a panel, so it is safe
    /// beside a run in progress and safe to run as often as you like.
    /// </para>
    /// </remarks>
    public class HealthCheckTask : IScheduledTask
    {
        private readonly HealthService _healthService;
        private readonly ILogger<HealthCheckTask> _logger;

        public HealthCheckTask(HealthService healthService, ILogger<HealthCheckTask> logger)
        {
            _healthService = healthService;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Curator: Health Check";

        /// <inheritdoc />
        public string Key => "CuratorHealthCheck";

        /// <inheritdoc />
        public string Description =>
            "Looks for the ways Curator goes quietly wrong — runs that have stopped happening, a missing "
            + "prerequisite plugin, library rows pointing at folders that no longer exist, an incomplete summary "
            + "cache — and reports them. Reads only; changes nothing and costs nothing.";

        /// <inheritdoc />
        public string Category => "Curator";

        /// <inheritdoc />
        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            progress?.Report(0);

            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                _logger.LogWarning("Curator: plugin configuration unavailable; skipping the health check");
                return Task.CompletedTask;
            }

            var report = _healthService.Check(config);

            if (report.Findings.Count == 0)
            {
                _logger.LogInformation("Curator health: nothing to report");
                progress?.Report(100);
                return Task.CompletedTask;
            }

            foreach (var finding in report.Findings)
            {
                // Logged at the severity it deserves, so a Problem shows up in
                // Jellyfin's own log filtering rather than being buried in INFO.
                if (finding.Severity == HealthSeverity.Problem)
                {
                    _logger.LogError("Curator health: {Title} — {Detail}", finding.Title, finding.Detail);
                }
                else
                {
                    _logger.LogWarning("Curator health: {Title} — {Detail}", finding.Title, finding.Detail);
                }
            }

            progress?.Report(100);
            return Task.CompletedTask;
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
