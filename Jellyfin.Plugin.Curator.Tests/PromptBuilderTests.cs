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

        /// <summary>
        /// A series reaches the model as watch depth, on its own line. Its rolled-up
        /// PlayCount is a sum over episodes, so routing it through "rewatched" would
        /// report a sitcom watched once end to end as 140 rewatches.
        /// </summary>
        [Fact]
        public void BuildActivitySection_SeriesReportDepthNotAPlayCount()
        {
            var series = Movie() with { Id = Guid.NewGuid(), Kind = MediaKind.Series, Name = "The Office" };
            var activity = new Dictionary<Guid, UserActivity>
            {
                [series.Id] = new UserActivity
                {
                    Played = true,
                    PlayCount = 152,
                    EpisodesPlayed = 140,
                    EpisodeCount = 201,
                },
            };

            var section = PromptBuilder.BuildActivitySection([series], activity);

            Assert.Contains("series, episodes watched of total: 0=140/201", section, StringComparison.Ordinal);
            Assert.DoesNotContain("rewatched", section, StringComparison.Ordinal);
            Assert.DoesNotContain("watched once", section, StringComparison.Ordinal);
        }

        /// <summary>
        /// Deepest-watched first: for a television viewer this line carries the
        /// strongest evidence in the section, and the model reads the head of a list
        /// most closely.
        /// </summary>
        [Fact]
        public void BuildActivitySection_SeriesAreOrderedByDepth()
        {
            var shallow = Movie() with { Id = Guid.NewGuid(), Kind = MediaKind.Series };
            var deep = Movie() with { Id = Guid.NewGuid(), Kind = MediaKind.Series };
            var activity = new Dictionary<Guid, UserActivity>
            {
                [shallow.Id] = new UserActivity { Played = true, PlayCount = 2, EpisodesPlayed = 2, EpisodeCount = 10 },
                [deep.Id] = new UserActivity { Played = true, PlayCount = 60, EpisodesPlayed = 60, EpisodeCount = 62 },
            };

            var section = PromptBuilder.BuildActivitySection([shallow, deep], activity);

            Assert.Contains("series, episodes watched of total: 1=60/62,0=2/10", section, StringComparison.Ordinal);
        }

        /// <summary>
        /// A series owned but never played contributes no line, exactly as an unwatched
        /// movie does — the absence is already the signal.
        /// </summary>
        [Fact]
        public void BuildActivitySection_UnwatchedSeriesGetNoLine()
        {
            var series = Movie() with { Id = Guid.NewGuid(), Kind = MediaKind.Series };
            var activity = new Dictionary<Guid, UserActivity>
            {
                [series.Id] = new UserActivity { EpisodesPlayed = 0, EpisodeCount = 24, IsFavorite = true },
            };

            var section = PromptBuilder.BuildActivitySection([series], activity);

            Assert.DoesNotContain("series, episodes watched", section, StringComparison.Ordinal);
            Assert.Contains("favourites: 0", section, StringComparison.Ordinal);
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

        [Theory]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(9)]
        public void SystemPrompts_AskForTheConfiguredMemberFloor(int minMembers)
        {
            // The floor the model is told must be the floor the Reconciler applies.
            // When these drifted apart the prompt asked for 3 and the filter demanded
            // 6, and 17 of 22 proposals in a measured run were binned on size alone.
            Assert.Contains(
                $"at least {minMembers} members",
                PromptBuilder.BuildSystemPrompt(new CategoryLimits(minMembers)),
                StringComparison.Ordinal);
            Assert.Contains(
                $"at least {minMembers} members",
                PromptBuilder.BuildPersonalSystemPrompt(new CategoryLimits(minMembers)),
                StringComparison.Ordinal);
        }

        [Fact]
        public void SystemPrompts_NeverAskForFewerThanTwoMembers()
        {
            // A one-member category is not a category, whatever the config says.
            Assert.Contains("at least 2 members", PromptBuilder.BuildSystemPrompt(new CategoryLimits(1)), StringComparison.Ordinal);
            Assert.Contains("at least 2 members", PromptBuilder.BuildSystemPrompt(new CategoryLimits(0)), StringComparison.Ordinal);
            Assert.Contains("at least 2 members", PromptBuilder.BuildPersonalSystemPrompt(new CategoryLimits(-3)), StringComparison.Ordinal);
        }

        [Fact]
        public void SystemPrompts_LeaveNoPlaceholderBehind()
        {
            foreach (var prompt in new[]
            {
                PromptBuilder.BuildSystemPrompt(new CategoryLimits(4)),
                PromptBuilder.BuildSystemPrompt(new CategoryLimits(6, 0, 20)),
                PromptBuilder.BuildPersonalSystemPrompt(new CategoryLimits(2)),
                PromptBuilder.BuildPersonalSystemPrompt(new CategoryLimits(2, 0, 6)),
            })
            {
                Assert.DoesNotContain("{MIN_MEMBERS}", prompt, StringComparison.Ordinal);
                Assert.DoesNotContain("{MAX_CATEGORIES}", prompt, StringComparison.Ordinal);
            }
        }

        [Theory]
        [InlineData(20)]
        [InlineData(8)]
        public void SystemPrompts_TellTheModelHowManyCategoriesToAimFor(int max)
        {
            // A ceiling the model cannot see is one it cannot aim at. With no target
            // count, one model read "find the threads" as be exhaustive and another
            // as satisfy the constraint — 23 categories against 5, same prompt.
            Assert.Contains(
                $"up to {max} categories",
                PromptBuilder.BuildSystemPrompt(new CategoryLimits(6, 0, max)),
                StringComparison.Ordinal);
            Assert.Contains(
                $"up to {max} categories",
                PromptBuilder.BuildPersonalSystemPrompt(new CategoryLimits(2, 0, max)),
                StringComparison.Ordinal);
        }

        [Fact]
        public void SystemPrompt_WithNoCap_AsksForWhatTheCollectionSupports()
        {
            var prompt = PromptBuilder.BuildSystemPrompt(new CategoryLimits(6, 0, 0));

            Assert.Contains("as many categories as the collection genuinely supports", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("up to 0 categories", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void SystemPrompts_StateTheMemberRangeNotJustTheFloor()
        {
            Assert.Contains(
                "between 6 and 20 members",
                PromptBuilder.BuildSystemPrompt(new CategoryLimits(6, 20, 10)),
                StringComparison.Ordinal);
            Assert.Contains(
                "between 2 and 20 members",
                PromptBuilder.BuildPersonalSystemPrompt(new CategoryLimits(2, 20, 6)),
                StringComparison.Ordinal);
        }

        [Fact]
        public void SystemPrompt_WithNoMemberCeiling_StatesOnlyTheFloor()
        {
            var prompt = PromptBuilder.BuildSystemPrompt(new CategoryLimits(6, 0, 10));

            Assert.Contains("at least 6 members", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("between", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void SystemPrompt_CeilingBelowTheFloor_FallsBackToTheFloorAlone()
        {
            // "between 6 and 4 members" is an impossible instruction; the floor wins.
            var prompt = PromptBuilder.BuildSystemPrompt(new CategoryLimits(6, 4, 10));

            Assert.Contains("at least 6 members", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("and 4 members", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void SystemPrompt_AsksForCoverageAndVariedSizes()
        {
            // The failure this addresses was not bad categories — it was five
            // categories all hugging the floor, covering a tenth of the library.
            var prompt = PromptBuilder.BuildSystemPrompt(new CategoryLimits(6, 0, 20));

            Assert.Contains("aim to place most of it", prompt, StringComparison.Ordinal);
            Assert.Contains("Sizes should vary", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void PersonalSystemPrompt_AsksTheModelToUnderstandTheViewerFirst()
        {
            // The personal pass is worth its tokens only if the history is read as
            // evidence about a person rather than as a list of titles.
            Assert.Contains("get to know this viewer", PromptBuilder.BuildPersonalSystemPrompt(new CategoryLimits(2)), StringComparison.Ordinal);

            // ...without licensing invention past what the history supports.
            Assert.Contains(
                "What you must not do is invent a",
                PromptBuilder.BuildPersonalSystemPrompt(new CategoryLimits(2)),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// The restraint clause used to read "if their history is too thin to support
        /// a real observation, propose nothing rather than padding", and a viewer with
        /// three watched films got back {"selected":[],"categories":[]} in a second
        /// flat. Restraint must bound what is invented, not authorise silence: a short
        /// history is answered with fewer and broader categories, and only a viewer
        /// with no history at all gets nothing.
        /// </summary>
        [Fact]
        public void PersonalSystemPrompt_TreatsAThinHistoryAsBroaderNotEmpty()
        {
            var prompt = PromptBuilder.BuildPersonalSystemPrompt(new CategoryLimits(2));

            Assert.Contains("fewer and broader categories, not none", prompt, StringComparison.Ordinal);
            Assert.Contains("no recorded history whatsoever", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("propose nothing rather than padding", prompt, StringComparison.Ordinal);
        }

        /// <summary>
        /// Television is watched by the episode, so a series reaches the model on the
        /// "series" line and never as a play count. The prompt has to say so, or a
        /// deeply watched show reads as one more title watched once.
        /// </summary>
        [Fact]
        public void PersonalSystemPrompt_ExplainsTheSeriesLine()
        {
            var prompt = PromptBuilder.BuildPersonalSystemPrompt(new CategoryLimits(2));

            Assert.Contains("series, episodes watched of total", prompt, StringComparison.Ordinal);
            Assert.Contains("Read watch depth as intensity", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void SystemPrompt_StatesTheIndexOnlyRule()
        {
            Assert.Contains("integer index", PromptBuilder.BuildSystemPrompt(new CategoryLimits(4)), StringComparison.Ordinal);
            Assert.Contains("Never invent items", PromptBuilder.BuildSystemPrompt(new CategoryLimits(4)), StringComparison.Ordinal);
        }
    }
}
