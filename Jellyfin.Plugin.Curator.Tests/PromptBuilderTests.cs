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

            Assert.Contains("\"w\":{", prompt, StringComparison.Ordinal);
            Assert.Contains("\"p\":true", prompt, StringComparison.Ordinal);
            Assert.Contains("\"n\":7", prompt, StringComparison.Ordinal);
            Assert.Contains("\"f\":true", prompt, StringComparison.Ordinal);
            Assert.Contains("\"ur\":9", prompt, StringComparison.Ordinal);
            Assert.Contains("\"d\":12", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildUserPrompt_WithoutActivity_HasNoWatchField()
        {
            Assert.DoesNotContain("\"w\":", PromptBuilder.BuildUserPrompt([Movie()]), StringComparison.Ordinal);
        }

        [Fact]
        public void BuildUserPrompt_ActivityForOtherItem_IsNotAttached()
        {
            var activity = new Dictionary<Guid, UserActivity>
            {
                [Guid.NewGuid()] = new UserActivity { Played = true },
            };

            var prompt = PromptBuilder.BuildUserPrompt([Movie()], activity);

            Assert.DoesNotContain("\"w\":", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void SystemPrompt_StatesTheIndexOnlyRule()
        {
            Assert.Contains("integer index", PromptBuilder.SystemPrompt, StringComparison.Ordinal);
            Assert.Contains("Never invent items", PromptBuilder.SystemPrompt, StringComparison.Ordinal);
        }
    }
}
