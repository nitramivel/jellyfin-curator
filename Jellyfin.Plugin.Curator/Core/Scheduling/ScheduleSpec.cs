using System;

namespace Jellyfin.Plugin.Curator.Core.Scheduling
{
    /// <summary>
    /// How often a task should run, in the terms the settings page offers.
    /// </summary>
    public enum ScheduleMode
    {
        /// <summary>Never — the task keeps no trigger and only runs when started by hand.</summary>
        Manual = 0,

        /// <summary>Every N hours.</summary>
        Interval = 1,

        /// <summary>Once a day at a given time.</summary>
        Daily = 2,

        /// <summary>Once a week on a given day and time.</summary>
        Weekly = 3,
    }

    /// <summary>
    /// One task's cadence, independent of Jellyfin's trigger types.
    /// </summary>
    /// <param name="Mode">Which kind of schedule.</param>
    /// <param name="IntervalHours">Hours between runs, for <see cref="ScheduleMode.Interval"/>.</param>
    /// <param name="TimeOfDayMinutes">Minutes past midnight, for daily and weekly.</param>
    /// <param name="DayOfWeek">The day, for weekly.</param>
    public sealed record ScheduleSpec(
        ScheduleMode Mode,
        double IntervalHours = 24,
        int TimeOfDayMinutes = 3 * 60,
        DayOfWeek DayOfWeek = System.DayOfWeek.Sunday)
    {
        /// <summary>The shortest interval the page will accept, in hours.</summary>
        /// <remarks>
        /// A category run costs money every time it fires, and the maintenance pass
        /// walks every playlist on the server. Neither belongs on a five-minute
        /// timer, and a mistyped 0 would otherwise mean "as fast as possible".
        /// </remarks>
        public const double MinIntervalHours = 0.25;

        /// <summary>The longest interval the page will accept, in hours.</summary>
        public const double MaxIntervalHours = 24 * 90;

        /// <summary>
        /// Clamps the values into ranges Jellyfin and the owner can both live with.
        /// </summary>
        /// <returns>A safe spec.</returns>
        public ScheduleSpec Normalized()
        {
            var hours = double.IsFinite(IntervalHours)
                ? Math.Clamp(IntervalHours, MinIntervalHours, MaxIntervalHours)
                : 24;

            // Wrap rather than clamp: 24:00 is midnight, and clamping it to 23:59
            // would silently move a schedule by a minute every time it round-tripped.
            var minutes = ((TimeOfDayMinutes % 1440) + 1440) % 1440;

            var day = Enum.IsDefined(DayOfWeek) ? DayOfWeek : System.DayOfWeek.Sunday;

            return this with
            {
                IntervalHours = hours,
                TimeOfDayMinutes = minutes,
                DayOfWeek = day,
            };
        }
    }
}
