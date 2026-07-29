using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Core.Reconciliation;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    public class ReconcilerTests
    {
        /// <summary>Deterministic GUID for item number n.</summary>
        private static Guid Id(int n) => new(n, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);

        private static CategoryProposal Proposal(string name, params int[] members) => new()
        {
            Name = name,
            Description = $"About {name}.",
            Members = members.Select(Id).ToArray(),
        };

        private static ReconcilerSettings Settings(int minSize = 1, int max = 0) => new(minSize, max);

        [Fact]
        public void CrossBatchSameName_MergesOnNameAloneWithDisjointMembers()
        {
            // Batches partition the library: identical categories from two batches
            // can never share a member. They must still merge.
            var result = Reconciler.Reconcile(
                [
                    Proposal("Comfort Rewatch", 1, 2, 3),
                    Proposal("Comfort Rewatch", 4, 5, 6),
                ],
                Settings());

            var category = Assert.Single(result);
            Assert.Equal("Comfort Rewatch", category.Name);
            Assert.Equal(2, category.SourceProposalCount);
            Assert.Equal(6, category.Members.Count);
        }

        [Fact]
        public void MergedMembers_AreRoundRobinInterleaved()
        {
            // Each list is confidence-ordered; the merged head should draw from
            // every source list, not concatenate batch 1 before batch 2.
            var result = Reconciler.Reconcile(
                [
                    Proposal("Comfort Rewatch", 1, 2, 3),
                    Proposal("Comfort Rewatch", 4, 5, 6),
                ],
                Settings());

            Assert.Equal(
                new[] { Id(1), Id(4), Id(2), Id(5), Id(3), Id(6) },
                result[0].Members);
        }

        [Fact]
        public void FuzzyNameVariants_Merge()
        {
            var result = Reconciler.Reconcile(
                [
                    Proposal("Comfort Rewatch", 1, 2, 3),
                    Proposal("Comfort Rewatches!", 4, 5, 6),
                ],
                Settings());

            Assert.Single(result);
        }

        [Fact]
        public void SameBatchHighOverlap_MergesDespiteDifferentNames()
        {
            // The model proposed one idea under two names within a single batch;
            // shared members give it away.
            var result = Reconciler.Reconcile(
                [
                    Proposal("Thinky Sci-Fi", 1, 2, 3, 4),
                    Proposal("Brainy Films", 2, 3, 4),
                ],
                Settings());

            var category = Assert.Single(result);
            Assert.Equal("Thinky Sci-Fi", category.Name); // larger proposal founds the cluster
            Assert.Equal([Id(1), Id(2), Id(3), Id(4)], category.Members); // deduped
        }

        [Fact]
        public void SmallSubsetOfLargerCategory_Merges()
        {
            // Overlap coefficient (|A∩B| / min) — a full subset merges even though
            // its Jaccard against the larger set would be low.
            var result = Reconciler.Reconcile(
                [
                    Proposal("Quietly Devastating", 1, 2, 3, 4, 5, 6),
                    Proposal("Sad Ones", 1, 2),
                ],
                Settings());

            var category = Assert.Single(result);
            Assert.Equal("Quietly Devastating", category.Name);
            Assert.Equal(6, category.Members.Count);
        }

        [Fact]
        public void DistinctCategories_StaySeparate()
        {
            var result = Reconciler.Reconcile(
                [
                    Proposal("Slow Burn Horror", 1, 2, 3),
                    Proposal("Slow Burn Romance", 4, 5, 6),
                ],
                Settings());

            Assert.Equal(2, result.Count);
        }

        [Fact]
        public void MinimumSize_AppliesAfterMerging()
        {
            // Two size-3 halves merge into 6 and survive a min of 5; the size-3
            // singleton does not.
            var result = Reconciler.Reconcile(
                [
                    Proposal("Lonely Little Category", 7, 8, 9),
                    Proposal("Comfort Rewatch", 1, 2, 3),
                    Proposal("Comfort Rewatch", 4, 5, 6),
                ],
                Settings(minSize: 5));

            var category = Assert.Single(result);
            Assert.Equal("Comfort Rewatch", category.Name);
        }

        [Fact]
        public void Cap_KeepsCategoriesProposedInMostBatches()
        {
            // Comfort Rewatch: 3 batches. Cerebral Sci-Fi: 2 batches. The giant:
            // 1 batch but the largest membership. Cross-batch agreement outranks raw size.
            var result = Reconciler.Reconcile(
                [
                    Proposal("Comfort Rewatch", 1, 2),
                    Proposal("Comfort Rewatch", 3, 4),
                    Proposal("Comfort Rewatch", 5, 6),
                    Proposal("Cerebral Sci-Fi", 7, 8),
                    Proposal("Cerebral Sci-Fi", 9, 10),
                    Proposal("Quietly Devastating", 11, 12, 13, 14, 15, 16, 17, 18, 19, 20),
                ],
                Settings(minSize: 1, max: 2));

            Assert.Equal(2, result.Count);
            Assert.Equal("Comfort Rewatch", result[0].Name);
            Assert.Equal("Cerebral Sci-Fi", result[1].Name);
        }

        [Fact]
        public void CapZero_MeansUncapped()
        {
            // Names must be genuinely distinct — numbered variants of one name
            // would (correctly) merge under fuzzy matching.
            var names = new[]
            {
                "Comfort Rewatch", "Cerebral Sci-Fi", "Quietly Devastating",
                "Saturday Afternoon Cable", "Dumb & Perfect", "Bottle Episodes",
                "Neon Noir", "Feel-Bad Masterpieces", "Popcorn Chaos",
                "Slow Cinema", "Heist Energy", "Coming Of Age", "Desert Westerns",
                "Talky Dramas", "Creature Features", "Cold War Paranoia",
                "Found Footage", "Courtroom Tension", "Road Trips", "Space Dread",
            };
            var proposals = names
                .Select((name, i) => Proposal(name, i * 2 + 100, i * 2 + 101))
                .ToArray();

            var result = Reconciler.Reconcile(proposals, Settings(minSize: 1, max: 0));

            Assert.Equal(names.Length, result.Count);
        }

        [Fact]
        public void NameAndDescription_ComeFromLargestProposal()
        {
            var small = Proposal("Comfort Rewatch", 1, 2, 3);
            var large = Proposal("Comfort Rewatches", 4, 5, 6, 7, 8);

            var result = Reconciler.Reconcile([small, large], Settings());

            var category = Assert.Single(result);
            Assert.Equal("Comfort Rewatches", category.Name);
            Assert.Equal("About Comfort Rewatches.", category.Description);
        }

        [Fact]
        public void ShuffledInput_ProducesSameCategoriesAndMemberSets()
        {
            var proposals = new[]
            {
                Proposal("Comfort Rewatch", 1, 2, 3),
                Proposal("Comfort Rewatch", 4, 5, 6),
                Proposal("Slow Burn Horror", 10, 11, 12),
                Proposal("Thinky Sci-Fi", 20, 21, 22, 23),
                Proposal("Brainy Films", 21, 22, 23),
            };

            var forward = Reconciler.Reconcile(proposals, Settings());
            var reversed = Reconciler.Reconcile(proposals.Reverse().ToArray(), Settings());

            Assert.Equal(
                forward.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal),
                reversed.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
            foreach (var category in forward)
            {
                var twin = reversed.Single(c => c.Name == category.Name);
                Assert.Equal(
                    category.Members.OrderBy(m => m),
                    twin.Members.OrderBy(m => m));
            }
        }

        [Fact]
        public void Ranking_IsProposalCountThenSize()
        {
            var result = Reconciler.Reconcile(
                [
                    Proposal("Single Big", 1, 2, 3, 4, 5, 6, 7, 8),
                    Proposal("Agreed Twice", 10, 11),
                    Proposal("Agreed Twice", 12, 13),
                ],
                Settings());

            Assert.Equal("Agreed Twice", result[0].Name);
            Assert.Equal("Single Big", result[1].Name);
        }

        [Fact]
        public void EmptyInput_YieldsEmptyResult()
        {
            Assert.Empty(Reconciler.Reconcile([], Settings()));
        }

        [Fact]
        public void ThreeWayMerge_CountsAllSources()
        {
            var result = Reconciler.Reconcile(
                [
                    Proposal("Saturday Afternoon Cable", 1, 2),
                    Proposal("Saturday Afternoon Cable", 3, 4),
                    Proposal("Saturday Afternoon Cable", 5, 6),
                ],
                Settings());

            var category = Assert.Single(result);
            Assert.Equal(3, category.SourceProposalCount);
            Assert.Equal(6, category.Members.Count);
        }
    }
}
