using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Curator.Core.Footer
{
    /// <summary>
    /// Writes Curator's footer into the File Transformation plugin's configuration,
    /// and takes it out again.
    ///
    /// <para>
    /// That plugin's <c>Transformations</c> array is a declarative
    /// pattern/search/replace applied to files served from the web client, and it is
    /// stored in that plugin's own configuration — which is the whole reason this
    /// route was chosen over its in-memory <c>RegisterTransformation</c> endpoint.
    /// Hard rule 22 exists because a registration held in a dictionary vanishes on
    /// restart and has to be re-registered by a startup task that can itself fail; a
    /// transformation written into a config file simply survives, and needs no task.
    /// </para>
    ///
    /// <para>
    /// The same two traps as <c>SectionConfigMerger</c> apply, for the same reasons:
    /// the server serializes plugin configuration as <b>camelCase</b> over HTTP while
    /// the C# type is PascalCase, so every property is matched case-insensitively —
    /// a naive implementation silently creates a second <c>Transformations</c> array
    /// the plugin ignores. And this <b>removes</b> Curator's entry when handed a
    /// footer with nothing to draw, because leaving a disabled fragment in another
    /// plugin's configuration is litter in somebody else's house.
    /// </para>
    ///
    /// <para>
    /// Curator's entry is recognised by a fixed ID, never by its contents. Every
    /// other entry in that array belongs to another plugin or to the owner and is
    /// never touched.
    /// </para>
    /// </summary>
    public static class FooterTransformationMerger
    {
        /// <summary>
        /// The stable ID of Curator's entry. Deriving it from the plugin GUID would
        /// be tidier and would also mean an upgrade could not find what an older
        /// version wrote; a literal cannot drift.
        /// </summary>
        public const string TransformationId = "c47a1e05-6b3f-4d21-9f76-0a2c8e5b1d44";

        /// <summary>
        /// The same ID, parsed once.
        /// </summary>
        /// <remarks>
        /// <b>Compared as a GUID, never as a string, and that is a bug fix rather
        /// than a preference.</b> The property is a <c>Guid</c> on the other
        /// plugin's type, so what comes back out of it is whatever format its
        /// serializer chose — measured on the owner's server, the round trip turned
        /// <c>c47a1e05-6b3f-4d21-9f76-0a2c8e5b1d44</c> into
        /// <c>c47a1e056b3f4d219f760a2c8e5b1d44</c>, dashes gone. String equality
        /// then failed to recognise Curator's own entry, so switching the footer off
        /// silently removed nothing — and a second publish would have appended a
        /// duplicate rather than replacing the first, stacking one fragment per save.
        /// </remarks>
        private static readonly Guid TransformationGuid = Guid.Parse(TransformationId);

        /// <summary>The web client file the footer is spliced into.</summary>
        public const string FileNamePattern = "index.html";

        /// <summary>
        /// The anchor the fragment is inserted before. <c>&lt;/body&gt;</c> is the
        /// last thing in the document and exists in every build of the client, so the
        /// footer lands after the app's own markup and cannot displace any of it.
        /// </summary>
        public const string Anchor = "</body>";

        /// <summary>
        /// Brings the configuration in line with the footer that should be drawn.
        /// </summary>
        /// <param name="configJson">The plugin's configuration, as fetched.</param>
        /// <param name="fragment">
        /// The fragment to inject, or null/empty to remove Curator's entry entirely.
        /// </param>
        /// <returns>True when something changed and the config needs writing back.</returns>
        public static bool Merge(JsonNode? configJson, string? fragment)
        {
            if (configJson is not JsonObject configObject)
            {
                return false;
            }

            var (key, node) = FindProperty(configObject, "Transformations");
            var transformations = node?.AsArray();
            if (transformations is null)
            {
                // Nothing to remove from a config that has no array at all.
                if (string.IsNullOrEmpty(fragment))
                {
                    return false;
                }

                transformations = [];
                configObject[key ?? "Transformations"] = transformations;
            }

            var existing = -1;
            for (var i = transformations.Count - 1; i >= 0; i--)
            {
                if (transformations[i] is JsonObject entry && IsCuratorEntry(entry))
                {
                    existing = i;
                    break;
                }
            }

            if (string.IsNullOrEmpty(fragment))
            {
                if (existing < 0)
                {
                    return false;
                }

                transformations.RemoveAt(existing);
                return true;
            }

            // The replacement puts the fragment back in front of the anchor, so the
            // transformation is idempotent: applying it to already-transformed
            // content would find the anchor again and produce the same result rather
            // than stacking a second copy.
            var replacement = string.Create(CultureInfo.InvariantCulture, $"{fragment}{Anchor}");

            if (existing >= 0 && transformations[existing] is JsonObject current)
            {
                if (string.Equals(GetString(current, "ReplaceText"), replacement, StringComparison.Ordinal)
                    && string.Equals(GetString(current, "SearchText"), Anchor, StringComparison.Ordinal)
                    && string.Equals(GetString(current, "FilenamePattern"), FileNamePattern, StringComparison.Ordinal))
                {
                    return false;
                }

                transformations.RemoveAt(existing);
            }

            transformations.Add(new JsonObject
            {
                ["Id"] = TransformationId,
                ["FilenamePattern"] = FileNamePattern,
                ["SearchText"] = Anchor,
                ["ReplaceText"] = replacement,
            });

            return true;
        }

        /// <summary>
        /// Whether this entry is Curator's, whatever format its ID came back in.
        /// </summary>
        private static bool IsCuratorEntry(JsonObject entry)
        {
            return Guid.TryParse(GetString(entry, "Id"), out var id) && id == TransformationGuid;
        }

        /// <summary>
        /// Case-insensitive property lookup, because the same document arrives
        /// PascalCase from the C# type and camelCase over HTTP.
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
    }
}
