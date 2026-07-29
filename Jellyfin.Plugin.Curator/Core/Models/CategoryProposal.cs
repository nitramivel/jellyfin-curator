using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Curator.Core.Models
{
    /// <summary>
    /// One category proposed by the LLM for one batch, already validated:
    /// members are real item IDs from the batch that produced it, in the
    /// model's confidence order.
    /// </summary>
    public sealed record CategoryProposal
    {
        /// <summary>Gets the category name, e.g. "Cerebral Sci-Fi".</summary>
        public required string Name { get; init; }

        /// <summary>Gets the one-line description.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Gets the member item IDs in confidence order.</summary>
        public required IReadOnlyList<Guid> Members { get; init; }
    }
}
