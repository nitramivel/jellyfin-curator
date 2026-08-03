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
    ///
    /// <para>
    /// <b>A startup trigger is the one exception, and it is load-bearing.</b> It is
    /// not a cadence — no box on the page can express it — so reading reports the
    /// task's *recurring* schedule and a startup-only task correctly reads as
    /// Manual. What must not follow is the save then deleting it. Publish Home
    /// Screen Rows carries a startup trigger and nothing else, because the sections
    /// it registers live in memory and are gone after a restart (rule 22); when the
    /// page showed it as Manual and the next save wrote that back, the rows stopped
    /// coming back on 3 Aug 2026 and every Curator row on the home screen was
    /// absent until the task was run by hand. Hence <see cref="ToTriggers(ScheduleSpec,
    /// IEnumerable{TaskTriggerInfo})"/>, which carries startup triggers across a
    /// save, and <see cref="HasStartupTrigger"/>, so the page can say so out loud
    /// rather than showing "Manual" and meaning something else.
    /// </para>
    /// </summary>
    public static class ScheduleTranslator
    {
        /// <summary>
        /// Reads a task's triggers as a single cadence.
        /// </summary>
        /// <remarks>
        /// Where a task carries several triggers the first recognised one wins, in
        /// interval-daily-weekly order. A startup trigger contributes nothing here —
        /// it is not a cadence, so a task carrying only one reads as Manual, which is
        /// the truth about its *recurring* schedule and not the whole truth about the
        /// task. Ask <see cref="HasStartupTrigger"/> for the rest of it, and save
        /// through the overload that preserves it.
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
        /// Whether a task runs at server start, on top of whatever cadence it has.
        /// </summary>
        /// <param name="triggers">The task's triggers.</param>
        /// <returns>True when a startup trigger is present.</returns>
        public static bool HasStartupTrigger(IEnumerable<TaskTriggerInfo>? triggers)
            => triggers?.Any(t => t is not null && t.Type == TaskTriggerInfoType.StartupTrigger) == true;

        /// <summary>
        /// Builds the trigger list for a cadence, keeping any startup trigger the
        /// task already carried.
        /// </summary>
        /// <remarks>
        /// This is the overload the save path must use. The cadence still replaces
        /// every recurring trigger — one editor, one cadence — but a startup trigger
        /// is not a cadence and the page has no way to ask for one, so dropping it
        /// would be the editor silently deleting a setting it never offered. That is
        /// exactly how Publish Home Screen Rows lost its only trigger; see the type
        /// remarks.
        /// </remarks>
        /// <param name="spec">The wanted cadence.</param>
        /// <param name="existing">The triggers the task carries now.</param>
        /// <returns>The triggers to store.</returns>
        public static IReadOnlyList<TaskTriggerInfo> ToTriggers(
            ScheduleSpec spec,
            IEnumerable<TaskTriggerInfo>? existing)
        {
            var triggers = ToTriggers(spec).ToList();

            if (HasStartupTrigger(existing))
            {
                triggers.Add(new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger });
            }

            return triggers;
        }

        /// <summary>
        /// Builds the trigger list for a cadence, and nothing else.
        /// </summary>
        /// <remarks>
        /// Returns the complete list the task should carry, not an addition to it —
        /// saving from the page replaces whatever was there. That is the honest
        /// behaviour for a one-cadence editor, and the page says so. Prefer the
        /// overload taking the existing triggers anywhere a real task is being
        /// written: this one drops a startup trigger on the floor.
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
