using Jellyfin.Plugin.Curator.Core;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Keeps items left behind by a removed or remounted library out of the prompt.
    /// Measured on the owner's server: 36 of 298 movies and series sat under
    /// /storage/, a mount that no longer exists, and the model picked from them as
    /// readily as from anything real.
    /// </summary>
    public class LibraryPathFilterTests
    {
        private static readonly string[] Roots =
            ["/data/Movies", "/data/Shows", "/external/Movies", "/external-2/Movies"];

        [Theory]
        [InlineData("/data/Movies/Arrival (2016)/Arrival.mkv")]
        [InlineData("/external/Movies/Help (1965)/Help.avi")]
        [InlineData("/data/Shows/The Beatles Anthology")]
        [InlineData("/data/Movies")]
        public void ItemsInsideALibraryFolderAreKept(string path)
        {
            Assert.True(LibraryPathFilter.IsInsideLibrary(path, Roots));
        }

        /// <summary>The exact shape of the orphaned rows on the owner's server.</summary>
        [Theory]
        [InlineData("/storage/Movies/Beatles 64 (2024)/Beatles 64 (2024).mp4")]
        [InlineData("/storage/Movies/Help (1965)/Help (1965).avi")]
        [InlineData("/mnt/old-nas/Movies/Arrival.mkv")]
        public void ItemsOutsideEveryLibraryFolderAreDropped(string path)
        {
            Assert.False(LibraryPathFilter.IsInsideLibrary(path, Roots));
        }

        /// <summary>
        /// A prefix match alone would let "/data/Movies2" pass as "/data/Movies",
        /// quietly re-admitting a whole tree the server does not serve.
        /// </summary>
        [Fact]
        public void ASiblingFolderSharingAPrefixIsNotInside()
        {
            Assert.False(LibraryPathFilter.IsInsideLibrary("/data/Movies2/Arrival.mkv", Roots));
            Assert.False(LibraryPathFilter.IsInsideLibrary("/data/MoviesOld/Arrival.mkv", Roots));
        }

        /// <summary>
        /// Failing closed would empty the library — and every category with it — the
        /// first time the folder list could not be read. Failing open costs at worst
        /// the dead rows we had before.
        /// </summary>
        [Fact]
        public void WithNoKnownRootsEverythingIsKept()
        {
            Assert.True(LibraryPathFilter.IsInsideLibrary("/storage/Movies/Anything.mkv", null));
            Assert.True(LibraryPathFilter.IsInsideLibrary("/storage/Movies/Anything.mkv", []));
        }

        [Fact]
        public void AnItemWithNoPathCannotBeShownToBelong()
        {
            Assert.False(LibraryPathFilter.IsInsideLibrary(null, Roots));
            Assert.False(LibraryPathFilter.IsInsideLibrary("   ", Roots));
        }

        [Fact]
        public void TrailingSeparatorsOnARootDoNotChangeTheAnswer()
        {
            Assert.True(LibraryPathFilter.IsInsideLibrary("/data/Movies/x.mkv", ["/data/Movies/"]));
            Assert.True(LibraryPathFilter.IsInsideLibrary(@"C:\Media\x.mkv", [@"C:\Media\"]));
        }

        [Fact]
        public void WindowsPathsMatchRegardlessOfCase()
        {
            Assert.True(LibraryPathFilter.IsInsideLibrary(@"c:\media\Movies\x.mkv", [@"C:\Media"]));
            Assert.False(LibraryPathFilter.IsInsideLibrary(@"D:\Other\x.mkv", [@"C:\Media"]));
        }

        [Fact]
        public void ARootOfSlashContainsEverything()
        {
            Assert.True(LibraryPathFilter.IsInsideLibrary("/anywhere/at/all.mkv", ["/"]));
        }
    }
}
