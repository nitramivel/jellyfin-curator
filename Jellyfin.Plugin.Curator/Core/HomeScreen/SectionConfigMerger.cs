using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Curator.Core.HomeScreen
{
    /// <summary>
    /// One home screen section Curator wants to exist.
    /// </summary>
    /// <param name="SectionId">The section's UniqueId ("curator-" + category id).</param>
    /// <param name="Name">Category name — both the display text and the name-based join key.</param>
    public sealed record DesiredSection(string SectionId, string Name);

    /// <summary>
    /// Pure JSON merge logic for the two integration writes: Collection Sections'
    /// plugin configuration and Home Screen Sections' per-user settings. Operates
    /// on <see cref="JsonNode"/> so every field we don't understand round-trips
    /// untouched, and only sections carrying Curator's "curator-" UniqueId prefix
    /// are ever added, updated, or removed — user-authored sections are never touched.
    /// </summary>
    public static class SectionConfigMerger
    {
        /// <summary>The UniqueId prefix marking a section as Curator-owned.</summary>
        public const string SectionIdPrefix = "curator-";

        /// <summary>
        /// Builds the section ID for a category.
        /// </summary>
        /// <param name="categoryId">The category's ID.</param>
        /// <returns>The section ID.</returns>
        public static string SectionIdFor(Guid categoryId)
        {
            return SectionIdPrefix + categoryId.ToString("N");
        }

        /// <summary>
        /// Merges the desired Curator sections into a Collection Sections
        /// configuration object: adds missing sections, updates renamed ones,
        /// and removes Curator-owned sections that no longer exist. Foreign
        /// sections and unknown configuration fields are preserved as-is.
        /// </summary>
        /// <param name="config">The configuration JSON from GET /Plugins/{id}/Configuration; mutated in place.</param>
        /// <param name="desired">The sections that should exist.</param>
        /// <param name="asPlaylists">True to type sections as Playlist, false as Collection.</param>
        /// <returns>True when the configuration changed and needs to be written back.</returns>
        public static bool MergeSections(JsonNode config, IReadOnlyList<DesiredSection> desired, bool asPlaylists)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(desired);

            var configObject = config.AsObject();
            var (sectionsKey, sectionsNode) = FindProperty(configObject, "Sections");
            var sections = sectionsNode?.AsArray();
            if (sections is null)
            {
                sections = [];
                configObject[sectionsKey ?? "Sections"] = sections;
            }

            var sectionType = asPlaylists ? "Playlist" : "Collection";
            var desiredById = desired.ToDictionary(d => d.SectionId, StringComparer.Ordinal);
            var changed = false;

            // Update or remove existing Curator-owned entries; leave everything else alone.
            for (var i = sections.Count - 1; i >= 0; i--)
            {
                if (sections[i] is not JsonObject entry)
                {
                    continue;
                }

                var uniqueId = GetString(entry, "UniqueId");
                if (uniqueId is null || !uniqueId.StartsWith(SectionIdPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!desiredById.TryGetValue(uniqueId, out var want))
                {
                    sections.RemoveAt(i);
                    changed = true;
                    continue;
                }

                changed |= SetString(entry, "DisplayText", want.Name);
                changed |= SetString(entry, "CollectionName", want.Name);
                changed |= SetString(entry, "SectionType", sectionType);
                desiredById.Remove(uniqueId);
            }

            // Whatever remains is new.
            foreach (var want in desired.Where(d => desiredById.ContainsKey(d.SectionId)))
            {
                sections.Add(new JsonObject
                {
                    ["UniqueId"] = want.SectionId,
                    ["DisplayText"] = want.Name,
                    ["CollectionName"] = want.Name,
                    ["SectionType"] = sectionType,
                });
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// Merges Curator's section IDs into one user's Modular Home settings:
        /// enables current sections, removes stale Curator entries, preserves
        /// everything else (including sections the user enabled themselves).
        /// </summary>
        /// <param name="userSettings">The settings JSON from GET /ModularHomeViews/UserSettings; mutated in place.</param>
        /// <param name="sectionIds">The Curator section IDs that should be enabled.</param>
        /// <returns>True when the settings changed and need to be written back.</returns>
        public static bool MergeEnabledSections(JsonNode userSettings, IReadOnlyList<string> sectionIds)
        {
            ArgumentNullException.ThrowIfNull(userSettings);
            ArgumentNullException.ThrowIfNull(sectionIds);

            var settingsObject = userSettings.AsObject();
            var (enabledKey, enabledNode) = FindProperty(settingsObject, "EnabledSections");
            var enabled = enabledNode?.AsArray();
            if (enabled is null)
            {
                enabled = [];
                settingsObject[enabledKey ?? "EnabledSections"] = enabled;
            }

            var changed = false;
            var present = new HashSet<string>(
                enabled.OfType<JsonValue>().Select(v => v.GetValue<string>()),
                StringComparer.Ordinal);

            for (var i = enabled.Count - 1; i >= 0; i--)
            {
                if (enabled[i] is JsonValue value
                    && value.TryGetValue<string>(out var id)
                    && id.StartsWith(SectionIdPrefix, StringComparison.Ordinal)
                    && !sectionIds.Contains(id, StringComparer.Ordinal))
                {
                    enabled.RemoveAt(i);
                    changed = true;
                }
            }

            foreach (var id in sectionIds)
            {
                if (!present.Contains(id))
                {
                    enabled.Add(JsonValue.Create(id));
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// Finds a property by name, tolerating both the camelCase the server's
        /// JSON options emit and the PascalCase of the underlying C# type.
        /// </summary>
        private static (string? Key, JsonNode? Node) FindProperty(JsonObject obj, string pascalName)
        {
            foreach (var pair in obj)
            {
                if (string.Equals(pair.Key, pascalName, StringComparison.OrdinalIgnoreCase))
                {
                    return (pair.Key, pair.Value);
                }
            }

            return (null, null);
        }

        private static string? GetString(JsonObject obj, string pascalName)
        {
            var (_, node) = FindProperty(obj, pascalName);
            return node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
        }

        private static bool SetString(JsonObject obj, string pascalName, string value)
        {
            var (key, node) = FindProperty(obj, pascalName);
            var current = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (string.Equals(current, value, StringComparison.Ordinal))
            {
                return false;
            }

            obj[key ?? pascalName] = value;
            return true;
        }
    }
}
