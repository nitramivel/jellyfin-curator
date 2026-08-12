using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services.Summaries
{
    /// <summary>
    /// Reads item themes out of the Concierge search plugin's enrichment cache.
    ///
    /// <para>
    /// A read-only, best-effort peek at a file Curator does not own, and every
    /// decision here follows from that. It parses with <see cref="JsonDocument"/>
    /// rather than deserializing into a mirrored type, so it depends on two field
    /// names — <c>ItemId</c> and <c>Enrichment.Themes</c> — instead of on the whole
    /// shape of somebody else's record. It never writes, never locks, and treats
    /// every failure as "no themes today".
    /// </para>
    ///
    /// <para>
    /// Hard rule 21 forbids taking a dependency on another plugin without asking,
    /// and this deliberately is not one: nothing here references a Concierge type,
    /// nothing fails if it is absent, uninstalled mid-run, or rewritten in a shape
    /// this does not recognise. The worst case is the pass costing what it cost
    /// before.
    /// </para>
    /// </summary>
    public class ConciergeThemeSource : IExternalThemeSource
    {
        /// <summary>The other plugin's data directory, beside Curator's own.</summary>
        public const string ConciergeDirectory = "concierge";

        /// <summary>Its enrichment cache.</summary>
        public const string EnrichmentFile = "enrichment.json";

        /// <summary>
        /// The most themes taken from any one item.
        /// </summary>
        /// <remarks>
        /// Concierge stores eight to twelve. All of them would crowd out the
        /// overview in the prompt — the thing the pass is actually there to
        /// compress — for diminishing returns, since the tonal phrases it writes
        /// tend to come first.
        /// </remarks>
        public const int MaxThemesPerItem = 8;

        private readonly string _path;
        private readonly ILogger<ConciergeThemeSource> _logger;
        private readonly object _lock = new();

        private IReadOnlyDictionary<Guid, IReadOnlyList<string>>? _cached;
        private DateTime _cachedWriteTimeUtc;

        public ConciergeThemeSource(IServerApplicationPaths applicationPaths, ILogger<ConciergeThemeSource> logger)
            : this(
                Path.Combine(applicationPaths.DataPath, ConciergeDirectory, EnrichmentFile),
                logger)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConciergeThemeSource"/> class
        /// at an explicit path. Used directly by tests.
        /// </summary>
        /// <param name="path">The enrichment file.</param>
        /// <param name="logger">The logger.</param>
        public ConciergeThemeSource(string path, ILogger<ConciergeThemeSource> logger)
        {
            _path = path;
            _logger = logger;
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<Guid, IReadOnlyList<string>> GetThemes()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_path))
                    {
                        return Empty();
                    }

                    // Keyed on write time: the file runs to several megabytes and a
                    // distillation pass asks for it once, but a re-index next door
                    // must not go unnoticed either.
                    var writeTime = File.GetLastWriteTimeUtc(_path);
                    if (_cached is not null && writeTime == _cachedWriteTimeUtc)
                    {
                        return _cached;
                    }

                    _cached = Read();
                    _cachedWriteTimeUtc = writeTime;

                    _logger.LogInformation(
                        "Curator: read tone descriptions for {Count} item(s) from the Concierge index; "
                        + "they ride along with the overview when an item is condensed",
                        _cached.Count);

                    return _cached;
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
                {
                    // Not a warning. Curator has no claim on this file, and an install
                    // without the other plugin is the normal case rather than a fault.
                    _logger.LogDebug(ex, "Curator: no usable Concierge themes at {Path}", _path);
                    return Empty();
                }
            }
        }

        private Dictionary<Guid, IReadOnlyList<string>> Read()
        {
            using var stream = File.OpenRead(_path);
            using var document = JsonDocument.Parse(stream);

            var root = document.RootElement;

            // The file has been an array at the top level; tolerate it being wrapped
            // in an object later, since guessing wrong should cost nothing.
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        root = property.Value;
                        break;
                    }
                }
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var themes = new Dictionary<Guid, IReadOnlyList<string>>();

            foreach (var entry in root.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("ItemId", out var idElement)
                    || idElement.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(idElement.GetString(), out var itemId)
                    || !entry.TryGetProperty("Enrichment", out var enrichment)
                    || enrichment.ValueKind != JsonValueKind.Object
                    || !enrichment.TryGetProperty("Themes", out var list)
                    || list.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var kept = new List<string>();
                foreach (var value in list.EnumerateArray())
                {
                    if (kept.Count >= MaxThemesPerItem)
                    {
                        break;
                    }

                    if (value.ValueKind == JsonValueKind.String)
                    {
                        var text = value.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(text))
                        {
                            kept.Add(text);
                        }
                    }
                }

                if (kept.Count > 0)
                {
                    themes[itemId] = kept;
                }
            }

            return themes;
        }

        private Dictionary<Guid, IReadOnlyList<string>> Empty()
        {
            var empty = new Dictionary<Guid, IReadOnlyList<string>>();
            _cached = empty;
            _cachedWriteTimeUtc = DateTime.MinValue;
            return empty;
        }
    }
}
