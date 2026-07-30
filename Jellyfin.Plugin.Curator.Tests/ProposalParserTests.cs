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

        /// <summary>
        /// The shape that cost two users their playlists on the first working run:
        /// a fenced object followed by commentary. Taking the last '}' in the buffer
        /// swallowed the trailing text and failed mid-parse.
        /// </summary>
        [Fact]
        public void Parse_TrailingProseAfterJson_IsIgnored()
        {
            var batch = Batch(3);
            const string response =
                """
                ```json
                {"categories":[{"name":"Quietly Devastating","description":"Grief that sneaks up.","members":[0,1,2]}]}
                ```

                I focused on emotional register rather than genre. Let me know if you'd
                like these regrouped -> perhaps by decade instead?
                """;

            var result = ProposalParser.Parse(response, batch);

            var proposal = Assert.Single(result.Proposals);
            Assert.Equal("Quietly Devastating", proposal.Name);
            Assert.Equal([batch[0].Id, batch[1].Id, batch[2].Id], proposal.Members);
        }

        [Fact]
        public void Parse_TrailingObjectAfterJson_IsIgnored()
        {
            var result = ProposalParser.Parse(
                """
                {"categories":[{"name":"Vibes","members":[0]}]}
                Some notes: {"unrelated": true}
                """,
                Batch(1));

            Assert.Single(result.Proposals);
        }

        /// <summary>
        /// A brace inside a description must not terminate the scan early.
        /// </summary>
        [Fact]
        public void Parse_BraceInsideStringLiteral_DoesNotTruncateTheObject()
        {
            var result = ProposalParser.Parse(
                """{"categories":[{"name":"Curly","description":"Uses } and { in prose","members":[0]}]}""",
                Batch(1));

            var proposal = Assert.Single(result.Proposals);
            Assert.Equal("Uses } and { in prose", proposal.Description);
        }

        [Fact]
        public void Parse_EscapedQuoteBeforeBrace_IsHandled()
        {
            var result = ProposalParser.Parse(
                """{"categories":[{"name":"Quote","description":"He said \"stop\" }","members":[0]}]}""",
                Batch(1));

            Assert.Equal("""He said "stop" }""", Assert.Single(result.Proposals).Description);
        }

        /// <summary>
        /// An object cut off by the output-token cap has an opening brace but never
        /// closes; that must be a clean FormatException, not a silent partial parse.
        /// </summary>
        [Fact]
        public void Parse_UnterminatedObject_Throws()
        {
            Assert.Throws<FormatException>(() => ProposalParser.Parse(
                """{"categories":[{"name":"Cut off here","members":[0,1""",
                Batch(2)));
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

        // ---- viewer pass ----

        [Fact]
        public void ParsePersonal_ReturnsNewCategories()
        {
            var batch = Batch(3);
            const string response =
                """
                {"categories":[{"name":"Sunday Afternoon Rewatch","description":"Worn smooth.","members":[0,1,2]}]}
                """;

            var result = ProposalParser.ParsePersonal(response, batch);

            Assert.Equal("Sunday Afternoon Rewatch", Assert.Single(result.Proposals).Name);
        }

        /// <summary>
        /// Shared categories go to every viewer, so the viewer pass no longer asks
        /// which ones they want. A model that volunteers a "selected" array anyway —
        /// an older prompt cached upstream, or a model padding to a shape it has seen
        /// before — must be ignored rather than throwing the batch away.
        /// </summary>
        [Fact]
        public void ParsePersonal_IgnoresAnUnsolicitedSelectedArray()
        {
            var result = ProposalParser.ParsePersonal(
                """{"selected":["Cerebral Sci-Fi"],"categories":[{"name":"Invented","members":[0]}]}""",
                Batch(1));

            Assert.Equal("Invented", Assert.Single(result.Proposals).Name);
        }

        /// <summary>
        /// A viewer with no recorded history at all is told to return an empty list,
        /// so an absent or empty categories array is a valid answer — not a parse
        /// error.
        /// </summary>
        [Fact]
        public void ParsePersonal_NoNewCategories_IsValid()
        {
            Assert.Empty(ProposalParser.ParsePersonal("""{}""", Batch(1)).Proposals);
            Assert.Empty(ProposalParser.ParsePersonal("""{"categories":[]}""", Batch(1)).Proposals);
        }

        [Fact]
        public void ParsePersonal_InventedCategory_StillCannotReferenceItemsOutsideTheBatch()
        {
            var batch = Batch(2);

            var result = ProposalParser.ParsePersonal(
                """{"categories":[{"name":"Invented","members":[0,1,99]}]}""",
                batch);

            var proposal = Assert.Single(result.Proposals);
            Assert.Equal([batch[0].Id, batch[1].Id], proposal.Members);
            Assert.Equal(1, result.DiscardedMemberCount);
        }

        [Fact]
        public void ParsePersonal_MalformedResponse_Throws()
        {
            Assert.Throws<FormatException>(() =>
                ProposalParser.ParsePersonal("no json here", Batch(1)));
            Assert.Throws<FormatException>(() =>
                ProposalParser.ParsePersonal("[1,2,3]", Batch(1)));
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
