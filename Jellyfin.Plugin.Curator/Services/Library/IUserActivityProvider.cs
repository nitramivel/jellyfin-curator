using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Services.Library
{
    /// <summary>
    /// Collects one user's watch activity for a set of library items, used to
    /// personalize playlist runs.
    /// </summary>
    public interface IUserActivityProvider
    {
        /// <summary>
        /// Gets the user's activity for each item they have interacted with.
        /// Items with no recorded activity are omitted from the result.
        /// </summary>
        /// <param name="userId">The user.</param>
        /// <param name="itemIds">The item IDs from the library scan.</param>
        /// <returns>Activity keyed by item ID; items without activity are absent.</returns>
        IReadOnlyDictionary<Guid, UserActivity> GetActivity(Guid userId, IReadOnlyCollection<Guid> itemIds);
    }
}
