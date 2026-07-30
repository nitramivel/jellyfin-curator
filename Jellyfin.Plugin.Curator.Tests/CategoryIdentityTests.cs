using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Models;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    public class CategoryIdentityTests
    {
        private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

        /// <summary>A shared category: no owner, linked to whoever selected it.</summary>
        private static CategoryDefinition Shared(string name, params Guid[] users)
            => Definition(name, owner: null, users);

        /// <summary>A category invented for one viewer, belonging to them alone.</summary>
        private static CategoryDefinition Personal(string name, Guid owner)
            => Definition(name, owner, owner);

        private static CategoryDefinition Definition(string name, Guid? owner, params Guid[] users)
        {
            var definition = new CategoryDefinition
            {
                Id = Guid.NewGuid(),
                Name = name,
                OwnerUserId = owner,
            };

            foreach (var user in users)
            {
                definition.UserPlaylists.Add(new UserPlaylistLink { UserId = user, PlaylistId = Guid.NewGuid() });
            }

            return definition;
        }

        /// <summary>
        /// Name-identity lookup: no members, so only the name path can match.
        /// The member path has its own tests below.
        /// </summary>
        private static CategoryDefinition? Match(
            IReadOnlyList<CategoryDefinition> stored,
            string name,
            Guid? scopedUserId)
            => CategoryIdentity.FindMatch(stored, name, [], scopedUserId)?.Definition;

        [Fact]
        public void SharedRun_MatchesByNameRegardlessOfUsers()
        {
            var stored = Shared("Comfort Rewatch", Alice, Bob);

            var match = Match([stored], "Comfort Rewatch", null);

            Assert.Same(stored, match);
        }

        [Fact]
        public void NameMatch_IsCaseInsensitive()
        {
            var stored = Shared("Comfort Rewatch", Alice);

            Assert.Same(stored, Match([stored], "comfort rewatch", null));
        }

        [Fact]
        public void NewCategory_HasNoMatch()
        {
            var stored = Shared("Comfort Rewatch", Alice);

            Assert.Null(Match([stored], "Cerebral Sci-Fi", null));
        }

        [Fact]
        public void PersonalizedRun_MatchesOnlyThatUsersDefinition()
        {
            var aliceCategory = Personal("Comfort Rewatch", Alice);
            var bobCategory = Personal("Comfort Rewatch", Bob);
            var stored = new List<CategoryDefinition> { aliceCategory, bobCategory };

            Assert.Same(aliceCategory, Match(stored, "Comfort Rewatch", Alice));
            Assert.Same(bobCategory, Match(stored, "Comfort Rewatch", Bob));
        }

        [Fact]
        public void PersonalizedRun_OtherUsersCategory_IsNotReused()
        {
            // Alice already has "Comfort Rewatch"; Bob's first personalized run must
            // create his own definition rather than hijacking hers.
            var aliceCategory = Personal("Comfort Rewatch", Alice);

            Assert.Null(Match([aliceCategory], "Comfort Rewatch", Bob));
        }

        [Fact]
        public void EmptyStore_HasNoMatch()
        {
            Assert.Null(Match([], "Anything", null));
            Assert.Null(Match([], "Anything", Alice));
        }

        [Fact]
        public void SharedDefinitionWithoutLinks_IsStillReusedByASharedRun()
        {
            // A shared category whose playlists were all removed (empty-category
            // lifecycle) keeps its definition and must be reused, not duplicated.
            var retired = Shared("Quietly Devastating");

            Assert.Same(retired, Match([retired], "Quietly Devastating", null));
            Assert.Null(Match([retired], "Quietly Devastating", Alice));
        }

        /// <summary>
        /// The distinction the two-phase run depends on: a shared category and a
        /// personal one can carry the same name without colliding.
        /// </summary>
        [Fact]
        public void SharedAndPersonal_WithTheSameName_AreDistinctDefinitions()
        {
            var shared = Shared("Comfort Rewatch", Alice, Bob);
            var alicesOwn = Personal("Comfort Rewatch", Alice);
            var stored = new List<CategoryDefinition> { shared, alicesOwn };

            Assert.Same(shared, Match(stored, "Comfort Rewatch", null));
            Assert.Same(alicesOwn, Match(stored, "Comfort Rewatch", Alice));
            Assert.Null(Match(stored, "Comfort Rewatch", Bob));
        }

        /// <summary>
        /// A user selecting a shared category must NOT be treated as owning it —
        /// otherwise the second selector would fork a duplicate definition.
        /// </summary>
        [Fact]
        public void SelectingASharedCategory_DoesNotMakeItPersonal()
        {
            var shared = Shared("Cerebral Sci-Fi", Alice, Bob);

            Assert.Same(shared, Match([shared], "Cerebral Sci-Fi", null));
            Assert.Null(Match([shared], "Cerebral Sci-Fi", Alice));
        }
    
        // ------------------------------------------------------------------
        // Recognising a renamed category by its members.
        //
        // Measured across three runs, not one category name survived to the next
        // — 0 of 16, then 0 of 33. The model re-derives the same threads and
        // words them differently every time, and every rename used to destroy a
        // home screen row.
        // ------------------------------------------------------------------

        private static Guid[] Items(int count, int offset = 0)
            => [.. Enumerable.Range(offset, count).Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:D12}"))];

        private static CategoryDefinition WithMembers(string name, Guid[] members, Guid? owner = null)
            => new()
            {
                Id = Guid.NewGuid(),
                Name = name,
                OwnerUserId = owner,
                Members = [.. members],
            };

        [Fact]
        public void RenamedCategory_WithTheSameMembers_IsRecognised()
        {
            var stored = WithMembers("Reality Coming Undone", Items(12));

            var match = CategoryIdentity.FindMatch(
                [stored], "Glitch in the Reality Engine", Items(12), scopedUserId: null);

            Assert.NotNull(match);
            Assert.Same(stored, match.Definition);
            Assert.False(match.MatchedByName);
            Assert.Equal(1.0, match.Similarity);
        }

        [Fact]
        public void RenamedCategory_ThatDriftedSomewhat_IsStillRecognised()
        {
            // 12 stored, 12 new, 9 in common -> 9/15 = 0.6, above the 0.5 floor.
            var stored = WithMembers("Dread Without Answers", Items(12));

            var match = CategoryIdentity.FindMatch(
                [stored], "Ambiguous Horror, No Answers", Items(12, offset: 3), scopedUserId: null);

            Assert.NotNull(match);
            Assert.Same(stored, match.Definition);
            Assert.False(match.MatchedByName);
        }

        [Fact]
        public void ADifferentCategory_SharingSomeTitles_IsNotRecognised()
        {
            // Two neighbouring themes from one library share titles. 12 and 12
            // with 4 in common -> 4/20 = 0.2, nowhere near the floor.
            var stored = WithMembers("Cerebral Sci-Fi", Items(12));

            Assert.Null(CategoryIdentity.FindMatch(
                [stored], "Existential First Contact", Items(12, offset: 8), scopedUserId: null));
        }

        [Fact]
        public void ASmallCategoryInsideALargerOne_DoesNotStealItsIdentity()
        {
            // The Reconciler's overlap coefficient would score this 1.0 because it
            // divides by the smaller set. Jaccard gives 5/20 = 0.25 — which is the
            // reason identity does not reuse that metric.
            var stored = WithMembers("Sprawling Thread", Items(20));

            Assert.Null(CategoryIdentity.FindMatch(
                [stored], "Narrow Thread", Items(5), scopedUserId: null));
        }

        [Fact]
        public void NameMatch_WinsOverASimilarButDifferentlyNamedDefinition()
        {
            var byName = WithMembers("Comfort Rewatch", Items(4, offset: 100));
            var byMembers = WithMembers("Something Else", Items(12));

            var match = CategoryIdentity.FindMatch(
                [byMembers, byName], "Comfort Rewatch", Items(12), scopedUserId: null);

            Assert.NotNull(match);
            Assert.Same(byName, match.Definition);
            Assert.True(match.MatchedByName);
        }

        [Fact]
        public void AClaimedDefinition_IsNotTakenTwice()
        {
            // Two categories from one run both resembling one stored definition
            // must not both claim it — the second would overwrite the first.
            var stored = WithMembers("Reality Coming Undone", Items(12));
            var claimed = new HashSet<Guid> { stored.Id };

            Assert.NotNull(CategoryIdentity.FindMatch([stored], "First Claim", Items(12), null));
            Assert.Null(CategoryIdentity.FindMatch([stored], "Second Claim", Items(12), null, claimed));
        }

        [Fact]
        public void TheBestMatchWins_NotMerelyTheFirstAboveTheFloor()
        {
            var weaker = WithMembers("Weaker", Items(12, offset: 4));   // 8/16 = 0.50
            var stronger = WithMembers("Stronger", Items(12, offset: 1)); // 11/13 = 0.85

            var match = CategoryIdentity.FindMatch(
                [weaker, stronger], "Renamed", Items(12), scopedUserId: null);

            Assert.NotNull(match);
            Assert.Same(stronger, match.Definition);
        }

        [Fact]
        public void AnotherUsersCategory_IsNeverRecognisedByMembers()
        {
            // Two viewers can genuinely share a taste; that must not let one
            // viewer's pass take over the other's definition.
            var alices = WithMembers("Alice's Thread", Items(12), Alice);

            Assert.Null(CategoryIdentity.FindMatch([alices], "Bob's Thread", Items(12), Bob));
            Assert.Null(CategoryIdentity.FindMatch([alices], "Shared Thread", Items(12), null));
        }

        [Fact]
        public void ACategoryWithNoMembers_MatchesOnNameOnly()
        {
            var stored = WithMembers("Quietly Devastating", Items(10));

            Assert.Null(CategoryIdentity.FindMatch([stored], "Renamed", [], scopedUserId: null));
            Assert.NotNull(CategoryIdentity.FindMatch([stored], "Quietly Devastating", [], null));
        }
    }
}
