using System;

namespace Jellyfin.Plugin.Curator.Core.Models
{
    /// <summary>
    /// One item's condensed summary: a short, tone-carrying rewrite of the
    /// Jellyfin overview, distilled once by a model and reused by every later run.
    /// </summary>
    /// <remarks>
    /// This is a Curator-side cache and never touches the library. Jellyfin's own
    /// <c>Overview</c> is left exactly as the metadata provider wrote it — the
    /// condensed text lives only in the plugin data directory, so deleting every
    /// summary is a safe, lossless operation and the originals cannot be damaged
    /// by anything here.
    /// </remarks>
    public sealed record CondensedSummary
    {
        /// <summary>Gets the Jellyfin item ID this summary describes.</summary>
        public required Guid ItemId { get; init; }

        /// <summary>Gets the condensed text sent to the model in place of the overview.</summary>
        public required string Text { get; init; }

        /// <summary>
        /// Gets the hash of the overview this was distilled from.
        /// <para>
        /// This is what makes re-running cheap and correct. A metadata refresh can
        /// rewrite an overview underneath us, and a summary distilled from the old
        /// text would then quietly describe the wrong thing forever. Comparing the
        /// hash finds exactly those items, so a re-run pays only for what actually
        /// changed instead of the whole library.
        /// </para>
        /// </summary>
        public required string SourceHash { get; init; }

        /// <summary>Gets the model that produced this text.</summary>
        public string? ModelId { get; init; }

        /// <summary>Gets when this summary was produced (UTC).</summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>Gets the title at distillation time, for display in the settings page.</summary>
        public string? Title { get; init; }

        /// <summary>Gets the length of the overview this replaced, for reporting the saving.</summary>
        public int SourceLength { get; init; }
    }
}
