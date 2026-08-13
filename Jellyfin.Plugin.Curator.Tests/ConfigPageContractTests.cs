using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Curator.Api;
using Jellyfin.Plugin.Curator.Services.Context;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// The coupling between the config page and the API records it reads.
    ///
    /// <para>
    /// This exists because of a bug that shipped. Jellyfin serializes controller
    /// responses in <b>PascalCase</b> — <c>status.IsRunning</c>, <c>result.Skipped</c>
    /// — while the plugin-configuration endpoints go over the wire in camelCase. The
    /// weather Test button was written against the wrong one, and the failure was
    /// silent in the worst way: the field read <c>undefined</c>, the page reported
    /// "No reading. Check the server log", and the lookup it was reporting on had
    /// actually succeeded. Nothing threw, nothing was logged, and the server log the
    /// message pointed at had nothing in it to find.
    /// </para>
    ///
    /// <para>
    /// The page now reads through a <c>field()</c> helper that tries both casings, so
    /// the casing itself can no longer bite. What it cannot defend against is a
    /// <i>renamed</i> property — so these tests assert that every name the page asks
    /// for is a name the record actually has.
    /// </para>
    /// </summary>
    public class ConfigPageContractTests
    {
        private static string ConfigPage()
        {
            // Walk up to the repository root: the test binary sits several levels
            // under it and the page is an embedded resource in the other project.
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(
                    directory.FullName,
                    "Jellyfin.Plugin.Curator",
                    "Configuration",
                    "configPage.html");

                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException("Could not locate configPage.html from " + AppContext.BaseDirectory);
        }

        /// <summary>Every name the page reads through the tolerant helper.</summary>
        private static IReadOnlyList<string> FieldsReadByThePage()
        {
            // Any receiver, not just `result` — the summaries list reads its rows
            // through the same helper under a different variable name, and a guard
            // that only watched one name would quietly stop covering new code.
            var matches = Regex.Matches(ConfigPage(), @"field\(\s*[A-Za-z_][A-Za-z0-9_]*\s*,\s*'([A-Za-z]+)'\s*\)");
            return [.. matches.Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];
        }

        private static bool HasProperty(Type type, string name)
            => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        [Fact]
        public void ThePageReadsSomethingAtAll()
        {
            // Guards the regex itself: a test that silently matches nothing would
            // pass forever while asserting nothing.
            Assert.NotEmpty(FieldsReadByThePage());
        }

        [Fact]
        public void EveryFieldThePageReadsExistsOnOneOfTheRecordsItReads()
        {
            // Deliberately checked against the union rather than per-endpoint: the
            // page reads several endpoints through one helper, and pinning which
            // field belongs to which call would break every time a handler moves.
            // A rename still fails this, which is the point.
            Type[] records =
            [
                typeof(WeatherProbe),
                typeof(ContextRowRefreshResult),
                typeof(Jellyfin.Plugin.Curator.Services.HomeScreen.SectionSyncResult),
                typeof(SummarySample),
                typeof(Jellyfin.Plugin.Curator.Services.Playlists.EmptyPlaylistSweepResult),
                typeof(Jellyfin.Plugin.Curator.Services.Playlists.EmptyPlaylistCandidate),
                typeof(SummaryPruneResult),
                typeof(Jellyfin.Plugin.Curator.Services.Footer.FooterApplyResult),
                typeof(Jellyfin.Plugin.Curator.Configuration.FooterLink),
            ];

            var missing = FieldsReadByThePage()
                .Where(name => !records.Any(type => HasProperty(type, name)))
                .ToList();

            Assert.True(
                missing.Count == 0,
                "The config page reads fields no API record exposes: " + string.Join(", ", missing));
        }

        [Fact]
        public void TheSummarySampleCarriesWhatTheListDisplays()
        {
            // The weather columns sit beside the summary because they are one answer:
            // the model wrote the rewrite and then read its own words back to judge
            // the weather. ContextJudged is separate from the lists on purpose — an
            // item judged to suit nothing has been judged, and most of a library
            // lands there, so emptiness must not render as "not done".
            foreach (var name in new[] { "Title", "Text", "Tags", "Weather", "Dayparts", "ContextJudged" })
            {
                Assert.True(HasProperty(typeof(SummarySample), name), name + " is missing from SummarySample");
            }
        }

        [Fact]
        public void TheWeatherProbeCarriesEverythingTheTestButtonDisplays()
        {
            // The button's whole job is telling apart four failures with four fixes,
            // so the reason has to survive the trip rather than being logged and
            // swallowed on the server.
            foreach (var name in new[]
            {
                "Ok", "Requested", "Resolved", "Conditions",
                "TemperatureCelsius", "TemperatureFahrenheit", "LocalTime", "Daypart", "Error",
            })
            {
                Assert.True(HasProperty(typeof(WeatherProbe), name), name + " is missing from WeatherProbe");
            }
        }

        [Fact]
        public void ThePageNeverReadsAResponseFieldWithoutTheHelper()
        {
            // The regression guard. A direct result.foo read is a bet on the casing,
            // and losing that bet reports failure for a call that worked.
            var page = ConfigPage();
            var direct = Regex.Matches(page, @"result\.([a-z][A-Za-z]*)")
                .Select(m => m.Groups[1].Value)
                .Where(name => !string.Equals(name, "push", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.True(
                direct.Count == 0,
                "These are read straight off an API response in camelCase, which Jellyfin does not send: "
                + string.Join(", ", direct));
        }
    }
}
