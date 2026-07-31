using System;
using Jellyfin.Plugin.Curator.Configuration;
using Jellyfin.Plugin.Curator.Core;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Which collections ride along on an item, and the one thing the two modes must
    /// not do: quietly become each other. An empty name list means "send none" when
    /// the owner is naming collections, and a run that surfaces everything must do it
    /// because the owner asked, never because a box was left blank.
    /// </summary>
    public class CollectionSurfacingTests
    {
        private static readonly string[] Awards = ["Oscar Winners", "Oscar Nominees"];

        [Fact]
        public void NamedMode_SurfacesOnlyTheNamedCollections()
        {
            var named = CollectionSurfacing.ParseNames("Oscar Winners, Oscar Nominees");

            Assert.True(CollectionSurfacing.ShouldSurface("Oscar Winners", named, surfaceAll: false));
            Assert.True(CollectionSurfacing.ShouldSurface("Oscar Nominees", named, surfaceAll: false));
            Assert.False(CollectionSurfacing.ShouldSurface("Marvel", named, surfaceAll: false));
            Assert.False(CollectionSurfacing.ShouldSurface("Star Wars Collection", named, surfaceAll: false));
        }

        [Fact]
        public void AllMode_SurfacesEverythingIncludingFranchises()
        {
            // The whole point of the setting, and the risk it takes on: the franchise
            // collections the named list existed to keep out now reach the model.
            var named = CollectionSurfacing.ParseNames("Oscar Winners");

            Assert.True(CollectionSurfacing.ShouldSurface("Marvel", named, surfaceAll: true));
            Assert.True(CollectionSurfacing.ShouldSurface("Star Wars Collection", named, surfaceAll: true));
            Assert.True(CollectionSurfacing.ShouldSurface("Oscar Winners", named, surfaceAll: true));
        }

        [Fact]
        public void AllMode_IgnoresTheNameListEntirelyEvenWhenItIsEmpty()
        {
            var named = CollectionSurfacing.ParseNames(null);

            Assert.True(CollectionSurfacing.SurfacesAnything(named, surfaceAll: true));
            Assert.True(CollectionSurfacing.ShouldSurface("The Beatles", named, surfaceAll: true));
        }

        [Fact]
        public void NamedMode_WithAnEmptyListSurfacesNothing()
        {
            // Clearing the box has always meant "send no collection membership at
            // all". It must not flip to meaning "send all of it" now that the other
            // mode exists.
            var named = CollectionSurfacing.ParseNames("   ");

            Assert.False(CollectionSurfacing.SurfacesAnything(named, surfaceAll: false));
            Assert.False(CollectionSurfacing.ShouldSurface("Oscar Winners", named, surfaceAll: false));
        }

        [Fact]
        public void ANamelessCollectionIsNeverSurfaced()
        {
            // The name is what gets written into the item's "in" list, so a blank one
            // reads to the model as a collection called nothing.
            var named = CollectionSurfacing.ParseNames("Oscar Winners");

            foreach (var blank in new[] { null, string.Empty, "   " })
            {
                Assert.False(CollectionSurfacing.ShouldSurface(blank, named, surfaceAll: true));
                Assert.False(CollectionSurfacing.ShouldSurface(blank, named, surfaceAll: false));
            }
        }

        [Fact]
        public void NamesAreMatchedCaseInsensitivelyAndTrimmed()
        {
            // They are typed by hand into a text box.
            var named = CollectionSurfacing.ParseNames("  oscar winners ,, OSCAR NOMINEES  ");

            Assert.Equal(2, named.Count);
            Assert.True(CollectionSurfacing.ShouldSurface("Oscar Winners", named, surfaceAll: false));
            Assert.True(CollectionSurfacing.ShouldSurface("Oscar Nominees", named, surfaceAll: false));
        }

        [Fact]
        public void TheDefaultIsToSendEveryCollection()
        {
            var config = new PluginConfiguration();

            Assert.True(config.SurfaceAllCollections);

            // The curated list stays populated underneath, so turning the toggle off
            // returns to awards-only rather than to nothing at all.
            var named = CollectionSurfacing.ParseNames(config.SurfacedCollections);
            Assert.Equal(Awards.Length, named.Count);
            foreach (var award in Awards)
            {
                Assert.Contains(award, named, StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
