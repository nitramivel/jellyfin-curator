using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Curator.Core.HomeScreen;
using Jellyfin.Plugin.Curator.Services.HomeScreen;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Pins the registration Curator hands to Home Screen Sections.
    ///
    /// <para>
    /// Everything asserted here is read back by another plugin through reflection,
    /// which is exactly the kind of coupling that breaks silently: a renamed method
    /// or a mistyped key produces a row that registers cleanly and then renders
    /// nothing, with no error anywhere. These tests are the only place that
    /// mismatch can be caught without a server.
    /// </para>
    /// </summary>
    public class SectionRegistrationTests
    {
        private static readonly Guid CategoryId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        private static JsonObject Build(string name = "Quietly Devastating Portraits", int members = 10)
        {
            var section = new DesiredSection(SectionConfigMerger.SectionIdFor(CategoryId), name, members);
            var json = SectionRegistration.BuildPayload(
                section,
                CategoryId.ToString("N"),
                "Jellyfin.Plugin.Curator, Version=1.0.0.0",
                "Jellyfin.Plugin.Curator.Services.HomeScreen.CuratorSectionResults");

            return JsonNode.Parse(json)!.AsObject();
        }

        [Fact]
        public void BuildPayload_UsesTheCamelCaseKeysTheOtherPluginReads()
        {
            var payload = Build();

            Assert.Equal(
                ["id", "displayText", "limit", "additionalData", "resultsAssembly", "resultsClass", "resultsMethod"],
                payload.Select(pair => pair.Key).ToArray());
        }

        [Fact]
        public void BuildPayload_IdentifiesTheCategoryByGuidNotByName()
        {
            // The whole reason for owning the row: Collection Sections could only be
            // told a playlist name, and six viewers share one.
            var payload = Build();

            Assert.Equal(CategoryId.ToString("N"), (string?)payload["additionalData"]);
            Assert.Equal(SectionConfigMerger.SectionIdFor(CategoryId), (string?)payload["id"]);
        }

        [Fact]
        public void BuildPayload_LimitIsOneInstance_NotTheItemCount()
        {
            // Limit is how many copies of the section to create, not how many cards
            // it holds. A category is one row however many members it has.
            var payload = Build(members: 25);

            Assert.Equal(1, (int?)payload["limit"]);
        }

        [Fact]
        public void BuildPayload_DisplayTextIsTheCategoryName()
        {
            Assert.Equal("Comfort Rewatch Sitcoms", (string?)Build("Comfort Rewatch Sitcoms")["displayText"]);
        }

        [Fact]
        public void ResultsMethod_ExistsOnTheHandler_AndIsNotOverloaded()
        {
            // Home Screen Sections resolves this with Type.GetMethod(string), which
            // throws on an overload and returns null on a rename. Either way the row
            // goes quiet rather than failing loudly, so it is pinned here.
            var handler = typeof(CuratorSectionResults);
            var matches = handler.GetMethods().Where(m => m.Name == SectionRegistration.ResultsMethodName).ToArray();

            var method = Assert.Single(matches);
            Assert.True(method.IsPublic);

            var parameter = Assert.Single(method.GetParameters());
            Assert.Equal(typeof(CuratorSectionPayload), parameter.ParameterType);
        }

        [Fact]
        public void ResultsClassAndAssembly_MatchTheHandlerTheOtherPluginWillLookUp()
        {
            // Resolved by exact string comparison on the far side.
            var payload = Build();

            Assert.Equal(typeof(CuratorSectionResults).FullName, (string?)payload["resultsClass"]);
            Assert.Equal(SectionRegistration.ResultsMethodName, (string?)payload["resultsMethod"]);
        }

        [Fact]
        public void PayloadFields_MatchWhatTheOtherPluginSends()
        {
            // Deserialized by Newtonsoft from that plugin's own payload object, so
            // the names have to line up. Nothing else is passed to a row.
            var properties = typeof(CuratorSectionPayload).GetProperties().Select(p => p.Name).ToArray();

            Assert.Contains("UserId", properties);
            Assert.Contains("AdditionalData", properties);
        }

        [Fact]
        public void BuildPayload_CarriesANonCategoryRowKeyUntouched()
        {
            // The context rows have no category and no playlist, so additionalData
            // names which of the two the row is instead of a GUID. It must go over
            // the wire exactly as the handler expects to read it back.
            var section = new DesiredSection(SectionConfigMerger.WeatherSectionId, "Picks for the Weather", 20);

            var payload = JsonNode.Parse(SectionRegistration.BuildPayload(
                section,
                CuratorContextSectionResults.WeatherRowKey,
                "Jellyfin.Plugin.Curator, Version=1.0.0.0",
                typeof(CuratorContextSectionResults).FullName!))!.AsObject();

            Assert.Equal(CuratorContextSectionResults.WeatherRowKey, (string?)payload["additionalData"]);
            Assert.Equal(SectionConfigMerger.WeatherSectionId, (string?)payload["id"]);
            Assert.Equal(
                "Jellyfin.Plugin.Curator.Services.HomeScreen.CuratorContextSectionResults",
                (string?)payload["resultsClass"]);
        }

        [Fact]
        public void ContextSectionIdsCarryTheCuratorPrefixAndCannotCollideWithACategory()
        {
            // The prefix is what makes every merge in SectionConfigMerger treat these
            // as ours — including the one that removes stale entries. A context row
            // left behind in Collection Sections' config would race the registration.
            Assert.StartsWith(
                SectionConfigMerger.SectionIdPrefix, SectionConfigMerger.WeatherSectionId, StringComparison.Ordinal);
            Assert.StartsWith(
                SectionConfigMerger.SectionIdPrefix, SectionConfigMerger.DaypartSectionId, StringComparison.Ordinal);
            Assert.NotEqual(SectionConfigMerger.WeatherSectionId, SectionConfigMerger.DaypartSectionId);

            // A category ID is 32 hex characters; neither of these is valid hex.
            Assert.NotEqual(SectionConfigMerger.WeatherSectionId, SectionConfigMerger.SectionIdFor(CategoryId));
        }

        [Fact]
        public void TheContextHandlerDeclaresExactlyOnePublicGetResults()
        {
            // Home Screen Sections resolves it with Type.GetMethod(string), which
            // THROWS on an overload. This is why the context rows are a second class
            // rather than a second method on the existing one.
            var methods = typeof(CuratorContextSectionResults)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == SectionRegistration.ResultsMethodName)
                .ToList();

            Assert.Single(methods);
            Assert.Single(methods[0].GetParameters());
            Assert.Equal(typeof(CuratorSectionPayload), methods[0].GetParameters()[0].ParameterType);
        }

        [Fact]
        public void TheTwoContextRowKeysAreDistinct()
        {
            Assert.NotEqual(
                CuratorContextSectionResults.WeatherRowKey,
                CuratorContextSectionResults.DaypartRowKey);
        }

        [Fact]
        public void BuildPayload_RejectsAnUnnamedResultsTarget()
        {
            var section = new DesiredSection(SectionConfigMerger.SectionIdFor(CategoryId), "Anything");

            Assert.Throws<ArgumentException>(() => SectionRegistration.BuildPayload(section, CategoryId.ToString("N"), string.Empty, "Type"));
            Assert.Throws<ArgumentException>(() => SectionRegistration.BuildPayload(section, CategoryId.ToString("N"), "Assembly", string.Empty));
        }
    }
}
