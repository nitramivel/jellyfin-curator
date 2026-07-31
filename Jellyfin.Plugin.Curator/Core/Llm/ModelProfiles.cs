using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Curator.Configuration;

namespace Jellyfin.Plugin.Curator.Core.Llm
{
    /// <summary>
    /// Turns whatever is stored in configuration into a usable list of model
    /// profiles and one profile to call.
    ///
    /// <para>
    /// Pure logic, so the awkward part — an install upgrading from the single
    /// API key that came before the list — is pinned by tests rather than
    /// discovered on someone's server. The config page runs the same rules in
    /// JavaScript; if you change one, change both.
    /// </para>
    /// </summary>
    public static class ModelProfiles
    {
        /// <summary>The name given to the profile synthesized from legacy settings.</summary>
        public const string MigratedProfileName = "Default";

        /// <summary>
        /// The result of normalizing configuration: a list that is always safe to
        /// index, and a default id that always names a member of it — unless the
        /// list is empty, which is the one state callers must still handle.
        /// </summary>
        /// <param name="Profiles">The profiles, each with a non-empty unique id.</param>
        /// <param name="DefaultProfileId">The default profile's id, or empty when there are no profiles.</param>
        /// <param name="Changed">Whether normalization altered anything, so a caller can persist the repair.</param>
        public sealed record NormalizedProfiles(
            IReadOnlyList<ModelProfile> Profiles,
            string DefaultProfileId,
            bool Changed);

        /// <summary>
        /// Normalizes the profile list on a configuration: migrates the legacy
        /// single-provider settings when the list is empty, gives every profile an
        /// id, and makes the default id point at something real.
        /// </summary>
        /// <param name="config">The plugin configuration. Not modified.</param>
        /// <returns>The normalized list and default id.</returns>
        public static NormalizedProfiles Normalize(PluginConfiguration config)
        {
            ArgumentNullException.ThrowIfNull(config);

            var profiles = (config.ModelProfiles ?? Array.Empty<ModelProfile>())
                .Where(p => p is not null)
                .ToList();
            var changed = false;

            // An install that predates the list arrives here with an empty list and
            // a working API key in the legacy fields. Fold it into one profile so the
            // owner keeps running without re-entering a credential they already gave
            // us. Only ever done for an empty list: once profiles exist, the legacy
            // fields are a stale snapshot and re-importing them would resurrect a
            // profile the owner deleted on every single run.
            if (profiles.Count == 0)
            {
                var migrated = FromLegacy(config);
                if (migrated is not null)
                {
                    profiles.Add(migrated);
                    changed = true;
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var profile in profiles)
            {
                // A blank id is what a profile added by hand or by an older page
                // looks like; a duplicate is what copying one looks like. Either
                // way the default id and the per-task assignments to come cannot
                // resolve it, so mint a fresh one.
                if (string.IsNullOrWhiteSpace(profile.Id) || !seen.Add(profile.Id))
                {
                    profile.Id = NewId();
                    seen.Add(profile.Id);
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(profile.Name))
                {
                    profile.Name = DescribeProfile(profile);
                    changed = true;
                }
            }

            var defaultId = config.DefaultModelProfileId ?? string.Empty;
            if (profiles.Count == 0)
            {
                if (defaultId.Length > 0)
                {
                    defaultId = string.Empty;
                    changed = true;
                }
            }
            else if (!profiles.Any(p => string.Equals(p.Id, defaultId, StringComparison.Ordinal)))
            {
                // Points at a profile that was deleted, or at nothing at all. Falling
                // back to the first profile keeps runs working; leaving it dangling
                // would fail every run with a configuration error the owner never made.
                defaultId = profiles[0].Id;
                changed = true;
            }

            return new NormalizedProfiles(profiles, defaultId, changed);
        }

        /// <summary>
        /// Resolves the profile a run should call.
        /// </summary>
        /// <param name="config">The plugin configuration.</param>
        /// <returns>The default profile.</returns>
        /// <exception cref="InvalidOperationException">No profile is configured.</exception>
        public static ModelProfile ResolveDefault(PluginConfiguration config)
        {
            var normalized = Normalize(config);
            if (normalized.Profiles.Count == 0)
            {
                throw new InvalidOperationException(
                    "Curator: no model profile configured. Add one on the Model tab of the plugin settings.");
            }

            return normalized.Profiles.First(
                p => string.Equals(p.Id, normalized.DefaultProfileId, StringComparison.Ordinal));
        }

        /// <summary>
        /// Resolves a profile by id, falling back to the default when the id is blank
        /// or names nothing.
        /// </summary>
        /// <remarks>
        /// This is the entry point the planned per-task model assignment uses: a task
        /// carries the id of the profile it wants, and anything unassigned — or
        /// assigned to a profile since deleted — quietly lands on the default rather
        /// than failing the run.
        /// </remarks>
        /// <param name="config">The plugin configuration.</param>
        /// <param name="profileId">The wanted profile id; blank means the default.</param>
        /// <returns>The resolved profile.</returns>
        /// <exception cref="InvalidOperationException">No profile is configured.</exception>
        public static ModelProfile Resolve(PluginConfiguration config, string? profileId)
            => Resolve(Normalize(config), profileId);

        /// <summary>
        /// Resolves a profile out of an already-normalized list.
        /// </summary>
        /// <remarks>
        /// Resolving two passes of one run — discovery and the per-viewer calls —
        /// must go through this overload against a single <see cref="Normalize"/>
        /// result. Normalizing per resolve is not idempotent for an install that
        /// predates the profile list: the migrated profile is synthesized afresh each
        /// time, with a new id, so two resolves of what is really one profile compare
        /// as two. A run would then build a second identical provider and report
        /// itself as running two models when it has only ever had one.
        /// </remarks>
        /// <param name="normalized">The normalized profile list.</param>
        /// <param name="profileId">The wanted profile id; blank means the default.</param>
        /// <returns>The resolved profile.</returns>
        /// <exception cref="InvalidOperationException">No profile is configured.</exception>
        public static ModelProfile Resolve(NormalizedProfiles normalized, string? profileId)
        {
            ArgumentNullException.ThrowIfNull(normalized);

            if (normalized.Profiles.Count == 0)
            {
                throw new InvalidOperationException(
                    "Curator: no model profile configured. Add one on the Model tab of the plugin settings.");
            }

            if (!string.IsNullOrWhiteSpace(profileId))
            {
                var match = normalized.Profiles.FirstOrDefault(
                    p => string.Equals(p.Id, profileId, StringComparison.Ordinal));
                if (match is not null)
                {
                    return match;
                }
            }

            return normalized.Profiles.First(
                p => string.Equals(p.Id, normalized.DefaultProfileId, StringComparison.Ordinal));
        }

        /// <summary>
        /// Builds a profile from the legacy single-provider settings, or null when
        /// they hold nothing worth carrying forward.
        /// </summary>
        private static ModelProfile? FromLegacy(PluginConfiguration config)
        {
            var hasSomething = !string.IsNullOrWhiteSpace(config.Model)
                || !string.IsNullOrWhiteSpace(config.ApiKey)
                || !string.IsNullOrWhiteSpace(config.BaseUrl);

            // A never-configured install has nothing to migrate. Synthesizing an
            // empty profile there would only put a broken entry in front of the
            // owner and claim it was theirs.
            if (!hasSomething)
            {
                return null;
            }

            var profile = new ModelProfile
            {
                Id = NewId(),
                Provider = config.Provider,
                Model = config.Model ?? string.Empty,
                ApiKey = config.ApiKey ?? string.Empty,
                BaseUrl = config.BaseUrl ?? string.Empty,
                InputCostPerMillion = config.InputCostPerMillion,
                CachedInputCostPerMillion = config.CachedInputCostPerMillion,
                OutputCostPerMillion = config.OutputCostPerMillion,
            };

            profile.Name = string.IsNullOrWhiteSpace(config.Model)
                ? MigratedProfileName
                : DescribeProfile(profile);
            return profile;
        }

        /// <summary>
        /// A readable fallback label for a profile the owner has not named.
        /// </summary>
        private static string DescribeProfile(ModelProfile profile)
        {
            return string.IsNullOrWhiteSpace(profile.Model)
                ? profile.Provider.ToString()
                : string.Create(CultureInfo.InvariantCulture, $"{profile.Provider} {profile.Model}");
        }

        private static string NewId() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    }
}
