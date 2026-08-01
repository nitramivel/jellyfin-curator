using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Configuration;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Models;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// The separation between "how many a run may propose" and "how many the store
    /// keeps". They used to be one number, which capped the collection at a single
    /// pass's worth: every run that proposed a full set deleted something to make
    /// room, and a category removed by the cap loses its identity and comes back as
    /// a new row (hard rule 7). Measured on one run with the two tied — 35 pruned,
    /// 21 renamed, 49 held on grace.
    /// </summary>
    public class CategoryRetentionCapTests
    {
        /// <summary>
        /// A live row: a category holding a playlist somebody can actually see, which
        /// is the kind retention spends last.
        /// </summary>
        private static CategoryDefinition Live(string name, Guid? owner, int age)
        {
            var category = new CategoryDefinition
            {
                Id = Guid.NewGuid(),
                Name = name,
                OwnerUserId = owner,
                UpdatedAt = DateTime.UtcNow.AddDays(-age),
            };
            category.GetOrAddLink(owner ?? Guid.NewGuid()).PlaylistId = Guid.NewGuid();
            return category;
        }

        private static CategoryDefinition Shared(int age)
            => Live("shared " + age.ToString(System.Globalization.CultureInfo.InvariantCulture), null, age);

        private static CategoryDefinition Personal(Guid owner, int age)
            => Live("personal " + age.ToString(System.Globalization.CultureInfo.InvariantCulture), owner, age);

        [Fact]
        public void ByDefaultTheStoreCapStillFollowsThePerRunCap()
        {
            // Back-compat: nothing stored for the new settings, so an upgrading
            // install keeps behaving exactly as it did.
            var config = new PluginConfiguration { MaxSharedCategories = 15, MaxPersonalCategories = 12 };

            Assert.Equal(15, config.EffectiveStoredSharedCategories);
            Assert.Equal(12, config.EffectiveStoredPersonalCategories);
        }

        [Fact]
        public void TheStoreCapCanBeRaisedAboveThePerRunCap()
        {
            // The point of the setting: propose 15 a run, accumulate a library of 40.
            var config = new PluginConfiguration
            {
                MaxSharedCategories = 15,
                MaxPersonalCategories = 12,
                MaxStoredSharedCategories = 40,
                MaxStoredPersonalCategories = 30,
            };

            Assert.Equal(40, config.EffectiveStoredSharedCategories);
            Assert.Equal(30, config.EffectiveStoredPersonalCategories);
        }

        [Fact]
        public void RaisingTheStoreCapStopsARunFullOfProposalsDeletingAnything()
        {
            // 15 proposed a run against a store of 40: three runs' worth can coexist,
            // so nothing is pruned and no category loses its identity.
            var config = new PluginConfiguration
            {
                MaxSharedCategories = 15,
                MaxStoredSharedCategories = 40,
            };
            var stored = Enumerable.Range(0, 30).Select(Shared).ToList();

            var doomed = CategoryRetention.SelectForRemoval(
                stored,
                config.EffectiveStoredSharedCategories,
                config.EffectiveStoredPersonalCategories);

            Assert.Empty(doomed);
        }

        [Fact]
        public void WithTheCapsTiedTheSameStoreLosesTheOverflow()
        {
            // The old behaviour, kept as the contrast: 30 stored against a per-run
            // cap of 15 means half the library is deleted every run.
            var config = new PluginConfiguration { MaxSharedCategories = 15 };
            var stored = Enumerable.Range(0, 30).Select(Shared).ToList();

            var doomed = CategoryRetention.SelectForRemoval(
                stored,
                config.EffectiveStoredSharedCategories,
                config.EffectiveStoredPersonalCategories);

            Assert.Equal(15, doomed.Count);
        }

        [Fact]
        public void ThePersonalStoreCapIsAppliedPerViewerNotAcrossAllOfThem()
        {
            // Two viewers with 20 categories each against a store cap of 25 is 40
            // definitions in total and nothing over the cap — the pools are separate.
            var config = new PluginConfiguration
            {
                MaxPersonalCategories = 12,
                MaxStoredPersonalCategories = 25,
            };
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var stored = new List<CategoryDefinition>();
            stored.AddRange(Enumerable.Range(0, 20).Select(i => Personal(a, i)));
            stored.AddRange(Enumerable.Range(0, 20).Select(i => Personal(b, i)));

            var doomed = CategoryRetention.SelectForRemoval(
                stored,
                config.EffectiveStoredSharedCategories,
                config.EffectiveStoredPersonalCategories);

            Assert.Empty(doomed);
        }

        [Fact]
        public void AStoreCapOfZeroInheritsRatherThanMeaningNoCap()
        {
            // 0 means inherit on this setting, while 0 on the per-run cap means no cap
            // — so inheriting an uncapped per-run number is itself uncapped.
            var tied = new PluginConfiguration { MaxSharedCategories = 15, MaxStoredSharedCategories = 0 };
            Assert.Equal(15, tied.EffectiveStoredSharedCategories);

            var uncapped = new PluginConfiguration { MaxSharedCategories = 0, MaxStoredSharedCategories = 0 };
            Assert.Equal(0, uncapped.EffectiveStoredSharedCategories);
            Assert.Empty(CategoryRetention.SelectForRemoval(
                [.. Enumerable.Range(0, 50).Select(Shared)],
                uncapped.EffectiveStoredSharedCategories,
                uncapped.EffectiveStoredPersonalCategories));
        }

        [Fact]
        public void AStoredConfigWithoutTheNewSettingsKeepsItsCurrentBehaviour()
        {
            const string Xml =
                """
                <?xml version="1.0" encoding="utf-8"?>
                <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
                  <MaxSharedCategories>15</MaxSharedCategories>
                  <MaxPersonalCategories>12</MaxPersonalCategories>
                </PluginConfiguration>
                """;

            using var reader = new System.IO.StringReader(Xml);
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
            var config = (PluginConfiguration)serializer.Deserialize(reader)!;

            Assert.Equal(0, config.MaxStoredSharedCategories);
            Assert.Equal(15, config.EffectiveStoredSharedCategories);
            Assert.Equal(12, config.EffectiveStoredPersonalCategories);
        }
    }
}
