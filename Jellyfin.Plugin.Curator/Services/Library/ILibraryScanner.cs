using System.Collections.Generic;
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
        /// <returns>The reduced records, in library enumeration order.</returns>
        IReadOnlyList<MediaItemRecord> ScanLibrary(bool includeEpisodes);
    }
}
