using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Services.Summaries;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    public sealed class SummaryStoreTests : IDisposable
    {
        private readonly string _directory;
        private readonly string _path;
        private readonly SummaryStore _store;

        public SummaryStoreTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "curator-tests-" + Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_directory, "summaries.json");
            _store = new SummaryStore(_path, NullLogger<SummaryStore>.Instance);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private static CondensedSummary Summary(Guid id, string text = "bleak and funny", string hash = "abc123") => new()
        {
            ItemId = id,
            Text = text,
            SourceHash = hash,
            ModelId = "grok-4.5",
            CreatedAt = DateTime.UtcNow,
            Title = "A Film",
            SourceLength = 280,
        };

        [Fact]
        public void GetAll_ReturnsEmptyBeforeAnythingIsWritten()
        {
            Assert.Empty(_store.GetAll());
        }

        [Fact]
        public void Upsert_ThenGetAll_RoundTripsEveryField()
        {
            var id = Guid.NewGuid();
            _store.Upsert([Summary(id)]);

            var stored = _store.GetAll()[id];

            Assert.Equal("bleak and funny", stored.Text);
            Assert.Equal("abc123", stored.SourceHash);
            Assert.Equal("grok-4.5", stored.ModelId);
            Assert.Equal(280, stored.SourceLength);
        }

        [Fact]
        public void Upsert_ReplacesAnExistingEntryRatherThanDuplicatingIt()
        {
            // A re-distilled item must replace its old summary. Appending instead
            // would grow the file forever and leave the stale text findable.
            var id = Guid.NewGuid();
            _store.Upsert([Summary(id, "first", "hash1")]);
            _store.Upsert([Summary(id, "second", "hash2")]);

            var all = _store.GetAll();

            Assert.Single(all);
            Assert.Equal("second", all[id].Text);
            Assert.Equal("hash2", all[id].SourceHash);
        }

        [Fact]
        public void Upsert_PreservesEntriesWrittenByAnEarlierBatch()
        {
            // Batches save as they go, so each write must merge with what is already
            // there — overwriting would mean only the last batch of a pass survived.
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            _store.Upsert([Summary(first)]);
            _store.Upsert([Summary(second)]);

            Assert.Equal(2, _store.GetAll().Count);
        }

        [Fact]
        public void Upsert_WithNothingToWriteDoesNotCreateAFile()
        {
            _store.Upsert([]);

            Assert.False(File.Exists(_path));
        }

        [Fact]
        public void Prune_DropsSummariesForItemsNoLongerInTheLibrary()
        {
            var kept = Guid.NewGuid();
            var gone = Guid.NewGuid();
            _store.Upsert([Summary(kept), Summary(gone)]);

            var removed = _store.Prune([kept]);

            Assert.Equal(1, removed);
            Assert.Equal(kept, Assert.Single(_store.GetAll()).Key);
        }

        [Fact]
        public void Prune_WithNothingToRemoveLeavesTheStoreAlone()
        {
            var id = Guid.NewGuid();
            _store.Upsert([Summary(id)]);

            Assert.Equal(0, _store.Prune([id]));
            Assert.Single(_store.GetAll());
        }

        [Fact]
        public void Clear_RemovesEverythingAndReportsHowMany()
        {
            _store.Upsert([Summary(Guid.NewGuid()), Summary(Guid.NewGuid())]);

            Assert.Equal(2, _store.Clear());
            Assert.Empty(_store.GetAll());
        }

        [Fact]
        public void GetAll_TreatsACorruptFileAsAnEmptyCache()
        {
            // The cache is an optimisation. A run that cannot read it must still
            // happen, just without the saving — never fail on unreadable JSON.
            Directory.CreateDirectory(_directory);
            File.WriteAllText(_path, "{ this is not json");

            Assert.Empty(_store.GetAll());
        }

        [Fact]
        public void GetAll_SkipsEntriesWithNoItemId()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(
                _path,
                """[{"ItemId":"00000000-0000-0000-0000-000000000000","Text":"orphan","SourceHash":"x"}]""");

            Assert.Empty(_store.GetAll());
        }
    }
}
