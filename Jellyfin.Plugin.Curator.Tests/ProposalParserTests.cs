using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Llm;
using Jellyfin.Plugin.Curator.Core.Models;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    public class ProposalParserTests
    {
        private static IReadOnlyList<MediaItemRecord> Batch(int count) => BatcherTests.MakeRecords(count);

        [Fact]
        public void Parse_ValidResponse_MapsIndexesToGuids()
        {
            var batch = Batch(5);
            const string response =
                """{"categories":[{"name":"Cerebral Sci-Fi","description":"Thinky ones.","members":[2,0,4]}]}""";

            var result = ProposalParser.Parse(response, batch);

            var proposal = Assert.Single(result.Proposals);
            Assert.Equal("Cerebral Sci-Fi", proposal.Name);
            Assert.Equal("Thinky ones.", proposal.Description);
            Assert.Equal([batch[2].Id, batch[0].Id, batch[4].Id], proposal.Members);
            Assert.Equal(0, result.DiscardedMemberCount);
            Assert.Equal(0, result.DiscardedCategoryCount);
        }

        [Fact]
        public void Parse_OutOfRangeIndexes_AreDiscarded()
        {
            var batch = Batch(3);
            const string response =
                """{"categories":[{"name":"Vibes","members":[0,7,-1,2]}]}""";

            var result = ProposalParser.Parse(response, batch);

            var proposal = Assert.Single(result.Proposals);
            Assert.Equal([batch[0].Id, batch[2].Id], proposal.Members);
            Assert.Equal(2, result.DiscardedMemberCount);
        }

        [Fact]
        public void Parse_DuplicateIndexes_AreDeduplicatedPreservingOrder()
        {
            var batch = Batch(3);
            const string response =
                """{"categories":[{"name":"Vibes","members":[1,1,0,1]}]}""";

            var result = ProposalParser.Parse(response, batch);

            Assert.Equal([batch[1].Id, batch[0].Id], result.Proposals[0].Members);
            Assert.Equal(2, result.DiscardedMemberCount);
        }

        [Fact]
        public void Parse_NonNumericMembers_AreDiscarded()
        {
            var batch = Batch(3);
            const string response =
                """{"categories":[{"name":"Vibes","members":["The Matrix",1.5,0]}]}""";

            var result = ProposalParser.Parse(response, batch);

            Assert.Equal([batch[0].Id], result.Proposals[0].Members);
            Assert.Equal(2, result.DiscardedMemberCount);
        }

        [Fact]
        public void Parse_CategoryWithNoValidMembers_IsDropped()
        {
            var batch = Batch(2);
            const string response =
                """{"categories":[{"name":"Ghost","members":[9,10]},{"name":"Real","members":[0]}]}""";

            var result = ProposalParser.Parse(response, batch);

            Assert.Equal("Real", Assert.Single(result.Proposals).Name);
            Assert.Equal(1, result.DiscardedCategoryCount);
        }

        [Fact]
        public void Parse_NamelessCategory_IsDropped()
        {
            var batch = Batch(2);
            const string response =
                """{"categories":[{"description":"no name","members":[0]},{"name":"  ","members":[0]}]}""";

            var result = ProposalParser.Parse(response, batch);

            Assert.Empty(result.Proposals);
            Assert.Equal(2, result.DiscardedCategoryCount);
        }

        [Fact]
        public void Parse_CodeFencedResponse_IsAccepted()
        {
            var batch = Batch(2);
            const string response =
                """
                Here you go!
                ```json
                {"categories":[{"name":"Vibes","members":[0,1]}]}
                ```
                """;

            var result = ProposalParser.Parse(response, batch);

            Assert.Single(result.Proposals);
        }

        [Fact]
        public void Parse_MissingDescription_DefaultsToEmpty()
        {
            var result = ProposalParser.Parse(
                """{"categories":[{"name":"Vibes","members":[0]}]}""",
                Batch(1));

            Assert.Equal(string.Empty, result.Proposals[0].Description);
        }

        [Fact]
        public void Parse_EmptyCategoriesArray_YieldsNoProposals()
        {
            var result = ProposalParser.Parse("""{"categories":[]}""", Batch(1));

            Assert.Empty(result.Proposals);
        }

        [Theory]
        [InlineData("not json at all")]
        [InlineData("{\"categories\": \"nope\"}")]
        [InlineData("{\"wrong\": []}")]
        [InlineData("[1,2,3]")]
        [InlineData("{ broken json")]
        public void Parse_MalformedResponses_Throw(string response)
        {
            Assert.Throws<FormatException>(() => ProposalParser.Parse(response, Batch(1)));
        }

        [Fact]
        public void Parse_LargeIndexOverflowingInt_IsDiscardedNotCrashing()
        {
            var result = ProposalParser.Parse(
                """{"categories":[{"name":"Vibes","members":[99999999999999999999,0]}]}""",
                Batch(1));

            Assert.Equal([Batch(1)[0].Id], result.Proposals.Single().Members);
            Assert.Equal(1, result.DiscardedMemberCount);
        }
    }
}
