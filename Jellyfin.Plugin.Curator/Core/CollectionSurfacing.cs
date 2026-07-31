using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Curator.Core
{
    /// <summary>
    /// Decides which of the server's collections an item's membership is sent for.
    ///
    /// <para>
    /// Two modes, and the difference between them is a judgement about the prompt
    /// rather than about the data. Naming a few collections sends only the ones the
    /// owner considers evidence — awards, typically. Sending everything hands the
    /// model the owner's whole grouping of the library, which is richer but includes
    /// franchises, and a franchise is a ready-made metadata category of exactly the
    /// kind the system prompt tells the model not to propose.
    /// </para>
    ///
    /// <para>
    /// Pure so the mode switch is pinned by tests rather than discovered on a run;
    /// the Jellyfin query that feeds it lives in <c>Services/Library/LibraryScanner</c>.
    /// </para>
    /// </summary>
    public static class CollectionSurfacing
    {
        /// <summary>
        /// Parses the configured comma-separated collection names.
        /// </summary>
        /// <param name="configured">The raw setting; null or empty yields an empty set.</param>
        /// <returns>The names, compared case-insensitively.</returns>
        public static ISet<string> ParseNames(string? configured)
            => (configured ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether membership of this collection is sent to the model.
        /// </summary>
        /// <remarks>
        /// A nameless collection is never surfaced in either mode: its name is what
        /// gets written into the item's "in" list, and an empty string there reads to
        /// the model as a collection called nothing.
        /// </remarks>
        /// <param name="collectionName">The collection's name.</param>
        /// <param name="named">The configured names, from <see cref="ParseNames"/>.</param>
        /// <param name="surfaceAll">Whether every collection is surfaced.</param>
        /// <returns>Whether to surface it.</returns>
        public static bool ShouldSurface(string? collectionName, ISet<string> named, bool surfaceAll)
        {
            ArgumentNullException.ThrowIfNull(named);

            if (string.IsNullOrWhiteSpace(collectionName))
            {
                return false;
            }

            return surfaceAll || named.Contains(collectionName);
        }

        /// <summary>
        /// Whether the collection query is worth running at all.
        /// </summary>
        /// <remarks>
        /// An empty name list still means "none" when the owner is choosing names, and
        /// only means "everything" when they have said so — so clearing the box cannot
        /// silently become the opposite of what it used to mean.
        /// </remarks>
        /// <param name="named">The configured names.</param>
        /// <param name="surfaceAll">Whether every collection is surfaced.</param>
        /// <returns>Whether anything could be surfaced.</returns>
        public static bool SurfacesAnything(ISet<string> named, bool surfaceAll)
        {
            ArgumentNullException.ThrowIfNull(named);
            return surfaceAll || named.Count > 0;
        }
    }
}
