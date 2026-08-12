using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Services.Library
{
    /// <summary>
    /// Enumerates the media library and reduces items to their LLM-facing records.
    /// </summary>
    /// <summary>Counts from a cheap look at the library, for the health check.</summary>
    /// <param name="Items">Movies and series inside a configured library folder.</param>
    /// <param name="Orphaned">
    /// Rows whose path sits outside every configured library folder — left behind
    /// when a folder is removed or a mount renamed. Indistinguishable from real
    /// items in Jellyfin, and they play back as nothing.
    /// </param>
    public sealed record LibraryHealth(int Items, int Orphaned);

    public interface ILibraryScanner
    {
        /// <summary>
        /// Counts real and orphaned library rows without building records for them.
        /// </summary>
        /// <returns>The counts.</returns>
        LibraryHealth Inspect();

        /// <summary>
        /// Scans the library for movies and series, plus episodes when requested,
        /// reduced to compact records. Virtual items (e.g. missing episodes) are excluded.
        /// </summary>
        /// <param name="includeEpisodes">Whether individual episodes are included alongside their series.</param>
        /// <param name="surfacedCollections">Comma-separated collection names to label items with; empty labels none.</param>
        /// <param name="surfaceAllCollections">
        /// Whether every collection an item belongs to is sent, ignoring
        /// <paramref name="surfacedCollections"/> entirely.
        /// </param>
        /// <param name="maxOverviewLength">
        /// Where to cut each overview. Pass <see cref="ItemReducer.NoOverviewLimit"/>
        /// to keep it whole, as the condensed-summary pass must.
        /// </param>
        /// <param name="condensedSummaries">
        /// Condensed summaries by item ID. Where one exists it replaces the overview
        /// on the record, which is how a distilled library reaches the model. Jellyfin's
        /// own overview is never modified — the substitution happens on the way out.
        /// </param>
        /// <param name="useCondensedTags">
        /// Whether an item's consolidated tags replace its scraped ones. Independent
        /// of the overview substitution: the two are built together but sending them
        /// is two separate decisions.
        /// </param>
        /// <summary>
        /// Looks up what kind of item each of these IDs is.
        /// </summary>
        /// <remarks>
        /// A deliberately narrow lookup rather than a scan. The caller — splitting a
        /// viewer's recommendations into a films list and a television one — needs
        /// one field about a few dozen items it already holds the IDs of, and a full
        /// <see cref="ScanLibrary"/> to learn it would reduce the whole library, read
        /// every overview and resolve every collection to answer a question about
        /// none of that. It also has to work on the free path, where nothing is sent
        /// to a model and no scan happens at all.
        /// <para>
        /// An ID that no longer resolves is simply absent from the result, which is
        /// the same way <c>ResolveMembers</c> treats an item that has left the
        /// library.
        /// </para>
        /// </remarks>
        /// <param name="itemIds">The items to look up.</param>
        /// <returns>The kind of each item that still exists.</returns>
        IReadOnlyDictionary<Guid, MediaKind> GetKinds(IReadOnlyCollection<Guid> itemIds);

        /// <returns>The reduced records, in library enumeration order.</returns>
        IReadOnlyList<MediaItemRecord> ScanLibrary(
            bool includeEpisodes,
            string? surfacedCollections = null,
            int maxOverviewLength = ItemReducer.DefaultMaxOverviewLength,
            IReadOnlyDictionary<Guid, CondensedSummary>? condensedSummaries = null,
            bool useCondensedTags = false,
            bool surfaceAllCollections = false);
    }
}
