using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Configuration;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Llm;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Core.Summaries;
using Jellyfin.Plugin.Curator.Services.Library;
using Jellyfin.Plugin.Curator.Services.Llm;
using Jellyfin.Plugin.Curator.Services.Runs;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services.Summaries
{
    /// <summary>
    /// What one distillation pass did.
    /// </summary>
    /// <param name="Distilled">Summaries written.</param>
    /// <param name="UpToDate">Items skipped because their stored summary still matched.</param>
    /// <param name="TooShort">Items skipped because their overview was already short.</param>
    /// <param name="NoOverview">Items with nothing to distil.</param>
    /// <param name="Failed">Items in a batch that errored or returned nothing usable.</param>
    /// <param name="Pruned">Stored summaries dropped because the item is gone from the library.</param>
    /// <param name="InputTokens">Uncached input tokens billed.</param>
    /// <param name="OutputTokens">Output tokens billed.</param>
    /// <param name="CachedTokens">Input tokens served from cache.</param>
    /// <param name="EstimatedCostUsd">Estimated spend, or null when no prices are set.</param>
    /// <param name="ModelId">The model that ran.</param>
    /// <param name="ProfileName">The model profile that ran, so the picker's effect is visible.</param>
    /// <param name="FinishedAt">When the pass ended (UTC).</param>
    /// <param name="Error">Why the pass stopped early, or null.</param>
    public sealed record SummaryRunResult(
        int Distilled,
        int UpToDate,
        int TooShort,
        int NoOverview,
        int Failed,
        int Pruned,
        long InputTokens,
        long OutputTokens,
        long CachedTokens,
        decimal? EstimatedCostUsd,
        string? ModelId,
        string? ProfileName,
        DateTime FinishedAt,
        string? Error);

    /// <summary>
    /// Rewrites every long overview in the library into a short, tone-carrying
    /// summary and caches the result.
    ///
    /// <para>
    /// This exists because overviews are roughly two thirds of every category
    /// prompt and the same text is re-sent on every run for the life of the
    /// library. Distilling is a one-off cost per item — after the first pass only
    /// new and rewritten items are paid for — traded against a permanently smaller
    /// prompt. Nothing here writes to Jellyfin: the library's own overviews are
    /// read and never modified, so the cache is disposable and the originals cannot
    /// be damaged.
    /// </para>
    /// </summary>
    public sealed class SummaryDistillService : IDisposable
    {
        private readonly ILibraryScanner _libraryScanner;
        private readonly ISummaryStore _store;
        private readonly ILlmProviderFactory _providerFactory;
        private readonly IRunLogStore _runLogStore;
        private readonly ILogger<SummaryDistillService> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _disposed;

        public SummaryDistillService(
            ILibraryScanner libraryScanner,
            ISummaryStore store,
            ILlmProviderFactory providerFactory,
            IRunLogStore runLogStore,
            ILogger<SummaryDistillService> logger)
        {
            _libraryScanner = libraryScanner;
            _store = store;
            _providerFactory = providerFactory;
            _runLogStore = runLogStore;
            _logger = logger;
        }

        /// <summary>
        /// Snapshots the settings that shaped a distillation pass. No API key, for
        /// the same reason the run's own snapshot omits it: this is the file someone
        /// attaches to a bug report.
        /// </summary>
        private static IReadOnlyDictionary<string, object?> DescribeSummarySettings(
            PluginConfiguration config,
            ModelProfile profile,
            SummaryPlan.Plan plan) => new Dictionary<string, object?>
            {
                ["modelProfile"] = profile.Name,
                ["model"] = profile.Model,
                ["provider"] = profile.Provider.ToString(),
                ["thinking"] = profile.ThinkingResolved(config.EnableThinking),
                ["maxOutputTokens"] = config.MaxOutputTokens,
                ["batchSize"] = config.SummaryBatchSize,
                ["maxLength"] = config.CondensedSummaryMaxLength,
                ["minSourceLength"] = config.SummaryMinSourceLength,
                ["consolidateTags"] = config.ConsolidateTags,
                ["maxConsolidatedTags"] = config.MaxConsolidatedTags,
                ["classifyViewingContext"] = config.ClassifyViewingContext,
                ["allowInventedTags"] = config.AllowInventedTags,
                ["itemsToDistil"] = plan.Work.Count,
            };

        /// <summary>Gets a value indicating whether a pass is in progress.</summary>
        public bool IsRunning => _lock.CurrentCount == 0;

        /// <summary>Gets how far the pass in progress has got, 0-100.</summary>
        public double Progress { get; private set; }

        /// <summary>Gets what the last completed pass did, or null if none has run.</summary>
        public SummaryRunResult? LastResult { get; private set; }

        /// <summary>
        /// Runs a distillation pass.
        /// </summary>
        /// <param name="config">Plugin configuration.</param>
        /// <param name="progress">Progress sink, 0-100.</param>
        /// <param name="force">Redo every item, ignoring what is already stored.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="profileIdOverride">
        /// The model profile to use for this pass only, overriding
        /// <see cref="PluginConfiguration.SummaryModelProfileId"/>. Blank or unknown
        /// falls back to the configured choice.
        /// <para>
        /// This exists so the picker on the Summaries tab works the moment it is
        /// changed. Without it the button posts to a server reading the *saved*
        /// configuration, so choosing a profile and pressing the button immediately
        /// runs the previous one and says nothing about it.
        /// </para>
        /// </param>
        /// <returns>What the pass did.</returns>
        /// <exception cref="InvalidOperationException">A pass is already running.</exception>
        public async Task<SummaryRunResult> DistillAsync(
            PluginConfiguration config,
            IProgress<double>? progress,
            bool force,
            CancellationToken cancellationToken,
            string? profileIdOverride = null)
        {
            ArgumentNullException.ThrowIfNull(config);

            if (!await _lock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Curator: a summary pass is already running.");
            }

            try
            {
                return await DistillCoreAsync(config, progress, force, profileIdOverride, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                Progress = 0;
                _lock.Release();
            }
        }

        private async Task<SummaryRunResult> DistillCoreAsync(
            PluginConfiguration config,
            IProgress<double>? progress,
            bool force,
            string? profileIdOverride,
            CancellationToken cancellationToken)
        {
            // Its own profile, falling back to the default when unset. Distillation
            // is a mechanical rewrite of one paragraph at a time and does not need
            // the model that finds threads across a whole library.
            //
            // An override from the caller wins, so the picker on the Summaries tab
            // takes effect on the next press rather than on the next save. Resolve
            // treats an unknown id as unset, so a stale one cannot fail the pass.
            var wanted = string.IsNullOrWhiteSpace(profileIdOverride)
                ? config.SummaryModelProfileId
                : profileIdOverride;

            var profile = ModelProfiles.Resolve(config, wanted);
            var provider = _providerFactory.Create(profile, config.EnableThinking);

            _logger.LogInformation(
                "Curator summaries: using model profile '{Profile}' ({Model})",
                profile.Name,
                profile.Model);

            // Episodes are deliberately excluded whatever the category run does:
            // an episode overview is already a sentence, and there are an order of
            // magnitude more of them than there are films.
            //
            // NoOverviewLimit matters. The reducer's default cuts overviews at 300
            // characters, and distilling that cut would store a compression of the
            // first 300 characters forever, with nothing downstream able to tell.
            var items = _libraryScanner.ScanLibrary(
                includeEpisodes: false,
                surfacedCollections: null,
                maxOverviewLength: ItemReducer.NoOverviewLimit);
            var existing = _store.GetAll();

            var pruned = _store.Prune([.. items.Select(i => i.Id)]);
            if (pruned > 0)
            {
                _logger.LogInformation(
                    "Curator summaries: dropped {Pruned} summary/summaries for items no longer in the library",
                    pruned);
                existing = _store.GetAll();
            }

            var wantTags = config.ConsolidateTags;
            var tagCeiling = wantTags ? Math.Max(1, config.MaxConsolidatedTags) : 0;
            var wantContext = config.ClassifyViewingContext;
            var plan = SummaryPlan.Create(
                items, existing, config.SummaryMinSourceLength, force, wantTags, wantContext);
            Report(progress, 2);

            _logger.LogInformation(
                "Curator summaries: {Work} to distil, {UpToDate} already current, {Short} too short, {None} without an overview",
                plan.Work.Count,
                plan.UpToDate,
                plan.TooShort,
                plan.NoOverview);

            // A run log of its own. This pass writes every prompt and response the
            // category run does, and until it did, diagnosing it meant grepping tens
            // of megabytes of server log for the one line that survived — which is
            // how a pass losing 185 of 212 items went unnoticed. Not tracked as the
            // current run: that snapshot belongs to the category run (see Begin).
            var runLog = _runLogStore.Begin(
                force ? "summaries-forced" : "summaries",
                DescribeSummarySettings(config, profile, plan),
                trackAsCurrent: false);
            runLog.SetProvider(
                profile.Provider.ToString(),
                provider.ModelId,
                profile.InputCostPerMillion,
                profile.OutputCostPerMillion,
                profile.CachedInputCostPerMillion);
            runLog.Step(
                "summaries.planned",
                $"{plan.Work.Count} to distil, {plan.UpToDate} current, {plan.TooShort} too short",
                new Dictionary<string, object?>
                {
                    ["work"] = plan.Work.Count,
                    ["upToDate"] = plan.UpToDate,
                    ["tooShort"] = plan.TooShort,
                    ["noOverview"] = plan.NoOverview,
                    ["pruned"] = pruned,
                    ["reasons"] = plan.Work
                        .GroupBy(w => w.Reason.ToString())
                        .ToDictionary(g => g.Key, g => (object?)g.Count()),
                });

            if (plan.Work.Count == 0)
            {
                runLog.Step("summaries.nothing", "Nothing needed distilling");
                runLog.Complete();
                return Finish(new SummaryRunResult(
                    0, plan.UpToDate, plan.TooShort, plan.NoOverview, 0, pruned,
                    0, 0, 0, null, provider.ModelId, profile.Name, DateTime.UtcNow, null));
            }

            var batches = Batcher.Split([.. plan.Work.Select(w => w.Item)], config.SummaryBatchSize);
            var maxLength = Math.Max(20, config.CondensedSummaryMaxLength);
            var system = SummaryPromptBuilder.BuildSystemPrompt(
                maxLength, tagCeiling, config.AllowInventedTags, wantContext);
            var conversationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

            long inputTokens = 0;
            long outputTokens = 0;
            long cachedTokens = 0;
            var distilled = 0;
            var failed = 0;
            string? error = null;

            // A work queue rather than a straight loop over the batches, because a
            // batch that comes back unusable can be halved and retried instead of
            // being written off. Measured before this existed: three truncated
            // batches out of eleven took 90 of 212 items down with them, and four
            // more batches returned a single summary each and the other 19 items in
            // each were simply counted lost. 27 of 212 survived a pass that had
            // already been paid for in full.
            var queue = new Queue<(IReadOnlyList<MediaItemRecord> Items, int Attempt)>();
            foreach (var batch in batches)
            {
                queue.Enqueue((batch, 0));
            }

            var processed = 0;
            var total = plan.Work.Count;

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (pending, attempt) = queue.Dequeue();

                try
                {
                    var request = new LlmRequest(
                        system,
                        SummaryPromptBuilder.BuildUserPrompt(pending, wantTags),
                        string.Empty,
                        config.MaxOutputTokens,

                        // Must track wantTags, which is the same flag the prompt is
                        // built from. Schema and prompt disagreeing about "t" is not a
                        // cosmetic mismatch: it leaves the model no legal way to answer.
                        SummaryShapes.For(wantTags, wantContext),
                        conversationId);

                    var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    LlmResult result;
                    try
                    {
                        result = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Recorded before it propagates: a call that threw is exactly
                        // the one whose prompt someone will want to read afterwards.
                        runLog.LlmCall(
                            "summaries", attempt, 1, null,
                            System.Diagnostics.Stopwatch.GetElapsedTime(startedAt),
                            request, null, "error", ex.Message);
                        throw;
                    }

                    inputTokens += result.InputTokens;
                    outputTokens += result.OutputTokens;
                    cachedTokens += result.CacheReadTokens;

                    if (result.Truncated)
                    {
                        _logger.LogWarning(
                            "Curator summaries: a {Count}-item request was cut off by the output cap. "
                            + "Thinking counts against that cap, so raising Max output tokens or lowering "
                            + "the batch size is the real fix; splitting and retrying now",
                            pending.Count);
                    }

                    SummaryParseResult parsed;
                    try
                    {
                        parsed = SummaryParser.Parse(result.Text, pending, maxLength, tagCeiling, wantContext);
                    }
                    catch
                    {
                        runLog.LlmCall(
                            "summaries", attempt, 1, null,
                            System.Diagnostics.Stopwatch.GetElapsedTime(startedAt),
                            request, result, "unparseable", null);
                        throw;
                    }

                    runLog.LlmCall(
                        "summaries", attempt, 1, null,
                        System.Diagnostics.Stopwatch.GetElapsedTime(startedAt),
                        request, result, "ok", null);
                    var written = parsed.Summaries.Select(s => new CondensedSummary
                    {
                        ItemId = s.Item.Id,
                        Text = s.Text,
                        SourceHash = SummaryPlan.HashOverview(s.Item.Overview),
                        ModelId = provider.ModelId,
                        CreatedAt = DateTime.UtcNow,
                        Title = s.Item.Name,
                        SourceLength = s.Item.Overview?.Length ?? 0,
                        Tags = s.Tags,

                        // Stamped only when tags were actually asked for. Recording a
                        // hash for a pass that never looked at tags would tell the
                        // next planner they were done when they were not.
                        TagSourceHash = wantTags && s.Item.Tags.Count > 0
                            ? SummaryPlan.HashTags(s.Item.Tags)
                            : null,

                        Weather = s.Context.Weather,
                        Dayparts = s.Context.Dayparts,

                        // Stamped whenever context was asked for, including when the
                        // answer came back empty — "this suits no weather in
                        // particular" is a judgement that was paid for, and leaving
                        // the hash null would re-buy it on every pass for most of the
                        // library, which is where most of the answers land by design.
                        ContextSourceHash = wantContext
                            ? SummaryPlan.HashOverview(s.Item.Overview)
                            : null,
                    }).ToList();

                    // Saved per batch, not at the end. A pass over a large library is
                    // many minutes of paid calls; losing all of it to a failure in the
                    // last batch would mean paying for the whole thing twice.
                    _store.Upsert(written);
                    distilled += written.Count;

                    if (parsed.DiscardedCount > 0 || parsed.TrimmedCount > 0)
                    {
                        _logger.LogInformation(
                            "Curator summaries: {Written} written, {Discarded} discarded, {Trimmed} trimmed to length",
                            written.Count,
                            parsed.DiscardedCount,
                            parsed.TrimmedCount);
                    }

                    // The model answered, but not for everything it was given. That is
                    // the quiet half of the old bug: a response covering one item out
                    // of thirty parsed cleanly and the other 29 were counted lost
                    // without anything ever being retried or logged as wrong.
                    var returned = parsed.Summaries.Select(x => x.Item.Id).ToHashSet();
                    var missing = pending.Where(i => !returned.Contains(i.Id)).ToList();
                    if (missing.Count > 0)
                    {
                        if (SummaryRetryPlan.ShouldRequeue(missing.Count, attempt))
                        {
                            // Asking again for what was missed only helps if the
                            // request was nearly answered. When barely any of it came
                            // back, the remainder is essentially the request that just
                            // failed, so halve it instead.
                            var severe = SummaryRetryPlan.AnswerWasSeverelyPartial(written.Count, pending.Count);
                            var retryAs = severe
                                ? SummaryRetryPlan.SplitForRetry(missing, attempt)
                                : [];

                            if (retryAs.Count > 0)
                            {
                                foreach (var part in retryAs)
                                {
                                    queue.Enqueue((part, attempt + 1));
                                }
                            }
                            else
                            {
                                queue.Enqueue((missing, attempt + 1));
                            }

                            _logger.LogInformation(
                                "Curator summaries: only {Written} of {Count} item(s) came back; retrying the other {Missing}{How}",
                                written.Count,
                                pending.Count,
                                missing.Count,
                                retryAs.Count > 0 ? " in two smaller requests" : string.Empty);
                        }
                        else
                        {
                            failed += missing.Count;
                            processed += missing.Count;
                            _logger.LogWarning(
                                "Curator summaries: giving up on {Missing} item(s) the model would not answer for",
                                missing.Count);
                        }
                    }

                    processed += written.Count;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (FormatException ex)
                {
                    // Unusable answer — nearly always the output cap cutting the JSON
                    // mid-object. Halving is what actually recovers it: the same items
                    // in two smaller requests fit under the cap. Only a single item
                    // that still fails is genuinely lost.
                    error ??= ex.Message;

                    var halves = SummaryRetryPlan.SplitForRetry(pending, attempt);
                    if (halves.Count > 0)
                    {
                        foreach (var half in halves)
                        {
                            queue.Enqueue((half, attempt + 1));
                        }

                        _logger.LogWarning(
                            "Curator summaries: unusable answer for {Count} item(s) — {Message}. Splitting into {A} and {B} and retrying",
                            pending.Count,
                            ex.Message,
                            halves[0].Count,
                            halves[1].Count);
                        runLog.Step(
                            "summaries.split",
                            $"Unusable answer for {pending.Count} item(s); split and retried",
                            new Dictionary<string, object?>
                            {
                                ["items"] = pending.Count,
                                ["attempt"] = attempt,
                                ["error"] = ex.Message,
                            });
                    }
                    else
                    {
                        failed += pending.Count;
                        processed += pending.Count;
                        _logger.LogError(
                            ex,
                            "Curator summaries: gave up on {Count} item(s) — {Message}",
                            pending.Count,
                            ex.Message);
                        runLog.Step(
                            "summaries.abandoned",
                            $"Gave up on {pending.Count} item(s) — {ex.Message}",
                            new Dictionary<string, object?>
                            {
                                ["items"] = pending.Count,
                                ["titles"] = pending.Select(i => i.Name).Take(20).ToArray(),
                            });
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException)
                {
                    // A transport or provider failure, not a malformed answer. The
                    // provider has already done its own 429/5xx backoff, and splitting
                    // a request the network refused only spends the same failure twice.
                    failed += pending.Count;
                    processed += pending.Count;
                    error ??= ex.Message;
                    _logger.LogError(
                        ex,
                        "Curator summaries: {Count} item(s) failed — {Message}",
                        pending.Count,
                        ex.Message);
                }

                Report(progress, 2 + (96.0 * Math.Min(processed, total) / Math.Max(1, total)));
            }

            var cost = EstimateCost(profile, inputTokens, outputTokens, cachedTokens);
            LogSpend(provider.ModelId, distilled, failed, inputTokens, outputTokens, cachedTokens, cost);

            runLog.Step(
                "summaries.complete",
                $"{distilled} distilled, {failed} failed",
                new Dictionary<string, object?>
                {
                    ["distilled"] = distilled,
                    ["failed"] = failed,
                    ["estimatedCostUsd"] = cost,
                });

            // Failed items are recorded, not fatal: the pass stored everything it
            // could and the log says exactly what it could not.
            runLog.Complete();

            return Finish(new SummaryRunResult(
                distilled, plan.UpToDate, plan.TooShort, plan.NoOverview, failed, pruned,
                inputTokens, outputTokens, cachedTokens, cost, provider.ModelId, profile.Name,
                DateTime.UtcNow, error));
        }

        /// <summary>
        /// Prices a pass the same way a category run is priced, including cache
        /// reads at their own rate. See hard rule 9.
        /// </summary>
        private static decimal? EstimateCost(
            ModelProfile profile,
            long inputTokens,
            long outputTokens,
            long cachedTokens)
        {
            if (profile.InputCostPerMillion <= 0 && profile.OutputCostPerMillion <= 0)
            {
                return null;
            }

            var cachedPrice = profile.CachedInputCostPerMillion > 0
                ? profile.CachedInputCostPerMillion
                : profile.InputCostPerMillion / 2m;

            return ((inputTokens * profile.InputCostPerMillion)
                + (cachedTokens * cachedPrice)
                + (outputTokens * profile.OutputCostPerMillion)) / 1_000_000m;
        }

        private void LogSpend(
            string modelId,
            int distilled,
            int failed,
            long inputTokens,
            long outputTokens,
            long cachedTokens,
            decimal? cost)
        {
            _logger.LogInformation(
                "Curator summaries: model {Model}, {Distilled} distilled ({Failed} failed), "
                + "{Input} input + {Cached} cached + {Output} output tokens{Cost}",
                modelId,
                distilled,
                failed,
                inputTokens,
                cachedTokens,
                outputTokens,
                cost is { } c ? string.Create(CultureInfo.InvariantCulture, $" (~${c:F2})") : string.Empty);
        }

        private SummaryRunResult Finish(SummaryRunResult result)
        {
            LastResult = result;
            return result;
        }

        private void Report(IProgress<double>? progress, double value)
        {
            Progress = Math.Clamp(value, 0, 100);
            progress?.Report(Progress);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lock.Dispose();
        }
    }
}
