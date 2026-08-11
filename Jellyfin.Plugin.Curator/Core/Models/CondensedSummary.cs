using System;
using System.Collections.Generic;

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

        /// <summary>
        /// Gets the consolidated tags for this item: the scraped keyword list
        /// reduced to the few that actually describe watching it.
        /// <para>
        /// Deliberately <em>not</em> a fixed count. A scraped list runs to about
        /// eighteen values an item and is mostly production trivia
        /// (aftercreditsstinger, based on novel or book, sequel) mixed with a
        /// handful of real mood words. Taking the first N keeps whatever the
        /// scraper happened to order first; asking for however many genuinely
        /// apply lets a richly-textured film keep six and a thin one keep two.
        /// Empty when tag consolidation has not been run for this item.
        /// </para>
        /// </summary>
        public IReadOnlyList<string> Tags { get; init; } = [];

        /// <summary>
        /// Gets the hash of the raw tag list these were consolidated from, so a
        /// re-scrape that changes the keywords can be detected the same way a
        /// rewritten overview is. Null when tags were never consolidated.
        /// </summary>
        public string? TagSourceHash { get; init; }

        /// <summary>
        /// Gets the weather this item suits, in <c>ContextVocabulary</c>'s closed
        /// vocabulary. Empty when the item suits no weather in particular, and also
        /// when context classification has never been run for it —
        /// <see cref="ContextSourceHash"/> is what tells those apart.
        /// </summary>
        public IReadOnlyList<string> Weather { get; init; } = [];

        /// <summary>
        /// Gets the parts of the day this item suits, in <c>ContextVocabulary</c>'s
        /// closed vocabulary. Empty on the same two conditions as
        /// <see cref="Weather"/>.
        /// </summary>
        public IReadOnlyList<string> Dayparts { get; init; } = [];

        /// <summary>
        /// Gets the hash of the overview these affinities were judged from, so a
        /// rewritten overview re-opens the judgement the same way it re-opens the
        /// summary. Null when context was never classified for this item.
        /// </summary>
        /// <remarks>
        /// A separate hash from <see cref="SourceHash"/> even though both passes read
        /// the same overview in the same call, and the reason is the same one
        /// <see cref="TagSourceHash"/> exists: switching the feature on has to be
        /// incremental. Without it, every item stored before context classification
        /// existed looks current by its summary hash and would never be re-queued, so
        /// the setting would appear to do nothing until the next metadata refresh.
        /// <para>
        /// An empty answer with a hash set is a real judgement — the model read the
        /// item and decided it suits no particular weather — and must not be
        /// re-bought on every pass.
        /// </para>
        /// </remarks>
        public string? ContextSourceHash { get; init; }

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
