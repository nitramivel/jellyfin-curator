using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Curator.Core.Context;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services.Context
{
    /// <summary>
    /// Default <see cref="IContextRowStore"/>: one JSON document at
    /// <c>{DataPath}/curator/context-rows.json</c>, written atomically via temp file
    /// and rename, following <c>SummaryStore</c>.
    /// </summary>
    /// <remarks>
    /// Read on the path that draws the home screen, so the whole document is held in
    /// memory after the first read and only re-read when the file changes underneath
    /// it. It is small — a few dozen title sets and two rows per viewer — but
    /// "small" is not a reason to hit the disk inside a page render.
    /// </remarks>
    public class ContextRowStore : IContextRowStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
        };

        private readonly string _path;
        private readonly ILogger<ContextRowStore> _logger;
        private readonly object _lock = new();

        private Document? _cached;
        private DateTime _cachedWriteTimeUtc;

        public ContextRowStore(IServerApplicationPaths applicationPaths, ILogger<ContextRowStore> logger)
            : this(Path.Combine(applicationPaths.DataPath, "curator", "context-rows.json"), logger)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ContextRowStore"/> class at an
        /// explicit path. Used directly by tests.
        /// </summary>
        /// <param name="path">The document.</param>
        /// <param name="logger">The logger.</param>
        public ContextRowStore(string path, ILogger<ContextRowStore> logger)
        {
            _path = path;
            _logger = logger;
        }

        /// <summary>The document as it sits on disk.</summary>
        private sealed class Document
        {
            public List<ContextTitleSet> Titles { get; set; } = [];

            public List<ContextRowSnapshot> Rows { get; set; } = [];
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, ContextTitleSet> GetTitles()
        {
            lock (_lock)
            {
                var map = new Dictionary<string, ContextTitleSet>(StringComparer.Ordinal);
                foreach (var set in ReadUnlocked().Titles)
                {
                    if (set is not null && !string.IsNullOrWhiteSpace(set.Condition))
                    {
                        map[set.Condition] = set;
                    }
                }

                return map;
            }
        }

        /// <inheritdoc />
        public void SaveTitles(IReadOnlyCollection<ContextTitleSet> titles)
        {
            ArgumentNullException.ThrowIfNull(titles);

            lock (_lock)
            {
                var document = ReadUnlocked();
                document.Titles = [.. titles.Where(t => t is not null)];
                WriteUnlocked(document);
            }
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, ContextRowSnapshot> GetSnapshots()
        {
            lock (_lock)
            {
                var map = new Dictionary<string, ContextRowSnapshot>(StringComparer.Ordinal);
                foreach (var row in ReadUnlocked().Rows)
                {
                    if (row is not null && !string.IsNullOrWhiteSpace(row.SectionId))
                    {
                        map[row.SectionId] = row;
                    }
                }

                return map;
            }
        }

        /// <inheritdoc />
        public void SaveSnapshots(IReadOnlyCollection<ContextRowSnapshot> snapshots)
        {
            ArgumentNullException.ThrowIfNull(snapshots);

            lock (_lock)
            {
                var document = ReadUnlocked();
                document.Rows = [.. snapshots.Where(s => s is not null)];
                WriteUnlocked(document);
            }
        }

        private Document ReadUnlocked()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    _cached = new Document();
                    _cachedWriteTimeUtc = DateTime.MinValue;
                    return _cached;
                }

                // The write time is the cache key. A row render must not deserialize
                // this on every home screen load, and must still see a refresh the
                // scheduled task wrote a second ago.
                var writeTime = File.GetLastWriteTimeUtc(_path);
                if (_cached is not null && writeTime == _cachedWriteTimeUtc)
                {
                    return _cached;
                }

                _cached = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path)) ?? new Document();
                _cachedWriteTimeUtc = writeTime;
                return _cached;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // An unreadable store means titles fall back to the configured names
                // and rows fall back to live conditions. Both are working states.
                _logger.LogWarning(ex, "Curator: could not read the context row store; treating it as empty");
                _cached = new Document();
                _cachedWriteTimeUtc = DateTime.MinValue;
                return _cached;
            }
        }

        private void WriteUnlocked(Document document)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Ordered so the file stays reviewable by hand and a diff between two
                // refreshes shows what changed rather than a reshuffle.
                document.Titles = [.. document.Titles.OrderBy(t => t.Condition, StringComparer.Ordinal)];
                document.Rows = [.. document.Rows.OrderBy(r => r.SectionId, StringComparer.Ordinal)];

                var tempPath = _path + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(document, SerializerOptions));
                File.Move(tempPath, _path, overwrite: true);

                _cached = document;
                _cachedWriteTimeUtc = File.GetLastWriteTimeUtc(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing this costs money on the next refresh — the titles would be
                // re-bought — but it does not cost correctness, so it must never take
                // the refresh down with it.
                _logger.LogError(ex, "Curator: could not write the context row store to {Path}", _path);
            }
        }
    }
}
