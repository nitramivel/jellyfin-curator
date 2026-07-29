using Jellyfin.Plugin.Curator.Core.Playlists;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    public class PlaylistSyncDecisionTests
    {
        [Theory]
        // Handed off: nothing else matters, ever.
        [InlineData(true, false, false, false, SyncAction.Skip)]
        [InlineData(true, false, false, true, SyncAction.Skip)]
        [InlineData(true, true, false, true, SyncAction.Skip)]
        [InlineData(true, true, true, true, SyncAction.Skip)]
        [InlineData(true, true, true, false, SyncAction.Skip)]
        // Tag removed by user: hand off before any other consideration,
        // including an empty category (never delete an untagged playlist).
        [InlineData(false, true, false, true, SyncAction.HandOff)]
        [InlineData(false, true, false, false, SyncAction.HandOff)]
        // Empty category lifecycle: delete tagged playlist, keep definition.
        [InlineData(false, true, true, false, SyncAction.Delete)]
        [InlineData(false, false, false, false, SyncAction.Nothing)]
        // Normal lifecycle.
        [InlineData(false, false, false, true, SyncAction.Create)]
        [InlineData(false, true, true, true, SyncAction.Update)]
        public void Decide_CoversTheOwnershipMatrix(
            bool handedOff,
            bool playlistFound,
            bool tagPresent,
            bool hasMembers,
            SyncAction expected)
        {
            Assert.Equal(expected, PlaylistSyncDecision.Decide(handedOff, playlistFound, tagPresent, hasMembers));
        }
    }
}
