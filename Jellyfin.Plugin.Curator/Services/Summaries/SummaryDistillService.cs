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
        private readonly LlmProviderFactory _providerFactory;
        private readonly ILogger<SummaryDistillService> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _disposed;

        public SummaryDistillService(
            ILibraryScanner libraryScanner,
            ISummaryStore store,
            LlmProviderFactory providerFactory,
            ILogger<SummaryDistillService> logger)
        {
            _libraryScanner = libraryScanner;
            _store = store;
            _providerFactory = providerFactory;
            _logger = logger;
        }

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
            var plan = SummaryPlan.Create(
                items, existing, config.SummaryMinSourceLength, force, wantTags);
            Report(progress, 2);

            _logger.LogInformation(
                "Curator summaries: {Work} to distil, {UpToDate} already current, {Short} too short, {None} without an overview",
                plan.Work.Count,
                plan.UpToDate,
                plan.TooShort,
                plan.NoOverview);

            if (plan.Work.Count == 0)
            {
                return Finish(new SummaryRunResult(
                    0, plan.UpToDate, plan.TooShort, plan.NoOverview, 0, pruned,
                    0, 0, 0, null, provider.ModelId, profile.Name, DateTime.UtcNow, null));
            }

            var batches = Batcher.Split([.. plan.Work.Select(w => w.Item)], config.SummaryBatchSize);
            var maxLength = Math.Max(20, config.CondensedSummaryMaxLength);
            var system = SummaryPromptBuilder.BuildSystemPrompt(maxLength, tagCeiling);
            var conversationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

            long inputTokens = 0;
            long outputTokens = 0;
            long cachedTokens = 0;
            var distilled = 0;
            var failed = 0;
            string? error = null;

            for (var b = 0; b < batches.Count; b++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = batches[b];

                try
                {
                    var request = new LlmRequest(
                        system,
                        SummaryPromptBuilder.BuildUserPrompt(batch, wantTags),
                        string.Empty,
                        config.MaxOutputTokens,
                        ResponseShape.Summaries,
                        conversationId);

                    var result = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                    inputTokens += result.InputTokens;
                    outputTokens += result.OutputTokens;
                    cachedTokens += result.CacheReadTokens;

                    if (result.Truncated)
                    {
                        _logger.LogWarning(
                            "Curator summaries: batch {Batch} of {Total} was cut off by the output cap; "
                            + "lower the batch size or raise Max output tokens",
                            b + 1,
                            batches.Count);
                    }

                    var parsed = SummaryParser.Parse(result.Text, batch, maxLength, tagCeiling);
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
                    }).ToList();

                    // Saved per batch, not at the end. A pass over a large library is
                    // many minutes of paid calls; losing all of it to a failure in the
                    // last batch would mean paying for the whole thing twice.
                    _store.Upsert(written);
                    distilled += written.Count;
                    failed += parsed.MissingCount;

                    if (parsed.DiscardedCount > 0 || parsed.TrimmedCount > 0)
                    {
                        _logger.LogInformation(
                            "Curator summaries: batch {Batch} — {Written} written, {Discarded} discarded, {Trimmed} trimmed to length",
                            b + 1,
                            written.Count,
                            parsed.DiscardedCount,
                            parsed.TrimmedCount);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is FormatException or InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException)
                {
                    // One bad batch must not cost the batches already paid for and
                    // stored. Record it and keep going.
                    failed += batch.Count;
                    error ??= ex.Message;
                    _logger.LogError(
                        ex,
                        "Curator summaries: batch {Batch} of {Total} failed — {Message}",
                        b + 1,
                        batches.Count,
                        ex.Message);
                }

                Report(progress, 2 + (96.0 * (b + 1) / batches.Count));
            }

            var cost = EstimateCost(profile, inputTokens, outputTokens, cachedTokens);
            LogSpend(provider.ModelId, distilled, failed, inputTokens, outputTokens, cachedTokens, cost);

            return Finish(new SummaryRunResult(
                distilled, plan.UpToDate, plan.TooShort, plan.NoOverview, failed, pruned,
                inputTokens, outputTokens, cachedTokens, cost, provider.ModelId, profile.Name,
                DateTime.UtcNow, error));
        }

        /// <summary>
        /// Prices a pass the same way a category run is priced, including cache
        /// reads at their own rate. See hard rule 6.
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
