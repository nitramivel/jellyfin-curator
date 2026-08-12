using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Curator.Core.Context;
using Jellyfin.Plugin.Curator.Services.Context;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// The store behind the context rows: the bought titles, and the snapshot that
    /// keeps each row's name and its cards answering the same question.
    /// </summary>
    public class ContextRowStoreTests : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "curator-context-" + Guid.NewGuid().ToString("N"));

        private ContextRowStore NewStore()
            => new(Path.Combine(_directory, "context-rows.json"), NullLogger<ContextRowStore>.Instance);

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        [Fact]
        public void AMissingFileReadsAsEmptyRatherThanThrowing()
        {
            var store = NewStore();

            Assert.Empty(store.GetTitles());
            Assert.Empty(store.GetSnapshots());
        }

        [Fact]
        public void TitlesRoundTrip()
        {
            var store = NewStore();
            var set = new ContextTitleSet(
                "weather:cold,rain", ["Grey Hours", "Rain-Soaked"], 3, new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc), "grok-4");

            store.SaveTitles([set]);

            var read = NewStore().GetTitles();
            var stored = Assert.Single(read).Value;

            Assert.Equal(["Grey Hours", "Rain-Soaked"], stored.Titles);
            Assert.Equal(3, stored.Rotation);
            Assert.Equal("grok-4", stored.ModelId);
            Assert.Equal(set.LastUsedUtc, stored.LastUsedUtc);
        }

        [Fact]
        public void TheStyleStampSurvivesAWriteAndAFileWrittenWithoutOneReadsAsLegacy()
        {
            var store = NewStore();
            store.SaveTitles(
                [new ContextTitleSet("rain|evening", ["Good for a wet evening"], 0, DateTime.UtcNow, "m", Style: 7)]);

            Assert.Equal(7, Assert.Single(NewStore().GetTitles()).Value.Style);

            // The other half, and the one that decides whether an upgrade actually
            // re-buys anything: every store already on disk was written before this
            // property existed, so the JSON has no "Style" and the constructor
            // default has to be what fills it in. If the serializer threw or filled
            // it with the current generation instead, stale titles would look
            // current and the rewritten prompt would reach nobody.
            File.WriteAllText(
                Path.Combine(_directory, "context-rows.json"),
                """
                {"Titles":[{"Condition":"rain|evening","Titles":["Slate Sky Slow Burns"],
                "Rotation":0,"LastUsedUtc":"2026-08-01T00:00:00Z","ModelId":"m"}],"Snapshots":[]}
                """);

            var legacy = Assert.Single(NewStore().GetTitles()).Value;

            Assert.Equal(["Slate Sky Slow Burns"], legacy.Titles);
            Assert.Equal(0, legacy.Style);
        }

        [Fact]
        public void SnapshotsRoundTripWithTheirPinnedConditions()
        {
            // The snapshot is what stops a row titled for rain filling itself from a
            // clear sky, so every field of the context has to survive the trip.
            var store = NewStore();
            var snapshot = new ContextRowSnapshot(
                "curator-context-now",
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                ["rain", "cold"],
                Daypart.Evening,
                "Rain-Soaked and Restless",
                "Pittsburgh",
                DateTime.UtcNow);

            store.SaveSnapshots([snapshot]);

            var read = Assert.Single(NewStore().GetSnapshots()).Value;

            Assert.Equal(["rain", "cold"], read.Weather);
            Assert.Equal(Daypart.Evening, read.Daypart);
            Assert.Equal("Rain-Soaked and Restless", read.Title);
            Assert.Equal("Pittsburgh", read.Place);
            Assert.Equal(["rain", "cold"], read.Context().Weather);
        }

        [Fact]
        public void SavingOneHalfLeavesTheOtherAlone()
        {
            // They share a file and are written by different steps of one pass.
            var store = NewStore();
            store.SaveTitles([new ContextTitleSet("weather:rain", ["A"], 0, DateTime.UtcNow)]);
            store.SaveSnapshots([
                new ContextRowSnapshot(
                    "curator-context-now", Guid.Empty,
                    [], Daypart.Morning, "Morning", "Pittsburgh", DateTime.UtcNow),
            ]);

            var reread = NewStore();

            Assert.Single(reread.GetTitles());
            Assert.Single(reread.GetSnapshots());
        }

        [Fact]
        public void SavingSnapshotsReplacesThemEntirely()
        {
            // How a viewer who is no longer targeted stops having a row on file.
            var store = NewStore();
            store.SaveSnapshots([
                new ContextRowSnapshot("a", Guid.Empty, ["rain"], Daypart.Evening, "A", "P", DateTime.UtcNow),
                new ContextRowSnapshot("b", Guid.Empty, [], Daypart.Evening, "B", "P", DateTime.UtcNow),
            ]);

            store.SaveSnapshots([
                new ContextRowSnapshot("a", Guid.Empty, ["snow"], Daypart.Morning, "A2", "P", DateTime.UtcNow),
            ]);

            var read = NewStore().GetSnapshots();

            Assert.Single(read);
            Assert.Equal("A2", read["a"].Title);
        }

        [Fact]
        public void AWriteIsVisibleToTheNextReadWithoutReopening()
        {
            // The read side caches on the file's write time, because it is called
            // from the path that draws the home screen. A refresh written a second
            // ago must still be seen.
            var store = NewStore();
            Assert.Empty(store.GetTitles());

            store.SaveTitles([new ContextTitleSet("weather:rain", ["A"], 0, DateTime.UtcNow)]);

            Assert.Single(store.GetTitles());
        }

        [Fact]
        public void AnUnreadableFileReadsAsEmptyRatherThanThrowing()
        {
            // An unusable store means titles fall back to the configured names and
            // rows fall back to live conditions. Both are working states, and this is
            // read from inside a home screen render.
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "context-rows.json"), "{ this is not json");

            var store = NewStore();

            Assert.Empty(store.GetTitles());
            Assert.Empty(store.GetSnapshots());
        }
    }
}
