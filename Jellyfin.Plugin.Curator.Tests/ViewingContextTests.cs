using System;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Context;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// The clock and the weather, reduced to the handful of words a row can use.
    /// </summary>
    public class ViewingContextTests
    {
        [Theory]
        [InlineData(5, Daypart.Morning)]
        [InlineData(9, Daypart.Morning)]
        [InlineData(11, Daypart.Morning)]
        [InlineData(12, Daypart.Afternoon)]
        [InlineData(16, Daypart.Afternoon)]
        [InlineData(17, Daypart.Evening)]
        [InlineData(21, Daypart.Evening)]
        [InlineData(22, Daypart.LateNight)]
        [InlineData(23, Daypart.LateNight)]
        public void TheDayIsCutIntoFourAtTheStatedHours(int hour, Daypart expected)
        {
            Assert.Equal(expected, ContextVocabulary.DaypartFor(TimeSpan.FromHours(hour)));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        [InlineData(4)]
        public void LateNightWrapsPastMidnight(int hour)
        {
            // 01:00 belongs with 23:00, not with the morning: the question is what
            // the viewer is up for, not which calendar day it is.
            Assert.Equal(Daypart.LateNight, ContextVocabulary.DaypartFor(TimeSpan.FromHours(hour)));
        }

        [Fact]
        public void MinutesInsideAnHourDoNotMoveTheBucket()
        {
            Assert.Equal(Daypart.Evening, ContextVocabulary.DaypartFor(new TimeSpan(21, 59, 0)));
            Assert.Equal(Daypart.LateNight, ContextVocabulary.DaypartFor(new TimeSpan(22, 0, 0)));
        }

        [Fact]
        public void OnlyVocabularyWordsAreAccepted()
        {
            Assert.True(ContextVocabulary.IsWeather("rain"));
            Assert.True(ContextVocabulary.IsWeather(" RAIN "));
            Assert.False(ContextVocabulary.IsWeather("drizzly"));
            Assert.False(ContextVocabulary.IsWeather(""));
            Assert.False(ContextVocabulary.IsWeather(null));

            Assert.True(ContextVocabulary.IsDaypart("latenight"));
            Assert.False(ContextVocabulary.IsDaypart("late night"));
            Assert.False(ContextVocabulary.IsDaypart("dusk"));
        }

        [Fact]
        public void EveryDaypartHasAWordAndItRoundTrips()
        {
            foreach (Daypart daypart in Enum.GetValues<Daypart>())
            {
                var word = ContextVocabulary.WordFor(daypart);
                Assert.True(ContextVocabulary.IsDaypart(word));
            }
        }

        // ---- weather codes ----

        [Theory]
        [InlineData(0, "clear")]
        [InlineData(1, "clear")]
        [InlineData(2, "cloudy")]
        [InlineData(3, "cloudy")]
        [InlineData(45, "fog")]
        [InlineData(48, "fog")]
        [InlineData(53, "rain")]
        [InlineData(61, "rain")]
        [InlineData(65, "rain")]
        [InlineData(67, "rain")]
        [InlineData(81, "rain")]
        [InlineData(71, "snow")]
        [InlineData(77, "snow")]
        [InlineData(86, "snow")]
        [InlineData(95, "storm")]
        [InlineData(99, "storm")]
        public void EachWmoRangeMapsToItsSkyWord(int code, string expected)
        {
            Assert.Equal([expected], WeatherCodes.Describe(code, temperatureCelsius: 15));
        }

        [Fact]
        public void AnUnrecognisedCodeDescribesNoSkyRatherThanGuessing()
        {
            Assert.Empty(WeatherCodes.Describe(4242, temperatureCelsius: 15));
        }

        [Fact]
        public void TemperatureAddsAtMostOneWord()
        {
            Assert.Equal(["clear", "hot"], WeatherCodes.Describe(0, 31));
            Assert.Equal(["clear", "cold"], WeatherCodes.Describe(0, -3));
            Assert.Equal(["clear"], WeatherCodes.Describe(0, 15));
            Assert.Equal(["clear"], WeatherCodes.Describe(0, null));
        }

        [Fact]
        public void TheTemperatureThresholdsAreInclusive()
        {
            Assert.Contains("hot", WeatherCodes.Describe(0, WeatherCodes.HotCelsius));
            Assert.Contains("cold", WeatherCodes.Describe(0, WeatherCodes.ColdCelsius));
            Assert.DoesNotContain("hot", WeatherCodes.Describe(0, WeatherCodes.HotCelsius - 0.1));
            Assert.DoesNotContain("cold", WeatherCodes.Describe(0, WeatherCodes.ColdCelsius + 0.1));
        }

        [Fact]
        public void AThunderstormIsStormAndNotAlsoRain()
        {
            // A row for a thunderstorm should be what the model picked for thunder.
            // Adding rain would dilute it with everything merely drizzly.
            Assert.Equal(["storm"], WeatherCodes.Describe(95, 15));
        }

        [Fact]
        public void EveryWordProducedIsInTheVocabulary()
        {
            for (var code = 0; code <= 100; code++)
            {
                foreach (var temperature in new double?[] { -20, 15, 40, null })
                {
                    foreach (var word in WeatherCodes.Describe(code, temperature))
                    {
                        Assert.True(ContextVocabulary.IsWeather(word), $"code {code} produced '{word}'");
                    }
                }
            }
        }

        [Fact]
        public void AContextWithNoReadingSaysSoRatherThanPretending()
        {
            var context = ViewingContext.ClockOnly(Daypart.Evening);

            Assert.False(context.HasWeather);
            Assert.Contains("no weather reading", context.Describe(), StringComparison.Ordinal);
        }

        /// <summary>
        /// Every WMO code Open-Meteo documents, so "all weather is handled" is a fact
        /// rather than a belief. A code that falls through the mapping produces no sky
        /// word at all, and a reading with no words is treated as no reading — the
        /// weather row silently disappears in exactly that weather.
        /// </summary>
        [Theory]
        [InlineData(0, "clear")]
        [InlineData(1, "clear")]
        [InlineData(2, "cloudy")]
        [InlineData(3, "cloudy")]
        [InlineData(45, "fog")]
        [InlineData(48, "fog")]
        [InlineData(51, "rain")]
        [InlineData(53, "rain")]
        [InlineData(55, "rain")]
        [InlineData(56, "rain")]
        [InlineData(57, "rain")]
        [InlineData(61, "rain")]
        [InlineData(63, "rain")]
        [InlineData(65, "rain")]
        [InlineData(66, "rain")]
        [InlineData(67, "rain")]
        [InlineData(71, "snow")]
        [InlineData(73, "snow")]
        [InlineData(75, "snow")]
        [InlineData(77, "snow")]
        [InlineData(80, "rain")]
        [InlineData(81, "rain")]
        [InlineData(82, "rain")]
        [InlineData(85, "snow")]
        [InlineData(86, "snow")]
        [InlineData(95, "storm")]
        [InlineData(96, "storm")]
        [InlineData(99, "storm")]
        public void EveryDocumentedWmoCodeProducesASkyWord(int code, string expected)
        {
            Assert.Equal([expected], WeatherCodes.Describe(code, temperatureCelsius: 15));
        }

        [Fact]
        public void NoDocumentedWmoCodeIsLeftUnmapped()
        {
            // The list Open-Meteo publishes, in full. Freezing drizzle and freezing
            // rain are rain — what falls is water, and the temperature word carries
            // the cold separately.
            int[] documented =
            [
                0, 1, 2, 3, 45, 48, 51, 53, 55, 56, 57, 61, 63, 65, 66, 67,
                71, 73, 75, 77, 80, 81, 82, 85, 86, 95, 96, 99,
            ];

            var unmapped = documented
                .Where(code => WeatherCodes.Describe(code, temperatureCelsius: 15).Count == 0)
                .ToList();

            Assert.True(unmapped.Count == 0, "unmapped WMO codes: " + string.Join(", ", unmapped));
        }

        [Fact]
        public void ExtremeTemperaturesStillCarryTheSkyWord()
        {
            // A snowy day is cold by definition; the sky word must not be lost to it.
            Assert.Equal(["snow", "cold"], WeatherCodes.Describe(75, -8));
            Assert.Equal(["clear", "hot"], WeatherCodes.Describe(0, 35));
            Assert.Equal(["storm", "hot"], WeatherCodes.Describe(95, 30));
        }

        // ---- stand-ins for rare conditions ----

        [Fact]
        public void EveryVocabularyWordHasAStandInOrDeliberatelyNone()
        {
            foreach (var word in ContextVocabulary.Weather)
            {
                foreach (var related in ContextVocabulary.RelatedTo(word))
                {
                    Assert.True(ContextVocabulary.IsWeather(related), word + " stands in with '" + related + "'");
                    Assert.NotEqual(word, related);
                }
            }
        }

        [Fact]
        public void ThunderFallsBackToRainThenCloud()
        {
            // The condition a library is least likely to have items for, on the
            // evening a viewer most wants the row.
            Assert.Equal(["rain", "cloudy"], ContextVocabulary.RelatedTo("storm"));
        }

        [Fact]
        public void TheCommonestSkiesReachForNothing()
        {
            // If clear or cloudy cannot fill a row, nothing else will either, and
            // reaching further would stop the row meaning anything.
            Assert.Empty(ContextVocabulary.RelatedTo("clear"));
            Assert.Empty(ContextVocabulary.RelatedTo("cloudy"));
            Assert.Empty(ContextVocabulary.RelatedTo("nonsense"));
            Assert.Empty(ContextVocabulary.RelatedTo(null));
        }

    }
}
