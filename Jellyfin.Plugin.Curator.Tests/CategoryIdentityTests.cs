using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Models;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    public class CategoryIdentityTests
    {
        private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

        private static CategoryDefinition Definition(string name, params Guid[] users)
        {
            var definition = new CategoryDefinition { Id = Guid.NewGuid(), Name = name };
            foreach (var user in users)
            {
                definition.UserPlaylists.Add(new UserPlaylistLink { UserId = user, PlaylistId = Guid.NewGuid() });
            }

            return definition;
        }

        [Fact]
        public void SharedRun_MatchesByNameRegardlessOfUsers()
        {
            var stored = Definition("Comfort Rewatch", Alice, Bob);

            var match = CategoryIdentity.FindMatch([stored], "Comfort Rewatch", scopedUserId: null);

            Assert.Same(stored, match);
        }

        [Fact]
        public void NameMatch_IsCaseInsensitive()
        {
            var stored = Definition("Comfort Rewatch", Alice);

            Assert.Same(stored, CategoryIdentity.FindMatch([stored], "comfort rewatch", null));
        }

        [Fact]
        public void NewCategory_HasNoMatch()
        {
            var stored = Definition("Comfort Rewatch", Alice);

            Assert.Null(CategoryIdentity.FindMatch([stored], "Cerebral Sci-Fi", null));
        }

        [Fact]
        public void PersonalizedRun_MatchesOnlyThatUsersDefinition()
        {
            var aliceCategory = Definition("Comfort Rewatch", Alice);
            var bobCategory = Definition("Comfort Rewatch", Bob);
            var stored = new List<CategoryDefinition> { aliceCategory, bobCategory };

            Assert.Same(aliceCategory, CategoryIdentity.FindMatch(stored, "Comfort Rewatch", Alice));
            Assert.Same(bobCategory, CategoryIdentity.FindMatch(stored, "Comfort Rewatch", Bob));
        }

        [Fact]
        public void PersonalizedRun_OtherUsersCategory_IsNotReused()
        {
            // Alice already has "Comfort Rewatch"; Bob's first personalized run must
            // create his own definition rather than hijacking hers.
            var aliceCategory = Definition("Comfort Rewatch", Alice);

            Assert.Null(CategoryIdentity.FindMatch([aliceCategory], "Comfort Rewatch", Bob));
        }

        [Fact]
        public void EmptyStore_HasNoMatch()
        {
            Assert.Null(CategoryIdentity.FindMatch([], "Anything", null));
            Assert.Null(CategoryIdentity.FindMatch([], "Anything", Alice));
        }

        [Fact]
        public void DefinitionWithoutLinks_MatchesSharedRunButNotPersonalized()
        {
            // A category whose playlists were all removed (empty-category lifecycle)
            // keeps its definition; a shared run should still reuse it.
            var retired = Definition("Quietly Devastating");

            Assert.Same(retired, CategoryIdentity.FindMatch([retired], "Quietly Devastating", null));
            Assert.Null(CategoryIdentity.FindMatch([retired], "Quietly Devastating", Alice));
        }
    }
}
