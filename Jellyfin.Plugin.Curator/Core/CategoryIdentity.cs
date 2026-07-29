using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Core
{
    /// <summary>
    /// Matches a freshly reconciled category against the stored definitions from
    /// previous runs.
    ///
    /// A category's identity across runs is its name: the model re-proposes
    /// "Comfort Rewatch" each time, and reusing the same definition keeps its
    /// playlist GUIDs, its handoff state, and its home screen section stable
    /// instead of churning a new row every run. In personalized mode each user
    /// gets their own definition per name, so two users' takes on the same
    /// category never overwrite each other's member lists.
    /// </summary>
    public static class CategoryIdentity
    {
        /// <summary>
        /// Finds the stored definition a reconciled category should update.
        /// </summary>
        /// <param name="existing">All stored definitions.</param>
        /// <param name="name">The reconciled category's name.</param>
        /// <param name="scopedUserId">The user this run is for, or null for a shared run.</param>
        /// <returns>The definition to reuse, or null when this is a new category.</returns>
        public static CategoryDefinition? FindMatch(
            IReadOnlyList<CategoryDefinition> existing,
            string name,
            Guid? scopedUserId)
        {
            ArgumentNullException.ThrowIfNull(existing);
            ArgumentNullException.ThrowIfNull(name);

            return existing.FirstOrDefault(definition =>
                string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase)
                && (scopedUserId is null || definition.UserPlaylists.Exists(link => link.UserId == scopedUserId)));
        }
    }
}
