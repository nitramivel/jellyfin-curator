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
    }
}
