using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Curator.Core.Models
{
    /// <summary>
    /// The kind of library item a record describes.
    /// </summary>
    public enum MediaKind
    {
        /// <summary>A movie.</summary>
        Movie = 0,

        /// <summary>A TV series (the whole show, not an episode).</summary>
        Series = 1,

        /// <summary>A single episode of a series.</summary>
        Episode = 2,
    }

    /// <summary>
    /// The compact, LLM-facing reduction of a library item. This is everything the model
    /// ever sees about an item — no file paths, no watch history, no user data.
    /// </summary>
    public sealed record MediaItemRecord
    {
        /// <summary>Gets the Jellyfin item ID. The model must echo this back; it is the only join key.</summary>
        public required Guid Id { get; init; }

        /// <summary>Gets the kind of item.</summary>
        public required MediaKind Kind { get; init; }

        /// <summary>Gets the display title.</summary>
        public required string Name { get; init; }

        /// <summary>Gets the production year, when known.</summary>
        public int? Year { get; init; }

        /// <summary>Gets the genres.</summary>
        public IReadOnlyList<string> Genres { get; init; } = [];

        /// <summary>Gets the tags.</summary>
        public IReadOnlyList<string> Tags { get; init; } = [];

        /// <summary>Gets the official (parental) rating, e.g. "PG-13".</summary>
        public string? OfficialRating { get; init; }

        /// <summary>Gets the runtime in whole minutes, when known.</summary>
        public int? RuntimeMinutes { get; init; }

        /// <summary>Gets the community rating (0-10), when known.</summary>
        public float? CommunityRating { get; init; }

        /// <summary>Gets the overview, truncated to the reducer's limit.</summary>
        public string? Overview { get; init; }

        /// <summary>
        /// Gets the names of surfaced collections this item belongs to, e.g.
        /// "Oscar Winners". Empty for an item in none of them.
        /// </summary>
        /// <remarks>
        /// Only collections the owner has named in configuration appear here. The
        /// point is to hand the model a judgement about a film — that it won an
        /// Oscar — not to hand it a franchise to file things under, which is exactly
        /// the metadata-shaped category the system prompt tells it to avoid.
        /// </remarks>
        public IReadOnlyList<string> Collections { get; init; } = [];

        /// <summary>Gets the parent series name. Episodes only.</summary>
        public string? SeriesName { get; init; }

        /// <summary>Gets the parent series ID. Episodes only.</summary>
        public Guid? SeriesId { get; init; }

        /// <summary>Gets the season number. Episodes only.</summary>
        public int? SeasonNumber { get; init; }

        /// <summary>Gets the episode number within the season. Episodes only.</summary>
        public int? EpisodeNumber { get; init; }
    }
}
