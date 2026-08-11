using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Curator.Core.Context
{
    /// <summary>
    /// Turns a weather reading into the handful of words the model was asked to
    /// judge items against.
    ///
    /// <para>
    /// Pure, and deliberately lossy. Open-Meteo reports a WMO code with 28 values
    /// and a temperature to one decimal place; what a row can use is
    /// <c>rain</c>, <c>cold</c>. Every distinction finer than the vocabulary is
    /// thrown away here rather than downstream, so there is one place that decides
    /// what "it is raining" means and one place to argue with when a row looks wrong.
    /// </para>
    /// </summary>
    public static class WeatherCodes
    {
        /// <summary>
        /// At or above this many degrees Celsius the day reads as hot.
        /// </summary>
        /// <remarks>
        /// 27°C — about 80°F. High enough that it is the thing you notice about the
        /// day, which is the bar for it earning a word at all. A threshold that fires
        /// on a merely pleasant afternoon would put <c>hot</c> on most of the summer
        /// and stop meaning anything.
        /// </remarks>
        public const double HotCelsius = 27.0;

        /// <summary>
        /// At or below this many degrees Celsius the day reads as cold.
        /// </summary>
        /// <remarks>
        /// 4°C — about 39°F, near freezing without assuming it. Same bar as
        /// <see cref="HotCelsius"/> in the other direction.
        /// </remarks>
        public const double ColdCelsius = 4.0;

        /// <summary>
        /// The words describing a reading.
        /// </summary>
        /// <remarks>
        /// A reading yields at most one sky word and at most one temperature word, so
        /// "cold and raining" is expressible and "raining and snowing" is not. A
        /// thunderstorm is reported as <c>storm</c> alone rather than as storm and
        /// rain: a row for a thunderstorm should be the one the model picked for
        /// thunder, and adding rain would dilute it with everything merely drizzly.
        /// </remarks>
        /// <param name="wmoCode">The WMO weather code, as Open-Meteo reports it.</param>
        /// <param name="temperatureCelsius">The current temperature, or null if unknown.</param>
        /// <returns>The context words, which may be empty for a code that is not recognised.</returns>
        public static IReadOnlyList<string> Describe(int wmoCode, double? temperatureCelsius)
        {
            var words = new List<string>(2);

            var sky = SkyWord(wmoCode);
            if (sky is not null)
            {
                words.Add(sky);
            }

            if (temperatureCelsius is { } celsius)
            {
                if (celsius >= HotCelsius)
                {
                    words.Add(ContextVocabulary.Hot);
                }
                else if (celsius <= ColdCelsius)
                {
                    words.Add(ContextVocabulary.Cold);
                }
            }

            return words;
        }

        /// <summary>
        /// The sky word for a WMO code, or null when the code is not one we map.
        /// </summary>
        /// <remarks>
        /// The ranges are WMO 4677 as Open-Meteo documents it. An unmapped code
        /// returns null rather than a guess — the honest answer for a value this
        /// mapping has never seen is that the sky is not described, and a row drawn
        /// from a wrong guess is worse than one drawn from the clock alone.
        /// </remarks>
        private static string? SkyWord(int wmoCode) => wmoCode switch
        {
            0 or 1 => ContextVocabulary.Clear,
            2 or 3 => ContextVocabulary.Cloudy,
            45 or 48 => ContextVocabulary.Fog,

            // Drizzle (51-57), rain (61-67) and rain showers (80-82). Freezing
            // drizzle and freezing rain are rain: what is falling is water, and the
            // temperature word already carries the cold.
            >= 51 and <= 67 => ContextVocabulary.Rain,
            >= 80 and <= 82 => ContextVocabulary.Rain,

            // Snowfall (71-75), snow grains (77) and snow showers (85-86).
            >= 71 and <= 77 => ContextVocabulary.Snow,
            85 or 86 => ContextVocabulary.Snow,

            // Thunderstorm, with and without hail.
            95 or 96 or 99 => ContextVocabulary.Storm,

            _ => null,
        };

        /// <summary>
        /// Converts Fahrenheit to Celsius, for a reading taken in the other unit.
        /// </summary>
        /// <param name="fahrenheit">The temperature in Fahrenheit.</param>
        /// <returns>The temperature in Celsius.</returns>
        public static double FromFahrenheit(double fahrenheit) => (fahrenheit - 32.0) * 5.0 / 9.0;

        /// <summary>
        /// Converts Celsius to Fahrenheit, for display.
        /// </summary>
        /// <param name="celsius">The temperature in Celsius.</param>
        /// <returns>The temperature in Fahrenheit.</returns>
        public static double ToFahrenheit(double celsius) => (celsius * 9.0 / 5.0) + 32.0;
    }
}
