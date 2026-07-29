using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.Curator.Core.Models;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services.Categories
{
    /// <summary>
    /// Default <see cref="ICategoryStore"/>: one JSON file per category at
    /// <c>{DataPath}/curator/categories/{id}.json</c> (same layout idea as
    /// SmartLists' data directory), written atomically via temp file + rename.
    /// </summary>
    public class CategoryStore : ICategoryStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
        };

        private readonly string _basePath;
        private readonly ILogger<CategoryStore> _logger;
        private readonly object _lock = new();

        public CategoryStore(IServerApplicationPaths applicationPaths, ILogger<CategoryStore> logger)
            : this(Path.Combine(applicationPaths.DataPath, "curator", "categories"), logger)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CategoryStore"/> class
        /// rooted at an explicit directory. Used directly by tests.
        /// </summary>
        /// <param name="basePath">The directory holding the category files.</param>
        /// <param name="logger">The logger.</param>
        public CategoryStore(string basePath, ILogger<CategoryStore> logger)
        {
            _basePath = basePath;
            _logger = logger;
        }

        /// <inheritdoc />
        public IReadOnlyList<CategoryDefinition> GetAll()
        {
            lock (_lock)
            {
                if (!Directory.Exists(_basePath))
                {
                    return [];
                }

                var categories = new List<CategoryDefinition>();
                foreach (var file in Directory.EnumerateFiles(_basePath, "*.json"))
                {
                    var category = TryRead(file);
                    if (category is not null)
                    {
                        categories.Add(category);
                    }
                }

                categories.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                return categories;
            }
        }

        /// <inheritdoc />
        public CategoryDefinition? Get(Guid id)
        {
            lock (_lock)
            {
                var path = PathFor(id);
                return File.Exists(path) ? TryRead(path) : null;
            }
        }

        /// <inheritdoc />
        public void Save(CategoryDefinition category)
        {
            ArgumentNullException.ThrowIfNull(category);

            lock (_lock)
            {
                Directory.CreateDirectory(_basePath);
                var path = PathFor(category.Id);
                var tempPath = path + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(category, SerializerOptions));
                File.Move(tempPath, path, overwrite: true);
            }
        }

        /// <inheritdoc />
        public bool Delete(Guid id)
        {
            lock (_lock)
            {
                var path = PathFor(id);
                if (!File.Exists(path))
                {
                    return false;
                }

                File.Delete(path);
                return true;
            }
        }

        private string PathFor(Guid id)
        {
            return Path.Combine(_basePath, id.ToString("N") + ".json");
        }

        private CategoryDefinition? TryRead(string path)
        {
            try
            {
                return JsonSerializer.Deserialize<CategoryDefinition>(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning(ex, "Curator: skipping unreadable category file {Path}", path);
                return null;
            }
        }
    }
}
