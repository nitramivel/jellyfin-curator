using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Core.Playlists;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Ordering one shared row for one viewer.
    ///
    /// Shared rows go to the whole household on purpose — they were once opt-in and
    /// it collapsed, because a category nobody picked went unbuilt for everyone.
    /// This is the personalization that cannot do that: every viewer keeps every
    /// row, and only the order inside their own copy differs.
    ///
    /// The property under test throughout is that this *nudges*. The model ranked
    /// members by how strongly each belongs to the thread, and no watch history
    /// replaces that.
    /// </summary>
    public class MemberOrderingTests
    {
        private static readonly Guid[] Pool =
            [.. Enumerable.Range(0, 40).Select(_ => Guid.NewGuid())];

        private static IReadOnlyList<Guid> Members(int count) => [.. Pool.Take(count)];

        private static IReadOnlyDictionary<Guid, UserActivity> Activity(
            params (Guid Id, UserActivity Value)[] entries)
            => entries.ToDictionary(e => e.Id, e => e.Value);

        private static UserActivity Favourite() => new() { IsFavorite = true };

        private static UserActivity Rated(float rating) => new() { UserRating = rating };

        private static UserActivity Watched() => new() { Played = true, PlayCount = 1 };

        private static IReadOnlyList<Guid> Order(
            IReadOnlyList<Guid> members,
            IReadOnlyDictionary<Guid, UserActivity>? activity)
            => MemberOrdering.For(members, id => id, activity);

        [Fact]
        public void AViewerWithNoActivityGetsTheModelsOrderExactly()
        {
            var members = Members(10);

            Assert.Equal(members, Order(members, null));
            Assert.Equal(members, Order(members, Activity()));
        }

        [Fact]
        public void AFavouriteRisesButDoesNotTakeTheLead()
        {
            // The whole design in one assertion. A favourite sitting 30th in a thread
            // it barely belongs to must not become the first card in the row.
            var members = Members(40);
            var ordered = Order(members, Activity((Pool[30], Favourite())));

            var moved = ordered.ToList().IndexOf(Pool[30]);
            Assert.True(moved < 30, "the favourite should rise");
            Assert.True(moved > 10, $"but not to the front — landed at {moved}");
            Assert.NotEqual(Pool[30], ordered[0]);
        }

        [Fact]
        public void ANearbyFavouriteDoesReachTheFront()
        {
            // Bounded does not mean toothless: a favourite the model already ranked
            // highly should lead this viewer's copy.
            var members = Members(10);
            var ordered = Order(members, Activity((Pool[4], Favourite())));

            Assert.Equal(Pool[4], ordered[0]);
        }

        [Fact]
        public void APoorlyRatedItemSinks()
        {
            var members = Members(10);
            var ordered = Order(members, Activity((Pool[0], Rated(2))));

            Assert.NotEqual(Pool[0], ordered[0]);
            Assert.True(ordered.ToList().IndexOf(Pool[0]) > 0);
        }

        [Fact]
        public void AHighlyRatedItemRises()
        {
            var members = Members(20);
            var ordered = Order(members, Activity((Pool[10], Rated(9))));

            Assert.True(ordered.ToList().IndexOf(Pool[10]) < 10);
        }

        [Fact]
        public void EveryMemberSurvivesWhateverTheViewerHasDone()
        {
            // The safety property: this reorders a row, it never shortens one.
            var members = Members(20);
            var activity = Activity(
                (Pool[0], Rated(1)),
                (Pool[5], Favourite()),
                (Pool[9], Watched()),
                (Pool[19], Rated(10)),
                (Guid.NewGuid(), Favourite()));

            var ordered = Order(members, activity);

            Assert.Equal(members.Count, ordered.Count);
            Assert.Equal(
                [.. members.OrderBy(x => x)],
                [.. ordered.OrderBy(x => x)]);
        }

        [Fact]
        public void ItemsTheViewerFeelsNothingAboutKeepTheModelsRelativeOrder()
        {
            // A stable sort, so the model's judgement still decides everything the
            // viewer has expressed no opinion on.
            var members = Members(10);
            var ordered = Order(members, Activity((Pool[9], Favourite())));

            var untouched = ordered.Where(id => id != Pool[9]).ToList();
            Assert.Equal(members.Where(id => id != Pool[9]).ToList(), untouched);
        }

        [Fact]
        public void ARowOfOneIsLeftAlone()
        {
            var members = Members(1);
            Assert.Equal(members, Order(members, Activity((Pool[0], Favourite()))));
        }

        [Fact]
        public void TwoViewersGetDifferentOrdersOfTheSameRow()
        {
            // The point of the feature, stated directly.
            var members = Members(12);
            var leveliese = Order(members, Activity((Pool[8], Favourite()), (Pool[9], Watched())));
            var kylie = Order(members, Activity((Pool[1], Rated(2))));

            Assert.NotEqual(leveliese, kylie);
            Assert.Equal(members.Count, leveliese.Count);
            Assert.Equal(members.Count, kylie.Count);
        }
    }
}
