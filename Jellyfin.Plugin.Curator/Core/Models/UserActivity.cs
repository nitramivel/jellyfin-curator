namespace Jellyfin.Plugin.Curator.Core.Models
{
    /// <summary>
    /// A user's watch activity for one library item — the personalization signal
    /// sent to the LLM when personalized playlists are enabled. Contains no
    /// identity information; it is keyed by item, not by user, on the wire.
    /// </summary>
    public sealed record UserActivity
    {
        /// <summary>Gets a value indicating whether the user has watched the item.</summary>
        public bool Played { get; init; }

        /// <summary>Gets the number of times the user has played the item.</summary>
        public int PlayCount { get; init; }

        /// <summary>Gets a value indicating whether the user marked the item a favorite.</summary>
        public bool IsFavorite { get; init; }

        /// <summary>Gets the user's personal rating (0-10), when set.</summary>
        public float? UserRating { get; init; }

        /// <summary>Gets whole days since the user last played the item, when known.</summary>
        public int? DaysSinceLastPlayed { get; init; }

        /// <summary>
        /// Gets how many of the series' episodes the user has played. Series only;
        /// null for movies and episodes.
        /// </summary>
        /// <remarks>
        /// Set by <see cref="Core.SeriesActivityRollup"/>, never read straight off a
        /// series' user data — Jellyfin does not keep watch depth there.
        /// </remarks>
        public int? EpisodesPlayed { get; init; }

        /// <summary>
        /// Gets how many episodes of the series the library holds. Series only; null
        /// for movies and episodes.
        /// </summary>
        public int? EpisodeCount { get; init; }
    }
}
