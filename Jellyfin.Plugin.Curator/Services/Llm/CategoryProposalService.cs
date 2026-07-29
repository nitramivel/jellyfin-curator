using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Core.Llm;
using Jellyfin.Plugin.Curator.Core.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services.Llm
{
    /// <summary>
    /// The outcome of a full proposal run over one library snapshot.
    /// </summary>
    /// <param name="Proposals">All validated proposals from all completed batches.</param>
    /// <param name="InputTokens">Total input tokens consumed.</param>
    /// <param name="OutputTokens">Total output tokens consumed.</param>
    /// <param name="BatchesCompleted">Batches successfully processed.</param>
    /// <param name="BatchesSkipped">Batches skipped: budget exhaustion or unparseable responses.</param>
    public sealed record ProposalRunResult(
        IReadOnlyList<CategoryProposal> Proposals,
        long InputTokens,
        long OutputTokens,
        int BatchesCompleted,
        int BatchesSkipped);

    /// <summary>
    /// Settings for one proposal run.
    /// </summary>
    /// <param name="BatchSize">Items per LLM request.</param>
    /// <param name="MaxOutputTokens">Output cap per LLM request.</param>
    /// <param name="TokenBudget">Hard cap on input+output tokens for the run; 0 disables.</param>
    /// <param name="InputCostPerMillion">Input USD per million tokens for the cost log; 0 omits cost.</param>
    /// <param name="OutputCostPerMillion">Output USD per million tokens for the cost log; 0 omits cost.</param>
    public sealed record ProposalRunSettings(
        int BatchSize,
        int MaxOutputTokens,
        long TokenBudget,
        decimal InputCostPerMillion = 0,
        decimal OutputCostPerMillion = 0);

    /// <summary>
    /// Drives the batch → prompt → complete → parse pipeline over a reduced
    /// library snapshot, enforcing the run token budget.
    /// </summary>
    public class CategoryProposalService
    {
        private readonly ILogger<CategoryProposalService> _logger;

        public CategoryProposalService(ILogger<CategoryProposalService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Runs all batches through the provider and collects validated proposals.
        /// A batch whose response cannot be parsed is logged and skipped rather
        /// than failing the run; a run that would exceed the token budget stops early.
        /// </summary>
        /// <param name="provider">The LLM provider.</param>
        /// <param name="records">The reduced library snapshot.</param>
        /// <param name="settings">Run settings.</param>
        /// <param name="activity">Per-item watch activity for the target user, or null for a non-personalized run.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The aggregated result.</returns>
        public async Task<ProposalRunResult> ProposeAsync(
            ILlmProvider provider,
            IReadOnlyList<MediaItemRecord> records,
            ProposalRunSettings settings,
            IReadOnlyDictionary<Guid, UserActivity>? activity = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentNullException.ThrowIfNull(records);
            ArgumentNullException.ThrowIfNull(settings);

            var batches = Batcher.Split(records, settings.BatchSize);
            var proposals = new List<CategoryProposal>();
            long inputTokens = 0;
            long outputTokens = 0;
            var completed = 0;
            var skipped = 0;

            for (var i = 0; i < batches.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (settings.TokenBudget > 0 && inputTokens + outputTokens >= settings.TokenBudget)
                {
                    skipped += batches.Count - i;
                    _logger.LogWarning(
                        "Curator: token budget {Budget} exhausted after {Completed} of {Total} batches; skipping the rest",
                        settings.TokenBudget,
                        completed,
                        batches.Count);
                    break;
                }

                var batch = batches[i];
                var request = new LlmRequest(
                    PromptBuilder.SystemPrompt,
                    PromptBuilder.BuildUserPrompt(batch, activity),
                    settings.MaxOutputTokens);

                var result = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                inputTokens += result.InputTokens;
                outputTokens += result.OutputTokens;

                if (result.Truncated)
                {
                    _logger.LogWarning(
                        "Curator: batch {Batch} response was truncated at {MaxTokens} output tokens; proposals from it may be incomplete. Consider a smaller batch size or a larger output cap.",
                        i,
                        settings.MaxOutputTokens);
                }

                try
                {
                    var parsed = ProposalParser.Parse(result.Text, batch);
                    proposals.AddRange(parsed.Proposals);
                    completed++;

                    if (parsed.DiscardedMemberCount > 0 || parsed.DiscardedCategoryCount > 0)
                    {
                        _logger.LogInformation(
                            "Curator: batch {Batch} discarded {Members} invalid member reference(s) and {Categories} unusable categor(ies)",
                            i,
                            parsed.DiscardedMemberCount,
                            parsed.DiscardedCategoryCount);
                    }
                }
                catch (FormatException ex)
                {
                    skipped++;
                    _logger.LogWarning(
                        ex,
                        "Curator: batch {Batch} produced an unparseable response; skipping it",
                        i);
                }
            }

            LogRunTotals(provider, settings, inputTokens, outputTokens, proposals.Count, completed, skipped);
            return new ProposalRunResult(proposals, inputTokens, outputTokens, completed, skipped);
        }

        private void LogRunTotals(
            ILlmProvider provider,
            ProposalRunSettings settings,
            long inputTokens,
            long outputTokens,
            int proposalCount,
            int completed,
            int skipped)
        {
            if (settings.InputCostPerMillion > 0 || settings.OutputCostPerMillion > 0)
            {
                var cost = (inputTokens * settings.InputCostPerMillion
                    + outputTokens * settings.OutputCostPerMillion) / 1_000_000m;
                _logger.LogInformation(
                    "Curator run: model {Model}, {Input} input + {Output} output tokens (~${Cost:F2}), {Proposals} proposals from {Completed} batches ({Skipped} skipped)",
                    provider.ModelId,
                    inputTokens,
                    outputTokens,
                    cost,
                    proposalCount,
                    completed,
                    skipped);
            }
            else
            {
                _logger.LogInformation(
                    "Curator run: model {Model}, {Input} input + {Output} output tokens, {Proposals} proposals from {Completed} batches ({Skipped} skipped)",
                    provider.ModelId,
                    inputTokens,
                    outputTokens,
                    proposalCount,
                    completed,
                    skipped);
            }
        }
    }
}
