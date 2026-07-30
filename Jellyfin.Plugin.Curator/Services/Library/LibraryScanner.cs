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
        public IReadOnlyList<MediaItemRecord> ScanLibrary(bool includeEpisodes, string? surfacedCollections = null)
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
            var collections = ResolveCollections(surfacedCollections);

            var records = new List<MediaItemRecord>(items.Count);
            var skipped = 0;
            var orphaned = 0;
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

                var record = ItemReducer.Reduce(item);
                if (record is null)
                {
                    skipped++;
                    continue;
                }

                if (collections.TryGetValue(item.Id, out var names))
                {
                    record = record with { Collections = names };
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
                "Library scan: {Count} items reduced ({Skipped} skipped, {Orphaned} outside the library), episodes {Episodes}",
                records.Count,
                skipped,
                orphaned,
                includeEpisodes ? "included" : "excluded");

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
        /// <returns>Collection names by item ID.</returns>
        private Dictionary<Guid, IReadOnlyList<string>> ResolveCollections(string? surfacedCollections)
        {
            var wanted = (surfacedCollections ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var map = new Dictionary<Guid, IReadOnlyList<string>>();
            if (wanted.Count == 0)
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
                    if (!wanted.Contains(boxSet.Name))
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
                    "Curator: {Count} item(s) carry a surfaced collection label", map.Count);
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
