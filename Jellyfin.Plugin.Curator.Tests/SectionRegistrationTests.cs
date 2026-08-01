using System;
using System.Linq;
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
                CategoryId,
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
        public void BuildPayload_RejectsAnUnnamedResultsTarget()
        {
            var section = new DesiredSection(SectionConfigMerger.SectionIdFor(CategoryId), "Anything");

            Assert.Throws<ArgumentException>(() => SectionRegistration.BuildPayload(section, CategoryId, string.Empty, "Type"));
            Assert.Throws<ArgumentException>(() => SectionRegistration.BuildPayload(section, CategoryId, "Assembly", string.Empty));
        }
    }
}
