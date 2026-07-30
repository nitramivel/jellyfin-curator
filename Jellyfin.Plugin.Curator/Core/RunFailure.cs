using System;

namespace Jellyfin.Plugin.Curator.Core
{
    /// <summary>
    /// Tells a run that died because the server was torn down underneath it from a
    /// run that died because something is wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Installing or updating any plugin makes Jellyfin restart its host <em>in the
    /// same process</em>: it sends shutdown notifications, disposes
    /// <c>CoreAppHost</c>, then rebuilds and reports "Startup complete" seconds
    /// later. A Curator run started before that point is not killed — the process
    /// is still alive and the task keeps executing — but every Jellyfin service it
    /// holds has been disposed. The next call into one throws, and the run dies
    /// somewhere arbitrary.
    /// </para>
    /// <para>
    /// Observed on 30 Jul 2026: a run started at 09:29:22, the host was disposed at
    /// 09:31:37 when 0.3.16.0 was installed, and the orphaned run carried on for
    /// another twenty seconds before reaching <c>GetUserById</c> — which goes
    /// through a pooled <c>DbContext</c> — and failing with
    /// <c>ObjectDisposedException: 'IServiceProvider'</c> at 53%. It read exactly
    /// like a defect in this plugin and was investigated as one. Naming it is the
    /// fix; a run cannot survive its own container being disposed.
    /// </para>
    /// </remarks>
    public static class RunFailure
    {
        /// <summary>
        /// Whether this exception means the host went away mid-run rather than the
        /// run being at fault.
        /// </summary>
        /// <param name="exception">The exception that ended the run.</param>
        /// <returns>True when the run was a casualty of shutdown.</returns>
        public static bool IsHostTeardown(Exception? exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                // The DI container, or a Jellyfin service holding one, has been
                // disposed. Nothing a run does can recover from this.
                if (current is ObjectDisposedException)
                {
                    return true;
                }

                // EF surfaces a disposed pooled context this way, and the type lives
                // in a package Curator does not reference.
                if (current is InvalidOperationException
                    && current.Message.Contains("disposed", StringComparison.OrdinalIgnoreCase)
                    && current.Message.Contains("context", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The message recorded and logged when a run is a casualty of shutdown.
        /// </summary>
        public const string HostTeardownMessage =
            "The Jellyfin server shut down or reloaded its plugins while this run was in progress, "
            + "so the run was abandoned part-way. Nothing was left half-built. Start another run once "
            + "the server is back up.";
    }
}
