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

            var records = new List<MediaItemRecord>(items.Count);
            var skipped = 0;
            foreach (var item in items)
            {
                var record = ItemReducer.Reduce(item);
                if (record is null)
                {
                    skipped++;
                    continue;
                }

                records.Add(record);
            }

            _logger.LogInformation(
                "Library scan: {Count} items reduced ({Skipped} skipped), episodes {Episodes}",
                records.Count,
                skipped,
                includeEpisodes ? "included" : "excluded");

            return records;
        }
    }
}
