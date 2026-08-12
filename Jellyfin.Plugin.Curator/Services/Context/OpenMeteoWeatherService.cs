using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Core.Context;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services.Context
{
    /// <summary>
    /// Reads the weather from Open-Meteo.
    ///
    /// <para>
    /// Open-Meteo because it needs no API key and no signup. That is not
    /// convenience: a credential is a thing the owner has to obtain before the
    /// feature does anything, store in plugin configuration, and replace when it
    /// expires — and a weather row silently going blank because a key lapsed is
    /// exactly the kind of quiet failure the health check exists to chase. There is
    /// nothing here to lapse.
    /// </para>
    ///
    /// <para>
    /// Two caches with very different lifetimes. A place name resolves to
    /// coordinates <b>once per process</b>, because Pittsburgh does not move;
    /// conditions are re-read on <see cref="RefreshInterval"/>. Nothing here is ever
    /// fetched on the path that draws the home screen — see
    /// <see cref="IWeatherService"/> for why that is a hard constraint rather than a
    /// preference.
    /// </para>
    /// </summary>
    public class OpenMeteoWeatherService : IWeatherService
    {
        /// <summary>The named HttpClient used for outbound weather calls.</summary>
        public const string HttpClientName = "CuratorWeather";

        /// <summary>Where a place name becomes a latitude and longitude.</summary>
        public const string GeocodingUrl = "https://geocoding-api.open-meteo.com/v1/search";

        /// <summary>Where coordinates become current conditions.</summary>
        public const string ForecastUrl = "https://api.open-meteo.com/v1/forecast";

        /// <summary>
        /// How long a reading is used before a refresh is started behind it.
        /// </summary>
        /// <remarks>
        /// Half an hour. Weather does not turn over faster than that in a way a row
        /// of films should chase, and the row is a mood rather than a forecast — the
        /// difference between "raining" and "raining twenty minutes ago" cannot
        /// change which films suit the evening.
        /// </remarks>
        public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

        /// <summary>
        /// How long a reading is still worth drawing a row from when refreshes have
        /// stopped working.
        /// </summary>
        /// <remarks>
        /// Six hours. Past that the server has been unable to reach Open-Meteo for
        /// long enough that the reading is a guess about a different part of the day,
        /// and no row is better than a wrong one. Deliberately far longer than
        /// <see cref="RefreshInterval"/> so a brief outage costs nothing.
        /// </remarks>
        public static readonly TimeSpan MaxReadingAge = TimeSpan.FromHours(6);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<OpenMeteoWeatherService> _logger;

        private readonly ConcurrentDictionary<string, WeatherReading> _readings =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, (double Latitude, double Longitude, string Place)> _places =
            new(StringComparer.OrdinalIgnoreCase);

        // One refresh per place at a time. Six viewers loading the home screen at
        // once must not become six identical requests to a free public API.
        private readonly ConcurrentDictionary<string, byte> _inFlight =
            new(StringComparer.OrdinalIgnoreCase);

        public OpenMeteoWeatherService(
            IHttpClientFactory httpClientFactory,
            ILogger<OpenMeteoWeatherService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <inheritdoc />
        public WeatherReading Current(string? location)
        {
            var place = Normalize(location);
            if (place.Length == 0)
            {
                return WeatherReading.None;
            }

            _readings.TryGetValue(place, out var reading);
            reading ??= WeatherReading.None;

            var age = DateTime.UtcNow - reading.ObservedAtUtc;

            if (!reading.IsUsable || age >= RefreshInterval)
            {
                StartBackgroundRefresh(place);
            }

            // Returned even while a refresh is running: the caller is drawing a page
            // right now and a stale answer beats a slow one. Past MaxReadingAge it
            // stops being an answer at all.
            return reading.IsUsable && age <= MaxReadingAge ? reading : WeatherReading.None;
        }

        /// <inheritdoc />
        public async Task<WeatherReading> RefreshAsync(string? location, CancellationToken cancellationToken)
        {
            var probe = await ProbeAsync(location, cancellationToken).ConfigureAwait(false);

            if (probe.Error is not null)
            {
                // Never fatal, and never louder than a warning. A home screen that
                // cannot say what the weather is has one row fewer; it is not broken.
                _logger.LogWarning(
                    "Curator: could not read the weather for '{Place}' — {Reason}. "
                    + "The weather row is left out until this succeeds.",
                    Normalize(location),
                    probe.Error);
            }

            return probe.Reading;
        }

        /// <inheritdoc />
        public async Task<WeatherProbeResult> ProbeAsync(string? location, CancellationToken cancellationToken)
        {
            var place = Normalize(location);
            if (place.Length == 0)
            {
                return WeatherProbeResult.Failed(
                    "No location is set. Type a place name on the Home screen tab and save.");
            }

            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                client.Timeout = TimeSpan.FromSeconds(15);

                var resolved = await ResolvePlaceAsync(client, place, cancellationToken).ConfigureAwait(false);
                if (resolved.Coordinates is not { } coordinates)
                {
                    return WeatherProbeResult.Failed(resolved.Error ?? "The place could not be resolved.");
                }

                var conditions = await ReadConditionsAsync(client, coordinates, cancellationToken).ConfigureAwait(false);
                if (conditions.Error is not null)
                {
                    return WeatherProbeResult.Failed(conditions.Error);
                }

                _readings[place] = conditions.Reading;
                _logger.LogDebug(
                    "Curator: weather for {Place} is {Words}",
                    conditions.Reading.Place,
                    string.Join(", ", conditions.Reading.Words));

                return WeatherProbeResult.Success(conditions.Reading);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException)
            {
                // Named specifically, because these four look identical from a config
                // page and have four different fixes: no DNS, blocked egress, a proxy
                // returning HTML, and a timeout.
                return WeatherProbeResult.Failed(
                    "Could not reach " + new Uri(ForecastUrl).Host + " — " + ex.Message
                    + ". Check that the server has outbound internet access.");
            }
        }

        /// <summary>
        /// Starts a refresh that nobody waits for.
        /// </summary>
        /// <remarks>
        /// Fire-and-forget with the exceptions already swallowed inside
        /// <see cref="RefreshAsync"/>, and one in flight per place. This exists so
        /// the first home screen load after a restart repairs the cache without
        /// blocking on it — the startup task primes it too, but a server that was
        /// offline at boot would otherwise never try again.
        /// </remarks>
        private void StartBackgroundRefresh(string place)
        {
            if (!_inFlight.TryAdd(place, 0))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await RefreshAsync(place, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // RefreshAsync handles everything it expects. Anything reaching
                    // here is unexpected and must still not reach the thread pool's
                    // unhandled handler, which would take the server down.
                    _logger.LogWarning(ex, "Curator: background weather refresh for '{Place}' failed", place);
                }
                finally
                {
                    _inFlight.TryRemove(place, out _);
                }
            });
        }

        /// <summary>
        /// Turns a place name into coordinates, once per process.
        /// </summary>
        /// <remarks>
        /// Cached without expiry on purpose. Geocoding is the only call here that
        /// takes a free-text string, and the answer for "Pittsburgh" is the same
        /// tomorrow — re-asking would spend a request on a public API to learn
        /// nothing. A renamed location in configuration is a different cache key, so
        /// editing the setting takes effect immediately.
        /// </remarks>
        private async Task<(( double Latitude, double Longitude, string Place)? Coordinates, string? Error)> ResolvePlaceAsync(
            HttpClient client,
            string place,
            CancellationToken cancellationToken)
        {
            if (_places.TryGetValue(place, out var cached))
            {
                return (cached, null);
            }

            var url = GeocodingUrl
                + "?count=1&language=en&format=json&name="
                + Uri.EscapeDataString(place);

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, Describe(
                    "The geocoding service answered",
                    response.StatusCode,
                    response.ReasonPhrase));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
            {
                // A name nothing matches is a typo in configuration, and the owner
                // needs to be told rather than left with a row that never appears.
                return (null,
                    "Reached the geocoding service, but it knows no place called '" + place
                    + "'. Try a plainer form, like 'Pittsburgh' or 'Pittsburgh, Pennsylvania'.");
            }

            var first = results[0];
            if (!TryGetDouble(first, "latitude", out var latitude)
                || !TryGetDouble(first, "longitude", out var longitude))
            {
                return (null, "The geocoding service answered without coordinates.");
            }

            var resolvedName = first.TryGetProperty("name", out var nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString() ?? place
                    : place;

            var coordinates = (latitude, longitude, resolvedName);
            _places[place] = coordinates;


            _logger.LogInformation(
                "Curator: weather location '{Place}' resolved to {Resolved} ({Latitude}, {Longitude})",
                place,
                resolvedName,
                latitude.ToString("F2", CultureInfo.InvariantCulture),
                longitude.ToString("F2", CultureInfo.InvariantCulture));

            return (coordinates, null);
        }

        /// <summary>
        /// Reads current conditions for a set of coordinates.
        /// </summary>
        private async Task<(WeatherReading Reading, string? Error)> ReadConditionsAsync(
            HttpClient client,
            (double Latitude, double Longitude, string Place) location,
            CancellationToken cancellationToken)
        {
            var url = ForecastUrl
                + "?current=temperature_2m,weather_code"
                + "&timezone=auto"
                + "&latitude=" + location.Latitude.ToString("F4", CultureInfo.InvariantCulture)
                + "&longitude=" + location.Longitude.ToString("F4", CultureInfo.InvariantCulture);

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (WeatherReading.None, Describe(
                    "Resolved " + location.Place + ", but the forecast service answered",
                    response.StatusCode,
                    response.ReasonPhrase));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("current", out var current)
                || current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty("weather_code", out var codeElement)
                || !codeElement.TryGetInt32(out var code))
            {
                return (WeatherReading.None,
                    "The forecast service answered without any current conditions.");
            }

            double? celsius = TryGetDouble(current, "temperature_2m", out var temperature) ? temperature : null;

            // timezone=auto makes the response carry the place's own offset, which is
            // what lets the daypart row mean the viewer's evening rather than the
            // server's. Absent, the caller falls back to the server clock.
            TimeSpan? utcOffset = TryGetDouble(document.RootElement, "utc_offset_seconds", out var offsetSeconds)
                ? TimeSpan.FromSeconds(offsetSeconds)
                : null;

            // Open-Meteo reports Celsius unless asked otherwise, and it is asked for
            // the default here so the unit is never in doubt. WeatherCodes is the one
            // place that decides what a code and a temperature mean.
            var words = WeatherCodes.Describe(code, celsius);
            if (words.Count == 0)
            {
                // Reached everything, understood nothing: a WMO code outside the
                // mapping. Worth saying precisely, because it is a gap in Curator
                // rather than a problem with the server or the network.
                return (WeatherReading.None,
                    "Reached the forecast service for " + location.Place + ", but weather code "
                    + code.ToString(CultureInfo.InvariantCulture)
                    + " is one Curator has no word for.");
            }

            return (new WeatherReading(words, celsius, location.Place, DateTime.UtcNow, utcOffset), null);
        }

        /// <summary>
        /// Says what an HTTP failure was, in words an owner can act on.
        /// </summary>
        /// <remarks>
        /// The 403 hint is there because it is the single most common shape of this
        /// failure that is <em>not</em> Curator's fault: a corporate proxy, a DNS
        /// sinkhole or an egress filter answering on the API's behalf, which reads
        /// from the config page exactly like a broken plugin.
        /// </remarks>
        private static string Describe(string what, HttpStatusCode status, string? reason)
        {
            var text = what + " " + ((int)status).ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(reason))
            {
                text += " (" + reason + ")";
            }

            return status is HttpStatusCode.Forbidden or HttpStatusCode.ProxyAuthenticationRequired
                ? text + ". Something between this server and the internet is likely intercepting the request."
                : text + ".";
        }

        private static bool TryGetDouble(JsonElement element, string property, out double value)
        {
            value = 0;
            return element.TryGetProperty(property, out var found)
                && found.ValueKind == JsonValueKind.Number
                && found.TryGetDouble(out value);
        }

        private static string Normalize(string? location)
            => string.IsNullOrWhiteSpace(location) ? string.Empty : location.Trim();
    }
}
