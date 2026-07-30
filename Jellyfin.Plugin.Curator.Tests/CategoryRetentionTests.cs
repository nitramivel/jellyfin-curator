using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Models;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    public class CategoryRetentionTests
    {
        private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <param name="name">The category name.</param>
        /// <param name="createdDay">Days after the epoch it was first created.</param>
        /// <param name="updatedDay">Days after the epoch it was last refreshed.</param>
        /// <param name="owner">The owning user, or null for a shared category.</param>
        private static CategoryDefinition Category(string name, int createdDay, int updatedDay, Guid? owner = null)
            => new()
            {
                Id = Guid.NewGuid(),
                Name = name,
                OwnerUserId = owner,
                CreatedAt = Epoch.AddDays(createdDay),
                UpdatedAt = Epoch.AddDays(updatedDay),
            };

        private static string[] Names(IEnumerable<CategoryDefinition> categories)
            => categories.Select(c => c.Name).ToArray();

        /// <summary>
        /// A category holding a live playlist — a row somebody can actually see.
        /// </summary>
        private static CategoryDefinition Live(string name, int createdDay, int updatedDay, Guid? owner = null)
        {
            var category = Category(name, createdDay, updatedDay, owner);
            category.GetOrAddLink(Alice).PlaylistId = Guid.NewGuid();
            return category;
        }

        /// <summary>
        /// A category whose playlist was handed off. The user owns it now, so the
        /// definition is still doing something and must not be treated as empty.
        /// </summary>
        private static CategoryDefinition HandedOff(string name, int createdDay, int updatedDay)
        {
            var category = Category(name, createdDay, updatedDay);
            var link = category.GetOrAddLink(Alice);
            link.PlaylistId = Guid.NewGuid();
            link.HandedOff = true;
            return category;
        }

        [Fact]
        public void UnderTheCap_NothingIsRemoved()
        {
            var stored = new[]
            {
                Category("A", 1, 1),
                Category("B", 2, 2),
            };

            Assert.Empty(CategoryRetention.SelectForRemoval(stored, maxShared: 8, maxPersonal: 6));
        }

        [Fact]
        public void OverTheCap_TheOldestAreRemovedDownToIt()
        {
            var stored = new[]
            {
                Category("newest", 1, 50),
                Category("middle", 2, 30),
                Category("oldest", 3, 10),
                Category("second-oldest", 4, 20),
            };

            var removed = CategoryRetention.SelectForRemoval(stored, maxShared: 2, maxPersonal: 0);

            Assert.Equal(["oldest", "second-oldest"], Names(removed));
        }

        [Fact]
        public void OldestMeansLeastRecentlyRefreshed_NotEarliestCreated()
        {
            // A category the model re-proposes every run has an ancient creation
            // date and is the last thing anyone wants deleted. Ordering on creation
            // would remove exactly the categories that have proved most durable.
            var durable = Category("proposed since the beginning", createdDay: 0, updatedDay: 100);
            var leftover = Category("coined once, months ago", createdDay: 60, updatedDay: 61);

            var removed = CategoryRetention.SelectForRemoval([durable, leftover], maxShared: 1, maxPersonal: 0);

            Assert.Equal("coined once, months ago", Assert.Single(removed).Name);
        }

        [Fact]
        public void CreationDateBreaksTiesBetweenEquallyStaleCategories()
        {
            var stored = new[]
            {
                Category("arrived second", createdDay: 5, updatedDay: 10),
                Category("arrived first", createdDay: 1, updatedDay: 10),
                Category("fresh", createdDay: 9, updatedDay: 99),
            };

            var removed = CategoryRetention.SelectForRemoval(stored, maxShared: 1, maxPersonal: 0);

            Assert.Equal(["arrived first", "arrived second"], Names(removed));
        }

        [Fact]
        public void PoolsAreCountedSeparately()
        {
            // Six categories, but three pools — none of them over a cap of 3.
            var stored = new[]
            {
                Category("shared 1", 1, 1),
                Category("shared 2", 2, 2),
                Category("alice 1", 1, 1, Alice),
                Category("alice 2", 2, 2, Alice),
                Category("bob 1", 1, 1, Bob),
                Category("bob 2", 2, 2, Bob),
            };

            Assert.Empty(CategoryRetention.SelectForRemoval(stored, maxShared: 3, maxPersonal: 3));
        }

        [Fact]
        public void OneUserOverTheCapDoesNotAffectAnother()
        {
            var stored = new[]
            {
                Category("alice old", 1, 1, Alice),
                Category("alice mid", 2, 2, Alice),
                Category("alice new", 3, 3, Alice),
                Category("bob only", 1, 1, Bob),
            };

            var removed = CategoryRetention.SelectForRemoval(stored, maxShared: 0, maxPersonal: 2);

            Assert.Equal("alice old", Assert.Single(removed).Name);
        }

        [Fact]
        public void SharedAndPersonalCapsApplyToTheirOwnPool()
        {
            var stored = new[]
            {
                Category("shared old", 1, 1),
                Category("shared new", 2, 2),
                Category("alice old", 1, 1, Alice),
                Category("alice new", 2, 2, Alice),
            };

            var removed = CategoryRetention.SelectForRemoval(stored, maxShared: 1, maxPersonal: 1);

            Assert.Equal(["shared old", "alice old"], Names(removed));
        }

        [Fact]
        public void ZeroCapMeansNoLimit()
        {
            var stored = new[]
            {
                Category("A", 1, 1),
                Category("B", 2, 2),
                Category("C", 3, 3, Alice),
                Category("D", 4, 4, Alice),
            };

            Assert.Empty(CategoryRetention.SelectForRemoval(stored, maxShared: 0, maxPersonal: 0));
        }

        [Fact]
        public void ExactlyAtTheCap_NothingIsRemoved()
        {
            var stored = new[]
            {
                Category("A", 1, 1),
                Category("B", 2, 2),
            };

            Assert.Empty(CategoryRetention.SelectForRemoval(stored, maxShared: 2, maxPersonal: 0));
        }

        [Fact]
        public void LoweringTheCapPrunesWhatIsAlreadyStored()
        {
            // The point of the whole thing: the Reconciler caps what one run
            // produces, so without this, lowering a cap in configuration would
            // never affect the categories already on disk.
            var stored = Enumerable.Range(0, 10)
                .Select(i => Category("cat " + i, i, i))
                .ToArray();

            var removed = CategoryRetention.SelectForRemoval(stored, maxShared: 3, maxPersonal: 0);

            Assert.Equal(7, removed.Count);
            Assert.Equal(["cat 0", "cat 1", "cat 2", "cat 3", "cat 4", "cat 5", "cat 6"], Names(removed));
        }

        [Fact]
        public void EmptyStore_IsHandled()
        {
            Assert.Empty(CategoryRetention.SelectForRemoval([], maxShared: 5, maxPersonal: 5));
        }

        // ---- empty categories go first ----

        /// <summary>
        /// A category with no playlist is showing nobody anything, so the cap spends
        /// it before a category that is currently a row on someone's home screen —
        /// however stale that row's definition looks by date.
        /// </summary>
        [Fact]
        public void EmptyCategoriesAreRemovedBeforeLiveOnes()
        {
            var stored = new[]
            {
                Live("live but stale", 1, 1),
                Live("live and fresh", 2, 90),
                Category("empty but freshly refreshed", 3, 99),
            };

            var removed = CategoryRetention.SelectForRemoval(stored, maxShared: 2, maxPersonal: 6);

            Assert.Equal(["empty but freshly refreshed"], Names(removed));
        }

        /// <summary>
        /// Among the empty ones, the existing rule still applies: least recently
        /// refreshed first.
        /// </summary>
        [Fact]
        public void AmongEmptyCategoriesTheOldestStillGoesFirst()
        {
            var stored = new[]
            {
                Live("live", 1, 1),
                Category("empty, refreshed later", 2, 40),
                Category("empty, refreshed earlier", 3, 20),
            };

            var removed = CategoryRetention.SelectForRemoval(stored, maxShared: 1, maxPersonal: 6);

            Assert.Equal(["empty, refreshed earlier", "empty, refreshed later"], Names(removed));
        }

        /// <summary>
        /// Once every empty category is gone the cap keeps going into the live ones,
        /// oldest first — the rule is an ordering, not an exemption.
        /// </summary>
        [Fact]
        public void WhenEmptiesAreNotEnoughLiveCategoriesFollowOldestFirst()
        {
            var stored = new[]
            {
                Live("live newest", 1, 90),
                Live("live oldest", 2, 10),
                Category("empty", 3, 50),
            };

            var removed = CategoryRetention.SelectForRemoval(stored, maxShared: 1, maxPersonal: 6);

            Assert.Equal(["empty", "live oldest"], Names(removed));
        }

        /// <summary>
        /// A handed-off playlist belongs to the user permanently. The definition is
        /// still doing something, so it must not be first out of the door.
        /// </summary>
        [Fact]
        public void AHandedOffPlaylistCountsAsHeld()
        {
            var stored = new[]
            {
                HandedOff("handed off", 1, 1),
                Category("empty", 2, 99),
            };

            var removed = CategoryRetention.SelectForRemoval(stored, maxShared: 1, maxPersonal: 6);

            Assert.Equal(["empty"], Names(removed));
        }

        [Fact]
        public void IsEmpty_ReadsTheLinks()
        {
            Assert.True(CategoryRetention.IsEmpty(Category("none", 1, 1)));
            Assert.False(CategoryRetention.IsEmpty(Live("live", 1, 1)));
            Assert.False(CategoryRetention.IsEmpty(HandedOff("handed", 1, 1)));
        }

        /// <summary>Personal pools order the same way as shared ones.</summary>
        [Fact]
        public void ThePersonalPoolAlsoSpendsEmptiesFirst()
        {
            var stored = new[]
            {
                Live("alice live", 1, 1, Alice),
                Category("alice empty", 2, 99, Alice),
                Live("bob live", 3, 1, Bob),
            };

            var removed = CategoryRetention.SelectForRemoval(stored, maxShared: 8, maxPersonal: 1);

            Assert.Equal(["alice empty"], Names(removed));
        }
    }
}
