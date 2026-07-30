using System;
using System.Linq;
using Jellyfin.Plugin.Curator.Configuration;
using Jellyfin.Plugin.Curator.Core.Llm;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Normalization of the model profile list.
    ///
    /// The case that matters most here is the one nobody sees coming: an install
    /// that has been running for months on the single API key that predates this
    /// list. Its key sits in the legacy scalar fields and nothing else has it. If
    /// migration drops it, the owner's next run fails with a credential error for
    /// a credential they did supply — so the first few tests are about that key
    /// surviving, and surviving exactly once.
    /// </summary>
    public class ModelProfilesTests
    {
        private static PluginConfiguration LegacyConfig() => new()
        {
            Provider = LlmProviderKind.Grok,
            Model = "grok-4.5",
            ApiKey = "xai-secret",
            BaseUrl = string.Empty,
            InputCostPerMillion = 2m,
            OutputCostPerMillion = 6m,
        };

        [Fact]
        public void Normalize_MigratesLegacySettingsIntoOneProfile()
        {
            var result = ModelProfiles.Normalize(LegacyConfig());

            var profile = Assert.Single(result.Profiles);
            Assert.Equal(LlmProviderKind.Grok, profile.Provider);
            Assert.Equal("grok-4.5", profile.Model);
            Assert.Equal("xai-secret", profile.ApiKey);
            Assert.True(result.Changed);
        }

        [Fact]
        public void Normalize_CarriesLegacyPricingOntoTheProfile()
        {
            // Pricing moved onto the profile, so a migration that left it behind
            // would silently report every run at 0 and the owner would think the
            // plugin had become free.
            var profile = Assert.Single(ModelProfiles.Normalize(LegacyConfig()).Profiles);

            Assert.Equal(2m, profile.InputCostPerMillion);
            Assert.Equal(6m, profile.OutputCostPerMillion);
        }

        [Fact]
        public void Normalize_MakesTheMigratedProfileTheDefault()
        {
            var result = ModelProfiles.Normalize(LegacyConfig());

            Assert.Equal(result.Profiles[0].Id, result.DefaultProfileId);
        }

        [Fact]
        public void Normalize_DoesNotReimportLegacySettingsOnceProfilesExist()
        {
            // The legacy fields are frozen at whatever they held on upgrade. Once
            // the owner curates a list, re-importing them every run would resurrect
            // a profile they had deleted — and do it silently, every single run.
            var config = LegacyConfig();
            config.ModelProfiles =
            [
                new ModelProfile { Id = "kept", Name = "Anthropic", Model = "claude-opus-5" },
            ];
            config.DefaultModelProfileId = "kept";

            var result = ModelProfiles.Normalize(config);

            Assert.Equal("kept", Assert.Single(result.Profiles).Id);
            Assert.False(result.Changed);
        }

        [Fact]
        public void Normalize_LeavesANeverConfiguredInstallEmpty()
        {
            // Nothing to migrate. Inventing a blank profile here would put a broken
            // entry in front of the owner and imply they had created it.
            var result = ModelProfiles.Normalize(new PluginConfiguration());

            Assert.Empty(result.Profiles);
            Assert.Equal(string.Empty, result.DefaultProfileId);
        }

        [Fact]
        public void Normalize_AssignsIdsToProfilesThatHaveNone()
        {
            var config = new PluginConfiguration
            {
                ModelProfiles =
                [
                    new ModelProfile { Name = "one", Model = "a" },
                    new ModelProfile { Name = "two", Model = "b" },
                ],
            };

            var result = ModelProfiles.Normalize(config);

            Assert.All(result.Profiles, p => Assert.False(string.IsNullOrWhiteSpace(p.Id)));
            Assert.Equal(2, result.Profiles.Select(p => p.Id).Distinct().Count());
            Assert.True(result.Changed);
        }

        [Fact]
        public void Normalize_BreaksDuplicateIds()
        {
            // What copying a profile looks like. Two rows sharing an id make the
            // default — and every per-task assignment later — ambiguous.
            var config = new PluginConfiguration
            {
                ModelProfiles =
                [
                    new ModelProfile { Id = "same", Name = "one", Model = "a" },
                    new ModelProfile { Id = "same", Name = "two", Model = "b" },
                ],
                DefaultModelProfileId = "same",
            };

            var result = ModelProfiles.Normalize(config);

            Assert.Equal(2, result.Profiles.Select(p => p.Id).Distinct().Count());
            Assert.Equal("same", result.Profiles[0].Id);
            Assert.Equal("same", result.DefaultProfileId);
        }

        [Fact]
        public void Normalize_FallsBackWhenTheDefaultIdNamesADeletedProfile()
        {
            var config = new PluginConfiguration
            {
                ModelProfiles = [new ModelProfile { Id = "b", Name = "kept", Model = "m" }],
                DefaultModelProfileId = "deleted",
            };

            var result = ModelProfiles.Normalize(config);

            Assert.Equal("b", result.DefaultProfileId);
            Assert.True(result.Changed);
        }

        [Fact]
        public void Normalize_NamesAnUnnamedProfileAfterItsProviderAndModel()
        {
            var config = new PluginConfiguration
            {
                ModelProfiles = [new ModelProfile { Id = "a", Provider = LlmProviderKind.Google, Model = "gemini-2.5-flash" }],
            };

            var profile = Assert.Single(ModelProfiles.Normalize(config).Profiles);

            Assert.Equal("Google gemini-2.5-flash", profile.Name);
        }

        [Fact]
        public void Normalize_IsIdempotent()
        {
            // Normalization runs on every read, so a second pass reporting Changed
            // would have the config page and the run path writing repairs forever.
            var config = LegacyConfig();
            var first = ModelProfiles.Normalize(config);

            config.ModelProfiles = [.. first.Profiles];
            config.DefaultModelProfileId = first.DefaultProfileId;

            Assert.False(ModelProfiles.Normalize(config).Changed);
        }

        [Fact]
        public void ResolveDefault_ReturnsTheDefaultProfile()
        {
            var config = new PluginConfiguration
            {
                ModelProfiles =
                [
                    new ModelProfile { Id = "a", Name = "first", Model = "m1" },
                    new ModelProfile { Id = "b", Name = "second", Model = "m2" },
                ],
                DefaultModelProfileId = "b",
            };

            Assert.Equal("second", ModelProfiles.ResolveDefault(config).Name);
        }

        [Fact]
        public void ResolveDefault_ThrowsWhenNothingIsConfigured()
        {
            Assert.Throws<InvalidOperationException>(
                () => ModelProfiles.ResolveDefault(new PluginConfiguration()));
        }

        /// <summary>
        /// A real 0.4.1.0 configuration file, as written by Jellyfin on a live
        /// server, with the key replaced. Deserialized rather than constructed
        /// because the risk being tested is a serializer one: XmlSerializer drops
        /// elements it has no property for, so deleting the legacy fields — or
        /// mistyping the new ones — would throw this key away silently.
        /// </summary>
        private const string LiveConfigXml =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <Provider>Grok</Provider>
              <Model>grok-4.5</Model>
              <ApiKey>xai-live-key</ApiKey>
              <BaseUrl />
              <BatchSize>0</BatchSize>
              <TokenBudget>3999999</TokenBudget>
              <MaxOutputTokens>16000</MaxOutputTokens>
              <InputCostPerMillion>2</InputCostPerMillion>
              <CachedInputCostPerMillion>0</CachedInputCostPerMillion>
              <OutputCostPerMillion>6</OutputCostPerMillion>
              <EnableThinking>true</EnableThinking>
              <TargetUsers />
              <AutoEnableSections>true</AutoEnableSections>
              <UseBatchApi>false</UseBatchApi>
            </PluginConfiguration>
            """;

        private static PluginConfiguration Deserialize(string xml)
        {
            using var reader = new System.IO.StringReader(xml);
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
            return (PluginConfiguration)serializer.Deserialize(reader)!;
        }

        [Fact]
        public void Normalize_MigratesARealPreProfileConfigFile()
        {
            var config = Deserialize(LiveConfigXml);

            var profile = Assert.Single(ModelProfiles.Normalize(config).Profiles);

            Assert.Equal(LlmProviderKind.Grok, profile.Provider);
            Assert.Equal("grok-4.5", profile.Model);
            Assert.Equal("xai-live-key", profile.ApiKey);
            Assert.Equal(2m, profile.InputCostPerMillion);
            Assert.Equal(6m, profile.OutputCostPerMillion);
        }

        [Fact]
        public void ModelProfiles_SurviveAnXmlRoundTrip()
        {
            // Jellyfin persists plugin config through XmlSerializer, which needs a
            // parameterless constructor and public setters. A profile type it cannot
            // serialize would lose every saved key on the next restart.
            var config = Deserialize(LiveConfigXml);
            var normalized = ModelProfiles.Normalize(config);
            config.ModelProfiles = [.. normalized.Profiles];
            config.DefaultModelProfileId = normalized.DefaultProfileId;

            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
            using var writer = new System.IO.StringWriter();
            serializer.Serialize(writer, config);

            var reloaded = Deserialize(writer.ToString());

            var profile = Assert.Single(reloaded.ModelProfiles);
            Assert.Equal("xai-live-key", profile.ApiKey);
            Assert.Equal("grok-4.5", profile.Model);
            Assert.Equal(profile.Id, reloaded.DefaultModelProfileId);
            Assert.False(ModelProfiles.Normalize(reloaded).Changed);
        }

        [Theory]
        [InlineData("a", "first")]
        [InlineData("", "second")]
        [InlineData("since-deleted", "second")]
        public void Resolve_FallsBackToTheDefaultForAnythingUnassignable(string requested, string expected)
        {
            // The contract the per-task model assignment will rely on: a task names
            // the profile it wants, and anything blank or dangling lands on the
            // default rather than failing the run.
            var config = new PluginConfiguration
            {
                ModelProfiles =
                [
                    new ModelProfile { Id = "a", Name = "first", Model = "m1" },
                    new ModelProfile { Id = "b", Name = "second", Model = "m2" },
                ],
                DefaultModelProfileId = "b",
            };

            Assert.Equal(expected, ModelProfiles.Resolve(config, requested).Name);
        }
    }
}
