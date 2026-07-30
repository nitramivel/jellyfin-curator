using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Models;
using MediaBrowser.Controller.Entities;
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
        public IReadOnlyList<MediaItemRecord> ScanLibrary(bool includeEpisodes)
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
