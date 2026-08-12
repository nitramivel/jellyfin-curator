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
    /// <param name="MemberCount">How many items the category holds; picks the card shape.</param>
    /// <param name="OrderIndex">
    /// Which lane the row sits in. Rows sharing a lane are shuffled by Home Screen
    /// Sections on every load, so this is the only way to pin a row's position
    /// relative to Curator's other rows.
    /// </param>
    public sealed record DesiredSection(
        string SectionId,
        string Name,
        int MemberCount = 0,
        int OrderIndex = SectionConfigMerger.OrderIndex);

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

        /// <summary>The prefix marking a section as one of the context rows.</summary>
        public const string ContextSectionIdPrefix = SectionIdPrefix + "context-";

        /// <summary>
        /// Which of Curator's rows a merge is authoritative over.
        /// </summary>
        /// <remarks>
        /// Load-bearing, and the reason is that both merges in this class <em>remove</em>
        /// Curator entries absent from the list they were handed. That is right for a
        /// caller syncing every row and catastrophic for one syncing two: category
        /// rows and context rows are published by different passes on wildly
        /// different cadences — a run, against several times a day — so a
        /// context-only sync claiming the whole <c>curator-</c> prefix would delete
        /// every category row from the section settings and from every viewer's
        /// enabled list, several times a day.
        /// <para>
        /// <see cref="Categories"/> and <see cref="Context"/> are therefore disjoint,
        /// and each pass claims exactly its own.
        /// </para>
        /// </remarks>
        public enum SectionScope
        {
            /// <summary>Every Curator section. For tearing all of them down.</summary>
            All = 0,

            /// <summary>Category rows only — Curator-owned, but not a context row.</summary>
            Categories = 1,

            /// <summary>The weather and time-of-day rows only.</summary>
            Context = 2,
        }

        /// <summary>
        /// Whether a section ID falls inside a scope.
        /// </summary>
        /// <param name="sectionId">The section ID.</param>
        /// <param name="scope">The scope.</param>
        /// <returns>Whether the merge may add, update or remove it.</returns>
        public static bool InScope(string? sectionId, SectionScope scope)
        {
            if (sectionId is null || !sectionId.StartsWith(SectionIdPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var isContext = sectionId.StartsWith(ContextSectionIdPrefix, StringComparison.Ordinal);

            return scope switch
            {
                SectionScope.Categories => !isContext,
                SectionScope.Context => isContext,
                _ => true,
            };
        }

        /// <summary>The section ID of the shared context row.</summary>
        /// <remarks>
        /// Fixed rather than derived from a GUID, because it has no stored definition
        /// to take an ID from. It still carries the Curator prefix, so every merge in
        /// this class treats it as ours — which matters most for the one that
        /// <em>removes</em> entries: a context row left behind in Collection
        /// Sections' configuration would race the registration.
        /// <para>
        /// The 32 hex characters a category ID produces cannot collide with these,
        /// since neither is valid hex.
        /// </para>
        /// </remarks>
        public const string ContextSectionId = SectionIdPrefix + "context-now";

        /// <summary>
        /// The section ID of one viewer's own context row.
        /// </summary>
        /// <remarks>
        /// Per-viewer sections exist because a row's title is a property of the
        /// <em>section</em>, and Home Screen Sections has no per-user display text —
        /// the only per-user structure it keeps is the enabled-sections set. So the
        /// only way two viewers in different weather can read two different titles is
        /// for them to be looking at two different sections. Each is then enabled for
        /// its own viewer and nobody else.
        /// <para>
        /// Only used when viewers have their own locations. With one location for the
        /// server the weather and the hour are the same for everyone, and N copies of
        /// an identical row would be N times the registrations for no difference.
        /// </para>
        /// </remarks>
        /// <param name="userId">The viewer.</param>
        /// <returns>The section ID.</returns>
        public static string ContextSectionIdFor(Guid userId)
            => SectionIdPrefix + "context-now-" + userId.ToString("N");

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
        /// <param name="scope">
        /// Which Curator sections this call is authoritative over. Entries in scope
        /// and absent from <paramref name="sectionIds"/> are removed; everything else
        /// is left alone. See <see cref="SectionScope"/> for why this is not optional
        /// in practice.
        /// </param>
        /// <returns>True when the settings changed and need to be written back.</returns>
        public static bool MergeEnabledSections(
            JsonNode userSettings,
            IReadOnlyList<string> sectionIds,
            SectionScope scope = SectionScope.All)
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
                    && InScope(id, scope)
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
        /// The row order Curator claims for its sections when nothing else is set.
        /// </summary>
        /// <remarks>
        /// Curator has no basis for ranking one category above another on a
        /// stranger's home screen, so by default it takes one lane and leaves the
        /// ordering within it, and around it, to be arranged by hand.
        /// <para>
        /// It is a default rather than a rule because of what Home Screen Sections
        /// does with a group: <c>CacheSectionsForUser</c> <b>shuffles</b> the rows
        /// sharing an order index before returning them, so every Curator row in one
        /// lane appears in a different position on every home screen load. Giving the
        /// context rows an index of their own is what lets them sit somewhere fixed —
        /// they are two rows about right now, and having them wander into the middle
        /// of fifty category rows wastes them.
        /// </para>
        /// </remarks>
        public const int OrderIndex = 500;

        /// <summary>
        /// Default item count at or above which a row renders as portrait posters
        /// rather than landscape thumbs. Overridable per install.
        /// </summary>
        /// <remarks>
        /// Landscape cards are wide, so a short row of six or seven fills the
        /// screen; portrait cards are narrow and fit more across, which suits a
        /// row with enough in it to be worth scrolling. Ten splits this library's
        /// categories — they run from six to seventeen items — near the middle,
        /// but the right number depends on the screen and the taste, so it is a
        /// setting rather than a rule.
        /// </remarks>
        public const int DefaultPortraitThreshold = 10;

        /// <summary>
        /// The fields Home Screen Sections sets for itself when it creates a
        /// section, and which Curator must therefore seed when it creates one on
        /// that plugin's behalf.
        /// </summary>
        /// <remarks>
        /// "Leave every other field alone" is the right rule for an entry that
        /// already exists, and the wrong one for an entry being created: an absent
        /// field is not left alone, it deserializes to the CLR default. Curator wrote
        /// new entries carrying only SectionId, OrderIndex and ViewMode, so every row
        /// it created came back Enabled=false with LowerLimit and UpperLimit at 0 —
        /// a row switched off and asking for no items. Measured on the owner's
        /// server: 40 of 46 Curator rows in that state, against every
        /// non-Curator row sitting at Enabled=true, 1 and 1.
        /// </remarks>
        public const int DefaultLowerLimit = 1;

        /// <summary>Upper item limit a new section is created with.</summary>
        public const int DefaultUpperLimit = 1;

        /// <summary>
        /// Picks the card shape for a category.
        /// </summary>
        /// <param name="memberCount">How many items the category holds.</param>
        /// <param name="portraitThreshold">
        /// Member count at or above which the row goes portrait. 0 or less makes
        /// every row portrait, which is what a threshold of "always" means.
        /// </param>
        /// <returns>"Portrait" or "Landscape".</returns>
        public static string ViewModeFor(int memberCount, int portraitThreshold = DefaultPortraitThreshold)
        {
            return memberCount >= portraitThreshold ? "Portrait" : "Landscape";
        }

        /// <summary>
        /// Merges Curator's sections into Home Screen Sections' own plugin
        /// configuration, which is where row order and card shape live — Collection
        /// Sections has no fields for either, so a section registered through it
        /// lands on whatever default Home Screen Sections assigns.
        /// </summary>
        /// <remarks>
        /// Entries are keyed by <c>SectionId</c> here rather than <c>UniqueId</c>.
        /// Only Curator-owned rows are touched; every other section's settings, and
        /// every field on our own entries that we do not set, round-trip untouched.
        /// </remarks>
        /// <param name="config">The configuration JSON from GET /Plugins/{id}/Configuration; mutated in place.</param>
        /// <param name="desired">The sections that should exist.</param>
        /// <param name="portraitThreshold">Member count at or above which a row goes portrait.</param>
        /// <param name="scope">
        /// Which Curator sections this call is authoritative over; entries in scope
        /// and absent from <paramref name="desired"/> are removed. See
        /// <see cref="SectionScope"/>.
        /// </param>
        /// <returns>True when the configuration changed and needs to be written back.</returns>
        public static bool MergeSectionSettings(
            JsonNode config,
            IReadOnlyList<DesiredSection> desired,
            int portraitThreshold = DefaultPortraitThreshold,
            SectionScope scope = SectionScope.All)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(desired);

            var configObject = config.AsObject();
            var (settingsKey, settingsNode) = FindProperty(configObject, "SectionSettings");
            var settings = settingsNode?.AsArray();
            if (settings is null)
            {
                settings = [];
                configObject[settingsKey ?? "SectionSettings"] = settings;
            }

            var desiredById = desired.ToDictionary(d => d.SectionId, StringComparer.Ordinal);
            var changed = false;

            for (var i = settings.Count - 1; i >= 0; i--)
            {
                if (settings[i] is not JsonObject entry)
                {
                    continue;
                }

                var sectionId = GetString(entry, "SectionId");
                if (sectionId is null || !InScope(sectionId, scope))
                {
                    continue;
                }

                if (!desiredById.TryGetValue(sectionId, out var want))
                {
                    settings.RemoveAt(i);
                    changed = true;
                    continue;
                }

                changed |= SetNumber(entry, "OrderIndex", want.OrderIndex);
                changed |= SetString(entry, "ViewMode", ViewModeFor(want.MemberCount, portraitThreshold));
                changed |= RepairIncompleteEntry(entry);
                desiredById.Remove(sectionId);
            }

            foreach (var want in desired.Where(d => desiredById.ContainsKey(d.SectionId)))
            {
                settings.Add(new JsonObject
                {
                    ["SectionId"] = want.SectionId,
                    ["Enabled"] = true,
                    ["AllowUserOverride"] = true,
                    ["LowerLimit"] = DefaultLowerLimit,
                    ["UpperLimit"] = DefaultUpperLimit,
                    ["OrderIndex"] = want.OrderIndex,
                    ["ViewMode"] = ViewModeFor(want.MemberCount, portraitThreshold),
                    ["HideWatchedItems"] = false,
                });
                changed = true;
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

        /// <summary>
        /// Fixes up a Curator row left incomplete by an older version, without
        /// touching a row the user has deliberately configured.
        /// </summary>
        /// <remarks>
        /// Both limits at zero is Curator's own fingerprint: Home Screen Sections
        /// never writes that pair, and a section asking for between zero and zero
        /// items is not something anyone chose. Only in that case are the fields
        /// seeded — including Enabled, which is equally ours when the limits are
        /// ours. A row with real limits keeps whatever the user set, Enabled
        /// included, because switching a row off by hand is a legitimate thing to do
        /// and must survive the next run.
        /// </remarks>
        /// <param name="entry">The section settings entry to inspect.</param>
        /// <returns>True when the entry was changed.</returns>
        private static bool RepairIncompleteEntry(JsonObject entry)
        {
            if (GetInt(entry, "LowerLimit") is not (null or 0)
                || GetInt(entry, "UpperLimit") is not (null or 0))
            {
                return false;
            }

            var changed = SetNumber(entry, "LowerLimit", DefaultLowerLimit);
            changed |= SetNumber(entry, "UpperLimit", DefaultUpperLimit);
            changed |= SetBool(entry, "Enabled", true);
            changed |= SetBool(entry, "AllowUserOverride", true);
            return changed;
        }

        private static int? GetInt(JsonObject obj, string pascalName)
        {
            var (_, node) = FindProperty(obj, pascalName);
            return node is JsonValue value && value.TryGetValue<int>(out var i) ? i : null;
        }

        private static bool SetBool(JsonObject obj, string pascalName, bool value)
        {
            var (key, node) = FindProperty(obj, pascalName);
            if (node is JsonValue v && v.TryGetValue<bool>(out var current) && current == value)
            {
                return false;
            }

            obj[key ?? pascalName] = value;
            return true;
        }

        private static string? GetString(JsonObject obj, string pascalName)
        {
            var (_, node) = FindProperty(obj, pascalName);
            return node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
        }

        private static bool SetNumber(JsonObject obj, string pascalName, int value)
        {
            var (key, node) = FindProperty(obj, pascalName);
            if (node is JsonValue v && v.TryGetValue<int>(out var current) && current == value)
            {
                return false;
            }

            obj[key ?? pascalName] = value;
            return true;
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
