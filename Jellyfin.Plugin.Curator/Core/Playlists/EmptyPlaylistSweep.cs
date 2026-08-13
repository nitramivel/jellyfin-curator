namespace Jellyfin.Plugin.Curator.Core.Playlists
{
    /// <summary>
    /// What the empty-playlist sweep should do with one playlist.
    /// </summary>
    public enum EmptyPlaylistVerdict
    {
        /// <summary>Leave it alone.</summary>
        Keep,

        /// <summary>
        /// An empty playlist with no owner at all — the shape a re-imported orphan
        /// directory takes, and one nothing else produces.
        /// </summary>
        Ghost,

        /// <summary>
        /// Curator's own playlist, tagged, holding nothing. Rule 7 says an empty
        /// category loses its playlist and keeps its definition; this is that
        /// playlist, left behind.
        /// </summary>
        Stranded,
    }

    /// <summary>
    /// Decides which empty playlists may be deleted.
    ///
    /// <para>
    /// This exists because "delete the empty playlists" is a sentence with a very
    /// bad reading available to it. A viewer who has made a playlist and not yet put
    /// anything in it owns an empty playlist, and deleting that is data loss caused
    /// by a cleanup button — the exact thing hard rule 6 forbids, arrived at through
    /// tidiness rather than malice. So the rule is not "empty" but "empty <b>and</b>
    /// demonstrably not a person's".
    /// </para>
    ///
    /// <para>
    /// Two shapes qualify and no others. A playlist carrying the <c>curator</c> tag
    /// is Curator's by the ownership contract and may be removed when it holds
    /// nothing. And a playlist with <b>no owner at all</b> cannot have been made by
    /// anybody: Jellyfin stamps the creating user onto every playlist made through
    /// the UI or the API, so an ownerless one is a directory Jellyfin found on disk
    /// and imported. Measured on the owner's server: 14 of them, created in a single
    /// second, all named after categories whose playlists Curator had deleted days
    /// earlier — the folders outlived the database rows and a later scan adopted
    /// them.
    /// </para>
    ///
    /// <para>
    /// Everything else is kept, including an untagged empty playlist that <i>does</i>
    /// have an owner. That is somebody's, and it stays.
    /// </para>
    /// </summary>
    public static class EmptyPlaylistSweep
    {
        /// <summary>
        /// Judges one playlist.
        /// </summary>
        /// <param name="hasItems">Whether it holds any members.</param>
        /// <param name="hasCuratorTag">Whether it carries Curator's ownership tag.</param>
        /// <param name="hasOwner">Whether Jellyfin records an owning user for it.</param>
        /// <returns>What to do with it.</returns>
        public static EmptyPlaylistVerdict Judge(bool hasItems, bool hasCuratorTag, bool hasOwner)
        {
            // Content is the first and strongest test, before ownership is even
            // considered. A playlist with something in it is never swept, whoever it
            // belongs to and whatever it is tagged with — the whole feature is about
            // rows that show nothing, and a sweep that can delete content is a
            // different and far more dangerous feature.
            if (hasItems)
            {
                return EmptyPlaylistVerdict.Keep;
            }

            if (hasCuratorTag)
            {
                return EmptyPlaylistVerdict.Stranded;
            }

            // Untagged and owned: a person's empty playlist, or one of Curator's that
            // a person untagged to keep. Rule 6 makes those permanently theirs, and
            // "it is empty" is not an exception to that — they may be about to fill
            // it.
            return hasOwner ? EmptyPlaylistVerdict.Keep : EmptyPlaylistVerdict.Ghost;
        }

        /// <summary>
        /// Whether a verdict means the playlist should be deleted.
        /// </summary>
        /// <param name="verdict">The verdict.</param>
        /// <returns>Whether to delete.</returns>
        public static bool ShouldPrune(EmptyPlaylistVerdict verdict)
            => verdict is EmptyPlaylistVerdict.Ghost or EmptyPlaylistVerdict.Stranded;
    }
}
