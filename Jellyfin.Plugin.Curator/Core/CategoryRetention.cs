using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Core
{
    /// <summary>
    /// Decides which stored categories to drop when a pool is over its cap.
    ///
    /// The Reconciler caps what one run may <em>produce</em>; this caps what is
    /// <em>kept</em>. Without it, definitions accumulate across runs forever —
    /// every category the model ever coined stays in the list, and lowering a cap
    /// in configuration has no effect on what is already there.
    /// </summary>
    public static class CategoryRetention
    {
        /// <summary>
        /// Selects the categories to remove so each pool fits its cap.
        /// <para>
        /// Pools are counted separately: all shared categories against one cap, and
        /// each user's own categories against another. A cap of 0 means no limit.
        /// </para>
        /// </summary>
        /// <param name="stored">Every stored definition.</param>
        /// <param name="maxShared">Ceiling on shared categories; 0 disables.</param>
        /// <param name="maxPersonal">Ceiling on one user's personal categories; 0 disables.</param>
        /// <returns>The definitions to remove, oldest first.</returns>
        public static IReadOnlyList<CategoryDefinition> SelectForRemoval(
            IReadOnlyList<CategoryDefinition> stored,
            int maxShared,
            int maxPersonal)
        {
            ArgumentNullException.ThrowIfNull(stored);

            var removal = new List<CategoryDefinition>();

            removal.AddRange(Overflow(stored.Where(c => c.OwnerUserId is null), maxShared));

            foreach (var group in stored.Where(c => c.OwnerUserId is not null).GroupBy(c => c.OwnerUserId!.Value))
            {
                removal.AddRange(Overflow(group, maxPersonal));
            }

            return removal;
        }

        /// <summary>
        /// Takes the excess from one pool, oldest first.
        /// </summary>
        /// <remarks>
        /// "Oldest" is the least recently <see cref="CategoryDefinition.UpdatedAt"/>,
        /// not the earliest created. A category the model re-proposes every run has
        /// an old creation date and is the last thing anyone wants deleted; ordering
        /// on creation would remove exactly the categories that have proved most
        /// durable and keep whatever was coined most recently. Ordering on the last
        /// refresh drops the leftovers — definitions no recent run has produced —
        /// which is what "oldest" means when you are looking at the list.
        /// Creation date breaks ties, so two never-refreshed categories still come
        /// off in the order they arrived.
        /// </remarks>
        private static IEnumerable<CategoryDefinition> Overflow(
            IEnumerable<CategoryDefinition> pool,
            int cap)
        {
            if (cap <= 0)
            {
                return [];
            }

            var ordered = pool
                .OrderBy(c => c.UpdatedAt)
                .ThenBy(c => c.CreatedAt)
                .ToList();

            return ordered.Count <= cap ? [] : ordered.Take(ordered.Count - cap);
        }
    }
}
