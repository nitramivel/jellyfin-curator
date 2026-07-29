namespace Jellyfin.Plugin.Curator.Core.Playlists
{
    /// <summary>
    /// What the sync pass should do for one (category, user) pair.
    /// </summary>
    public enum SyncAction
    {
        /// <summary>Link was handed off to the user earlier; touch nothing, forever.</summary>
        Skip,

        /// <summary>Playlist exists but no longer carries the ownership tag; record the handoff and never touch it again.</summary>
        HandOff,

        /// <summary>Category has members and no playlist exists; create one.</summary>
        Create,

        /// <summary>Category has members and a tagged playlist exists; update it in place.</summary>
        Update,

        /// <summary>Category is empty and a tagged playlist exists; remove the playlist, keep the definition.</summary>
        Delete,

        /// <summary>Category is empty and no playlist exists; nothing to do.</summary>
        Nothing,
    }

    /// <summary>
    /// The pure decision table for one (category, user) pair. Extracted so the
    /// ownership rules are testable without a Jellyfin server.
    /// </summary>
    public static class PlaylistSyncDecision
    {
        /// <summary>
        /// Decides the action for one user's link.
        /// </summary>
        /// <param name="handedOff">The link was previously handed off to the user.</param>
        /// <param name="playlistFound">The tracked playlist resolves to a live Jellyfin playlist.</param>
        /// <param name="tagPresent">That playlist still carries Curator's ownership tag.</param>
        /// <param name="hasMembers">The category currently has members.</param>
        /// <returns>The action.</returns>
        public static SyncAction Decide(bool handedOff, bool playlistFound, bool tagPresent, bool hasMembers)
        {
            if (handedOff)
            {
                return SyncAction.Skip;
            }

            // Ownership check comes before everything else: a playlist without our
            // tag belongs to the user now, even if the category emptied out.
            if (playlistFound && !tagPresent)
            {
                return SyncAction.HandOff;
            }

            if (!hasMembers)
            {
                return playlistFound ? SyncAction.Delete : SyncAction.Nothing;
            }

            return playlistFound ? SyncAction.Update : SyncAction.Create;
        }
    }
}
