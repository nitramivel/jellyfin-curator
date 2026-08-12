using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Curator.Core.HomeScreen;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    public class SectionConfigMergerTests
    {
        private static readonly Guid CategoryId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        private static DesiredSection Section(string name, Guid? id = null) =>
            new(SectionConfigMerger.SectionIdFor(id ?? CategoryId), name);

        private static JsonArray Sections(JsonNode config) => config["Sections"]!.AsArray();

        private static JsonObject FindSection(JsonNode config, string sectionId) =>
            Sections(config).OfType<JsonObject>().Single(s => (string?)s["UniqueId"] == sectionId);

        [Fact]
        public void SectionIdFor_IsPrefixedAndStable()
        {
            var id = SectionConfigMerger.SectionIdFor(CategoryId);

            Assert.StartsWith("curator-", id, StringComparison.Ordinal);
            Assert.Equal(id, SectionConfigMerger.SectionIdFor(CategoryId));
            Assert.NotEqual(id, SectionConfigMerger.SectionIdFor(Guid.NewGuid()));
        }

        [Fact]
        public void MergeSections_EmptyConfig_AddsSectionWithNameAsJoinKey()
        {
            var config = JsonNode.Parse("{}")!;

            var changed = SectionConfigMerger.MergeSections(config, [Section("Comfort Rewatch")], asPlaylists: true);

            Assert.True(changed);
            var section = Assert.Single(Sections(config).OfType<JsonObject>());
            Assert.Equal(SectionConfigMerger.SectionIdFor(CategoryId), (string?)section["UniqueId"]);
            Assert.Equal("Comfort Rewatch", (string?)section["DisplayText"]);
            // CollectionName is the name-based join key Collection Sections resolves by.
            Assert.Equal("Comfort Rewatch", (string?)section["CollectionName"]);
            Assert.Equal("Playlist", (string?)section["SectionType"]);
        }

        [Fact]
        public void MergeSections_CollectionOutput_UsesCollectionType()
        {
            var config = JsonNode.Parse("{}")!;

            SectionConfigMerger.MergeSections(config, [Section("Cerebral Sci-Fi")], asPlaylists: false);

            Assert.Equal("Collection", (string?)Sections(config)[0]!["SectionType"]);
        }

        [Fact]
        public void MergeSections_PreservesForeignSectionsAndUnknownFields()
        {
            var config = JsonNode.Parse(
                """
                {
                  "SomeFutureSetting": 42,
                  "Sections": [
                    {"UniqueId":"user-made-1","DisplayText":"My Row","CollectionName":"My Row","SectionType":"Collection","ExtraField":"keep me"}
                  ]
                }
                """)!;

            SectionConfigMerger.MergeSections(config, [Section("Comfort Rewatch")], asPlaylists: true);

            Assert.Equal(42, (int)config["SomeFutureSetting"]!);
            Assert.Equal(2, Sections(config).Count);
            var foreign = FindSection(config, "user-made-1");
            Assert.Equal("My Row", (string?)foreign["DisplayText"]);
            Assert.Equal("keep me", (string?)foreign["ExtraField"]);
        }

        [Fact]
        public void MergeSections_RenamedCategory_UpdatesBothNameFields()
        {
            var config = JsonNode.Parse("{}")!;
            SectionConfigMerger.MergeSections(config, [Section("Old Name")], asPlaylists: true);

            var changed = SectionConfigMerger.MergeSections(config, [Section("New Name")], asPlaylists: true);

            Assert.True(changed);
            var section = Assert.Single(Sections(config).OfType<JsonObject>());
            Assert.Equal("New Name", (string?)section["DisplayText"]);
            Assert.Equal("New Name", (string?)section["CollectionName"]);
        }

        [Fact]
        public void MergeSections_UnchangedInput_ReportsNoChange()
        {
            var config = JsonNode.Parse("{}")!;
            SectionConfigMerger.MergeSections(config, [Section("Comfort Rewatch")], asPlaylists: true);

            var changed = SectionConfigMerger.MergeSections(config, [Section("Comfort Rewatch")], asPlaylists: true);

            Assert.False(changed);
        }

        [Fact]
        public void MergeSections_RemovedCategory_DropsOnlyOurSection()
        {
            // An empty desired list is also how the integrated path hands over:
            // both plugins register under the same section IDs into one dictionary,
            // so Curator's entries have to leave Collection Sections' config or the
            // two race for the row. Sections the owner made there must survive that.
            var config = JsonNode.Parse(
                """{"Sections":[{"UniqueId":"user-made-1","DisplayText":"Mine","CollectionName":"Mine","SectionType":"Collection"}]}""")!;
            SectionConfigMerger.MergeSections(config, [Section("Going Away")], asPlaylists: true);
            Assert.Equal(2, Sections(config).Count);

            var changed = SectionConfigMerger.MergeSections(config, [], asPlaylists: true);

            Assert.True(changed);
            var remaining = Assert.Single(Sections(config).OfType<JsonObject>());
            Assert.Equal("user-made-1", (string?)remaining["UniqueId"]);
        }

        [Fact]
        public void MergeSections_CamelCaseConfig_IsUnderstoodNotDuplicated()
        {
            // The server serializes plugin configuration as camelCase over HTTP.
            var config = JsonNode.Parse(
                $$"""
                {"sections":[{"uniqueId":"{{SectionConfigMerger.SectionIdFor(CategoryId)}}","displayText":"Old","collectionName":"Old","sectionType":"Playlist"}]}
                """)!;

            var changed = SectionConfigMerger.MergeSections(config, [Section("Renamed")], asPlaylists: true);

            Assert.True(changed);
            var sections = config["sections"]!.AsArray();
            var section = Assert.Single(sections.OfType<JsonObject>());
            Assert.Equal("Renamed", (string?)section["displayText"]);
            Assert.Equal("Renamed", (string?)section["collectionName"]);
            Assert.Null(config["Sections"]);
        }

        [Fact]
        public void MergeSections_MultipleCategories_AllAppear()
        {
            var config = JsonNode.Parse("{}")!;
            var desired = new List<DesiredSection>
            {
                Section("Comfort Rewatch", Guid.NewGuid()),
                Section("Cerebral Sci-Fi", Guid.NewGuid()),
                Section("Bottle Episodes", Guid.NewGuid()),
            };

            SectionConfigMerger.MergeSections(config, desired, asPlaylists: true);

            Assert.Equal(3, Sections(config).Count);
        }

        [Fact]
        public void MergeEnabledSections_AddsOurIdsKeepingTheirs()
        {
            var settings = JsonNode.Parse("""{"UserId":"x","EnabledSections":["myMovies","nextUp"]}""")!;
            var sectionId = SectionConfigMerger.SectionIdFor(CategoryId);

            var changed = SectionConfigMerger.MergeEnabledSections(settings, [sectionId]);

            Assert.True(changed);
            var enabled = settings["EnabledSections"]!.AsArray().Select(n => (string?)n).ToList();
            Assert.Equal(["myMovies", "nextUp", sectionId], enabled);
        }

        [Fact]
        public void MergeEnabledSections_MissingArray_IsCreated()
        {
            var settings = JsonNode.Parse("""{"UserId":"x"}""")!;

            var changed = SectionConfigMerger.MergeEnabledSections(settings, ["curator-abc"]);

            Assert.True(changed);
            Assert.Equal(["curator-abc"], settings["EnabledSections"]!.AsArray().Select(n => (string?)n));
        }

        [Fact]
        public void MergeEnabledSections_StaleCuratorIds_AreRemoved()
        {
            var settings = JsonNode.Parse(
                """{"EnabledSections":["myMovies","curator-gone","curator-stays"]}""")!;

            var changed = SectionConfigMerger.MergeEnabledSections(settings, ["curator-stays"]);

            Assert.True(changed);
            Assert.Equal(
                ["myMovies", "curator-stays"],
                settings["EnabledSections"]!.AsArray().Select(n => (string?)n));
        }

        [Fact]
        public void MergeEnabledSections_AlreadyCorrect_ReportsNoChange()
        {
            var settings = JsonNode.Parse("""{"EnabledSections":["myMovies","curator-stays"]}""")!;

            Assert.False(SectionConfigMerger.MergeEnabledSections(settings, ["curator-stays"]));
        }

        [Fact]
        public void MergeEnabledSections_PreservesLockedAndDefaultLists()
        {
            var settings = JsonNode.Parse(
                """{"EnabledSections":[],"LockedSections":["locked1"],"DefaultEnabledSections":["def1"]}""")!;

            SectionConfigMerger.MergeEnabledSections(settings, ["curator-abc"]);

            Assert.Equal(["locked1"], settings["LockedSections"]!.AsArray().Select(n => (string?)n));
            Assert.Equal(["def1"], settings["DefaultEnabledSections"]!.AsArray().Select(n => (string?)n));
        }

        [Fact]
        public void MergeEnabledSections_CamelCaseSettings_AreUnderstood()
        {
            var settings = JsonNode.Parse("""{"userId":"x","enabledSections":["myMovies"]}""")!;

            var changed = SectionConfigMerger.MergeEnabledSections(settings, ["curator-abc"]);

            Assert.True(changed);
            Assert.Equal(
                ["myMovies", "curator-abc"],
                settings["enabledSections"]!.AsArray().Select(n => (string?)n));
            Assert.Null(settings["EnabledSections"]);
        }
    
        // ------------------------------------------------------------------
        // Row order and card shape, which live in Home Screen Sections' own
        // configuration — Collection Sections has no fields for either.
        // ------------------------------------------------------------------

        [Fact]
        public void SectionSettings_AreCreatedAtOrder500WithAShapeFromTheItemCount()
        {
            var config = JsonNode.Parse("{}")!;

            var changed = SectionConfigMerger.MergeSectionSettings(config, [
                new DesiredSection("curator-a", "Small Row", 6),
                new DesiredSection("curator-b", "Full Row", 14),
            ]);

            Assert.True(changed);
            var settings = config["SectionSettings"]!.AsArray();
            Assert.Equal(2, settings.Count);

            Assert.Equal(500, settings[0]!["OrderIndex"]!.GetValue<int>());
            Assert.Equal("Landscape", settings[0]!["ViewMode"]!.GetValue<string>());
            Assert.Equal(500, settings[1]!["OrderIndex"]!.GetValue<int>());
            Assert.Equal("Portrait", settings[1]!["ViewMode"]!.GetValue<string>());
        }

        [Theory]
        [InlineData(2, "Landscape")]
        [InlineData(9, "Landscape")]
        [InlineData(10, "Portrait")]
        [InlineData(40, "Portrait")]
        public void ViewMode_FlipsAtTheDefaultThreshold(int members, string expected)
        {
            Assert.Equal(expected, SectionConfigMerger.ViewModeFor(members));
        }

        [Theory]
        [InlineData(5, 6, "Landscape")]
        [InlineData(6, 6, "Portrait")]
        [InlineData(19, 20, "Landscape")]
        [InlineData(20, 20, "Portrait")]
        public void ViewMode_FlipsAtTheConfiguredThreshold(int members, int threshold, string expected)
        {
            Assert.Equal(expected, SectionConfigMerger.ViewModeFor(members, threshold));
        }

        /// <summary>
        /// The two ends of the range are the way to turn the behaviour off in either
        /// direction, so they must not need a special case in the caller.
        /// </summary>
        [Fact]
        public void ViewMode_ThresholdOfZeroIsAlwaysPortrait_AndAHugeOneAlwaysLandscape()
        {
            Assert.Equal("Portrait", SectionConfigMerger.ViewModeFor(0, 0));
            Assert.Equal("Portrait", SectionConfigMerger.ViewModeFor(1, 0));
            Assert.Equal("Landscape", SectionConfigMerger.ViewModeFor(500, 501));
        }

        [Fact]
        public void SectionSettings_UseTheConfiguredThreshold()
        {
            var config = JsonNode.Parse("""{"SectionSettings":[]}""")!;

            SectionConfigMerger.MergeSectionSettings(
                config,
                [new DesiredSection("curator-a", "Small", 6), new DesiredSection("curator-b", "Smaller", 5)],
                portraitThreshold: 6);

            var settings = config["SectionSettings"]!.AsArray();
            Assert.Equal("Portrait", settings[0]!["ViewMode"]!.GetValue<string>());
            Assert.Equal("Landscape", settings[1]!["ViewMode"]!.GetValue<string>());
        }

        // ---- the fields Home Screen Sections sets for itself ----

        /// <summary>
        /// A new entry must carry every field Home Screen Sections would have set,
        /// not only the two Curator owns. An absent field is not "left alone" — it
        /// deserializes to the CLR default, which made every row Curator created
        /// arrive switched off and asking for between zero and zero items.
        /// </summary>
        [Fact]
        public void SectionSettings_NewEntryIsEnabledAndAsksForOneItem()
        {
            var config = JsonNode.Parse("""{"SectionSettings":[]}""")!;

            Assert.True(SectionConfigMerger.MergeSectionSettings(
                config, [new DesiredSection("curator-a", "Cerebral Sci-Fi", 12)]));

            var entry = config["SectionSettings"]!.AsArray()[0]!;
            Assert.True(entry["Enabled"]!.GetValue<bool>());
            Assert.True(entry["AllowUserOverride"]!.GetValue<bool>());
            Assert.Equal(1, entry["LowerLimit"]!.GetValue<int>());
            Assert.Equal(1, entry["UpperLimit"]!.GetValue<int>());
            Assert.False(entry["HideWatchedItems"]!.GetValue<bool>());
            Assert.Equal(500, entry["OrderIndex"]!.GetValue<int>());
            Assert.Equal("Portrait", entry["ViewMode"]!.GetValue<string>());
        }

        /// <summary>
        /// The rows an older version already wrote have to be healed, or they stay
        /// switched off forever — nothing else ever revisits them.
        /// </summary>
        [Fact]
        public void SectionSettings_HealARowLeftIncompleteByAnOlderVersion()
        {
            var config = JsonNode.Parse("""
                {"SectionSettings":[
                  {"SectionId":"curator-a","Enabled":false,"AllowUserOverride":false,
                   "LowerLimit":0,"UpperLimit":0,"OrderIndex":500,"ViewMode":"Portrait"}
                ]}
                """)!;

            Assert.True(SectionConfigMerger.MergeSectionSettings(
                config, [new DesiredSection("curator-a", "Cerebral Sci-Fi", 12)]));

            var entry = config["SectionSettings"]!.AsArray()[0]!;
            Assert.True(entry["Enabled"]!.GetValue<bool>());
            Assert.Equal(1, entry["LowerLimit"]!.GetValue<int>());
            Assert.Equal(1, entry["UpperLimit"]!.GetValue<int>());
        }

        /// <summary>
        /// Switching a row off by hand is a legitimate thing to do and must survive
        /// the next run. Only the zero/zero fingerprint — which Home Screen Sections
        /// never writes — is treated as ours to overwrite.
        /// </summary>
        [Fact]
        public void SectionSettings_ARowTheUserDisabledStaysDisabled()
        {
            var config = JsonNode.Parse("""
                {"SectionSettings":[
                  {"SectionId":"curator-a","Enabled":false,"AllowUserOverride":true,
                   "LowerLimit":1,"UpperLimit":3,"OrderIndex":500,"ViewMode":"Portrait"}
                ]}
                """)!;

            SectionConfigMerger.MergeSectionSettings(
                config, [new DesiredSection("curator-a", "Cerebral Sci-Fi", 12)]);

            var entry = config["SectionSettings"]!.AsArray()[0]!;
            Assert.False(entry["Enabled"]!.GetValue<bool>());
            Assert.Equal(1, entry["LowerLimit"]!.GetValue<int>());
            Assert.Equal(3, entry["UpperLimit"]!.GetValue<int>());
        }

        /// <summary>
        /// Healing is not a reason to rewrite a file that is already correct — a
        /// write fires Collection Sections' ConfigurationChanged and re-registers
        /// every section.
        /// </summary>
        [Fact]
        public void SectionSettings_AHealthyRowReportsNoChange()
        {
            var config = JsonNode.Parse("""
                {"SectionSettings":[
                  {"SectionId":"curator-a","Enabled":true,"AllowUserOverride":true,
                   "LowerLimit":1,"UpperLimit":1,"OrderIndex":500,"ViewMode":"Portrait",
                   "HideWatchedItems":false}
                ]}
                """)!;

            Assert.False(SectionConfigMerger.MergeSectionSettings(
                config, [new DesiredSection("curator-a", "Cerebral Sci-Fi", 12)]));
        }

        /// <summary>
        /// The repair is keyed on the Curator prefix like everything else here, so a
        /// foreign section sitting at zero/zero is none of our business.
        /// </summary>
        [Fact]
        public void SectionSettings_DoNotHealForeignSections()
        {
            var config = JsonNode.Parse("""
                {"SectionSettings":[
                  {"SectionId":"myplugin-a","Enabled":false,"LowerLimit":0,"UpperLimit":0}
                ]}
                """)!;

            SectionConfigMerger.MergeSectionSettings(config, []);

            var entry = config["SectionSettings"]!.AsArray()[0]!;
            Assert.False(entry["Enabled"]!.GetValue<bool>());
            Assert.Equal(0, entry["LowerLimit"]!.GetValue<int>());
        }

        [Fact]
        public void SectionSettings_LeaveOtherPluginsSectionsAlone()
        {
            var config = JsonNode.Parse("""
                {"SectionSettings":[
                    {"SectionId":"ContinueWatching","OrderIndex":999,"ViewMode":"Landscape","HideWatchedItems":false},
                    {"SectionId":"MARVEL","OrderIndex":12,"ViewMode":"Square"}
                ]}
                """)!;

            SectionConfigMerger.MergeSectionSettings(config, [new DesiredSection("curator-a", "Mine", 12)]);

            var settings = config["SectionSettings"]!.AsArray();
            Assert.Equal(3, settings.Count);
            Assert.Equal(999, settings[0]!["OrderIndex"]!.GetValue<int>());
            Assert.Equal(12, settings[1]!["OrderIndex"]!.GetValue<int>());
            Assert.Equal("Square", settings[1]!["ViewMode"]!.GetValue<string>());
        }

        [Fact]
        public void SectionSettings_PreserveFieldsWeDoNotSet()
        {
            // HideWatchedItems, limits and per-user override are the user's to
            // choose; we only ever touch order and shape.
            var config = JsonNode.Parse("""
                {"SectionSettings":[
                    {"SectionId":"curator-a","Enabled":true,"AllowUserOverride":true,
                     "LowerLimit":1,"UpperLimit":1,"OrderIndex":7,"ViewMode":"Square",
                     "HideWatchedItems":true}
                ]}
                """)!;

            SectionConfigMerger.MergeSectionSettings(config, [new DesiredSection("curator-a", "Mine", 3)]);

            var entry = config["SectionSettings"]!.AsArray()[0]!;
            Assert.Equal(500, entry["OrderIndex"]!.GetValue<int>());
            Assert.Equal("Landscape", entry["ViewMode"]!.GetValue<string>());
            Assert.True(entry["HideWatchedItems"]!.GetValue<bool>());
            Assert.True(entry["AllowUserOverride"]!.GetValue<bool>());
            Assert.Equal(1, entry["UpperLimit"]!.GetValue<int>());
        }

        [Fact]
        public void SectionSettings_RemoveCuratorEntriesForCategoriesThatAreGone()
        {
            var config = JsonNode.Parse("""
                {"SectionSettings":[
                    {"SectionId":"curator-old","OrderIndex":500,"ViewMode":"Portrait"},
                    {"SectionId":"Genre","OrderIndex":3,"ViewMode":"Square"}
                ]}
                """)!;

            Assert.True(SectionConfigMerger.MergeSectionSettings(config, []));

            var settings = config["SectionSettings"]!.AsArray();
            Assert.Equal("Genre", Assert.Single(settings)!["SectionId"]!.GetValue<string>());
        }

        [Fact]
        public void SectionSettings_AnEntryMissingItsLimitsIsHealed()
        {
            // This shape — SectionId, OrderIndex and ViewMode and nothing else — is
            // what older versions wrote, and it was the bug: the absent fields
            // deserialize to Enabled=false with both limits at 0. It used to be
            // asserted here as "already correct". It is not, and reporting no change
            // would leave the row switched off forever, since nothing else revisits
            // it. See SectionSettings_AHealthyRowReportsNoChange for the real
            // no-change contract.
            var config = JsonNode.Parse("""
                {"SectionSettings":[{"SectionId":"curator-a","OrderIndex":500,"ViewMode":"Portrait"}]}
                """)!;

            Assert.True(SectionConfigMerger.MergeSectionSettings(
                config, [new DesiredSection("curator-a", "Mine", 12)]));

            var entry = config["SectionSettings"]!.AsArray()[0]!;
            Assert.True(entry["Enabled"]!.GetValue<bool>());
            Assert.Equal(1, entry["LowerLimit"]!.GetValue<int>());
            Assert.Equal(1, entry["UpperLimit"]!.GetValue<int>());
        }

        [Fact]
        public void SectionSettings_TolerateCamelCaseFromTheServer()
        {
            // The server serializes plugin config as camelCase over HTTP while the
            // C# type is PascalCase; a naive merge creates a second array.
            var config = JsonNode.Parse("""
                {"sectionSettings":[{"sectionId":"curator-a","orderIndex":9,"viewMode":"Square"}]}
                """)!;

            Assert.True(SectionConfigMerger.MergeSectionSettings(
                config, [new DesiredSection("curator-a", "Mine", 12)]));

            Assert.Null(config["SectionSettings"]);
            var entry = config["sectionSettings"]!.AsArray()[0]!;
            Assert.Equal(500, entry["orderIndex"]!.GetValue<int>());
            Assert.Equal("Portrait", entry["viewMode"]!.GetValue<string>());
        }

        // ---- category rows and context rows must not delete each other ----

        /// <summary>
        /// The failure this pair exists for. Both merges REMOVE Curator entries
        /// absent from the list they were handed. Category rows are published by a
        /// run; the context rows are republished several times a day. Scoped to the
        /// whole "curator-" prefix, the frequent one would delete every category row
        /// from the section settings — and from every viewer's enabled list — several
        /// times a day, with nothing anywhere reporting it.
        /// </summary>
        [Fact]
        public void AContextOnlySyncLeavesTheCategoryRowsAlone()
        {
            var config = JsonNode.Parse("""
            {
              "SectionSettings": [
                { "SectionId": "curator-aaaa", "OrderIndex": 500, "Enabled": true, "LowerLimit": 1, "UpperLimit": 1 },
                { "SectionId": "curator-context-weather", "OrderIndex": 500, "Enabled": true, "LowerLimit": 1, "UpperLimit": 1 }
              ]
            }
            """)!;

            SectionConfigMerger.MergeSectionSettings(
                config,
                [new DesiredSection("curator-context-weather", "Rain-Soaked", 20, 100)],
                SectionConfigMerger.DefaultPortraitThreshold,
                SectionConfigMerger.SectionScope.Context);

            var ids = config["SectionSettings"]!.AsArray()
                .Select(e => (string?)e!["SectionId"]).ToList();

            Assert.Contains("curator-aaaa", ids);
            Assert.Contains("curator-context-weather", ids);
        }

        [Fact]
        public void ACategorySyncLeavesTheContextRowsAlone()
        {
            // The mirror image: a run must not delete the two rows the other pass owns.
            var config = JsonNode.Parse("""
            {
              "SectionSettings": [
                { "SectionId": "curator-aaaa", "OrderIndex": 500, "Enabled": true, "LowerLimit": 1, "UpperLimit": 1 },
                { "SectionId": "curator-context-daypart", "OrderIndex": 100, "Enabled": true, "LowerLimit": 1, "UpperLimit": 1 }
              ]
            }
            """)!;

            SectionConfigMerger.MergeSectionSettings(
                config,
                [new DesiredSection("curator-aaaa", "Kept", 12)],
                SectionConfigMerger.DefaultPortraitThreshold,
                SectionConfigMerger.SectionScope.Categories);

            var ids = config["SectionSettings"]!.AsArray()
                .Select(e => (string?)e!["SectionId"]).ToList();

            Assert.Contains("curator-context-daypart", ids);
            Assert.Contains("curator-aaaa", ids);
        }

        [Fact]
        public void AContextOnlyEnableDoesNotUnenrolTheCategoryRows()
        {
            var settings = JsonNode.Parse("""
            {
              "UserId": "11111111-1111-1111-1111-111111111111",
              "EnabledSections": ["curator-aaaa", "curator-context-weather", "somebody-elses-row"]
            }
            """)!;

            SectionConfigMerger.MergeEnabledSections(
                settings,
                ["curator-context-weather"],
                SectionConfigMerger.SectionScope.Context);

            var enabled = settings["EnabledSections"]!.AsArray()
                .Select(e => e!.GetValue<string>()).ToList();

            Assert.Contains("curator-aaaa", enabled);
            Assert.Contains("curator-context-weather", enabled);
            Assert.Contains("somebody-elses-row", enabled);
        }

        [Fact]
        public void AContextOnlyEnableStillRemovesAStaleContextRow()
        {
            // Scoping must not become "never remove anything": switching from
            // per-viewer to shared locations leaves per-viewer rows behind.
            var settings = JsonNode.Parse("""
            {
              "EnabledSections": ["curator-context-weather-abc", "curator-context-weather"]
            }
            """)!;

            SectionConfigMerger.MergeEnabledSections(
                settings,
                ["curator-context-weather"],
                SectionConfigMerger.SectionScope.Context);

            var enabled = settings["EnabledSections"]!.AsArray()
                .Select(e => e!.GetValue<string>()).ToList();

            Assert.Equal(["curator-context-weather"], enabled);
        }

        [Theory]
        [InlineData("curator-aaaa", SectionConfigMerger.SectionScope.Categories, true)]
        [InlineData("curator-aaaa", SectionConfigMerger.SectionScope.Context, false)]
        [InlineData("curator-context-weather", SectionConfigMerger.SectionScope.Categories, false)]
        [InlineData("curator-context-weather", SectionConfigMerger.SectionScope.Context, true)]
        [InlineData("curator-context-daypart-abc", SectionConfigMerger.SectionScope.Context, true)]
        [InlineData("curator-anything", SectionConfigMerger.SectionScope.All, true)]
        [InlineData("curator-context-weather", SectionConfigMerger.SectionScope.All, true)]
        [InlineData("somebody-elses-row", SectionConfigMerger.SectionScope.All, false)]
        [InlineData(null, SectionConfigMerger.SectionScope.All, false)]
        public void TheTwoScopesAreDisjointAndNeverClaimForeignRows(
            string? sectionId,
            SectionConfigMerger.SectionScope scope,
            bool expected)
        {
            Assert.Equal(expected, SectionConfigMerger.InScope(sectionId, scope));
        }

        // ---- the order index ----

        [Fact]
        public void EachSectionCarriesItsOwnLane()
        {
            var config = JsonNode.Parse("""{"SectionSettings":[]}""")!;

            SectionConfigMerger.MergeSectionSettings(
                config,
                [
                    new DesiredSection("curator-aaaa", "Category", 12, 500),
                    new DesiredSection("curator-context-weather", "Weather", 20, 100),
                ]);

            var byId = config["SectionSettings"]!.AsArray()
                .ToDictionary(e => (string)e!["SectionId"]!, e => (int)e!["OrderIndex"]!);

            Assert.Equal(500, byId["curator-aaaa"]);
            Assert.Equal(100, byId["curator-context-weather"]);
        }

        [Fact]
        public void AnExistingEntryHasItsLaneUpdated()
        {
            var config = JsonNode.Parse("""
            {"SectionSettings":[{"SectionId":"curator-aaaa","OrderIndex":500,"Enabled":true,"LowerLimit":1,"UpperLimit":1}]}
            """)!;

            var changed = SectionConfigMerger.MergeSectionSettings(
                config, [new DesiredSection("curator-aaaa", "Category", 12, 220)]);

            Assert.True(changed);
            Assert.Equal(220, (int)config["SectionSettings"]![0]!["OrderIndex"]!);
        }

    }
}
