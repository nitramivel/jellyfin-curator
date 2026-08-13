using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Configuration;
using Jellyfin.Plugin.Curator.Core.Footer;
using Jellyfin.Plugin.Curator.Services.HomeScreen;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services.Footer
{
    /// <summary>What applying the footer did.</summary>
    /// <param name="Applied">Whether the footer is now published.</param>
    /// <param name="Removed">Whether Curator's fragment was taken out instead.</param>
    /// <param name="Available">Whether the File Transformation plugin is installed.</param>
    /// <param name="Message">A sentence for the config page, always populated.</param>
    public sealed record FooterApplyResult(bool Applied, bool Removed, bool Available, string Message);

    /// <summary>
    /// Publishes the footer by writing a transformation into the File Transformation
    /// plugin's configuration.
    ///
    /// <para>
    /// Degrades exactly as the home screen integration does, and for the same
    /// reason: the other plugin is not a project reference, it may be absent, and a
    /// footer is the least important thing this plugin does. Nothing here throws out
    /// into a caller — a missing plugin is reported as unavailable, and every network
    /// failure is a warning and a false.
    /// </para>
    /// </summary>
    public class FooterIntegrationService
    {
        /// <summary>The File Transformation plugin's GUID, read from its meta.json.</summary>
        public const string FileTransformationPluginId = "5e87cc92-571a-4d8d-8d98-d2d4147f9f90";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServerApplicationHost _applicationHost;
        private readonly IApiKeyProvider _apiKeyProvider;
        private readonly ILogger<FooterIntegrationService> _logger;

        public FooterIntegrationService(
            IHttpClientFactory httpClientFactory,
            IServerApplicationHost applicationHost,
            IApiKeyProvider apiKeyProvider,
            ILogger<FooterIntegrationService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _applicationHost = applicationHost;
            _apiKeyProvider = apiKeyProvider;
            _logger = logger;
        }

        /// <summary>
        /// Whether the plugin that does the injecting is installed.
        /// </summary>
        public static bool IsAvailable()
        {
            return AssemblyLoadContext.All
                .SelectMany(context => context.Assemblies)
                .Any(a => a.FullName?.Contains("FileTransformation", StringComparison.OrdinalIgnoreCase) == true);
        }

        /// <summary>
        /// Brings the published footer in line with the configuration.
        /// </summary>
        /// <remarks>
        /// Called whenever the footer settings are saved. It is not on any schedule
        /// and needs no startup task, which is the advantage of writing into a
        /// configuration file rather than registering in memory — hard rule 22's
        /// failure mode does not exist on this path.
        /// </remarks>
        /// <param name="config">Plugin configuration.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What happened, in a form the config page can show.</returns>
        public async Task<FooterApplyResult> ApplyAsync(
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);

            var model = new FooterModel(
                config.FooterHeading ?? string.Empty,
                config.FooterText ?? string.Empty,
                [.. (config.FooterLinks ?? []).Select(l => new FooterLinkModel(l.Label ?? string.Empty, l.Url ?? string.Empty))],
                config.FooterScope == FooterScope.HomeOnly);

            // An enabled footer with nothing in it draws a rule across the bottom of
            // the page and puzzles whoever finds it, so it is treated as off.
            var wanted = config.EnableFooter && FooterMarkup.HasContent(model);
            var fragment = wanted ? FooterMarkup.Build(model) : null;

            if (!IsAvailable())
            {
                // Not an error when the footer is off: nothing was wanted and nothing
                // is there.
                var message = wanted
                    ? "The File Transformation plugin is not installed, so the footer cannot be shown. "
                      + "Install it from the plugin catalogue and save again."
                    : "The footer is off. The File Transformation plugin is not installed, which is only "
                      + "needed to show one.";

                _logger.LogInformation("Curator footer: {Message}", message);
                return new FooterApplyResult(Applied: false, Removed: false, Available: false, message);
            }

            try
            {
                using var client = await CreateClientAsync().ConfigureAwait(false);
                var path = $"/Plugins/{FileTransformationPluginId}/Configuration";

                using var getResponse = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
                if (!getResponse.IsSuccessStatusCode)
                {
                    return Failed(
                        $"Could not read the File Transformation configuration ({(int)getResponse.StatusCode}).");
                }

                var body = await getResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var configJson = JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body) ?? new JsonObject();

                if (!FooterTransformationMerger.Merge(configJson, fragment))
                {
                    var unchanged = wanted
                        ? "The footer is already published and unchanged."
                        : "The footer is off and nothing was published.";
                    _logger.LogInformation("Curator footer: {Message}", unchanged);
                    return new FooterApplyResult(wanted, Removed: false, Available: true, unchanged);
                }

                using var content = new StringContent(configJson.ToJsonString(), Encoding.UTF8, "application/json");
                using var postResponse = await client.PostAsync(path, content, cancellationToken).ConfigureAwait(false);
                if (!postResponse.IsSuccessStatusCode)
                {
                    return Failed(
                        $"Could not save the File Transformation configuration ({(int)postResponse.StatusCode}).");
                }

                var done = wanted
                    ? "Footer published. Reload Jellyfin in your browser to see it — the page is cached until then."
                    : "Footer removed.";

                _logger.LogInformation("Curator footer: {Message}", done);
                return new FooterApplyResult(wanted, Removed: !wanted, Available: true, done);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Curator footer: could not update File Transformation — {Message}", ex.Message);
                return Failed("Could not reach the File Transformation plugin. Check the server log.");
            }
        }

        private FooterApplyResult Failed(string message)
        {
            _logger.LogWarning("Curator footer: {Message}", message);
            return new FooterApplyResult(Applied: false, Removed: false, Available: true, message);
        }

        private async Task<HttpClient> CreateClientAsync()
        {
            var client = _httpClientFactory.CreateClient(HomeScreenIntegrationService.HttpClientName);
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
