using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services.Library
{
    /// <summary>
    /// Default <see cref="ILibraryScanner"/> backed by <see cref="ILibraryManager"/>.
    /// Query shape follows SmartLists: recursive, virtual items excluded.
    /// </summary>
    public class LibraryScanner : ILibraryScanner
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<LibraryScanner> _logger;

        public LibraryScanner(ILibraryManager libraryManager, ILogger<LibraryScanner> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        /// <inheritdoc />
        public LibraryHealth Inspect()
        {
            var items = _libraryManager.GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                Recursive = true,
                IsVirtualItem = false,
            }).Items;

            var roots = LibraryRoots();
            var orphaned = 0;
            foreach (var item in items)
            {
                if (!LibraryPathFilter.IsInsideLibrary(item.Path, roots))
                {
                    orphaned++;
                }
            }

            return new LibraryHealth(items.Count - orphaned, orphaned);
        }

        /// <inheritdoc />
        public IReadOnlyList<MediaItemRecord> ScanLibrary(
            bool includeEpisodes,
            string? surfacedCollections = null,
            int maxOverviewLength = ItemReducer.DefaultMaxOverviewLength,
            IReadOnlyDictionary<Guid, CondensedSummary>? condensedSummaries = null,
            bool useCondensedTags = false,
            bool surfaceAllCollections = false)
        {
            var kinds = includeEpisodes
                ? new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode }
                : new[] { BaseItemKind.Movie, BaseItemKind.Series };

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = kinds,
                Recursive = true,
                IsVirtualItem = false,
            };

            var items = _libraryManager.GetItemsResult(query).Items;
            var roots = LibraryRoots();
            var collections = ResolveCollections(surfacedCollections, surfaceAllCollections);

            var records = new List<MediaItemRecord>(items.Count);
            var skipped = 0;
            var orphaned = 0;
            var condensedUsed = 0;
            var condensedTags = 0;
            foreach (var item in items)
            {
                // A library folder that was removed or remounted leaves its items
                // behind, path and all. They are indistinguishable from real ones
                // here and play back as nothing, so they must not reach the model.
                if (!LibraryPathFilter.IsInsideLibrary(item.Path, roots))
                {
                    orphaned++;
                    continue;
                }

                var record = ItemReducer.Reduce(item, maxOverviewLength);
                if (record is null)
                {
                    skipped++;
                    continue;
                }

                if (collections.TryGetValue(item.Id, out var names))
                {
                    record = record with { Collections = names };
                }

                // Substituted on the way out, never written back: Jellyfin's own
                // overview stays exactly as the metadata provider left it, and
                // deleting every summary restores the previous behaviour exactly.
                if (condensedSummaries is not null
                    && condensedSummaries.TryGetValue(item.Id, out var condensed))
                {
                    if (!string.IsNullOrWhiteSpace(condensed.Text))
                    {
                        record = record with { Overview = condensed.Text };
                        condensedUsed++;
                    }

                    // Only when there is something to swap in. An item the model
                    // judged had no tags worth keeping should fall back to nothing
                    // rather than to eighteen lines of production trivia.
                    if (useCondensedTags && condensed.Tags.Count > 0)
                    {
                        record = record with { Tags = [.. condensed.Tags] };
                        condensedTags++;
                    }
                }

                records.Add(record);
            }

            if (orphaned > 0)
            {
                _logger.LogWarning(
                    "Curator: {Orphaned} item(s) sit outside every configured library folder and were left out. "
                    + "These are rows left behind by a removed or remounted library; they play back as nothing. "
                    + "A library scan in Jellyfin clears them.",
                    orphaned);
            }

            _logger.LogInformation(
                "Library scan: {Count} items reduced ({Skipped} skipped, {Orphaned} outside the library), "
                + "episodes {Episodes}, {Condensed} using a condensed summary, {Tagged} using consolidated tags",
                records.Count,
                skipped,
                orphaned,
                includeEpisodes ? "included" : "excluded",
                condensedUsed,
                condensedTags);

            return records;
        }

        /// <summary>
        /// Maps item IDs to the names of the surfaced collections holding them.
        /// </summary>
        /// <remarks>
        /// Collection membership is a <em>link</em>, not a parent: a BoxSet holds its
        /// items in LinkedChildren, so querying children by parent ID returns nothing.
        /// It has to be read off each BoxSet and inverted.
        /// </remarks>
        /// <param name="surfacedCollections">Comma-separated collection names; empty surfaces none.</param>
        /// <param name="surfaceAll">
        /// Whether every collection is surfaced, ignoring the name list entirely.
        /// </param>
        /// <returns>Collection names by item ID.</returns>
        private Dictionary<Guid, IReadOnlyList<string>> ResolveCollections(
            string? surfacedCollections,
            bool surfaceAll)
        {
            var wanted = CollectionSurfacing.ParseNames(surfacedCollections);

            var map = new Dictionary<Guid, IReadOnlyList<string>>();
            if (!CollectionSurfacing.SurfacesAnything(wanted, surfaceAll))
            {
                return map;
            }

            try
            {
                var boxSets = _libraryManager.GetItemsResult(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.BoxSet],
                    Recursive = true,
                }).Items.OfType<BoxSet>();

                foreach (var boxSet in boxSets)
                {
                    if (!CollectionSurfacing.ShouldSurface(boxSet.Name, wanted, surfaceAll))
                    {
                        continue;
                    }

                    foreach (var child in boxSet.GetLinkedChildren())
                    {
                        if (map.TryGetValue(child.Id, out var existing))
                        {
                            map[child.Id] = [.. existing, boxSet.Name];
                        }
                        else
                        {
                            map[child.Id] = [boxSet.Name];
                        }
                    }
                }

                _logger.LogInformation(
                    "Curator: {Count} item(s) carry a collection label ({Mode})",
                    map.Count,
                    surfaceAll ? "every collection" : $"{wanted.Count} named collection(s)");
            }
            catch (Exception ex)
            {
                // Never fatal: a label is a nice-to-have, a run is not.
                _logger.LogWarning(ex, "Curator: could not read collections; items go to the model unlabelled");
            }

            return map;
        }

        /// <summary>
        /// The server's configured library folder paths.
        /// </summary>
        /// <remarks>
        /// Returns an empty list rather than throwing when the folder list cannot be
        /// read; <see cref="LibraryPathFilter"/> treats that as "keep everything",
        /// which is the right way to fail.
        /// </remarks>
        private IReadOnlyCollection<string> LibraryRoots()
        {
            try
            {
                var roots = new List<string>();
                foreach (var folder in _libraryManager.GetVirtualFolders())
                {
                    if (folder.Locations is null)
                    {
                        continue;
                    }

                    foreach (var location in folder.Locations)
                    {
                        if (!string.IsNullOrWhiteSpace(location))
                        {
                            roots.Add(location);
                        }
                    }
                }

                return roots;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Curator: could not read the library folder list; scanning everything");
                return [];
            }
        }
    }
}
