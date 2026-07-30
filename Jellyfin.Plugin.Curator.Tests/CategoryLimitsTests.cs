using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
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
    }
}
