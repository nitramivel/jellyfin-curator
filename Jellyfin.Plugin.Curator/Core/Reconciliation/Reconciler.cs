using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Core.Reconciliation
{
    /// <summary>
    /// Settings for the reconciliation pass.
    /// </summary>
    /// <param name="MinCategorySize">Categories with fewer members after merging are dropped.</param>
    /// <param name="MaxCategories">Ceiling on the final category count; 0 disables the cap.</param>
    /// <param name="NameSimilarityThreshold">Normalized-name similarity at or above which two proposals merge.</param>
    /// <param name="MemberOverlapThreshold">Member overlap coefficient at or above which two proposals merge.</param>
    public sealed record ReconcilerSettings(
        int MinCategorySize,
        int MaxCategories,
        double NameSimilarityThreshold = 0.85,
        double MemberOverlapThreshold = 0.7);

    /// <summary>
    /// Merges the per-batch proposals into the final category set.
    ///
    /// Two proposals merge when their names are near-identical OR their member
    /// sets overlap heavily. This is deliberately OR, not AND: batches partition
    /// the library, so two batches' halves of the same category ("Comfort
    /// Rewatch" from batch 1 and batch 3) can never share a member — they merge
    /// on name alone. High member overlap catches the same-batch case where the
    /// model proposed one idea under two names.
    /// </summary>
    public static class Reconciler
    {
        /// <summary>
        /// Runs merge → minimum-size filter → cap over all batch proposals.
        /// </summary>
        /// <param name="proposals">All validated proposals from all batches.</param>
        /// <param name="settings">Reconciliation settings.</param>
        /// <returns>The final categories, strongest-ranked first.</returns>
        public static IReadOnlyList<ReconciledCategory> Reconcile(
            IReadOnlyList<CategoryProposal> proposals,
            ReconcilerSettings settings)
        {
            ArgumentNullException.ThrowIfNull(proposals);
            ArgumentNullException.ThrowIfNull(settings);

            var clusters = BuildClusters(proposals, settings);

            var categories = clusters
                .Select(MergeCluster)
                .Where(category => category.Members.Count >= Math.Max(1, settings.MinCategorySize))
                .OrderByDescending(category => category.SourceProposalCount)
                .ThenByDescending(category => category.Members.Count)
                .ThenBy(category => category.Name, StringComparer.Ordinal)
                .ToList();

            if (settings.MaxCategories > 0 && categories.Count > settings.MaxCategories)
            {
                categories = categories.Take(settings.MaxCategories).ToList();
            }

            return categories;
        }

        /// <summary>
        /// Greedy agglomerative clustering. Proposals are visited largest-first so
        /// the strongest statement of a category founds its cluster (and donates
        /// its name); a candidate joins the first cluster where it matches any
        /// existing member proposal.
        /// </summary>
        private static List<List<ClusterEntry>> BuildClusters(
            IReadOnlyList<CategoryProposal> proposals,
            ReconcilerSettings settings)
        {
            var ordered = proposals
                .Select((proposal, index) => new ClusterEntry(
                    proposal,
                    StringSimilarity.Normalize(proposal.Name),
                    new HashSet<Guid>(proposal.Members),
                    index))
                .OrderByDescending(entry => entry.Proposal.Members.Count)
                .ThenBy(entry => entry.NormalizedName, StringComparer.Ordinal)
                .ThenBy(entry => entry.OriginalIndex)
                .ToList();

            var clusters = new List<List<ClusterEntry>>();
            foreach (var candidate in ordered)
            {
                var home = clusters.Find(cluster => cluster.Exists(entry => Matches(entry, candidate, settings)));
                if (home is null)
                {
                    clusters.Add([candidate]);
                }
                else
                {
                    home.Add(candidate);
                }
            }

            return clusters;
        }

        private static bool Matches(ClusterEntry a, ClusterEntry b, ReconcilerSettings settings)
        {
            if (StringSimilarity.NormalizedNameSimilarity(a.NormalizedName, b.NormalizedName)
                >= settings.NameSimilarityThreshold)
            {
                return true;
            }

            return OverlapCoefficient(a.MemberSet, b.MemberSet) >= settings.MemberOverlapThreshold;
        }

        /// <summary>
        /// Overlap coefficient: |A ∩ B| / min(|A|, |B|). Chosen over Jaccard so a
        /// small category fully contained in a larger one scores 1.0.
        /// </summary>
        private static double OverlapCoefficient(HashSet<Guid> a, HashSet<Guid> b)
        {
            if (a.Count == 0 || b.Count == 0)
            {
                return 0.0;
            }

            var (smaller, larger) = a.Count <= b.Count ? (a, b) : (b, a);
            var intersection = smaller.Count(larger.Contains);
            return (double)intersection / smaller.Count;
        }

        /// <summary>
        /// Merges one cluster into a category. Name and description come from the
        /// founding (largest) proposal. Members are round-robin interleaved across
        /// the cluster's proposals — each list is in confidence order, and
        /// interleaving keeps the visible head of the final list (Collection
        /// Sections renders the first 16) representative of every batch instead of
        /// whichever batch happened to come first.
        /// </summary>
        private static ReconciledCategory MergeCluster(List<ClusterEntry> cluster)
        {
            var founder = cluster[0].Proposal;

            var members = new List<Guid>();
            var seen = new HashSet<Guid>();
            var position = 0;
            var exhausted = 0;
            while (exhausted < cluster.Count)
            {
                exhausted = 0;
                foreach (var entry in cluster)
                {
                    if (position >= entry.Proposal.Members.Count)
                    {
                        exhausted++;
                        continue;
                    }

                    var member = entry.Proposal.Members[position];
                    if (seen.Add(member))
                    {
                        members.Add(member);
                    }
                }

                position++;
            }

            return new ReconciledCategory
            {
                Name = founder.Name,
                Description = founder.Description,
                Members = members,
                SourceProposalCount = cluster.Count,
            };
        }

        private sealed record ClusterEntry(
            CategoryProposal Proposal,
            string NormalizedName,
            HashSet<Guid> MemberSet,
            int OriginalIndex);
    }
}
