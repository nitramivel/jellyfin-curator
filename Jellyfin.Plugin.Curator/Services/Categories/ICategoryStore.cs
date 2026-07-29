using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Services.Categories
{
    /// <summary>
    /// Persistence for category definitions: one JSON file per category under the
    /// plugin data directory. Deliberately not the plugin config XML, which is a
    /// single blob rewritten wholesale and does not scale to many categories.
    /// </summary>
    public interface ICategoryStore
    {
        /// <summary>
        /// Loads all category definitions. Unreadable files are skipped with a log
        /// entry rather than failing the run.
        /// </summary>
        /// <returns>All definitions.</returns>
        IReadOnlyList<CategoryDefinition> GetAll();

        /// <summary>
        /// Loads one definition.
        /// </summary>
        /// <param name="id">The category ID.</param>
        /// <returns>The definition, or null if absent.</returns>
        CategoryDefinition? Get(Guid id);

        /// <summary>
        /// Saves a definition atomically (temp file + rename).
        /// </summary>
        /// <param name="category">The definition.</param>
        void Save(CategoryDefinition category);

        /// <summary>
        /// Deletes a definition.
        /// </summary>
        /// <param name="id">The category ID.</param>
        /// <returns>True if a file was removed.</returns>
        bool Delete(Guid id);
    }
}
