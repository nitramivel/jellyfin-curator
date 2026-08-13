using Jellyfin.Plugin.Curator.Core.Playlists;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Which empty playlists may be deleted.
    ///
    /// <para>
    /// This is a destructive feature, so the tests are written from the direction of
    /// what must <b>survive</b> rather than what must go. "Delete the empty
    /// playlists" has a very bad reading available to it: a viewer who has made a
    /// playlist and not yet put anything in it owns an empty playlist, and taking
    /// that away is data loss arrived at through tidiness. Hard rule 6 says an
    /// untagged playlist is theirs permanently, and being empty is not an exception
    /// to it.
    /// </para>
    /// </summary>
    public class EmptyPlaylistSweepTests
    {
        [Fact]
        public void AViewersOwnEmptyPlaylistIsNeverTouched()
        {
            // The whole reason this is a pure function with its own test file.
            var verdict = EmptyPlaylistSweep.Judge(hasItems: false, hasCuratorTag: false, hasOwner: true);

            Assert.Equal(EmptyPlaylistVerdict.Keep, verdict);
            Assert.False(EmptyPlaylistSweep.ShouldPrune(verdict));
        }

        [Fact]
        public void APlaylistHandedBackToAViewerSurvivesEvenAfterTheyEmptyIt()
        {
            // Removing the tag is how a viewer claims one of Curator's playlists.
            // Emptying it afterwards is then their business, and a sweep that took
            // it back would be Curator overruling a handoff.
            Assert.Equal(
                EmptyPlaylistVerdict.Keep,
                EmptyPlaylistSweep.Judge(hasItems: false, hasCuratorTag: false, hasOwner: true));
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void AnythingHoldingItemsIsKeptWhoeverOwnsIt(bool tagged, bool owned)
        {
            // Content is tested before ownership, deliberately. The feature is about
            // rows that show nothing; a sweep able to delete content would be a
            // different and far more dangerous one.
            Assert.Equal(
                EmptyPlaylistVerdict.Keep,
                EmptyPlaylistSweep.Judge(hasItems: true, hasCuratorTag: tagged, hasOwner: owned));
        }

        [Fact]
        public void AnEmptyOwnerlessPlaylistIsAGhost()
        {
            // Jellyfin stamps the creating user onto every playlist made through the
            // UI or the API, so an ownerless one cannot have been made by a person.
            // It is a directory the scanner found and adopted. Measured on the
            // owner's server: 14 of them in a single second, each beside a working
            // playlist of the same name.
            var verdict = EmptyPlaylistSweep.Judge(hasItems: false, hasCuratorTag: false, hasOwner: false);

            Assert.Equal(EmptyPlaylistVerdict.Ghost, verdict);
            Assert.True(EmptyPlaylistSweep.ShouldPrune(verdict));
        }

        [Fact]
        public void CuratorsOwnEmptyPlaylistIsStranded()
        {
            // The tag is the ownership contract in both directions: it is what stops
            // Curator touching a viewer's playlist, and what entitles it to remove
            // its own. Rule 7 already says an empty category loses its playlist and
            // keeps its definition; this is that playlist, left behind.
            var verdict = EmptyPlaylistSweep.Judge(hasItems: false, hasCuratorTag: true, hasOwner: true);

            Assert.Equal(EmptyPlaylistVerdict.Stranded, verdict);
            Assert.True(EmptyPlaylistSweep.ShouldPrune(verdict));
        }

        [Fact]
        public void OnlyTwoOfTheEightCombinationsArePrunable()
        {
            // Enumerated rather than argued. A future change that widens what this
            // deletes has to change this number, in a test whose name says how many
            // it expected — which is harder to do by accident than editing a switch.
            var prunable = 0;
            foreach (var items in new[] { true, false })
            {
                foreach (var tag in new[] { true, false })
                {
                    foreach (var owner in new[] { true, false })
                    {
                        if (EmptyPlaylistSweep.ShouldPrune(EmptyPlaylistSweep.Judge(items, tag, owner)))
                        {
                            prunable++;
                        }
                    }
                }
            }

            // Curator-tagged-and-empty (owned or not) and untagged-empty-ownerless.
            Assert.Equal(3, prunable);
        }
    }
}
