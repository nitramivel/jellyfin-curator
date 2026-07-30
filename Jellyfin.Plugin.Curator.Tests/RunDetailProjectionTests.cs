using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Curator.Services.Runs;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// The projection is what the run history panel shows. It reads step detail
    /// that arrived from disk as <see cref="JsonElement"/>, so every accessor has
    /// to survive a missing key, a wrong type, and a step written by a newer
    /// version — a run detail page must degrade, never throw.
    /// </summary>
    public class RunDetailProjectionTests
    {
        private static readonly Guid Levi = Guid.Parse("f6b161c2-504d-40a1-a4d0-e8250ad9ab21");
        private static readonly Guid Eric = Guid.Parse("0d53e9e8-e95b-4bb9-8819-1f0f1bd72e54");

        /// <summary>
        /// Round-trips through JSON so Detail values are JsonElement exactly as they
        /// are when a stored run is read back, rather than the live CLR types.
        /// </summary>
        private static RunLogDocument Stored(params RunStep[] steps)
        {
            var document = new RunLogDocument
            {
                RunId = Guid.NewGuid(),
                Trigger = "manual",
                Status = RunStatus.Completed,
                StartedAt = DateTime.UtcNow,
                Settings = new Dictionary<string, object?>(),
            };
            document.Steps.AddRange(steps);

            var json = JsonSerializer.Serialize(document);
            return JsonSerializer.Deserialize<RunLogDocument>(json)!;
        }

        private static RunStep Step(int seq, string name, Dictionary<string, object?>? detail = null)
            => new(seq, DateTime.UtcNow, name, name, detail);

        [Fact]
        public void ReadsTheScanAndDiscoveryFacts()
        {
            var document = Stored(
                Step(1, "library.scanned", new() { ["itemCount"] = 297, ["includeEpisodes"] = false }),
                Step(2, "discovery.reconciled", new()
                {
                    ["proposalCount"] = 10,
                    ["candidateCount"] = 8,
                    ["batchesSkipped"] = 1,
                }));

            var detail = RunDetailProjection.Project(document);

            Assert.Equal(297, detail.Library!.ItemCount);
            Assert.False(detail.Library.IncludeEpisodes);
            Assert.Equal(10, detail.Discovery!.ProposalCount);
            Assert.Equal(8, detail.Discovery.CandidateCount);
            Assert.Equal(1, detail.Discovery.BatchesSkipped);
        }

        [Fact]
        public void CountsWhatHappenedToTheCategorySet()
        {
            var document = Stored(
                Step(1, "category.built"), Step(2, "category.built"),
                Step(3, "category.renamed"),
                Step(4, "category.retired"), Step(5, "category.retired"), Step(6, "category.retired"),
                Step(7, "category.pruned"),
                Step(8, "homescreen.synced"), Step(9, "run.complete"));

            var c = RunDetailProjection.Project(document).Categories;

            Assert.Equal(2, c.Built);
            Assert.Equal(1, c.Renamed);
            Assert.Equal(3, c.Retired);
            Assert.Equal(1, c.Pruned);
        }

        [Fact]
        public void BuildsOneRowPerViewer_SkippedAndPersonalised()
        {
            var document = Stored(
                Step(1, "user.skipped", new()
                {
                    ["userId"] = Eric.ToString(),
                    ["watchedCount"] = 0,
                }),
                Step(2, "user.pass", new()
                {
                    ["userId"] = Levi.ToString(),
                    ["watchedCount"] = 93,
                    ["seriesWithHistory"] = 29,
                    ["proposedCount"] = 10,
                }));

            var names = new Dictionary<Guid, string> { [Levi] = "levi", [Eric] = "eric" };
            var users = RunDetailProjection.Project(document, names).Users;

            Assert.Equal(2, users.Count);

            Assert.Equal("eric", users[0].Name);
            Assert.True(users[0].Skipped);
            Assert.Equal(0, users[0].WatchedCount);

            Assert.Equal("levi", users[1].Name);
            Assert.False(users[1].Skipped);
            Assert.Equal(93, users[1].WatchedCount);
            Assert.Equal(29, users[1].SeriesWithHistory);
            Assert.Equal(10, users[1].PersonalCount);
        }

        /// <summary>
        /// seriesWithHistory did not exist before the episode rollup landed, so every
        /// run recorded before it must still project rather than throwing.
        /// </summary>
        [Fact]
        public void AStepFromAnOlderVersionReadsAsZeroNotAnError()
        {
            var document = Stored(Step(1, "user.pass", new()
            {
                ["userId"] = Levi.ToString(),
                ["watchedCount"] = 64,
                ["proposedCount"] = 6,
            }));

            var user = Assert.Single(RunDetailProjection.Project(document).Users);
            Assert.Equal(0, user.SeriesWithHistory);
            Assert.Equal(64, user.WatchedCount);
        }

        [Fact]
        public void AStepWithNoDetailAtAllIsSurvivable()
        {
            var document = Stored(
                Step(1, "library.scanned"),
                Step(2, "user.pass"),
                Step(3, "discovery.reconciled"));

            var detail = RunDetailProjection.Project(document);

            Assert.Equal(0, detail.Library!.ItemCount);
            Assert.Equal(Guid.Empty, Assert.Single(detail.Users).UserId);
        }

        [Fact]
        public void AnUnknownUserIdLeavesTheNameNull()
        {
            var document = Stored(Step(1, "user.pass", new()
            {
                ["userId"] = Levi.ToString(),
                ["watchedCount"] = 1,
            }));

            Assert.Null(Assert.Single(RunDetailProjection.Project(document, new Dictionary<Guid, string>()).Users).Name);
        }

        [Fact]
        public void UnknownStepsAreIgnoredRatherThanCounted()
        {
            var document = Stored(
                Step(1, "something.new.from.a.later.version"),
                Step(2, "category.built"));

            Assert.Equal(1, RunDetailProjection.Project(document).Categories.Built);
        }

        [Fact]
        public void CarriesTheFileSizeForTheDownloadLink()
        {
            Assert.Equal(238_000, RunDetailProjection.Project(Stored(), null, 238_000).FileSizeBytes);
        }
    }
}
