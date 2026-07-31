using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Curator.Configuration;
using Jellyfin.Plugin.Curator.Core.Llm;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Core.Reconciliation;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// The contract between what the model is told and what is enforced on its
    /// answer.
    ///
    /// This project has shipped that contract broken twice, in opposite
    /// directions — a prompt asking for 3-member categories against a filter
    /// demanding 6, and a filter capping at 8 categories against a prompt naming
    /// no target at all. Both were invisible in the code because the numbers were
    /// passed separately to the prompt and to the Reconciler. These tests read the
    /// numbers back out of the generated prompt and check them against what the
    /// Reconciler actually does with the same limits.
    /// </summary>
    public class CategoryLimitsTests
    {
        private const int SliceSize = 60;

        private static readonly Guid[] Pool =
            [.. Enumerable.Range(0, 30 * SliceSize).Select(_ => Guid.NewGuid())];

        /// <summary>
        /// A proposal with members nothing else shares and a name nothing else
        /// resembles — the Reconciler merges on either, and a fixture that trips
        /// those would measure clustering rather than the limits under test.
        /// </summary>
        private static CategoryProposal Proposal(int index, int members)
            => new()
            {
                Name = new string((char)('A' + (index % 26)), 4 + (index / 26)),
                Members = [.. Pool.Skip(index * SliceSize).Take(members)],
            };

        /// <summary>Pulls "between 6 and 20 members" or "at least 6 members" out of a prompt.</summary>
        private static (int Floor, int? Ceiling) ReadMemberRange(string prompt)
        {
            var between = Regex.Match(prompt, @"between (\d+) and (\d+) members");
            if (between.Success)
            {
                return (
                    int.Parse(between.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(between.Groups[2].Value, CultureInfo.InvariantCulture));
            }

            var atLeast = Regex.Match(prompt, @"at least (\d+) members");
            Assert.True(atLeast.Success, "prompt states no member requirement at all");
            return (int.Parse(atLeast.Groups[1].Value, CultureInfo.InvariantCulture), null);
        }

        /// <summary>Pulls "up to 10 categories" out of a prompt, or null when uncapped.</summary>
        private static int? ReadCategoryCap(string prompt)
        {
            var match = Regex.Match(prompt, @"up to (\d+) categories");
            return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        }

        public static TheoryData<int, int, int> LimitCombinations => new()
        {
            { 6, 20, 10 },   // the shipped defaults
            { 2, 20, 6 },    // the personal pool
            { 4, 0, 8 },     // no member ceiling
            { 6, 20, 0 },    // no category cap
            { 3, 3, 5 },     // ceiling equal to floor — must be dropped, not honoured
            { 1, 40, 12 },   // floor below the hard minimum
            { 0, 0, 0 },     // everything off
        };

        [Theory]
        [MemberData(nameof(LimitCombinations))]
        public void TheMemberFloorInThePromptIsTheFloorTheReconcilerApplies(int min, int max, int cap)
        {
            var limits = new CategoryLimits(min, max, cap);
            var (floor, _) = ReadMemberRange(PromptBuilder.BuildSystemPrompt(limits));

            // One member below the stated floor must not survive; one at it must.
            var justUnder = Reconciler.Reconcile(
                [Proposal(0, floor - 1)],
                new ReconcilerSettings(limits));
            var exactly = Reconciler.Reconcile(
                [Proposal(0, floor)],
                new ReconcilerSettings(limits));

            Assert.Empty(justUnder);
            Assert.Single(exactly);
        }

        [Theory]
        [MemberData(nameof(LimitCombinations))]
        public void TheMemberCeilingInThePromptIsTheCeilingTheReconcilerApplies(int min, int max, int cap)
        {
            var limits = new CategoryLimits(min, max, cap);
            var (_, ceiling) = ReadMemberRange(PromptBuilder.BuildSystemPrompt(limits));

            var result = Reconciler.Reconcile(
                [Proposal(0, 50)],
                new ReconcilerSettings(limits));

            var built = Assert.Single(result);
            if (ceiling is { } stated)
            {
                Assert.Equal(stated, built.Members.Count);
            }
            else
            {
                // No ceiling stated means none applied — nothing may be trimmed.
                Assert.Equal(50, built.Members.Count);
            }
        }

        [Theory]
        [MemberData(nameof(LimitCombinations))]
        public void TheCategoryCapInThePromptIsTheCapTheReconcilerApplies(int min, int max, int cap)
        {
            var limits = new CategoryLimits(min, max, cap);
            var stated = ReadCategoryCap(PromptBuilder.BuildSystemPrompt(limits));

            var proposals = Enumerable.Range(0, 20)
                .Select(i => Proposal(i, 45 - i))
                .ToArray();

            var result = Reconciler.Reconcile(proposals, new ReconcilerSettings(limits));

            if (stated is { } capInPrompt)
            {
                Assert.True(
                    result.Count <= capInPrompt,
                    $"prompt promised at most {capInPrompt} categories, Reconciler returned {result.Count}");
                Assert.Equal(capInPrompt, result.Count);
            }
            else
            {
                Assert.True(result.Count > 0);
            }
        }

        [Theory]
        [MemberData(nameof(LimitCombinations))]
        public void BothPromptsDescribeTheirOwnLimits(int min, int max, int cap)
        {
            var limits = new CategoryLimits(min, max, cap);

            // The per-viewer prompt is a different template and has drifted from
            // the discovery one before; hold it to the same contract.
            Assert.Equal(
                ReadMemberRange(PromptBuilder.BuildSystemPrompt(limits)),
                ReadMemberRange(PromptBuilder.BuildPersonalSystemPrompt(limits)));
            Assert.Equal(
                ReadCategoryCap(PromptBuilder.BuildSystemPrompt(limits)),
                ReadCategoryCap(PromptBuilder.BuildPersonalSystemPrompt(limits)));
        }

        [Fact]
        public void AMemberCeilingAtOrBelowTheFloorIsDropped()
        {
            // Honouring it would tell the model "between 6 and 4" and leave the
            // Reconciler trimming every category below the size it just demanded,
            // which empties the run.
            Assert.Equal(0, new CategoryLimits(6, 4, 10).EffectiveMaxMembers);
            Assert.Equal(0, new CategoryLimits(6, 6, 10).EffectiveMaxMembers);
            Assert.Equal(20, new CategoryLimits(6, 20, 10).EffectiveMaxMembers);
        }

        [Fact]
        public void TheFloorIsNeverBelowTwo()
        {
            Assert.Equal(2, new CategoryLimits(1).EffectiveMinMembers);
            Assert.Equal(2, new CategoryLimits(0).EffectiveMinMembers);
            Assert.Equal(2, new CategoryLimits(-5).EffectiveMinMembers);
            Assert.Equal(9, new CategoryLimits(9).EffectiveMinMembers);
        }

        [Fact]
        public void ZeroMeansNoCapNotACapOfZero()
        {
            Assert.False(new CategoryLimits(6, 0, 0).HasCategoryCap);
            Assert.True(new CategoryLimits(6, 0, 1).HasCategoryCap);

            var result = Reconciler.Reconcile(
                [Proposal(0, 10), Proposal(1, 9), Proposal(2, 8)],
                new ReconcilerSettings(new CategoryLimits(2, 0, 0)));

            Assert.Equal(3, result.Count);
        }

        // ---- the per-pool size range, and what 0 means on each setting ----

        [Fact]
        public void EachPoolCeiling_InheritsTheSharedFallbackUntilItIsSet()
        {
            // The back-compat case, and the only one an existing install starts in:
            // both per-pool ceilings at 0, so both pools keep running on exactly the
            // number that was already saved.
            var config = new PluginConfiguration
            {
                MaxCategoryMembers = 20,
                MaxSharedCategorySize = 0,
                MaxPersonalCategorySize = 0,
            };

            Assert.Equal(20, config.EffectiveSharedCategorySize);
            Assert.Equal(20, config.EffectivePersonalCategorySize);
        }

        [Fact]
        public void EachPoolCeiling_OverridesTheFallbackIndependently()
        {
            // The point of the setting: a thread through the whole library can carry
            // thirty items where one drawn from a single viewer's history cannot.
            var config = new PluginConfiguration
            {
                MaxCategoryMembers = 20,
                MaxSharedCategorySize = 30,
                MaxPersonalCategorySize = 12,
            };

            Assert.Equal(30, config.EffectiveSharedCategorySize);
            Assert.Equal(12, config.EffectivePersonalCategorySize);
        }

        [Fact]
        public void OnePoolMayOverrideWhileTheOtherStillInherits()
        {
            var config = new PluginConfiguration
            {
                MaxCategoryMembers = 20,
                MaxSharedCategorySize = 0,
                MaxPersonalCategorySize = 8,
            };

            Assert.Equal(20, config.EffectiveSharedCategorySize);
            Assert.Equal(8, config.EffectivePersonalCategorySize);
        }

        [Fact]
        public void ZeroOnTheFallback_IsInheritedAsNoLimitNotAsACeilingOfZero()
        {
            // 0 means two different things one line apart — inherit on the per-pool
            // settings, no limit on the fallback — so pin the composition: a pool
            // inheriting from an unlimited fallback is itself unlimited, and must not
            // come out as a category trimmed to nothing.
            var config = new PluginConfiguration
            {
                MaxCategoryMembers = 0,
                MaxSharedCategorySize = 0,
                MaxPersonalCategorySize = 0,
            };

            Assert.Equal(0, config.EffectiveSharedCategorySize);
            Assert.Equal(0, config.EffectivePersonalCategorySize);
            Assert.Equal(0, new CategoryLimits(4, config.EffectiveSharedCategorySize).EffectiveMaxMembers);
        }

        [Fact]
        public void APoolCeiling_MayExceedAnUnlimitedFallback()
        {
            // No limit overall, but this pool capped — expressible, and the direction
            // someone reaches for after finding one pool's rows too long.
            var config = new PluginConfiguration
            {
                MaxCategoryMembers = 0,
                MaxSharedCategorySize = 0,
                MaxPersonalCategorySize = 10,
            };

            Assert.Equal(0, config.EffectiveSharedCategorySize);
            Assert.Equal(10, config.EffectivePersonalCategorySize);
        }

        [Fact]
        public void ThePerPoolCeiling_ReachesTheModelAsTheRangeItWillBeJudgedBy()
        {
            // The contract this whole class exists to protect, applied to the new
            // setting: the ceiling the owner typed is the ceiling in the sentence.
            var config = new PluginConfiguration
            {
                MaxCategoryMembers = 20,
                MaxPersonalCategorySize = 12,
                MinPersonalCategorySize = 4,
            };
            var limits = new CategoryLimits(
                config.MinPersonalCategorySize,
                config.EffectivePersonalCategorySize);

            var prompt = PromptBuilder.BuildPersonalSystemPrompt(limits);

            Assert.Contains("between 4 and 12 members", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void AStoredConfigWithoutTheNewSettings_TakesTheNewCeilingButKeepsItsStoredFloor()
        {
            // The asymmetry an upgrade actually lands in, pinned because it is
            // surprising. Stored values beat code defaults, and only the floor was
            // ever stored — so a config written before these settings existed keeps
            // its saved floor and picks up the new ceiling, landing on a range the
            // owner chose neither end of until they open the page and save.
            // The ceiling defaults to a real number rather than to 0-inherit because
            // the two boxes are meant to read as "between 6 and 25 items"; a box
            // showing 0 does not say that.
            const string Xml =
                """
                <?xml version="1.0" encoding="utf-8"?>
                <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
                  <MinSharedCategorySize>4</MinSharedCategorySize>
                  <MinPersonalCategorySize>4</MinPersonalCategorySize>
                  <MaxCategoryMembers>20</MaxCategoryMembers>
                </PluginConfiguration>
                """;

            using var reader = new System.IO.StringReader(Xml);
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
            var config = (PluginConfiguration)serializer.Deserialize(reader)!;

            // Floor: stored, so it survives the upgrade untouched.
            Assert.Equal(4, config.MinSharedCategorySize);
            Assert.Equal(4, config.MinPersonalCategorySize);

            // Ceiling: never stored, so the new default applies immediately and the
            // old MaxCategoryMembers of 20 stops governing either pool.
            Assert.Equal(25, config.MaxSharedCategorySize);
            Assert.Equal(25, config.MaxPersonalCategorySize);
            Assert.Equal(25, config.EffectiveSharedCategorySize);
            Assert.Equal(25, config.EffectivePersonalCategorySize);
        }

        [Fact]
        public void TheShippedDefaultRangeIsSixToTwentyFive()
        {
            var config = new PluginConfiguration();

            Assert.Equal(6, config.MinSharedCategorySize);
            Assert.Equal(6, config.MinPersonalCategorySize);
            Assert.Equal(25, config.EffectiveSharedCategorySize);
            Assert.Equal(25, config.EffectivePersonalCategorySize);

            // And the range the model is actually told, both pools.
            Assert.Contains(
                "between 6 and 25 members",
                PromptBuilder.BuildSystemPrompt(
                    new CategoryLimits(config.MinSharedCategorySize, config.EffectiveSharedCategorySize)),
                StringComparison.Ordinal);
            Assert.Contains(
                "between 6 and 25 members",
                PromptBuilder.BuildPersonalSystemPrompt(
                    new CategoryLimits(config.MinPersonalCategorySize, config.EffectivePersonalCategorySize)),
                StringComparison.Ordinal);
        }
    }
}
