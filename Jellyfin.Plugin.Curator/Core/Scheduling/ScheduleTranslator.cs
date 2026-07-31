using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Curator.Core.Scheduling
{
    /// <summary>
    /// Converts between the cadence the settings page talks about and the trigger
    /// list Jellyfin stores.
    ///
    /// <para>
    /// Pure and separately tested because this is a lossy boundary in one
    /// direction. Jellyfin lets a task carry several triggers of mixed kinds; the
    /// page offers one. Reading has to pick sensibly from whatever is there, and
    /// writing has to be honest that it replaces the lot.
    /// </para>
    /// </summary>
    public static class ScheduleTranslator
    {
        /// <summary>
        /// Reads a task's triggers as a single cadence.
        /// </summary>
        /// <remarks>
        /// Where a task carries several triggers the first recognised one wins, in
        /// interval-daily-weekly order. A startup trigger is ignored rather than
        /// reported as "manual": it is a real trigger the owner may have set in
        /// Jellyfin's own page, and saying "never" about it would be a lie the next
        /// save then made true.
        /// </remarks>
        /// <param name="triggers">The task's triggers.</param>
        /// <returns>The cadence to show.</returns>
        public static ScheduleSpec FromTriggers(IEnumerable<TaskTriggerInfo>? triggers)
        {
            var list = triggers?.Where(t => t is not null).ToList() ?? [];

            if (list.FirstOrDefault(t => t.Type == TaskTriggerInfoType.IntervalTrigger) is { } interval
                && interval.IntervalTicks is { } ticks
                && ticks > 0)
            {
                return new ScheduleSpec(
                    ScheduleMode.Interval,
                    IntervalHours: TimeSpan.FromTicks(ticks).TotalHours).Normalized();
            }

            if (list.FirstOrDefault(t => t.Type == TaskTriggerInfoType.DailyTrigger) is { } daily)
            {
                return new ScheduleSpec(
                    ScheduleMode.Daily,
                    TimeOfDayMinutes: MinutesFromTicks(daily.TimeOfDayTicks)).Normalized();
            }

            if (list.FirstOrDefault(t => t.Type == TaskTriggerInfoType.WeeklyTrigger) is { } weekly)
            {
                return new ScheduleSpec(
                    ScheduleMode.Weekly,
                    TimeOfDayMinutes: MinutesFromTicks(weekly.TimeOfDayTicks),
                    DayOfWeek: weekly.DayOfWeek ?? DayOfWeek.Sunday).Normalized();
            }

            return new ScheduleSpec(ScheduleMode.Manual);
        }

        /// <summary>
        /// Builds the trigger list for a cadence.
        /// </summary>
        /// <remarks>
        /// Returns the complete list the task should carry, not an addition to it —
        /// saving from the page replaces whatever was there. That is the honest
        /// behaviour for a one-cadence editor, and the page says so.
        /// </remarks>
        /// <param name="spec">The wanted cadence.</param>
        /// <returns>The triggers to store; empty for manual-only.</returns>
        public static IReadOnlyList<TaskTriggerInfo> ToTriggers(ScheduleSpec spec)
        {
            ArgumentNullException.ThrowIfNull(spec);

            var safe = spec.Normalized();

            switch (safe.Mode)
            {
                case ScheduleMode.Interval:
                    return
                    [
                        new TaskTriggerInfo
                        {
                            Type = TaskTriggerInfoType.IntervalTrigger,
                            IntervalTicks = TimeSpan.FromHours(safe.IntervalHours).Ticks,
                        },
                    ];

                case ScheduleMode.Daily:
                    return
                    [
                        new TaskTriggerInfo
                        {
                            Type = TaskTriggerInfoType.DailyTrigger,
                            TimeOfDayTicks = TimeSpan.FromMinutes(safe.TimeOfDayMinutes).Ticks,
                        },
                    ];

                case ScheduleMode.Weekly:
                    return
                    [
                        new TaskTriggerInfo
                        {
                            Type = TaskTriggerInfoType.WeeklyTrigger,
                            DayOfWeek = safe.DayOfWeek,
                            TimeOfDayTicks = TimeSpan.FromMinutes(safe.TimeOfDayMinutes).Ticks,
                        },
                    ];

                default:
                    return [];
            }
        }

        private static int MinutesFromTicks(long? ticks)
        {
            if (ticks is not { } value || value < 0)
            {
                return 0;
            }

            return (int)Math.Round(TimeSpan.FromTicks(value).TotalMinutes);
        }
    }
}
