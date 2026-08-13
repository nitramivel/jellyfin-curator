using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Services.Playlists;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services
{
    /// <summary>
    /// The scheduled task entry point: "Prune Empty Playlists", under the Curator
    /// category.
    /// </summary>
    /// <remarks>
    /// Daily and free. It exists because the thing it cleans up <b>comes back on its
    /// own</b>: a playlist directory that outlives its database row is adopted by the
    /// next library scan as a fresh, ownerless, empty playlist, and once created that
    /// way it carries no ownership tag, so every other pass in this plugin is
    /// obliged to leave it alone forever. Measured on the owner's server: fourteen of
    /// them, created inside one second, each sitting beside a working playlist of the
    /// same name. A one-off button would clear those fourteen and meet the next
    /// fourteen unarmed.
    /// <para>
    /// Safe to run unattended, which is the only reason it is a task at all.
    /// <c>EmptyPlaylistSweep</c> will not touch a playlist that holds anything, and
    /// will not touch an untagged playlist that has an owner — that is somebody's,
    /// empty or not. What is left is Curator's own empties and rows nobody can have
    /// made.
    /// </para>
    /// <para>
    /// Deliberately <b>not</b> folded into the maintenance task, for the same reason
    /// the recommendation refresh was pulled out of it: this deletes things, and a
    /// job that deletes things should be one a person can find, read the description
    /// of, and switch off by itself.
    /// </para>
    /// </remarks>
    public class PruneEmptyPlaylistsTask : IScheduledTask
    {
        private readonly ICuratorPlaylistService _playlistService;
        private readonly CuratorRunService _runService;
        private readonly ILogger<PruneEmptyPlaylistsTask> _logger;

        public PruneEmptyPlaylistsTask(
            ICuratorPlaylistService playlistService,
            CuratorRunService runService,
            ILogger<PruneEmptyPlaylistsTask> logger)
        {
            _playlistService = playlistService;
            _runService = runService;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Prune Empty Playlists";

        /// <inheritdoc />
        public string Key => "CuratorPruneEmptyPlaylists";

        /// <inheritdoc />
        public string Description =>
            "Deletes playlists that hold nothing and belong to nobody — leftovers Jellyfin re-imports from "
            + "folders whose playlists were already deleted, which otherwise sit on the home screen forever as "
            + "empty duplicates. Never touches a playlist with anything in it, and never touches one of yours: "
            + "an untagged playlist with an owner is left alone whether it is empty or not. Costs nothing and "
            + "calls no model.";

        /// <inheritdoc />
        public string Category => "Curator";

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            progress?.Report(0);

            // A run is rewriting these very playlists, and one mid-creation holds no
            // members yet. Sweeping alongside it could delete a playlist that was
            // about to be filled, so this waits for the next day rather than racing.
            if (_runService.IsRunning)
            {
                _logger.LogInformation(
                    "Curator: a run is in progress; skipping the empty-playlist sweep this time");
                progress?.Report(100);
                return;
            }

            var result = await _playlistService
                .SweepEmptyPlaylistsAsync(apply: true, cancellationToken)
                .ConfigureAwait(false);

            if (result.Deleted == 0)
            {
                _logger.LogInformation(
                    "Curator: no empty playlists to prune ({Examined} examined)", result.Examined);
            }
            else
            {
                _logger.LogInformation(
                    "Curator: pruned {Deleted} empty playlist(s) and {Folders} leftover folder(s)",
                    result.Deleted,
                    result.DirectoriesRemoved);
            }

            progress?.Report(100);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Daily at 04:20, and daily rather than an interval on purpose. Jellyfin arms
        /// an interval trigger at <c>max(lastEnd, lastStart, now + 1min) + interval</c>
        /// and re-arms it on every server start, so on a server touched more often
        /// than the interval — and installing any plugin restarts the host — an
        /// interval task never fires at all. A wall-clock time is immune to that.
        /// </remarks>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4.333).Ticks,
            };
        }
    }
}
