using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Api;
using Jellyfin.Plugin.Curator.Core.Models;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Covers the projection the configuration page renders from. The controller
    /// itself needs a server to host, but the shaping logic is worth pinning:
    /// a "playlist count" that accidentally counted handed-off or removed links
    /// would misreport the state of every category on the page.
    /// </summary>
    public class CategorySummaryTests
    {
        /// <summary>Names the config page resolves for the per-user detail rows.</summary>
        private static readonly Dictionary<Guid, string> UserNames = [];

        private static CategorySummary Summarize(CategoryDefinition category) => new(
            category.Id,
            category.Name,
            category.Description,
            category.Members.Count,
            category.UserPlaylists.Count(link => link.PlaylistId is not null),
            category.UserPlaylists.Count(link => link.HandedOff),
            category.UpdatedAt,
            category.ModelId,
            category.CreatedAt,
            category.SourceProposalCount,
            category.UserPlaylists
                .Select(link => new CategoryUserLink(
                    link.UserId,
                    UserNames.GetValueOrDefault(link.UserId),
                    link.PlaylistId,
                    link.HandedOff))
                .ToArray(),
            category.SourceProposals,
            category.OwnerUserId,
            category.OwnerUserId is { } o ? UserNames.GetValueOrDefault(o) : null);

        private static CategoryDefinition Category(params UserPlaylistLink[] links) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Comfort Rewatch",
            Description = "The ones you put on without thinking.",
            Members = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()],
            UpdatedAt = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc),
            ModelId = "claude-opus-5",
            UserPlaylists = [.. links],
        };

        /// <summary>
        /// The expanded detail panel lists one row per link, including links whose
        /// playlist is absent — that is exactly the state worth being able to see.
        /// </summary>
        [Fact]
        public void Summary_ExposesEveryUserLinkIncludingPlaylistlessOnes()
        {
            var withPlaylist = Guid.NewGuid();
            var withoutPlaylist = Guid.NewGuid();

            var summary = Summarize(Category(
                new UserPlaylistLink { UserId = withPlaylist, PlaylistId = Guid.NewGuid() },
                new UserPlaylistLink { UserId = withoutPlaylist, PlaylistId = null }));

            Assert.Equal(2, summary.Users.Count);
            Assert.Equal(1, summary.PlaylistCount);

            var empty = Assert.Single(summary.Users, link => link.UserId == withoutPlaylist);
            Assert.Null(empty.PlaylistId);
            Assert.False(empty.HandedOff);
        }

        [Fact]
        public void Summary_UnknownUser_HasNullName()
        {
            var summary = Summarize(Category(
                new UserPlaylistLink { UserId = Guid.NewGuid(), PlaylistId = Guid.NewGuid() }));

            // A category can outlive the account it was built for; the page renders
            // this as "deleted user" rather than dropping the row.
            Assert.Null(Assert.Single(summary.Users).UserName);
        }

        [Fact]
        public void Summary_CarriesCreatedAtAndProposalCount()
        {
            var category = Category();
            category.CreatedAt = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);
            category.SourceProposalCount = 4;

            var summary = Summarize(category);

            Assert.Equal(category.CreatedAt, summary.CreatedAt);
            Assert.Equal(4, summary.SourceProposalCount);
        }

        [Fact]
        public void Summary_CountsLivePlaylistsOnly()
        {
            var category = Category(
                new UserPlaylistLink { UserId = Guid.NewGuid(), PlaylistId = Guid.NewGuid() },
                new UserPlaylistLink { UserId = Guid.NewGuid(), PlaylistId = Guid.NewGuid() },
                new UserPlaylistLink { UserId = Guid.NewGuid(), PlaylistId = null });

            var summary = Summarize(category);

            Assert.Equal(2, summary.PlaylistCount);
            Assert.Equal(3, summary.MemberCount);
            Assert.Equal(0, summary.HandedOffCount);
        }

        [Fact]
        public void Summary_CountsHandedOffSeparately()
        {
            var category = Category(
                new UserPlaylistLink { UserId = Guid.NewGuid(), PlaylistId = Guid.NewGuid() },
                new UserPlaylistLink { UserId = Guid.NewGuid(), PlaylistId = Guid.NewGuid(), HandedOff = true });

            var summary = Summarize(category);

            Assert.Equal(2, summary.PlaylistCount);
            Assert.Equal(1, summary.HandedOffCount);
        }

        [Fact]
        public void Summary_EmptyCategory_ReportsZeroes()
        {
            var category = Category();
            category.Members.Clear();

            var summary = Summarize(category);

            Assert.Equal(0, summary.MemberCount);
            Assert.Equal(0, summary.PlaylistCount);
            Assert.Equal("Comfort Rewatch", summary.Name);
            Assert.Equal("claude-opus-5", summary.ModelId);
        }
    }
}
