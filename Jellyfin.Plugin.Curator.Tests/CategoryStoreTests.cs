using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Services.Categories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    public sealed class CategoryStoreTests : IDisposable
    {
        private readonly string _directory;
        private readonly CategoryStore _store;

        public CategoryStoreTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "curator-tests-" + Guid.NewGuid().ToString("N"));
            _store = new CategoryStore(_directory, NullLogger<CategoryStore>.Instance);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private static CategoryDefinition Category(string name = "Comfort Rewatch")
        {
            var userId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
            return new CategoryDefinition
            {
                Id = Guid.NewGuid(),
                Name = name,
                Description = "The ones you put on without thinking.",
                Members = [Guid.NewGuid(), Guid.NewGuid()],
                SourceProposalCount = 3,
                CreatedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc),
                ModelId = "claude-opus-5",
                UserPlaylists =
                [
                    new UserPlaylistLink { UserId = userId, PlaylistId = Guid.NewGuid() },
                    new UserPlaylistLink { UserId = Guid.NewGuid(), PlaylistId = null, HandedOff = true },
                ],
            };
        }

        [Fact]
        public void SaveAndGet_RoundTripsAllFields()
        {
            var original = Category();

            _store.Save(original);
            var loaded = _store.Get(original.Id);

            Assert.NotNull(loaded);
            Assert.Equal(original.Id, loaded.Id);
            Assert.Equal(original.Name, loaded.Name);
            Assert.Equal(original.Description, loaded.Description);
            Assert.Equal(original.Members, loaded.Members);
            Assert.Equal(original.SourceProposalCount, loaded.SourceProposalCount);
            Assert.Equal(original.CreatedAt, loaded.CreatedAt);
            Assert.Equal(original.UpdatedAt, loaded.UpdatedAt);
            Assert.Equal(original.ModelId, loaded.ModelId);
            Assert.Equal(original.UserPlaylists.Count, loaded.UserPlaylists.Count);
            Assert.Equal(original.UserPlaylists[0].UserId, loaded.UserPlaylists[0].UserId);
            Assert.Equal(original.UserPlaylists[0].PlaylistId, loaded.UserPlaylists[0].PlaylistId);
            Assert.False(loaded.UserPlaylists[0].HandedOff);
            Assert.Null(loaded.UserPlaylists[1].PlaylistId);
            Assert.True(loaded.UserPlaylists[1].HandedOff);
        }

        [Fact]
        public void Save_Overwrite_KeepsOneFilePerCategory()
        {
            var category = Category();
            _store.Save(category);

            category.Name = "Renamed";
            category.Members.Add(Guid.NewGuid());
            _store.Save(category);

            Assert.Single(Directory.GetFiles(_directory, "*.json"));
            var loaded = _store.Get(category.Id);
            Assert.Equal("Renamed", loaded!.Name);
            Assert.Equal(3, loaded.Members.Count);
        }

        [Fact]
        public void GetAll_ReturnsAllSortedByName()
        {
            _store.Save(Category("Zebra Documentaries"));
            _store.Save(Category("Airplane Movies"));
            _store.Save(Category("Midnight Horror"));

            var all = _store.GetAll();

            Assert.Equal(
                ["Airplane Movies", "Midnight Horror", "Zebra Documentaries"],
                all.Select(c => c.Name).ToArray());
        }

        [Fact]
        public void GetAll_SkipsCorruptFiles()
        {
            _store.Save(Category("Good One"));
            File.WriteAllText(Path.Combine(_directory, "corrupt.json"), "{ not json");

            var all = _store.GetAll();

            Assert.Equal("Good One", Assert.Single(all).Name);
        }

        [Fact]
        public void Get_MissingCategory_ReturnsNull()
        {
            Assert.Null(_store.Get(Guid.NewGuid()));
        }

        [Fact]
        public void GetAll_MissingDirectory_ReturnsEmpty()
        {
            Assert.Empty(_store.GetAll());
        }

        [Fact]
        public void Delete_RemovesFile()
        {
            var category = Category();
            _store.Save(category);

            Assert.True(_store.Delete(category.Id));
            Assert.Null(_store.Get(category.Id));
            Assert.False(_store.Delete(category.Id));
        }

        [Fact]
        public void Save_LeavesNoTempFilesBehind()
        {
            _store.Save(Category());

            Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        }

        [Fact]
        public void GetOrAddLink_ReusesExistingLink()
        {
            var category = Category();
            var existingUser = category.UserPlaylists[0].UserId;

            var link = category.GetOrAddLink(existingUser);

            Assert.Same(category.UserPlaylists[0], link);
            Assert.Equal(2, category.UserPlaylists.Count);

            var newLink = category.GetOrAddLink(Guid.NewGuid());
            Assert.Equal(3, category.UserPlaylists.Count);
            Assert.Null(newLink.PlaylistId);
        }
    }
}
