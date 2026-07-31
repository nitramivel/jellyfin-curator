using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Services.Library
{
    /// <summary>
    /// Enumerates the media library and reduces items to their LLM-facing records.
    /// </summary>
    public interface ILibraryScanner
    {
        /// <summary>
        /// Scans the library for movies and series, plus episodes when requested,
        /// reduced to compact records. Virtual items (e.g. missing episodes) are excluded.
        /// </summary>
        /// <param name="includeEpisodes">Whether individual episodes are included alongside their series.</param>
        /// <param name="surfacedCollections">Comma-separated collection names to label items with; empty labels none.</param>
        /// <param name="maxOverviewLength">
        /// Where to cut each overview. Pass <see cref="ItemReducer.NoOverviewLimit"/>
        /// to keep it whole, as the condensed-summary pass must.
        /// </param>
        /// <param name="condensedSummaries">
        /// Condensed summaries by item ID. Where one exists it replaces the overview
        /// on the record, which is how a distilled library reaches the model. Jellyfin's
        /// own overview is never modified — the substitution happens on the way out.
        /// </param>
        /// <returns>The reduced records, in library enumeration order.</returns>
        IReadOnlyList<MediaItemRecord> ScanLibrary(
            bool includeEpisodes,
            string? surfacedCollections = null,
            int maxOverviewLength = ItemReducer.DefaultMaxOverviewLength,
            IReadOnlyDictionary<Guid, string>? condensedSummaries = null);
    }
}
