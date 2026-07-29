using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Configuration;
using Jellyfin.Plugin.Curator.Core.HomeScreen;
using Jellyfin.Plugin.Curator.Core.Models;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services.HomeScreen
{
    /// <summary>
    /// Default <see cref="IHomeScreenIntegrationService"/>.
    ///
    /// Sections go through Collection Sections' own configuration rather than
    /// registering with Home Screen Sections directly: its RegisterSection call
    /// is in-memory only and would vanish on restart, whereas saving Collection
    /// Sections' config persists the sections and makes it re-register them
    /// itself on every startup and on each configuration change.
    ///
    /// Both writes are plain HTTP calls against this server's own API, because
    /// the target plugins expose no in-process interface we can reference
    /// without taking a hard dependency on them being installed.
    /// </summary>
    public class HomeScreenIntegrationService : IHomeScreenIntegrationService
    {
        /// <summary>Collection Sections' plugin GUID.</summary>
        public const string CollectionSectionsPluginId = "043b2c48-b3e0-4610-b398-8217b146d1a4";

        /// <summary>The named HttpClient used for loopback calls to this server.</summary>
        public const string HttpClientName = "CuratorLoopback";

        private readonly IServerApplicationHost _applicationHost;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IApiKeyProvider _apiKeyProvider;
        private readonly ILogger<HomeScreenIntegrationService> _logger;

        public HomeScreenIntegrationService(
            IServerApplicationHost applicationHost,
            IHttpClientFactory httpClientFactory,
            IApiKeyProvider apiKeyProvider,
            ILogger<HomeScreenIntegrationService> logger)
        {
            _applicationHost = applicationHost;
            _httpClientFactory = httpClientFactory;
            _apiKeyProvider = apiKeyProvider;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<bool> SyncSectionsAsync(
            IReadOnlyList<CategoryDefinition> categories,
            IReadOnlyList<Guid> targetUserIds,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(categories);
            ArgumentNullException.ThrowIfNull(targetUserIds);

            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                _logger.LogWarning("Curator: plugin configuration unavailable; skipping home screen integration");
                return false;
            }

            if (!IsPluginLoaded("HomeScreenSections"))
            {
                _logger.LogWarning(
                    "Curator: the Home Screen Sections plugin is not installed, so categories cannot appear as home screen rows. "
                    + "Playlists were still created and are available under Playlists. Install Home Screen Sections and Collection Sections to enable rows.");
                return false;
            }

            if (!IsPluginLoaded("CollectionSections"))
            {
                _logger.LogWarning(
                    "Curator: the Collection Sections plugin is not installed, so categories cannot appear as home screen rows. "
                    + "Playlists were still created and are available under Playlists. Install Collection Sections to enable rows.");
                return false;
            }

            // Only categories that actually have a playlist somewhere can render a row.
            var desired = categories
                .Where(category => category.UserPlaylists.Exists(link => link.PlaylistId is not null))
                .Select(category => new DesiredSection(SectionConfigMerger.SectionIdFor(category.Id), category.Name))
                .ToList();

            try
            {
                using var client = await CreateClientAsync().ConfigureAwait(false);

                if (!await WriteCollectionSectionsConfigAsync(client, desired, config, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                if (config.AutoEnableSections && desired.Count > 0)
                {
                    await EnableSectionsForUsersAsync(client, desired, targetUserIds, cancellationToken).ConfigureAwait(false);
                }
                else if (!config.AutoEnableSections)
                {
                    _logger.LogInformation(
                        "Curator: auto-enable is off; {Count} section(s) are registered and available to switch on in each user's home screen settings",
                        desired.Count);
                }

                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                _logger.LogWarning(
                    ex,
                    "Curator: home screen integration failed; playlists were still created. Categories can be added as rows manually in Collection Sections.");
                return false;
            }
        }

        /// <summary>
        /// Reads Collection Sections' configuration, merges Curator's sections
        /// into it, and posts it back. Saving fires its ConfigurationChanged
        /// handler, which re-registers every section with Home Screen Sections.
        /// </summary>
        private async Task<bool> WriteCollectionSectionsConfigAsync(
            HttpClient client,
            IReadOnlyList<DesiredSection> desired,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var path = $"/Plugins/{CollectionSectionsPluginId}/Configuration";

            using var getResponse = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (!getResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Curator: could not read Collection Sections configuration ({Status}); skipping home screen integration",
                    (int)getResponse.StatusCode);
                return false;
            }

            var body = await getResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var configJson = JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body) ?? new JsonObject();

            if (!SectionConfigMerger.MergeSections(configJson, desired, config.OutputType == OutputKind.Playlist))
            {
                _logger.LogInformation("Curator: Collection Sections already lists the current {Count} section(s); nothing to update", desired.Count);
                return true;
            }

            using var content = new StringContent(configJson.ToJsonString(), Encoding.UTF8, "application/json");
            using var postResponse = await client.PostAsync(path, content, cancellationToken).ConfigureAwait(false);
            if (!postResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Curator: could not save Collection Sections configuration ({Status}); home screen rows may be out of date",
                    (int)postResponse.StatusCode);
                return false;
            }

            _logger.LogInformation("Curator: registered {Count} home screen section(s) through Collection Sections", desired.Count);
            return true;
        }

        /// <summary>
        /// Enables Curator's sections for each target user. A section must be
        /// registered before it can be enabled, which the config write above
        /// has just done.
        /// </summary>
        private async Task EnableSectionsForUsersAsync(
            HttpClient client,
            IReadOnlyList<DesiredSection> desired,
            IReadOnlyList<Guid> targetUserIds,
            CancellationToken cancellationToken)
        {
            var sectionIds = desired.Select(d => d.SectionId).ToList();

            foreach (var userId in targetUserIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var getPath = "/ModularHomeViews/UserSettings?userId="
                    + userId.ToString("D", CultureInfo.InvariantCulture);

                using var getResponse = await client.GetAsync(getPath, cancellationToken).ConfigureAwait(false);
                if (!getResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Curator: could not read home screen settings for user {UserId} ({Status}); sections left for them to enable manually",
                        userId,
                        (int)getResponse.StatusCode);
                    continue;
                }

                var body = await getResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var settings = JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body) ?? new JsonObject();

                // The GET falls back to defaults without a UserId when a user has
                // no saved settings; the POST routes on it, so make sure it is set.
                settings["UserId"] = userId.ToString("D", CultureInfo.InvariantCulture);

                if (!SectionConfigMerger.MergeEnabledSections(settings, sectionIds))
                {
                    continue;
                }

                using var content = new StringContent(settings.ToJsonString(), Encoding.UTF8, "application/json");
                using var postResponse = await client
                    .PostAsync("/ModularHomeViews/UserSettings", content, cancellationToken)
                    .ConfigureAwait(false);

                if (postResponse.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Curator: enabled {Count} section(s) on the home screen for user {UserId}", sectionIds.Count, userId);
                }
                else
                {
                    _logger.LogWarning(
                        "Curator: could not save home screen settings for user {UserId} ({Status}); sections left for them to enable manually",
                        userId,
                        (int)postResponse.StatusCode);
                }
            }
        }

        /// <summary>
        /// Detects a loaded plugin by assembly name, so a missing prerequisite is
        /// reported clearly instead of surfacing as an HTTP 404.
        /// </summary>
        private static bool IsPluginLoaded(string assemblyNameFragment)
        {
            return AssemblyLoadContext.All
                .SelectMany(context => context.Assemblies)
                .Any(assembly => assembly.FullName?.Contains(assemblyNameFragment, StringComparison.OrdinalIgnoreCase) == true);
        }

        private async Task<HttpClient> CreateClientAsync()
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            client.BaseAddress = new Uri(_applicationHost.GetApiUrlForLocalAccess(allowHttps: false));
            client.Timeout = TimeSpan.FromSeconds(60);

            var token = await _apiKeyProvider.GetTokenAsync().ConfigureAwait(false);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "MediaBrowser",
                $"Token=\"{token}\", Client=\"Curator\", Device=\"Server\", DeviceId=\"curator-plugin\", Version=\"1.0.0\"");

            return client;
        }
    }
}
