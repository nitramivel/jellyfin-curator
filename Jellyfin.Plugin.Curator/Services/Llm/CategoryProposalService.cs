using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Core.Llm;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Services.Runs;
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
    /// <param name="CacheWriteTokens">Input tokens written to the prompt cache.</param>
    /// <param name="CacheReadTokens">Input tokens served from the prompt cache.</param>
    public sealed record ProposalRunResult(
        IReadOnlyList<CategoryProposal> Proposals,
        long InputTokens,
        long OutputTokens,
        int BatchesCompleted,
        int BatchesSkipped,
        long CacheWriteTokens = 0,
        long CacheReadTokens = 0);

    /// <summary>
    /// The outcome of one viewer's pass.
    /// </summary>
    /// <param name="NewProposals">Categories invented for this viewer alone.</param>
    /// <param name="InputTokens">Uncached input tokens consumed.</param>
    /// <param name="OutputTokens">Output tokens consumed.</param>
    /// <param name="BatchesSkipped">Batches whose response could not be parsed.</param>
    /// <param name="CacheWriteTokens">Input tokens written to the prompt cache.</param>
    /// <param name="CacheReadTokens">Input tokens served from the prompt cache.</param>
    public sealed record PersonalRunResult(
        IReadOnlyList<CategoryProposal> NewProposals,
        long InputTokens,
        long OutputTokens,
        int BatchesSkipped,
        long CacheWriteTokens = 0,
        long CacheReadTokens = 0);

    /// <summary>
    /// Settings for one proposal run.
    /// </summary>
    /// <param name="BatchSize">Items per LLM request.</param>
    /// <param name="MaxOutputTokens">Output cap per LLM request.</param>
    /// <param name="TokenBudget">Hard cap on input+output tokens for the run; 0 disables.</param>
    /// <param name="InputCostPerMillion">Input USD per million tokens for the cost log; 0 omits cost.</param>
    /// <param name="OutputCostPerMillion">Output USD per million tokens for the cost log; 0 omits cost.</param>
    /// <param name="UseBatchApi">Submit through the provider's async batch endpoint when it has one.</param>
    /// <param name="MaxTagsPerItem">Tags per item sent to the model; 0 omits them.</param>
    /// <param name="SharedLimits">
    /// Limits for the shared pool. This same instance is what the Reconciler is
    /// given, so what the prompt asks for is what gets enforced.
    /// </param>
    /// <param name="PersonalLimits">Limits for the personal pool, on the same terms.</param>
    public sealed record ProposalRunSettings(
        int BatchSize,
        int MaxOutputTokens,
        long TokenBudget,
        decimal InputCostPerMillion = 0,
        decimal OutputCostPerMillion = 0,
        bool UseBatchApi = false,
        int MaxTagsPerItem = 0,
        CategoryLimits? SharedLimits = null,
        CategoryLimits? PersonalLimits = null)
    {
        /// <summary>Gets the shared-pool limits, defaulted for callers that supply none.</summary>
        public CategoryLimits Shared => SharedLimits ?? new CategoryLimits(6);

        /// <summary>Gets the personal-pool limits, defaulted for callers that supply none.</summary>
        public CategoryLimits Personal => PersonalLimits ?? new CategoryLimits(2);
    }

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
        /// <param name="runLog">Recorder for this run; defaults to recording nothing.</param>
        /// <returns>The aggregated result.</returns>
        public async Task<ProposalRunResult> ProposeAsync(
            ILlmProvider provider,
            IReadOnlyList<MediaItemRecord> records,
            ProposalRunSettings settings,
            IReadOnlyDictionary<Guid, UserActivity>? activity = null,
            CancellationToken cancellationToken = default,
            IRunLog? runLog = null)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentNullException.ThrowIfNull(records);
            ArgumentNullException.ThrowIfNull(settings);

            var log = runLog ?? NullRunLog.Instance;
            var batches = Batcher.Split(records, settings.BatchSize);

            if (settings.UseBatchApi && provider is IBatchLlmProvider batchProvider)
            {
                return await ProposeViaBatchApiAsync(
                    provider, batchProvider, batches, settings, activity, cancellationToken, log).ConfigureAwait(false);
            }

            var proposals = new List<CategoryProposal>();
            long inputTokens = 0;
            long outputTokens = 0;
            long cacheWriteTokens = 0;
            long cacheReadTokens = 0;
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
                    PromptBuilder.BuildSystemPrompt(settings.Shared),
                    PromptBuilder.BuildItemList(batch, settings.MaxTagsPerItem),
                    PromptBuilder.BuildActivitySection(batch, activity),
                    settings.MaxOutputTokens,
                    ResponseShape.Categories);

                // One retry on a malformed response. The model occasionally emits
                // invalid JSON — an unescaped quote inside a description is the usual
                // culprit — and a second sample almost always comes back clean. This
                // costs one extra call on failure rather than losing a whole user's
                // categories for the run.
                ParseResult? parsed = null;
                for (var attempt = 0; attempt < 2 && parsed is null; attempt++)
                {
                    var startedAt = Stopwatch.GetTimestamp();
                    LlmResult result;
                    try
                    {
                        result = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Recorded before rethrowing: a run that dies on a provider
                        // error is precisely the one whose prompt someone will want
                        // to read afterwards.
                        log.LlmCall(
                            "discovery", i, attempt + 1, null, Stopwatch.GetElapsedTime(startedAt),
                            request, null, "error", ex.Message);
                        throw;
                    }

                    inputTokens += result.InputTokens;
                    outputTokens += result.OutputTokens;
                    cacheWriteTokens += result.CacheWriteTokens;
                    cacheReadTokens += result.CacheReadTokens;

                    WarnIfTruncated(i, result, settings);

                    string outcome;
                    string? parseError = null;
                    try
                    {
                        parsed = ProposalParser.Parse(result.Text, batch);
                        outcome = "ok";
                    }
                    catch (FormatException ex)
                    {
                        var lastAttempt = attempt == 1;
                        outcome = "unparseable";
                        parseError = ex.Message;
                        _logger.LogWarning(
                            ex,
                            "Curator: batch {Batch} produced an unparseable response{Action}. Response was: {Response}",
                            i,
                            lastAttempt ? "; skipping it" : "; retrying once",
                            Excerpt(result.Text));
                    }

                    log.LlmCall(
                        "discovery", i, attempt + 1, null, Stopwatch.GetElapsedTime(startedAt),
                        request, result, outcome, parseError);
                }

                if (parsed is null)
                {
                    skipped++;
                }
                else
                {
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
            }

            LogRunTotals(provider, settings, inputTokens, outputTokens, proposals.Count, completed, skipped);

            if (cacheWriteTokens > 0 || cacheReadTokens > 0)
            {
                _logger.LogInformation(
                    "Curator: prompt cache — {Read} tokens read, {Write} written. A read count of zero across passes means the cached prefix is not matching.",
                    cacheReadTokens,
                    cacheWriteTokens);
            }

            return new ProposalRunResult(
                proposals,
                inputTokens,
                outputTokens,
                completed,
                skipped,
                cacheWriteTokens,
                cacheReadTokens);
        }

        /// <summary>
        /// Runs one viewer's pass: the same library (so the cached prefix still
        /// matches) plus the categories the shared pass found and this viewer's
        /// history. The model picks which shared categories suit them and invents
        /// new ones of its own.
        /// </summary>
        /// <param name="provider">The LLM provider.</param>
        /// <param name="records">The reduced library snapshot.</param>
        /// <param name="candidates">Categories the shared discovery pass produced.</param>
        /// <param name="settings">Run settings.</param>
        /// <param name="activity">This viewer's watch activity.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="runLog">Recorder for this run; defaults to recording nothing.</param>
        /// <param name="userId">The viewer, recorded against this pass's calls in the run log.</param>
        /// <returns>The invented categories and token usage.</returns>
        public async Task<PersonalRunResult> ProposePersonalAsync(
            ILlmProvider provider,
            IReadOnlyList<MediaItemRecord> records,
            IReadOnlyList<ReconciledCategory> candidates,
            ProposalRunSettings settings,
            IReadOnlyDictionary<Guid, UserActivity>? activity,
            CancellationToken cancellationToken = default,
            IRunLog? runLog = null,
            Guid? userId = null)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentNullException.ThrowIfNull(records);
            ArgumentNullException.ThrowIfNull(candidates);
            ArgumentNullException.ThrowIfNull(settings);

            var log = runLog ?? NullRunLog.Instance;
            var batches = Batcher.Split(records, settings.BatchSize);

            var proposals = new List<CategoryProposal>();
            long inputTokens = 0;
            long outputTokens = 0;
            long cacheWriteTokens = 0;
            long cacheReadTokens = 0;
            var skipped = 0;

            for (var i = 0; i < batches.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = batches[i];
                var request = new LlmRequest(
                    PromptBuilder.BuildPersonalSystemPrompt(settings.Personal),
                    // Byte-identical to the discovery pass, so this is a cache read.
                    PromptBuilder.BuildItemList(batch, settings.MaxTagsPerItem),
                    PromptBuilder.BuildPersonalSuffix(batch, candidates, activity),
                    settings.MaxOutputTokens,
                    ResponseShape.PersonalCategories);

                PersonalParseResult? parsed = null;
                for (var attempt = 0; attempt < 2 && parsed is null; attempt++)
                {
                    var startedAt = Stopwatch.GetTimestamp();
                    LlmResult result;
                    try
                    {
                        result = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LlmCall(
                            "personal", i, attempt + 1, userId, Stopwatch.GetElapsedTime(startedAt),
                            request, null, "error", ex.Message);
                        throw;
                    }

                    inputTokens += result.InputTokens;
                    outputTokens += result.OutputTokens;
                    cacheWriteTokens += result.CacheWriteTokens;
                    cacheReadTokens += result.CacheReadTokens;
                    WarnIfTruncated(i, result, settings);

                    string outcome;
                    string? parseError = null;
                    try
                    {
                        parsed = ProposalParser.ParsePersonal(result.Text, batch);
                        outcome = "ok";
                    }
                    catch (FormatException ex)
                    {
                        outcome = "unparseable";
                        parseError = ex.Message;
                        _logger.LogWarning(
                            ex,
                            "Curator: viewer batch {Batch} produced an unparseable response{Action}. Response was: {Response}",
                            i,
                            attempt == 1 ? "; skipping it" : "; retrying once",
                            Excerpt(result.Text));
                    }

                    log.LlmCall(
                        "personal", i, attempt + 1, userId, Stopwatch.GetElapsedTime(startedAt),
                        request, result, outcome, parseError);
                }

                if (parsed is null)
                {
                    skipped++;
                    continue;
                }

                proposals.AddRange(parsed.Proposals);
            }

            _logger.LogInformation(
                "Curator viewer pass: {New} new categories proposed alongside {Candidates} shared, {Input} input + {Output} output tokens ({Read} cached)",
                proposals.Count,
                candidates.Count,
                inputTokens,
                outputTokens,
                cacheReadTokens);

            return new PersonalRunResult(
                proposals,
                inputTokens,
                outputTokens,
                skipped,
                cacheWriteTokens,
                cacheReadTokens);
        }

        /// <summary>
        /// Submits every batch as one asynchronous job. Half the token price, but the
        /// whole run is committed up front: the per-batch token-budget brake in the
        /// direct path cannot apply here, so the budget is checked once before
        /// submitting and not again.
        /// </summary>
        private async Task<ProposalRunResult> ProposeViaBatchApiAsync(
            ILlmProvider provider,
            IBatchLlmProvider batchProvider,
            IReadOnlyList<IReadOnlyList<MediaItemRecord>> batches,
            ProposalRunSettings settings,
            IReadOnlyDictionary<Guid, UserActivity>? activity,
            CancellationToken cancellationToken,
            IRunLog log)
        {
            var requests = new List<BatchLlmRequest>(batches.Count);
            for (var i = 0; i < batches.Count; i++)
            {
                var batch = batches[i];
                requests.Add(new BatchLlmRequest(
                    CustomIdFor(i),
                    new LlmRequest(
                        PromptBuilder.BuildSystemPrompt(settings.Shared),
                        PromptBuilder.BuildItemList(batch, settings.MaxTagsPerItem),
                        PromptBuilder.BuildActivitySection(batch, activity),
                        settings.MaxOutputTokens,
                        ResponseShape.Categories)));
            }

            _logger.LogInformation(
                "Curator: submitting {Count} batches as one job to the batch endpoint; this is asynchronous and may take up to 24 hours",
                requests.Count);

            var results = await batchProvider
                .CompleteBatchAsync(requests, cancellationToken)
                .ConfigureAwait(false);

            // Results come back in arbitrary order — index them by the key we set.
            var byId = new Dictionary<string, BatchLlmResult>(results.Count, StringComparer.Ordinal);
            foreach (var result in results)
            {
                byId[result.CustomId] = result;
            }

            var proposals = new List<CategoryProposal>();
            long inputTokens = 0;
            long outputTokens = 0;
            long cacheWriteTokens = 0;
            long cacheReadTokens = 0;
            var completed = 0;
            var skipped = 0;

            for (var i = 0; i < batches.Count; i++)
            {
                if (!byId.TryGetValue(CustomIdFor(i), out var entry))
                {
                    skipped++;
                    _logger.LogWarning("Curator: batch {Batch} is missing from the job results; skipping it", i);
                    log.LlmCall(
                        "discovery-batch", i, 1, null, TimeSpan.Zero,
                        requests[i].Request, null, "error", "missing from job results");
                    continue;
                }

                if (entry.Result is not { } result)
                {
                    skipped++;
                    _logger.LogWarning(
                        "Curator: batch {Batch} did not succeed in the job ({Reason}); skipping it",
                        i,
                        entry.Error ?? "unknown");
                    log.LlmCall(
                        "discovery-batch", i, 1, null, TimeSpan.Zero,
                        requests[i].Request, null, "error", entry.Error ?? "unknown");
                    continue;
                }

                inputTokens += result.InputTokens;
                outputTokens += result.OutputTokens;
                cacheWriteTokens += result.CacheWriteTokens;
                cacheReadTokens += result.CacheReadTokens;

                WarnIfTruncated(i, result, settings);

                string outcome;
                string? parseError = null;
                try
                {
                    var parsed = ProposalParser.Parse(result.Text, batches[i]);
                    proposals.AddRange(parsed.Proposals);
                    completed++;
                    outcome = "ok";
                }
                catch (FormatException ex)
                {
                    skipped++;
                    outcome = "unparseable";
                    parseError = ex.Message;
                    _logger.LogWarning(
                        ex,
                        "Curator: batch {Batch} produced an unparseable response; skipping it. Response was: {Response}",
                        i,
                        Excerpt(result.Text));
                }

                // No per-request timing here: the job runs them in parallel on the
                // provider's side and reports nothing about individual durations.
                log.LlmCall(
                    "discovery-batch", i, 1, null, TimeSpan.Zero,
                    requests[i].Request, result, outcome, parseError);
            }

            LogRunTotals(provider, settings, inputTokens, outputTokens, proposals.Count, completed, skipped);

            return new ProposalRunResult(
                proposals,
                inputTokens,
                outputTokens,
                completed,
                skipped,
                cacheWriteTokens,
                cacheReadTokens);
        }

        /// <summary>
        /// Warns when the output cap cut a response short, naming the cause when
        /// the provider reports one.
        /// </summary>
        /// <remarks>
        /// Truncation is worse than it sounds now that providers can be held to a
        /// response schema: a cut-off body is invalid JSON, so the batch is lost
        /// outright rather than merely shortened. When most of the budget went on
        /// reasoning the remedy is a bigger cap, and a smaller batch will not help
        /// at all — thinking does not shrink with the item list.
        /// </remarks>
        private void WarnIfTruncated(int batch, LlmResult result, ProposalRunSettings settings)
        {
            if (!result.Truncated)
            {
                return;
            }

            if (result.ThinkingTokens > 0)
            {
                _logger.LogWarning(
                    "Curator: batch {Batch} response was truncated at {MaxTokens} output tokens, of which {Thinking} went on thinking. Raise the output cap — a smaller batch will not help, since thinking does not shrink with the item list.",
                    batch,
                    settings.MaxOutputTokens,
                    result.ThinkingTokens);
                return;
            }

            _logger.LogWarning(
                "Curator: batch {Batch} response was truncated at {MaxTokens} output tokens; proposals from it may be incomplete. Consider a smaller batch size or a larger output cap.",
                batch,
                settings.MaxOutputTokens);
        }

        private static string CustomIdFor(int batchIndex)
            => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"batch-{batchIndex}");

        private static string Excerpt(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "<empty>";
            }

            return text.Length <= 4000 ? text : text[..4000] + "…";
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
