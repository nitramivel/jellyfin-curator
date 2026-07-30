namespace Jellyfin.Plugin.Curator.Core.Models
{
    /// <summary>
    /// One LLM proposal that merged into a reconciled category, kept for display.
    /// <para>
    /// Reconciliation collapses proposals from different batches — and, with
    /// personalization on, different passes — into a single category under the
    /// founder's name. That merge is otherwise invisible: only the count survived,
    /// so there was no way to see that "Cerebral Sci-Fi" and "Thinky Science
    /// Fiction" were the same thread found twice.
    /// </para>
    /// </summary>
    public sealed record CategorySourceProposal
    {
        /// <summary>Gets the name the model gave this proposal.</summary>
        public required string Name { get; init; }

        /// <summary>Gets the description the model gave this proposal.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Gets how many members this proposal contributed before merging.</summary>
        public required int MemberCount { get; init; }
    }
}
