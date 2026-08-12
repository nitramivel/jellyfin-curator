using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Core.Context;

namespace Jellyfin.Plugin.Curator.Services.Context
{
    /// <summary>
    /// What the weather is doing where a viewer is.
    ///
    /// <para>
    /// The shape of this interface is dictated by where it is called from. Home
    /// Screen Sections asks a section for its contents through a <b>synchronous</b>
    /// method, inside the one request that draws the whole home screen — so the read
    /// cannot be a network call, cannot block, and cannot throw. Hence the split:
    /// <see cref="Current"/> answers instantly from cache and never fails, and
    /// <see cref="RefreshAsync"/> is what actually goes out to the internet, driven
    /// from a task rather than from a render.
    /// </para>
    /// </summary>
    public interface IWeatherService
    {
        /// <summary>
        /// The last reading for a place, without going anywhere to get it.
        /// </summary>
        /// <remarks>
        /// Returns empty rather than null or an exception when there is no usable
        /// reading — no location configured, nothing fetched yet, the last fetch
        /// failed, or the reading has aged out. Every one of those is a normal state
        /// and the caller does the same thing with all of them: draw no weather row.
        /// <para>
        /// May start a background refresh when the cached value is stale, and returns
        /// the stale value immediately regardless. A row drawn from a reading forty
        /// minutes old is right; a home screen that waits on an HTTP round trip is not.
        /// </para>
        /// </remarks>
        /// <param name="location">The place name, as configured.</param>
        /// <returns>The weather words, empty when there is no usable reading.</returns>
        WeatherReading Current(string? location);

        /// <summary>
        /// Fetches and caches the current conditions for a place.
        /// </summary>
        /// <param name="location">The place name, as configured.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The reading, or an empty one when it could not be fetched.</returns>
        Task<WeatherReading> RefreshAsync(string? location, CancellationToken cancellationToken);

        /// <summary>
        /// Fetches conditions and says <em>why</em> when it cannot.
        /// </summary>
        /// <remarks>
        /// For the diagnostic endpoint, and the difference from
        /// <see cref="RefreshAsync"/> is the whole reason it exists. Every ordinary
        /// call here is a background one whose failure must be silent and harmless,
        /// so it swallows the reason and returns an empty reading — which leaves the
        /// owner's Test button able to report "nothing came back" and nothing else,
        /// for a question whose entire subject is <em>what went wrong</em>.
        /// <para>
        /// A wrong place name, no outbound DNS, a blocked egress and a rate limit are
        /// four different problems with four different fixes, and they are
        /// indistinguishable from the config page unless the reason survives the trip.
        /// </para>
        /// </remarks>
        /// <param name="location">The place name to look up.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The reading, and the reason it is empty when it is.</returns>
        Task<WeatherProbeResult> ProbeAsync(string? location, CancellationToken cancellationToken);
    }

    /// <summary>
    /// One diagnostic lookup: what came back, or what stopped it.
    /// </summary>
    /// <param name="Reading">The reading; unusable when the lookup failed.</param>
    /// <param name="Error">Why it failed, in words an owner can act on. Null on success.</param>
    public sealed record WeatherProbeResult(WeatherReading Reading, string? Error)
    {
        /// <summary>A successful lookup.</summary>
        /// <param name="reading">The reading.</param>
        /// <returns>The result.</returns>
        public static WeatherProbeResult Success(WeatherReading reading) => new(reading, null);

        /// <summary>A failed lookup.</summary>
        /// <param name="error">Why.</param>
        /// <returns>The result.</returns>
        public static WeatherProbeResult Failed(string error) => new(WeatherReading.None, error);
    }
}
