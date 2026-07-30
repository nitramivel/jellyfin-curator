using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Core.Llm;
using Jellyfin.Plugin.Curator.Core.Models;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    public class PromptBuilderTests
    {
        private static readonly Guid ItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        private static MediaItemRecord Movie() => new()
        {
            Id = ItemId,
            Kind = MediaKind.Movie,
            Name = "Blade Runner",
            Year = 1982,
            Genres = ["Science Fiction"],
            Tags = ["neo-noir"],
            OfficialRating = "R",
            RuntimeMinutes = 117,
            CommunityRating = 8.1f,
            Overview = "A blade runner hunts replicants.",
        };

        [Fact]
        public void BuildUserPrompt_ContainsIndexedItemFields()
        {
            var prompt = PromptBuilder.BuildUserPrompt([Movie()]);

            Assert.Contains("\"i\":0", prompt, StringComparison.Ordinal);
            Assert.Contains("\"title\":\"Blade Runner\"", prompt, StringComparison.Ordinal);
            Assert.Contains("\"year\":1982", prompt, StringComparison.Ordinal);
            Assert.Contains("\"genres\":[\"Science Fiction\"]", prompt, StringComparison.Ordinal);
            Assert.Contains("\"min\":117", prompt, StringComparison.Ordinal);
            Assert.Contains("\"overview\":", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildUserPrompt_NeverContainsItemGuids()
        {
            var prompt = PromptBuilder.BuildUserPrompt([Movie()]);

            Assert.DoesNotContain("11111111", prompt, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BuildUserPrompt_OmitsAbsentFields()
        {
            var bare = new MediaItemRecord { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Bare" };

            var prompt = PromptBuilder.BuildUserPrompt([bare]);

            Assert.DoesNotContain("\"year\"", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("\"genres\"", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("\"rated\"", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("\"overview\"", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildUserPrompt_Episode_CarriesSeriesContext()
        {
            var episode = new MediaItemRecord
            {
                Id = Guid.NewGuid(),
                Kind = MediaKind.Episode,
                Name = "Fly",
                SeriesName = "Breaking Bad",
                SeasonNumber = 3,
                EpisodeNumber = 10,
            };

            var prompt = PromptBuilder.BuildUserPrompt([episode]);

            Assert.Contains("\"kind\":\"episode\"", prompt, StringComparison.Ordinal);
            Assert.Contains("\"series\":\"Breaking Bad\"", prompt, StringComparison.Ordinal);
            Assert.Contains("\"ep\":\"S03E10\"", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildUserPrompt_WithActivity_IncludesWatchData()
        {
            var activity = new Dictionary<Guid, UserActivity>
            {
                [ItemId] = new UserActivity
                {
                    Played = true,
                    PlayCount = 7,
                    IsFavorite = true,
                    UserRating = 9f,
                    DaysSinceLastPlayed = 12,
                },
            };

            var prompt = PromptBuilder.BuildUserPrompt([Movie()], activity);

            Assert.Contains("Watch activity:", prompt, StringComparison.Ordinal);
            Assert.Contains("favourites: 0", prompt, StringComparison.Ordinal);
            Assert.Contains("rated 0-10: 0=9", prompt, StringComparison.Ordinal);
            Assert.Contains("rewatched, times played: 0x7", prompt, StringComparison.Ordinal);
            Assert.Contains("watched recently, days ago: 0=12", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildUserPrompt_WithoutActivity_HasNoActivitySection()
        {
            Assert.DoesNotContain(
                "Watch activity:",
                PromptBuilder.BuildUserPrompt([Movie()]),
                StringComparison.Ordinal);
        }

        [Fact]
        public void BuildUserPrompt_ActivityForOtherItem_IsNotAttached()
        {
            var activity = new Dictionary<Guid, UserActivity>
            {
                [Guid.NewGuid()] = new UserActivity { Played = true },
            };

            var prompt = PromptBuilder.BuildUserPrompt([Movie()], activity);

            Assert.DoesNotContain("Watch activity:", prompt, StringComparison.Ordinal);
        }

        /// <summary>
        /// The whole point of the prefix/suffix split: the item list must not vary
        /// with the user, or every per-user pass misses the prompt cache.
        /// </summary>
        [Fact]
        public void BuildItemList_IsIdenticalRegardlessOfActivity()
        {
            var batch = new[] { Movie() };
            var activity = new Dictionary<Guid, UserActivity>
            {
                [ItemId] = new UserActivity { Played = true, PlayCount = 7, IsFavorite = true },
            };

            var withoutUser = PromptBuilder.BuildItemList(batch);
            var withUser = PromptBuilder.BuildItemList(batch);

            Assert.Equal(withoutUser, withUser);
            Assert.DoesNotContain("rewatched", withUser, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Watch activity:",
                PromptBuilder.BuildItemList(batch),
                StringComparison.Ordinal);

            // The activity has to survive somewhere — just not in the cached half.
            Assert.Contains(
                "rewatched, times played: 0x7",
                PromptBuilder.BuildActivitySection(batch, activity),
                StringComparison.Ordinal);
        }

        [Fact]
        public void BuildActivitySection_GroupsIndexesRatherThanRepeatingKeys()
        {
            var batch = new List<MediaItemRecord>();
            var activity = new Dictionary<Guid, UserActivity>();
            for (var i = 0; i < 4; i++)
            {
                var item = Movie() with { Id = Guid.NewGuid(), Name = "Film " + i };
                batch.Add(item);
                activity[item.Id] = new UserActivity { PlayCount = i + 2 };
            }

            var section = PromptBuilder.BuildActivitySection(batch, activity);

            // One line for all four, most-replayed first.
            Assert.Contains("rewatched, times played: 3x5,2x4,1x3,0x2", section, StringComparison.Ordinal);
            Assert.DoesNotContain("\"n\"", section, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildActivitySection_SeparatesRewatchedFromWatchedOnce()
        {
            var once = Movie() with { Id = Guid.NewGuid() };
            var twice = Movie() with { Id = Guid.NewGuid() };
            var activity = new Dictionary<Guid, UserActivity>
            {
                [once.Id] = new UserActivity { PlayCount = 1, Played = true },
                [twice.Id] = new UserActivity { PlayCount = 2 },
            };

            var section = PromptBuilder.BuildActivitySection([once, twice], activity);

            Assert.Contains("watched once: 0", section, StringComparison.Ordinal);
            Assert.Contains("rewatched, times played: 1x2", section, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildActivitySection_ItemCanAppearInSeveralGroups()
        {
            var activity = new Dictionary<Guid, UserActivity>
            {
                [ItemId] = new UserActivity { IsFavorite = true, UserRating = 8f, PlayCount = 3 },
            };

            var section = PromptBuilder.BuildActivitySection([Movie()], activity);

            // Favourite, rated and rewatched are facets, not a partition.
            Assert.Contains("favourites: 0", section, StringComparison.Ordinal);
            Assert.Contains("rated 0-10: 0=8", section, StringComparison.Ordinal);
            Assert.Contains("rewatched, times played: 0x3", section, StringComparison.Ordinal);
        }

        /// <summary>
        /// Past roughly half a year, "when" adds nothing a play count doesn't, and
        /// keeping the whole tail would undo the point of grouping.
        /// </summary>
        [Fact]
        public void BuildActivitySection_OldPlaysAreNotListedAsRecent()
        {
            var recent = Movie() with { Id = Guid.NewGuid() };
            var stale = Movie() with { Id = Guid.NewGuid() };
            var activity = new Dictionary<Guid, UserActivity>
            {
                [recent.Id] = new UserActivity { PlayCount = 1, DaysSinceLastPlayed = 30 },
                [stale.Id] = new UserActivity { PlayCount = 1, DaysSinceLastPlayed = 900 },
            };

            var section = PromptBuilder.BuildActivitySection([recent, stale], activity);

            Assert.Contains("watched recently, days ago: 0=30", section, StringComparison.Ordinal);
            Assert.DoesNotContain("900", section, StringComparison.Ordinal);

            // Dropping it from "recent" must not drop it from the section entirely.
            Assert.Contains("watched once: 0,1", section, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildActivitySection_RecentIsOrderedMostRecentFirst()
        {
            var older = Movie() with { Id = Guid.NewGuid() };
            var newer = Movie() with { Id = Guid.NewGuid() };
            var activity = new Dictionary<Guid, UserActivity>
            {
                [older.Id] = new UserActivity { PlayCount = 1, DaysSinceLastPlayed = 100 },
                [newer.Id] = new UserActivity { PlayCount = 1, DaysSinceLastPlayed = 2 },
            };

            var section = PromptBuilder.BuildActivitySection([older, newer], activity);

            Assert.Contains("watched recently, days ago: 1=2,0=100", section, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildItemList_DefaultsToNoTags()
        {
            var tagged = Movie() with { Tags = ["superhero", "aftercreditsstinger", "wry"] };

            Assert.DoesNotContain("\"tags\"", PromptBuilder.BuildItemList([tagged]), StringComparison.Ordinal);
        }

        [Fact]
        public void BuildItemList_CapsTagsAndKeepsLeadingOnes()
        {
            var tagged = Movie() with { Tags = ["superhero", "aftercreditsstinger", "wry"] };

            var prompt = PromptBuilder.BuildItemList([tagged], maxTagsPerItem: 2);

            Assert.Contains("\"tags\":[\"superhero\",\"aftercreditsstinger\"]", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("wry", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildItemList_CapAboveAvailableTags_KeepsAll()
        {
            var tagged = Movie() with { Tags = ["superhero", "wry"] };

            var prompt = PromptBuilder.BuildItemList([tagged], maxTagsPerItem: 50);

            Assert.Contains("\"tags\":[\"superhero\",\"wry\"]", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildItemList_ItemWithNoTags_OmitsTheField()
        {
            var prompt = PromptBuilder.BuildItemList([Movie() with { Tags = [] }], maxTagsPerItem: 5);

            Assert.DoesNotContain("\"tags\"", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildItemList_PlusActivitySection_EqualsUserPrompt()
        {
            var batch = new[] { Movie() };
            var activity = new Dictionary<Guid, UserActivity>
            {
                [ItemId] = new UserActivity { Played = true },
            };

            Assert.Equal(
                PromptBuilder.BuildUserPrompt(batch, activity),
                PromptBuilder.BuildItemList(batch) + PromptBuilder.BuildActivitySection(batch, activity));
        }

        /// <summary>
        /// Jellyfin writes a UserData row for merely touching an item, so entries that
        /// reduce to {"i":N,"p":false} say nothing the absence of a line doesn't.
        /// </summary>
        [Fact]
        public void BuildActivitySection_EntryWithNoSignal_IsOmitted()
        {
            var activity = new Dictionary<Guid, UserActivity>
            {
                [ItemId] = new UserActivity { Played = false },
            };

            var section = PromptBuilder.BuildActivitySection([Movie()], activity);

            Assert.DoesNotContain("Watch activity:", section, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(true, 0, false, null)]      // played
        [InlineData(false, 3, false, null)]     // partially played
        [InlineData(false, 0, true, null)]      // favourited but unwatched
        [InlineData(false, 0, false, 8f)]       // rated but unwatched
        public void BuildActivitySection_EntryWithAnySignal_IsKept(
            bool played, int playCount, bool favorite, float? rating)
        {
            var activity = new Dictionary<Guid, UserActivity>
            {
                [ItemId] = new UserActivity
                {
                    Played = played,
                    PlayCount = playCount,
                    IsFavorite = favorite,
                    UserRating = rating,
                },
            };

            var section = PromptBuilder.BuildActivitySection([Movie()], activity);

            Assert.Contains("Watch activity:", section, StringComparison.Ordinal);
            Assert.Matches(@"(favourites|rated 0-10|rewatched, times played|watched once): 0", section);
        }

        [Fact]
        public void BuildActivitySection_MixedEntries_KeepsOnlyTheInformativeOnes()
        {
            var second = Movie() with { Id = Guid.NewGuid(), Name = "Second" };
            var activity = new Dictionary<Guid, UserActivity>
            {
                [ItemId] = new UserActivity { Played = false },
                [second.Id] = new UserActivity { Played = true, PlayCount = 4 },
            };

            var section = PromptBuilder.BuildActivitySection([Movie(), second], activity);

            Assert.Contains("rewatched, times played: 1x4", section, StringComparison.Ordinal);
            Assert.DoesNotContain(": 0", section, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildActivitySection_IndexesMatchBatchPositions()
        {
            var second = Movie() with { Id = Guid.NewGuid(), Name = "Second" };
            var activity = new Dictionary<Guid, UserActivity>
            {
                [second.Id] = new UserActivity { Played = true, PlayCount = 3 },
            };

            var section = PromptBuilder.BuildActivitySection([Movie(), second], activity);

            // Index 1, not 0 — activity is keyed by batch position, and an off-by-one
            // here would silently attribute history to the wrong film.
            Assert.Contains("rewatched, times played: 1x3", section, StringComparison.Ordinal);
            Assert.DoesNotContain(": 0", section, StringComparison.Ordinal);
        }

        [Fact]
        public void SystemPrompt_StatesTheIndexOnlyRule()
        {
            Assert.Contains("integer index", PromptBuilder.SystemPrompt, StringComparison.Ordinal);
            Assert.Contains("Never invent items", PromptBuilder.SystemPrompt, StringComparison.Ordinal);
        }
    }
}
