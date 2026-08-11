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
    /// <para>
    /// There are two ways a category becomes a home screen row and this picks
    /// between them. <b>Integrated</b> registers the row with Home Screen Sections
    /// directly and answers for its contents in
    /// <see cref="CuratorSectionResults"/>. <b>Collection Sections</b> writes the
    /// row into that plugin's configuration and lets it answer instead — the
    /// original path, kept because a home screen that has stopped working is a bad
    /// place to have no second option.
    /// </para>
    ///
    /// <para>
    /// The integrated path exists because the other one loses items. Collection
    /// Sections resolves a row's playlist <i>by name</i>, out of a cache built once
    /// at server startup and never refreshed, so a row shows whatever that playlist
    /// held at the last restart — measured here as a ten-item category rendering
    /// seven cards in the wrong order, while the same playlist was correct in every
    /// other client. Curator cannot fix that from outside: it holds a playlist GUID
    /// per user and there is no field to pass one, and six viewers share one
    /// category name.
    /// </para>
    ///
    /// <para>
    /// What owning the row does <b>not</b> remove is Home Screen Sections itself.
    /// Row order and card shape live in that plugin's own configuration and it
    /// overrides whatever a registration claims, so the section settings write
    /// below runs in both modes. Two plugin dependencies become one, not zero.
    /// </para>
    /// </summary>
    public class HomeScreenIntegrationService : IHomeScreenIntegrationService
    {
        /// <summary>Collection Sections' plugin GUID.</summary>
        public const string CollectionSectionsPluginId = "043b2c48-b3e0-4610-b398-8217b146d1a4";

        /// <summary>Home Screen Sections' plugin GUID.</summary>
        public const string HomeScreenSectionsPluginId = "b8298e01-2697-407a-b44d-aa8dc795e850";

        /// <summary>The named HttpClient used for loopback calls to this server.</summary>
        public const string HttpClientName = "CuratorLoopback";

        private readonly IServerApplicationHost _applicationHost;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IApiKeyProvider _apiKeyProvider;
        private readonly ISectionRegistrar _registrar;
        private readonly ILogger<HomeScreenIntegrationService> _logger;

        public HomeScreenIntegrationService(
            IServerApplicationHost applicationHost,
            IHttpClientFactory httpClientFactory,
            IApiKeyProvider apiKeyProvider,
            ISectionRegistrar registrar,
            ILogger<HomeScreenIntegrationService> logger)
        {
            _applicationHost = applicationHost;
            _httpClientFactory = httpClientFactory;
            _apiKeyProvider = apiKeyProvider;
            _registrar = registrar;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<SectionSyncResult> SyncSectionsAsync(
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
                return SectionSyncResult.Failed;
            }

            if (!IsPluginLoaded("HomeScreenSections"))
            {
                _logger.LogWarning(
                    "Curator: the Home Screen Sections plugin is not installed, so categories cannot appear as home screen rows. "
                    + "Playlists were still created and are available under Playlists. Install Home Screen Sections to enable rows.");
                return SectionSyncResult.Failed;
            }

            // Only categories that actually have a playlist somewhere can render a row.
            var claimed = categories
                .Where(category => category.UserPlaylists.Exists(link => link.PlaylistId is not null))
                .Select(category => new SectionRegistrationRequest(
                    new DesiredSection(
                        SectionConfigMerger.SectionIdFor(category.Id),
                        category.Name,
                        category.Members.Count),
                    category.Id.ToString("N"),
                    typeof(CuratorSectionResults)))
                .ToList();

            // The two context rows, which have no playlist behind them and no
            // category either. They join the registration list and the section
            // settings write, but never the Collection Sections path below — that
            // plugin resolves a row by playlist name, and these have no playlist to
            // name. See CuratorContextSectionResults for why they cannot have one.
            var contextRows = ContextRows(config);
            var registrations = claimed.Concat(contextRows).ToList();

            var desired = claimed.ConvertAll(entry => entry.Section);
            var desiredWithContext = registrations.ConvertAll(entry => entry.Section);

            try
            {
                using var client = await CreateClientAsync().ConfigureAwait(false);

                var integrated = config.SectionDelivery == SectionDelivery.Integrated
                    && _registrar.RegisterSections(registrations) is not null;

                if (integrated)
                {
                    // Both plugins register under the same section IDs, and the
                    // registration table is a dictionary keyed on that ID — so
                    // leaving Curator's rows in Collection Sections' configuration
                    // makes the two race, and whichever registered last owns the
                    // row. Clearing them is what makes the integrated path
                    // actually take effect rather than usually take effect.
                    await ClearCollectionSectionsAsync(client, config, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (config.SectionDelivery == SectionDelivery.Integrated)
                    {
                        _logger.LogWarning(
                            "Curator: could not register home screen rows directly; falling back to Collection Sections for this sync");
                    }

                    if (!IsPluginLoaded("CollectionSections"))
                    {
                        _logger.LogWarning(
                            "Curator: the Collection Sections plugin is not installed, so categories cannot appear as home screen rows. "
                            + "Playlists were still created and are available under Playlists. "
                            + "Install Collection Sections, or set the home screen row source to Curator, to enable rows.");
                        return SectionSyncResult.Failed;
                    }

                    if (!await WriteCollectionSectionsConfigAsync(client, desired, config, cancellationToken).ConfigureAwait(false))
                    {
                        return SectionSyncResult.Failed;
                    }
                }

                // Row order and card shape live in Home Screen Sections' own
                // configuration, whichever plugin answers for the row's contents —
                // it overrides what a registration claims. Done after the
                // registration above, because a section has to exist before its
                // settings mean anything.
                //
                // The context rows are included only when they were actually
                // registered: under the Collection Sections path they do not exist,
                // and writing settings for a section nothing registered leaves an
                // entry pointing at nothing.
                var settingsFor = integrated ? desiredWithContext : desired;
                await WriteSectionSettingsAsync(client, settingsFor, cancellationToken).ConfigureAwait(false);

                if (config.AutoEnableSections && settingsFor.Count > 0)
                {
                    await EnableSectionsForUsersAsync(client, settingsFor, targetUserIds, cancellationToken).ConfigureAwait(false);
                }
                else if (!config.AutoEnableSections)
                {
                    _logger.LogInformation(
                        "Curator: auto-enable is off; {Count} section(s) are registered and available to switch on in each user's home screen settings",
                        desired.Count);
                }

                // Degraded only when the integrated path was wanted and not used.
                // Choosing Collection Sections deliberately is not a degradation,
                // and reporting it as one would have the startup task retry
                // something that is already doing what was asked.
                return new SectionSyncResult(
                    Published: true,
                    Degraded: !integrated && config.SectionDelivery == SectionDelivery.Integrated);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                _logger.LogWarning(
                    ex,
                    "Curator: home screen integration failed; playlists were still created. Categories can be added as rows manually in Collection Sections.");
                return SectionSyncResult.Failed;
            }
        }

        /// <summary>
        /// The weather and time-of-day rows, when they are switched on.
        /// </summary>
        /// <remarks>
        /// Their names are whatever the owner typed and do not track the sky, because
        /// a row's display text is fixed at registration and registration happens at
        /// startup and on sync. A title reading "For a rainy night" would therefore be
        /// a claim about the weather several hours ago — worse than a general one,
        /// since the row's <em>contents</em> are exact and the name should not promise
        /// precision it cannot keep.
        /// <para>
        /// The member count decides card shape through <c>PortraitThreshold</c>, and
        /// the configured row length is the honest answer for it — these rows have no
        /// stored membership to count.
        /// </para>
        /// </remarks>
        private static List<SectionRegistrationRequest> ContextRows(PluginConfiguration config)
        {
            if (!config.ContextRows)
            {
                return [];
            }

            var size = Math.Max(0, config.MaxContextRowItems);

            return
            [
                new SectionRegistrationRequest(
                    new DesiredSection(
                        SectionConfigMerger.WeatherSectionId,
                        Named(config.WeatherRowName, "Picks for the Weather"),
                        size),
                    CuratorContextSectionResults.WeatherRowKey,
                    typeof(CuratorContextSectionResults)),
                new SectionRegistrationRequest(
                    new DesiredSection(
                        SectionConfigMerger.DaypartSectionId,
                        Named(config.DaypartRowName, "Picks for the Hour"),
                        size),
                    CuratorContextSectionResults.DaypartRowKey,
                    typeof(CuratorContextSectionResults)),
            ];
        }

        /// <summary>
        /// A row name that is never blank — an empty display text renders as an
        /// unlabelled row rather than as a hidden one.
        /// </summary>
        private static string Named(string? configured, string fallback)
            => string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();

        /// <summary>
        /// Sets the row order and card shape for Curator's sections in Home Screen
        /// Sections' configuration.
        /// </summary>
        /// <remarks>
        /// Never fatal. These are presentation details: a row in the wrong shape is
        /// still a row, and failing the whole integration over it would throw away
        /// the registration that just succeeded.
        /// </remarks>
        private async Task WriteSectionSettingsAsync(
            HttpClient client,
            IReadOnlyList<DesiredSection> desired,
            CancellationToken cancellationToken)
        {
            var path = $"/Plugins/{HomeScreenSectionsPluginId}/Configuration";

            using var getResponse = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (!getResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Curator: could not read Home Screen Sections configuration ({Status}); rows keep their existing order and shape",
                    (int)getResponse.StatusCode);
                return;
            }

            var body = await getResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var configJson = JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body) ?? new JsonObject();

            var portraitThreshold = Plugin.Instance?.Configuration.PortraitThreshold
                ?? SectionConfigMerger.DefaultPortraitThreshold;

            if (!SectionConfigMerger.MergeSectionSettings(configJson, desired, portraitThreshold))
            {
                return;
            }

            using var content = new StringContent(configJson.ToJsonString(), Encoding.UTF8, "application/json");
            using var postResponse = await client.PostAsync(path, content, cancellationToken).ConfigureAwait(false);
            if (!postResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Curator: could not save Home Screen Sections configuration ({Status}); rows keep their existing order and shape",
                    (int)postResponse.StatusCode);
                return;
            }

            var portrait = desired.Count(d => d.MemberCount >= portraitThreshold);
            _logger.LogInformation(
                "Curator: set {Count} section(s) to order {Order} — {Portrait} portrait, {Landscape} landscape",
                desired.Count,
                SectionConfigMerger.OrderIndex,
                portrait,
                desired.Count - portrait);
        }

        /// <summary>
        /// Removes Curator's sections from Collection Sections' configuration,
        /// leaving every section the owner made there untouched.
        /// </summary>
        /// <remarks>
        /// Never fatal, and a no-op when that plugin is not installed. Failing to
        /// clean up costs a row its ordering fix, because the stale entry may win
        /// the registration race; failing the whole sync over it would cost every
        /// row its existence.
        /// </remarks>
        private async Task ClearCollectionSectionsAsync(
            HttpClient client,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            if (!IsPluginLoaded("CollectionSections"))
            {
                return;
            }

            var path = $"/Plugins/{CollectionSectionsPluginId}/Configuration";

            using var getResponse = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (!getResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Curator: could not read Collection Sections configuration ({Status}); any rows it still holds for Curator may override the integrated ones",
                    (int)getResponse.StatusCode);
                return;
            }

            var body = await getResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var configJson = JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body) ?? new JsonObject();

            // An empty desired list removes every Curator-owned entry and touches
            // nothing else — the merge already knows which entries are ours.
            if (!SectionConfigMerger.MergeSections(configJson, [], config.OutputType == OutputKind.Playlist))
            {
                return;
            }

            using var content = new StringContent(configJson.ToJsonString(), Encoding.UTF8, "application/json");
            using var postResponse = await client.PostAsync(path, content, cancellationToken).ConfigureAwait(false);
            if (postResponse.IsSuccessStatusCode)
            {
                _logger.LogInformation("Curator: removed its own sections from Collection Sections; rows are served directly now");
            }
            else
            {
                _logger.LogWarning(
                    "Curator: could not save Collection Sections configuration ({Status}); the rows it still holds for Curator may override the integrated ones",
                    (int)postResponse.StatusCode);
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

        /// <inheritdoc />
        public (bool CollectionSections, bool HomeScreenSections) GetPrerequisites()
            => (IsPluginLoaded("CollectionSections"), IsPluginLoaded("HomeScreenSections"));

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
