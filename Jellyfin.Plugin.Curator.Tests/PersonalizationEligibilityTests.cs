using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Models;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    public class PersonalizationEligibilityTests
    {
        private static Dictionary<Guid, UserActivity> Activity(params UserActivity[] entries)
        {
            var result = new Dictionary<Guid, UserActivity>(entries.Length);
            foreach (var entry in entries)
            {
                result[Guid.NewGuid()] = entry;
            }

            return result;
        }

        private static UserActivity Watched(int playCount = 1)
            => new() { Played = true, PlayCount = playCount };

        [Fact]
        public void CountWatched_CountsPlayedItems()
        {
            var activity = Activity(Watched(), Watched(), Watched(3));

            Assert.Equal(3, PersonalizationEligibility.CountWatched(activity));
        }

        [Fact]
        public void CountWatched_CountsAnItemOnceRegardlessOfPlayCount()
        {
            var activity = Activity(Watched(playCount: 12));

            Assert.Equal(1, PersonalizationEligibility.CountWatched(activity));
        }

        [Fact]
        public void CountWatched_CountsPlayCountWithoutPlayedFlag()
        {
            // A partially-watched item carries a play count but is not marked played.
            var activity = Activity(new UserActivity { Played = false, PlayCount = 1 });

            Assert.Equal(1, PersonalizationEligibility.CountWatched(activity));
        }

        [Fact]
        public void CountWatched_IgnoresFavoritesAndRatingsWithoutPlays()
        {
            var activity = Activity(
                new UserActivity { IsFavorite = true },
                new UserActivity { UserRating = 9f });

            Assert.Equal(0, PersonalizationEligibility.CountWatched(activity));
        }

        [Fact]
        public void CountWatched_TreatsNoActivityAsZero()
        {
            Assert.Equal(0, PersonalizationEligibility.CountWatched(new Dictionary<Guid, UserActivity>()));
            Assert.Equal(0, PersonalizationEligibility.CountWatched(null));
        }

        [Theory]
        [InlineData(0, 2, false)]
        [InlineData(1, 2, false)]
        [InlineData(2, 2, true)]
        [InlineData(68, 2, true)]
        public void IsEligible_AppliesTheFloorInclusively(int watched, int minimum, bool expected)
        {
            Assert.Equal(expected, PersonalizationEligibility.IsEligible(watched, minimum));
        }

        [Fact]
        public void IsEligible_ZeroMinimumPersonalizesEveryone()
        {
            Assert.True(PersonalizationEligibility.IsEligible(0, 0));
        }

        [Fact]
        public void IsEligible_NegativeMinimumPersonalizesEveryone()
        {
            // Config comes from a number input; a negative value must not lock everyone out.
            Assert.True(PersonalizationEligibility.IsEligible(0, -5));
        }
    }
}
