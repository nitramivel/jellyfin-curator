using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Core.Scheduling;
using MediaBrowser.Model.Tasks;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// The boundary between the cadence the settings page offers and the trigger
    /// list Jellyfin stores.
    ///
    /// It is lossy in one direction on purpose: Jellyfin allows several triggers of
    /// mixed kinds, the page offers one. What must not happen is the page showing a
    /// cadence the task does not actually have, so reading and writing are pinned
    /// against each other here.
    /// </summary>
    public class ScheduleTranslatorTests
    {
        [Fact]
        public void FromTriggers_ReadsAnInterval()
        {
            var spec = ScheduleTranslator.FromTriggers(
            [
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromHours(6).Ticks,
                },
            ]);

            Assert.Equal(ScheduleMode.Interval, spec.Mode);
            Assert.Equal(6, spec.IntervalHours, 3);
        }

        [Fact]
        public void FromTriggers_ReadsADailyTime()
        {
            var spec = ScheduleTranslator.FromTriggers(
            [
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.DailyTrigger,
                    TimeOfDayTicks = TimeSpan.FromHours(3.5).Ticks,
                },
            ]);

            Assert.Equal(ScheduleMode.Daily, spec.Mode);
            Assert.Equal(210, spec.TimeOfDayMinutes);
        }

        [Fact]
        public void FromTriggers_ReadsAWeeklyDayAndTime()
        {
            var spec = ScheduleTranslator.FromTriggers(
            [
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.WeeklyTrigger,
                    DayOfWeek = DayOfWeek.Wednesday,
                    TimeOfDayTicks = TimeSpan.FromHours(2).Ticks,
                },
            ]);

            Assert.Equal(ScheduleMode.Weekly, spec.Mode);
            Assert.Equal(DayOfWeek.Wednesday, spec.DayOfWeek);
            Assert.Equal(120, spec.TimeOfDayMinutes);
        }

        [Fact]
        public void FromTriggers_ReportsManualWhenThereAreNoTriggers()
        {
            Assert.Equal(ScheduleMode.Manual, ScheduleTranslator.FromTriggers(null).Mode);
            Assert.Equal(ScheduleMode.Manual, ScheduleTranslator.FromTriggers([]).Mode);
        }

        [Fact]
        public void FromTriggers_IgnoresAnIntervalOfZeroTicks()
        {
            // A zero interval is not "run constantly", it is a broken trigger. Reading
            // it as an interval would show 0 hours and then save it back as one.
            var spec = ScheduleTranslator.FromTriggers(
            [
                new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = 0 },
            ]);

            Assert.Equal(ScheduleMode.Manual, spec.Mode);
        }

        [Fact]
        public void FromTriggers_PrefersTheIntervalWhenATaskCarriesSeveral()
        {
            var spec = ScheduleTranslator.FromTriggers(
            [
                new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
                new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = 0 },
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromHours(12).Ticks,
                },
            ]);

            Assert.Equal(ScheduleMode.Interval, spec.Mode);
            Assert.Equal(12, spec.IntervalHours, 3);
        }

        [Theory]
        [InlineData(ScheduleMode.Interval, TaskTriggerInfoType.IntervalTrigger)]
        [InlineData(ScheduleMode.Daily, TaskTriggerInfoType.DailyTrigger)]
        [InlineData(ScheduleMode.Weekly, TaskTriggerInfoType.WeeklyTrigger)]
        public void ToTriggers_EmitsExactlyOneTriggerOfTheRightKind(ScheduleMode mode, TaskTriggerInfoType expected)
        {
            var triggers = ScheduleTranslator.ToTriggers(new ScheduleSpec(mode));

            Assert.Equal(expected, Assert.Single(triggers).Type);
        }

        [Fact]
        public void ToTriggers_EmitsNothingForManual()
        {
            Assert.Empty(ScheduleTranslator.ToTriggers(new ScheduleSpec(ScheduleMode.Manual)));
        }

        [Theory]
        [InlineData(ScheduleMode.Interval)]
        [InlineData(ScheduleMode.Daily)]
        [InlineData(ScheduleMode.Weekly)]
        public void RoundTrip_PreservesTheCadence(ScheduleMode mode)
        {
            // The property that matters: what the page saves is what the page reads
            // back. Anything else and the settings drift every time they are opened.
            var original = new ScheduleSpec(mode, IntervalHours: 8, TimeOfDayMinutes: 195, DayOfWeek: DayOfWeek.Friday);

            var round = ScheduleTranslator.FromTriggers(ScheduleTranslator.ToTriggers(original));

            Assert.Equal(mode, round.Mode);
            if (mode == ScheduleMode.Interval)
            {
                Assert.Equal(8, round.IntervalHours, 3);
            }
            else
            {
                Assert.Equal(195, round.TimeOfDayMinutes);
            }

            if (mode == ScheduleMode.Weekly)
            {
                Assert.Equal(DayOfWeek.Friday, round.DayOfWeek);
            }
        }

        [Fact]
        public void Normalized_ClampsAnIntervalThatWouldHammerTheServer()
        {
            // A category run costs money each time it fires, so 0 must not mean
            // "as fast as possible".
            Assert.Equal(
                ScheduleSpec.MinIntervalHours,
                new ScheduleSpec(ScheduleMode.Interval, IntervalHours: 0).Normalized().IntervalHours);

            Assert.Equal(
                ScheduleSpec.MaxIntervalHours,
                new ScheduleSpec(ScheduleMode.Interval, IntervalHours: 1_000_000).Normalized().IntervalHours);
        }

        [Fact]
        public void Normalized_WrapsTimeOfDayRatherThanClampingIt()
        {
            // 24:00 is midnight. Clamping to 23:59 would quietly move a schedule by a
            // minute every time it round-tripped.
            Assert.Equal(0, new ScheduleSpec(ScheduleMode.Daily, TimeOfDayMinutes: 1440).Normalized().TimeOfDayMinutes);
            Assert.Equal(1380, new ScheduleSpec(ScheduleMode.Daily, TimeOfDayMinutes: -60).Normalized().TimeOfDayMinutes);
        }

        [Fact]
        public void Normalized_RejectsANonsenseDayOfWeek()
        {
            var spec = new ScheduleSpec(ScheduleMode.Weekly, DayOfWeek: (DayOfWeek)99).Normalized();

            Assert.Equal(DayOfWeek.Sunday, spec.DayOfWeek);
        }

        /// <summary>
        /// A startup trigger is not a cadence, so it reads as Manual — that much is
        /// honest. What follows must not be the save then deleting it.
        /// </summary>
        [Fact]
        public void FromTriggers_ReadsAStartupOnlyTaskAsManual()
        {
            var triggers = new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger } };

            Assert.Equal(ScheduleMode.Manual, ScheduleTranslator.FromTriggers(triggers).Mode);
            Assert.True(ScheduleTranslator.HasStartupTrigger(triggers));
        }

        [Fact]
        public void HasStartupTrigger_IsFalseWithoutOne()
        {
            Assert.False(ScheduleTranslator.HasStartupTrigger(null));
            Assert.False(ScheduleTranslator.HasStartupTrigger([]));
            Assert.False(ScheduleTranslator.HasStartupTrigger(
            [
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromHours(6).Ticks,
                },
            ]));
        }

        /// <summary>
        /// The regression this exists for. Publish Home Screen Rows carries a startup
        /// trigger and nothing else; the page read it as Manual and the next save
        /// wrote that back as an empty trigger list, so after the following restart
        /// every Curator row on the home screen was absent. Saving Manual over a
        /// startup-only task must leave the task still running at startup.
        /// </summary>
        [Fact]
        public void ToTriggers_KeepsAStartupTriggerAcrossAManualSave()
        {
            var existing = new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger } };

            var saved = ScheduleTranslator.ToTriggers(new ScheduleSpec(ScheduleMode.Manual), existing);

            Assert.Single(saved);
            Assert.Equal(TaskTriggerInfoType.StartupTrigger, saved[0].Type);
            Assert.True(ScheduleTranslator.HasStartupTrigger(saved));
        }

        [Fact]
        public void ToTriggers_KeepsAStartupTriggerAlongsideANewCadence()
        {
            var existing = new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger } };

            var saved = ScheduleTranslator.ToTriggers(
                new ScheduleSpec(ScheduleMode.Interval, IntervalHours: 6),
                existing);

            Assert.Equal(2, saved.Count);
            Assert.True(ScheduleTranslator.HasStartupTrigger(saved));

            var interval = Assert.Single(saved, t => t.Type == TaskTriggerInfoType.IntervalTrigger);
            Assert.Equal(TimeSpan.FromHours(6).Ticks, interval.IntervalTicks);
        }

        [Fact]
        public void ToTriggers_DoesNotInventAStartupTrigger()
        {
            var saved = ScheduleTranslator.ToTriggers(
                new ScheduleSpec(ScheduleMode.Daily, TimeOfDayMinutes: 180),
                [new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger }]);

            Assert.False(ScheduleTranslator.HasStartupTrigger(saved));
            Assert.Equal(TaskTriggerInfoType.DailyTrigger, Assert.Single(saved).Type);
        }

        /// <summary>
        /// The whole point, stated as a loop: a startup-only task must survive being
        /// read and written any number of times. It did not survive once.
        /// </summary>
        [Fact]
        public void StartupOnlyTask_SurvivesRepeatedRoundTrips()
        {
            IReadOnlyList<TaskTriggerInfo> triggers =
                [new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger }];

            for (var i = 0; i < 5; i++)
            {
                var spec = ScheduleTranslator.FromTriggers(triggers);
                Assert.Equal(ScheduleMode.Manual, spec.Mode);

                triggers = ScheduleTranslator.ToTriggers(spec, triggers);
                Assert.True(ScheduleTranslator.HasStartupTrigger(triggers));
            }

            Assert.Single(triggers);
        }

        /// <summary>
        /// A cadence set alongside a startup trigger must round-trip unchanged too —
        /// the preserved trigger must not accumulate a duplicate on every save.
        /// </summary>
        [Fact]
        public void CadenceWithStartup_RoundTripsWithoutAccumulating()
        {
            IReadOnlyList<TaskTriggerInfo> triggers =
            [
                new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromHours(12).Ticks,
                },
            ];

            for (var i = 0; i < 5; i++)
            {
                var spec = ScheduleTranslator.FromTriggers(triggers);
                Assert.Equal(ScheduleMode.Interval, spec.Mode);
                Assert.Equal(12, spec.IntervalHours, 3);

                triggers = ScheduleTranslator.ToTriggers(spec, triggers);
            }

            Assert.Equal(2, triggers.Count);
            Assert.Single(triggers, t => t.Type == TaskTriggerInfoType.StartupTrigger);
            Assert.Single(triggers, t => t.Type == TaskTriggerInfoType.IntervalTrigger);
        }
    }
}
