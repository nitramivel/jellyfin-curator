using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Curator.Core.Models
{
    /// <summary>
    /// A category that survived reconciliation: batch proposals merged, small
    /// categories dropped, total capped. This is what Phase 4 turns into playlists.
    /// </summary>
    public sealed record ReconciledCategory
    {
        /// <summary>Gets the category name.</summary>
        public required string Name { get; init; }

        /// <summary>Gets the one-line description.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Gets the member item IDs, strongest-first (round-robin merged across source proposals).</summary>
        public required IReadOnlyList<Guid> Members { get; init; }

        /// <summary>
        /// Gets how many batch proposals merged into this category. Categories
        /// proposed independently across many batches are library-wide threads,
        /// and rank above single-batch ones when the cap applies.
        /// </summary>
        public required int SourceProposalCount { get; init; }

        /// <summary>
        /// Gets the individual proposals that merged into this category, founder
        /// first. Always <see cref="SourceProposalCount"/> entries long.
        /// </summary>
        public IReadOnlyList<CategorySourceProposal> SourceProposals { get; init; } = [];
    }
}
