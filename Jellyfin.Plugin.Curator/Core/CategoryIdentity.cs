using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Core
{
    /// <summary>
    /// How a reconciled category was recognised as one already stored.
    /// </summary>
    /// <param name="Definition">The stored definition to reuse.</param>
    /// <param name="MatchedByName">True when the name was identical; false when it was recognised by its members.</param>
    /// <param name="Similarity">Jaccard similarity of the member sets, for logging. 1.0 on a name match.</param>
    public sealed record CategoryMatch(
        CategoryDefinition Definition,
        bool MatchedByName,
        double Similarity);

    /// <summary>
    /// Matches a freshly reconciled category against the stored definitions from
    /// previous runs.
    ///
    /// <para>
    /// Identity is what keeps a home screen row alive. A matched category reuses
    /// its definition, and with it its playlist GUIDs, its handoff state and its
    /// home screen section; an unmatched one is a stranger, and the row it
    /// replaces is torn down.
    /// </para>
    /// <para>
    /// The name alone is not enough to carry that. Measured across three runs of
    /// this plugin, <em>not one</em> category name survived to the next run —
    /// 0 of 16, then 0 of 33. The model re-derives the same threads from the same
    /// library and words them differently every time ("Reality Coming Undone" →
    /// "Reality Is Optional" → "Glitch in the Reality Engine"), which is not a
    /// mistake: it never sees what it called them last time. So every run replaced
    /// every row.
    /// </para>
    /// <para>
    /// A category is therefore recognised by its <em>members</em> as well as its
    /// name. The name is a label the model is free to rewrite; the set of items is
    /// what the category actually is. Both the section (keyed
    /// <c>curator-{categoryId}</c>) and the playlist (keyed by stored GUID) survive
    /// a rename, so recognising a renamed category costs nothing and saves the row.
    /// </para>
    /// <para>
    /// Owner still separates the two kinds. A shared category (OwnerUserId null)
    /// comes from the library-wide pass and is one definition shared by everyone
    /// given it. A personal category belongs to one viewer, so two users coining
    /// the same name keep separate member lists — and a personal category is never
    /// matched against another user's.
    /// </para>
    /// </summary>
    public static class CategoryIdentity
    {
        /// <summary>
        /// How alike two member sets must be to count as the same category.
        /// </summary>
        /// <remarks>
        /// At 0.5, more than half of everything either category mentions is common
        /// to both. Two distinct themes drawn from one library do share titles —
        /// measured here, related-but-different sci-fi categories overlapped by
        /// about a quarter — so this sits well clear of thematic neighbours while
        /// still recognising a category that kept roughly two thirds of its items
        /// and changed its name.
        /// </remarks>
        public const double DefaultSimilarityThreshold = 0.5;

        /// <summary>
        /// Finds the stored definition a reconciled category should update.
        /// </summary>
        /// <param name="existing">All stored definitions.</param>
        /// <param name="name">The reconciled category's name.</param>
        /// <param name="members">Its member item IDs.</param>
        /// <param name="scopedUserId">The owning user for a personal category, or null for a shared one.</param>
        /// <param name="claimed">
        /// Definitions already taken by an earlier category in this run. Without
        /// this, two categories that both resemble one stored definition would both
        /// claim it and the second would overwrite the first.
        /// </param>
        /// <param name="similarityThreshold">Member similarity required for a rename match.</param>
        /// <returns>The match, or null when this is a genuinely new category.</returns>
        public static CategoryMatch? FindMatch(
            IReadOnlyList<CategoryDefinition> existing,
            string name,
            IReadOnlyCollection<Guid> members,
            Guid? scopedUserId,
            IReadOnlySet<Guid>? claimed = null,
            double similarityThreshold = DefaultSimilarityThreshold)
        {
            ArgumentNullException.ThrowIfNull(existing);
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(members);

            var candidates = existing
                .Where(definition => definition.OwnerUserId == scopedUserId)
                .Where(definition => claimed is null || !claimed.Contains(definition.Id))
                .ToList();

            // The name still wins when it is there. It is the model's own statement
            // that this is the same category, and it is exact where members are a
            // judgement call.
            var byName = candidates.Find(definition =>
                string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return new CategoryMatch(byName, MatchedByName: true, Similarity: 1.0);
            }

            if (members.Count == 0)
            {
                return null;
            }

            var memberSet = members as HashSet<Guid> ?? [.. members];

            CategoryDefinition? best = null;
            var bestSimilarity = 0.0;
            foreach (var definition in candidates)
            {
                var similarity = Similarity(memberSet, definition.Members);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    best = definition;
                }
            }

            return best is not null && bestSimilarity >= similarityThreshold
                ? new CategoryMatch(best, MatchedByName: false, bestSimilarity)
                : null;
        }

        /// <summary>
        /// Jaccard similarity: the shared items over every item either mentions.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT the overlap coefficient the Reconciler uses. That one
        /// divides by the smaller set, so a small category wholly inside a large
        /// one scores 1.0 — which is what you want when merging a partial
        /// restatement of an idea within a single run, and exactly what you do not
        /// want across runs, where it would let any six-item category swallow the
        /// identity of the twenty-item category containing it. Jaccard is symmetric
        /// and demands the two be alike in size as well as content.
        /// </remarks>
        private static double Similarity(HashSet<Guid> members, IReadOnlyList<Guid> storedMembers)
        {
            if (members.Count == 0 || storedMembers.Count == 0)
            {
                return 0.0;
            }

            var intersection = 0;
            var stored = new HashSet<Guid>(storedMembers);
            foreach (var id in stored)
            {
                if (members.Contains(id))
                {
                    intersection++;
                }
            }

            var union = members.Count + stored.Count - intersection;
            return union == 0 ? 0.0 : (double)intersection / union;
        }
    }
}
