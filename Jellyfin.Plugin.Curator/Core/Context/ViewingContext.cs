using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Curator.Core.Context
{
    /// <summary>
    /// Which part of the day it is where the viewer is.
    /// </summary>
    /// <remarks>
    /// Four buckets rather than an hour, because the judgement being asked of the
    /// model is "does this suit a weeknight after work" and not "does this suit
    /// 19:40". Four is also as many as a model can hold apart: asked to place a film
    /// on a six-way split it produces answers that do not survive being asked twice.
    /// </remarks>
    public enum Daypart
    {
        /// <summary>05:00 to 11:59.</summary>
        Morning = 0,

        /// <summary>12:00 to 16:59.</summary>
        Afternoon = 1,

        /// <summary>17:00 to 21:59.</summary>
        Evening = 2,

        /// <summary>22:00 to 04:59.</summary>
        LateNight = 3,
    }

    /// <summary>
    /// What a model may say about when an item suits watching.
    ///
    /// <para>
    /// A closed vocabulary, and closed for the same reason <c>AllowInventedTags</c>
    /// is off by default: a context word is worth something only if the same word
    /// means the same thing across every item. Left open, one film comes back
    /// "drizzly", the next "overcast", the next "grey", and the row that is supposed
    /// to answer "it is raining" matches none of them. Anything the model returns
    /// that is not in here is discarded rather than mapped onto a neighbour —
    /// guessing what it meant is how a vocabulary quietly reopens.
    /// </para>
    /// </summary>
    public static class ContextVocabulary
    {
        /// <summary>Bright and sunny.</summary>
        public const string Clear = "clear";

        /// <summary>Grey and overcast.</summary>
        public const string Cloudy = "cloudy";

        /// <summary>Rain of any strength, including drizzle and showers.</summary>
        public const string Rain = "rain";

        /// <summary>Thunder and wild weather.</summary>
        public const string Storm = "storm";

        /// <summary>Snow of any strength.</summary>
        public const string Snow = "snow";

        /// <summary>Fog and mist.</summary>
        public const string Fog = "fog";

        /// <summary>Hot enough to be the thing you notice about the day.</summary>
        public const string Hot = "hot";

        /// <summary>Cold enough to be the thing you notice about the day.</summary>
        public const string Cold = "cold";

        /// <summary>
        /// Every weather word the model may use, in the order the prompt lists them.
        /// </summary>
        public static readonly IReadOnlyList<string> Weather =
        [
            Clear, Cloudy, Rain, Storm, Snow, Fog, Hot, Cold,
        ];

        /// <summary>
        /// Every daypart word the model may use, in the order the prompt lists them.
        /// </summary>
        public static readonly IReadOnlyList<string> Dayparts =
        [
            "morning", "afternoon", "evening", "latenight",
        ];

        private static readonly HashSet<string> WeatherSet =
            new(Weather, StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> DaypartSet =
            new(Dayparts, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether a word is a weather word this vocabulary knows.
        /// </summary>
        /// <param name="value">The candidate word.</param>
        /// <returns>Whether it is in the vocabulary.</returns>
        public static bool IsWeather(string? value)
            => !string.IsNullOrWhiteSpace(value) && WeatherSet.Contains(value.Trim());

        /// <summary>
        /// Whether a word is a daypart word this vocabulary knows.
        /// </summary>
        /// <param name="value">The candidate word.</param>
        /// <returns>Whether it is in the vocabulary.</returns>
        public static bool IsDaypart(string? value)
            => !string.IsNullOrWhiteSpace(value) && DaypartSet.Contains(value.Trim());

        /// <summary>
        /// Conditions close enough to stand in when the exact one has too few items.
        /// </summary>
        /// <remarks>
        /// A rescue, not a widening. The rarer a condition is, the fewer items the
        /// model will have judged as suiting it — and the rarest conditions are
        /// exactly the ones a viewer most wants a row for. A thunderstorm is the
        /// clearest case: <c>storm</c> is a word few films earn, so a strict row
        /// would be empty precisely when the weather is at its most dramatic, and
        /// the feature would appear broken on the one evening it should shine.
        /// <para>
        /// These are only ever consulted when the exact matches cannot fill a row,
        /// and they always rank below every exact match — see
        /// <c>ContextRanker</c>. Rain standing in for a thunderstorm is defensible;
        /// rain <em>outranking</em> thunder during a thunderstorm is not.
        /// </para>
        /// </remarks>
        /// <param name="word">A weather word.</param>
        /// <returns>Words that may stand in for it, nearest first.</returns>
        public static IReadOnlyList<string> RelatedTo(string? word) => word?.Trim().ToLowerInvariant() switch
        {
            // Thunder is rain at its most extreme, under the darkest sky.
            Storm => [Rain, Cloudy],

            // Snow is the cold you can see.
            Snow => [Cold, Cloudy],

            // Fog and rain both mean a sky that has closed in.
            Fog => [Cloudy],
            Rain => [Cloudy],

            // Heat belongs with glare.
            Hot => [Clear],
            Cold => [Cloudy],

            // Clear and cloudy are the two commonest skies there are. If neither has
            // enough items, nothing else will either, and reaching further would
            // stop the row meaning anything at all.
            _ => [],
        };

        /// <summary>
        /// The hours either side of one on the clock.
        /// </summary>
        /// <remarks>
        /// Used only to top up a row that would otherwise be thin, and scored below
        /// every exact match. The four buckets are a convenience, not a cliff: at
        /// 16:55 the afternoon becomes the evening, and nothing about what someone
        /// wants to watch changes across that minute. Late night wraps to the morning
        /// for the same reason it wraps in <see cref="DaypartFor"/> — four in the
        /// morning is nearer dawn than it is to the previous evening.
        /// <para>
        /// This is deliberately a ring rather than a general loosening. Morning and
        /// evening stay unrelated, because a film that suits one genuinely does not
        /// suit the other, and a row that reached that far would stop meaning
        /// anything.
        /// </para>
        /// </remarks>
        /// <param name="daypart">The current daypart.</param>
        /// <returns>The neighbouring dayparts.</returns>
        public static IReadOnlyList<string> AdjacentTo(Daypart daypart) => daypart switch
        {
            Daypart.Morning => [WordFor(Daypart.Afternoon), WordFor(Daypart.LateNight)],
            Daypart.Afternoon => [WordFor(Daypart.Evening), WordFor(Daypart.Morning)],
            Daypart.Evening => [WordFor(Daypart.LateNight), WordFor(Daypart.Afternoon)],
            _ => [WordFor(Daypart.Evening), WordFor(Daypart.Morning)],
        };

        /// <summary>
        /// The vocabulary word for a daypart.
        /// </summary>
        /// <param name="daypart">The daypart.</param>
        /// <returns>Its word.</returns>
        public static string WordFor(Daypart daypart) => Dayparts[(int)daypart];

        /// <summary>
        /// Which daypart a local time falls in.
        /// </summary>
        /// <remarks>
        /// Late night deliberately wraps midnight: 01:00 belongs with 23:00 and not
        /// with the morning, because the question is what the viewer is up for rather
        /// than which calendar day it is.
        /// </remarks>
        /// <param name="localTime">The viewer's local time of day.</param>
        /// <returns>The daypart.</returns>
        public static Daypart DaypartFor(TimeSpan localTime)
        {
            var hour = (int)localTime.TotalHours;

            return hour switch
            {
                >= 5 and < 12 => Daypart.Morning,
                >= 12 and < 17 => Daypart.Afternoon,
                >= 17 and < 22 => Daypart.Evening,
                _ => Daypart.LateNight,
            };
        }
    }

    /// <summary>
    /// One item's context affinities, as the model judged them.
    /// </summary>
    /// <param name="Weather">Weather words from <see cref="ContextVocabulary"/>; may be empty.</param>
    /// <param name="Dayparts">Daypart words from <see cref="ContextVocabulary"/>; may be empty.</param>
    public sealed record ItemContextAffinity(
        IReadOnlyList<string> Weather,
        IReadOnlyList<string> Dayparts)
    {
        /// <summary>An item the model judged suits nothing in particular.</summary>
        public static readonly ItemContextAffinity None = new([], []);
    }

    /// <summary>
    /// One observation of the weather somewhere.
    /// </summary>
    /// <param name="Words">
    /// The reading reduced to <see cref="ContextVocabulary"/>'s words. Empty means
    /// there is no usable reading — which is a normal state, not an error.
    /// </param>
    /// <param name="TemperatureCelsius">The temperature, for display; null when unknown.</param>
    /// <param name="Place">The resolved place name, for display; empty when unknown.</param>
    /// <param name="ObservedAtUtc">When this was fetched.</param>
    /// <param name="UtcOffset">
    /// The place's offset from UTC, so the daypart is the viewer's evening rather
    /// than the server's. Null falls back to the server's own clock — which is
    /// right for the common case of a household watching where its server lives,
    /// and wrong only for a per-user location in another timezone, which is exactly
    /// the case this field exists to get right.
    /// </param>
    public sealed record WeatherReading(
        IReadOnlyList<string> Words,
        double? TemperatureCelsius,
        string Place,
        DateTime ObservedAtUtc,
        TimeSpan? UtcOffset = null)
    {
        /// <summary>No reading: nothing configured, nothing fetched, or a failed fetch.</summary>
        public static readonly WeatherReading None = new([], null, string.Empty, DateTime.MinValue);

        /// <summary>Gets a value indicating whether this reading says anything at all.</summary>
        public bool IsUsable => Words.Count > 0;

        /// <summary>
        /// The local time of day where this reading was taken.
        /// </summary>
        /// <param name="utcNow">The current UTC time.</param>
        /// <returns>The local time of day, or null when the offset is unknown.</returns>
        public TimeSpan? LocalTimeOfDay(DateTime utcNow)
            => UtcOffset is { } offset ? (utcNow + offset).TimeOfDay : null;
    }

    /// <summary>
    /// The conditions a row is being drawn for right now.
    /// </summary>
    /// <param name="Weather">
    /// The weather words describing the sky and the temperature outside, or empty
    /// when there is no reading. Empty is a real state and not an error — a server
    /// with no configured location, or one that has not reached the internet since
    /// it started, still has to draw a home screen.
    /// </param>
    /// <param name="Daypart">The part of the day it is, which is always known.</param>
    public sealed record ViewingContext(IReadOnlyList<string> Weather, Daypart Daypart)
    {
        /// <summary>
        /// Gets a value indicating whether there is a usable weather reading.
        /// </summary>
        public bool HasWeather => Weather.Count > 0;

        /// <summary>
        /// A context with the clock but no weather.
        /// </summary>
        /// <param name="daypart">The daypart.</param>
        /// <returns>The context.</returns>
        public static ViewingContext ClockOnly(Daypart daypart) => new([], daypart);

        /// <summary>
        /// A short description, for a log line explaining why a row looks the way it does.
        /// </summary>
        /// <returns>Something like <c>rain, cold at evening</c>.</returns>
        public string Describe()
            => (HasWeather ? string.Join(", ", Weather) : "no weather reading")
                + " at "
                + ContextVocabulary.WordFor(Daypart);
    }
}
