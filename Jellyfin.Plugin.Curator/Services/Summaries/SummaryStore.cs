using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Curator.Core.Models;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services.Summaries
{
    /// <summary>
    /// Default <see cref="ISummaryStore"/>: a single JSON document at
    /// <c>{DataPath}/curator/summaries.json</c>, written atomically via temp file
    /// + rename.
    /// </summary>
    public class SummaryStore : ISummaryStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
        };

        private readonly string _path;
        private readonly ILogger<SummaryStore> _logger;
        private readonly object _lock = new();

        public SummaryStore(IServerApplicationPaths applicationPaths, ILogger<SummaryStore> logger)
            : this(Path.Combine(applicationPaths.DataPath, "curator", "summaries.json"), logger)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SummaryStore"/> class at an
        /// explicit path. Used directly by tests.
        /// </summary>
        /// <param name="path">The summaries file.</param>
        /// <param name="logger">The logger.</param>
        public SummaryStore(string path, ILogger<SummaryStore> logger)
        {
            _path = path;
            _logger = logger;
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<Guid, CondensedSummary> GetAll()
        {
            lock (_lock)
            {
                return ReadUnlocked();
            }
        }

        /// <inheritdoc />
        public void Upsert(IReadOnlyCollection<CondensedSummary> summaries)
        {
            ArgumentNullException.ThrowIfNull(summaries);

            if (summaries.Count == 0)
            {
                return;
            }

            lock (_lock)
            {
                var all = new Dictionary<Guid, CondensedSummary>(ReadUnlocked());
                foreach (var summary in summaries)
                {
                    all[summary.ItemId] = summary;
                }

                WriteUnlocked(all);
            }
        }

        /// <inheritdoc />
        public int Clear()
        {
            lock (_lock)
            {
                var count = ReadUnlocked().Count;
                WriteUnlocked(new Dictionary<Guid, CondensedSummary>());
                return count;
            }
        }

        /// <inheritdoc />
        public int Prune(IReadOnlyCollection<Guid> liveItemIds)
        {
            ArgumentNullException.ThrowIfNull(liveItemIds);

            lock (_lock)
            {
                var all = ReadUnlocked();
                var live = liveItemIds as HashSet<Guid> ?? [.. liveItemIds];
                var kept = all.Where(pair => live.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);

                var removed = all.Count - kept.Count;
                if (removed > 0)
                {
                    WriteUnlocked(kept);
                }

                return removed;
            }
        }

        private Dictionary<Guid, CondensedSummary> ReadUnlocked()
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            try
            {
                var stored = JsonSerializer.Deserialize<List<CondensedSummary>>(File.ReadAllText(_path));
                if (stored is null)
                {
                    return [];
                }

                var map = new Dictionary<Guid, CondensedSummary>(stored.Count);
                foreach (var summary in stored)
                {
                    if (summary is not null && summary.ItemId != Guid.Empty)
                    {
                        map[summary.ItemId] = summary;
                    }
                }

                return map;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // An unreadable cache is a cache miss, not a failure: the run that
                // asked for it must still happen, just without the saving.
                _logger.LogWarning(ex, "Curator: could not read the condensed summaries; treating them as absent");
                return [];
            }
        }

        private void WriteUnlocked(Dictionary<Guid, CondensedSummary> summaries)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Ordered by title so the file stays reviewable by hand and a diff
                // between two runs shows what actually changed rather than a reshuffle.
                var ordered = summaries.Values
                    .OrderBy(s => s.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(s => s.ItemId)
                    .ToList();

                var tempPath = _path + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(ordered, SerializerOptions));
                File.Move(tempPath, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing the cache costs money on the next run; it does not cost
                // correctness, so it must never take the run down with it.
                _logger.LogError(ex, "Curator: could not write the condensed summaries to {Path}", _path);
            }
        }
    }
}
