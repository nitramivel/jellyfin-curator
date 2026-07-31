using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Services.Summaries
{
    /// <summary>
    /// Persistence for condensed summaries.
    /// </summary>
    /// <remarks>
    /// One file for the whole set rather than one file per item, unlike
    /// <see cref="Categories.ICategoryStore"/>. A category is edited, deleted and
    /// read one at a time by a human; summaries are written in bulk by a batch and
    /// read wholesale at the start of every run, so a few hundred separate files
    /// would be several hundred opens to answer one question.
    /// </remarks>
    public interface ISummaryStore
    {
        /// <summary>
        /// Loads every stored summary, keyed by item ID. Returns empty when nothing
        /// has been distilled yet or the file cannot be read.
        /// </summary>
        /// <returns>Summaries by item ID.</returns>
        IReadOnlyDictionary<Guid, CondensedSummary> GetAll();

        /// <summary>
        /// Merges summaries into the store, replacing any existing entry for the
        /// same item, and writes the file atomically.
        /// </summary>
        /// <param name="summaries">The summaries to add or replace.</param>
        void Upsert(IReadOnlyCollection<CondensedSummary> summaries);

        /// <summary>
        /// Removes every stored summary.
        /// </summary>
        /// <returns>How many were removed.</returns>
        int Clear();

        /// <summary>
        /// Removes summaries for items that are no longer in the library.
        /// </summary>
        /// <param name="liveItemIds">The IDs still present.</param>
        /// <returns>How many were removed.</returns>
        int Prune(IReadOnlyCollection<Guid> liveItemIds);
    }
}
