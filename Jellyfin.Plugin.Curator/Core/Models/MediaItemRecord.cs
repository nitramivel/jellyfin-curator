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
    /// The compact, LLM-facing reduction of a library item. This is almost everything
    /// the model ever sees about an item — no file paths, no watch history, no user
    /// data.
    /// <para>
    /// "Almost", because <see cref="PrimaryVersionId"/> and <see cref="ExternalId"/>
    /// are join keys for <see cref="DuplicateItems"/> and are never serialized into a
    /// prompt. <c>PromptBuilder</c> writes its fields one at a time rather than
    /// reflecting over this type, which is what keeps that true — and hard rule 1
    /// depends on it staying true, since an external ID is still an identifier the
    /// model has no business seeing.
    /// </para>
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

        /// <summary>
        /// Gets the item this one is an alternate version of, when Jellyfin's own
        /// version merge has been used. Null on a row that is nobody's alternate.
        /// </summary>
        /// <remarks>
        /// Read from <c>Video.PrimaryVersionId</c>, and it is the strongest duplicate
        /// signal there is: the owner has already told Jellyfin these two files are one
        /// film. Every other client hides the alternate behind a version picker on the
        /// primary's page, so a Curator row showing both is showing something the rest
        /// of the server treats as a single title.
        /// </remarks>
        public Guid? PrimaryVersionId { get; init; }

        /// <summary>
        /// Gets the item's identity at its metadata provider, as <c>tmdb:78</c> —
        /// null when nothing has been scraped for it.
        /// </summary>
        /// <remarks>
        /// One value rather than the whole provider dictionary, resolved in a fixed
        /// provider order by <c>ItemReducer</c>, so two rows either carry the same
        /// string or they do not. That is deliberately an exact equality test and not
        /// a similarity one: hard rule 2 forbids softening the title match, and this
        /// does not soften it — it asks a different and better question. "Blade
        /// Runner" (1982) and "Blade Runner: The Final Cut" (2007) agree on neither
        /// title nor year and are one film; "Freaky Friday" 2003 and 1995 agree on
        /// the title and are two, and their TMDb IDs say so.
        /// </remarks>
        public string? ExternalId { get; init; }

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
