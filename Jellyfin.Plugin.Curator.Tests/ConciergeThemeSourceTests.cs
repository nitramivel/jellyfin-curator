using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Core.Summaries;
using Jellyfin.Plugin.Curator.Services.Summaries;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Reading tone notes out of another plugin's cache.
    ///
    /// <para>
    /// Every test here is really the same test: this must never cost a pass that
    /// would otherwise run. Curator has no claim on that file, cannot version it,
    /// and cannot stop it being deleted mid-pass — so absence, corruption and an
    /// unrecognised shape are all ordinary outcomes rather than faults, and the
    /// answer to all of them is "no themes today".
    /// </para>
    /// </summary>
    public class ConciergeThemeSourceTests : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "curator-themes-" + Guid.NewGuid().ToString("N"));

        private string Write(string json)
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "enrichment.json");
            File.WriteAllText(path, json);
            return path;
        }

        private static ConciergeThemeSource At(string path)
            => new(path, NullLogger<ConciergeThemeSource>.Instance);

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        [Fact]
        public void ReadsTheShapeTheOtherPluginActuallyWrites()
        {
            // Trimmed from a real enrichment.json: the fields this depends on are
            // ItemId and Enrichment.Themes, and nothing else.
            var path = Write("""
            [
              {
                "ItemId": "11111111-1111-1111-1111-111111111111",
                "SourceHash": "abc123",
                "Enrichment": {
                  "Premise": "A washed-up wrestler grinds through weekend shows.",
                  "Moments": ["the staple gun match"],
                  "Themes": ["lonely and heartbreaking", "spent body", "fluorescent gyms"],
                  "Asks": ["the one where the wrestler works at a deli"],
                  "Spoiler": false
                },
                "GeneratedUtc": "2026-08-11T18:44:00Z",
                "Model": "grok-4.3",
                "CostUsd": 0.0001
              }
            ]
            """);

            var themes = At(path).GetThemes();

            var entry = Assert.Single(themes);
            Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), entry.Key);
            Assert.Equal(["lonely and heartbreaking", "spent body", "fluorescent gyms"], entry.Value);
        }

        [Fact]
        public void CapsHowManyThemesOneItemContributes()
        {
            // All of them would crowd out the overview, which is the thing the pass
            // exists to compress.
            var many = string.Join(",", Enumerable.Range(0, 20).Select(i => "\"theme" + i + "\""));
            var path = Write(
                "[{\"ItemId\":\"11111111-1111-1111-1111-111111111111\",\"Enrichment\":{\"Themes\":["
                + many + "]}}]");

            Assert.Equal(
                ConciergeThemeSource.MaxThemesPerItem,
                Assert.Single(At(path).GetThemes()).Value.Count);
        }

        [Fact]
        public void AMissingFileIsTheOrdinaryCase()
        {
            // Most installs do not have the other plugin at all.
            Assert.Empty(At(Path.Combine(_directory, "nothing-here.json")).GetThemes());
        }

        [Fact]
        public void UnreadableJsonYieldsNothingRatherThanThrowing()
        {
            Assert.Empty(At(Write("{ this is not json")).GetThemes());
        }

        [Fact]
        public void AnUnrecognisedShapeYieldsNothing()
        {
            // The other plugin is free to change its file. This must notice and
            // shrug, not fail the distillation pass that asked.
            Assert.Empty(At(Write("""{"version":2,"entries":{"a":1}}""")).GetThemes());
            Assert.Empty(At(Write("""[{"Nope":1}]""")).GetThemes());
        }

        [Fact]
        public void EntriesWithoutUsableThemesAreSkipped()
        {
            var path = Write("""
            [
              {"ItemId":"11111111-1111-1111-1111-111111111111","Enrichment":{"Themes":[]}},
              {"ItemId":"not-a-guid","Enrichment":{"Themes":["x"]}},
              {"ItemId":"22222222-2222-2222-2222-222222222222","Enrichment":{"Themes":["  ","kept"]}},
              {"ItemId":"33333333-3333-3333-3333-333333333333"}
            ]
            """);

            var themes = At(path).GetThemes();

            var entry = Assert.Single(themes);
            Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), entry.Key);
            Assert.Equal(["kept"], entry.Value);
        }

        [Fact]
        public void ARewrittenFileIsPickedUp()
        {
            // The index next door is rebuilt on its own schedule; a pass an hour
            // later must see the new themes, not the ones cached at startup.
            var path = Write("""
            [{"ItemId":"11111111-1111-1111-1111-111111111111","Enrichment":{"Themes":["first"]}}]
            """);
            var source = At(path);
            Assert.Equal(["first"], Assert.Single(source.GetThemes()).Value);

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
            File.WriteAllText(path, """
            [{"ItemId":"11111111-1111-1111-1111-111111111111","Enrichment":{"Themes":["second"]}}]
            """);

            Assert.Equal(["second"], Assert.Single(source.GetThemes()).Value);
        }

        // ---- what reaches the prompt ----

        private static MediaItemRecord Item(Guid id) => new()
        {
            Id = id,
            Kind = MediaKind.Movie,
            Name = "The Wrestler",
            Overview = "A washed-up wrestler grinds through weekend shows in school gymnasiums.",
        };

        [Fact]
        public void ThemesReachThePromptAsNotesAfterTheOverview()
        {
            // Ordering is deliberate: the model reads the source material first and
            // these as commentary on it, not the other way round.
            var id = Guid.NewGuid();
            var prompt = SummaryPromptBuilder.BuildUserPrompt(
                [Item(id)],
                includeTags: false,
                themes: new Dictionary<Guid, IReadOnlyList<string>> { [id] = ["lonely and heartbreaking"] });

            Assert.Contains("\"notes\"", prompt, StringComparison.Ordinal);
            Assert.Contains("lonely and heartbreaking", prompt, StringComparison.Ordinal);
            Assert.True(
                prompt.IndexOf("\"overview\"", StringComparison.Ordinal)
                    < prompt.IndexOf("\"notes\"", StringComparison.Ordinal),
                "the overview must be written before the notes about it");
        }

        [Fact]
        public void AnItemWithNoThemesIsDescribedExactlyAsBefore()
        {
            // 5% of the library on the server this was built against. They must not
            // be degraded by a feature that is meant to be additive.
            var id = Guid.NewGuid();
            var withThemes = SummaryPromptBuilder.BuildUserPrompt(
                [Item(id)],
                includeTags: false,
                themes: new Dictionary<Guid, IReadOnlyList<string>> { [Guid.NewGuid()] = ["something else"] });

            var without = SummaryPromptBuilder.BuildUserPrompt([Item(id)]);

            Assert.Equal(without, withThemes);
        }

        [Fact]
        public void NoThemesAtAllLeavesThePromptUnchanged()
        {
            var id = Guid.NewGuid();

            Assert.Equal(
                SummaryPromptBuilder.BuildUserPrompt([Item(id)]),
                SummaryPromptBuilder.BuildUserPrompt(
                    [Item(id)], false, new Dictionary<Guid, IReadOnlyList<string>>()));
        }

        [Fact]
        public void ThePromptTellsTheModelWhatTheNotesAreFor()
        {
            var system = SummaryPromptBuilder.BuildSystemPrompt(90);

            Assert.Contains("notes", system, StringComparison.Ordinal);

            // The two failure modes worth naming: parroting them back, and trusting
            // them over the item's own overview.
            Assert.Contains("do not quote them back", system, StringComparison.Ordinal);
        }
    }
}
