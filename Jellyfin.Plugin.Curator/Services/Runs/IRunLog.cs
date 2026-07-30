using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Services.Llm;

namespace Jellyfin.Plugin.Curator.Services.Runs
{
    /// <summary>
    /// The recorder for one run. Handed down the call chain so every stage writes
    /// into the same file.
    ///
    /// Every method on this interface is best-effort and must never throw: a run
    /// that fails because its diagnostics failed would be strictly worse than one
    /// with no diagnostics at all.
    /// </summary>
    public interface IRunLog
    {
        /// <summary>Gets the run's ID, which names its file.</summary>
        Guid RunId { get; }

        /// <summary>
        /// Records one step of the run.
        /// </summary>
        /// <param name="step">A stable machine-readable name, e.g. "library.scanned".</param>
        /// <param name="message">A one-line human summary.</param>
        /// <param name="detail">Structured payload for this step, or null.</param>
        void Step(string step, string message, IReadOnlyDictionary<string, object?>? detail = null);

        /// <summary>
        /// Records progress, mirroring what the scheduled task reports.
        /// </summary>
        /// <param name="percent">Progress from 0 to 100.</param>
        void Progress(double percent);

        /// <summary>
        /// Records one LLM exchange in full, including attempts that failed.
        /// </summary>
        /// <param name="phase">"discovery", "personal", or "discovery-batch".</param>
        /// <param name="batch">Which batch of the library this covers.</param>
        /// <param name="attempt">1 for the first try, 2 for the retry.</param>
        /// <param name="userId">The viewer this pass is for, on personal calls.</param>
        /// <param name="duration">Wall-clock time for the call.</param>
        /// <param name="request">What was sent.</param>
        /// <param name="result">What came back, or null when the call threw.</param>
        /// <param name="outcome">"ok", "unparseable", or "error".</param>
        /// <param name="error">The failure message, when there was one.</param>
        void LlmCall(
            string phase,
            int batch,
            int attempt,
            Guid? userId,
            TimeSpan duration,
            LlmRequest request,
            LlmResult? result,
            string outcome,
            string? error);

        /// <summary>
        /// Records the model and provider once they are known, which is after the
        /// provider is built rather than at the moment the run starts.
        /// </summary>
        /// <param name="provider">The provider name.</param>
        /// <param name="model">The model identifier.</param>
        /// <param name="inputCostPerMillion">Input price, for the running cost estimate; 0 leaves it unknown.</param>
        /// <param name="outputCostPerMillion">Output price, for the running cost estimate; 0 leaves it unknown.</param>
        /// <param name="cachedCostPerMillion">
        /// Cache-read price. 0 falls back to half the input price — cache reads are
        /// discounted, not free, and reporting them as free understated real runs.
        /// </param>
        void SetProvider(
            string provider,
            string model,
            decimal inputCostPerMillion = 0,
            decimal outputCostPerMillion = 0,
            decimal cachedCostPerMillion = 0);

        /// <summary>Marks the run finished and flushes the file.</summary>
        void Complete();

        /// <summary>
        /// Marks the run failed and flushes the file.
        /// </summary>
        /// <param name="error">What went wrong.</param>
        void Fail(string error);
    }

    /// <summary>
    /// Opens run logs. One file per run, in their own directory.
    /// </summary>
    public interface IRunLogStore
    {
        /// <summary>
        /// Starts recording a new run.
        /// </summary>
        /// <param name="trigger">"manual" or "scheduled".</param>
        /// <param name="settings">The settings that shaped the run.</param>
        /// <returns>The recorder.</returns>
        IRunLog Begin(string trigger, IReadOnlyDictionary<string, object?> settings);

        /// <summary>
        /// Lists recorded runs, newest first.
        /// </summary>
        /// <param name="limit">The most to return.</param>
        /// <returns>The summaries.</returns>
        IReadOnlyList<RunLogSummary> List(int limit = 50);

        /// <summary>
        /// A live snapshot of the run in flight, read from memory, or null when
        /// nothing is running.
        /// </summary>
        /// <remarks>
        /// Exists so the configuration page can move a progress bar without pulling
        /// a run log — every prompt in full, hundreds of kilobytes — off disk on
        /// every poll.
        /// </remarks>
        /// <returns>The current run's summary, or null.</returns>
        RunLogSummary? Current();

        /// <summary>
        /// One run reduced to what the configuration page shows when its row is
        /// expanded: the scan, the discovery pass, one line per viewer, category
        /// counts and the call table, with every prompt body stripped.
        /// </summary>
        /// <param name="runId">The run.</param>
        /// <param name="userNames">Display names by user ID, for the per-viewer rows.</param>
        /// <returns>The detail, or null when no such run exists.</returns>
        RunDetail? Detail(Guid runId, IReadOnlyDictionary<Guid, string>? userNames = null);

        /// <summary>
        /// Reads one run's whole document as raw JSON, exactly as stored.
        /// </summary>
        /// <param name="runId">The run ID.</param>
        /// <returns>The JSON, or null when there is no such run.</returns>
        string? ReadRaw(Guid runId);
    }

    /// <summary>
    /// The recorder used when nothing is recording — tests, and any caller that
    /// has no run log to hand. Every method does nothing.
    /// </summary>
    public sealed class NullRunLog : IRunLog
    {
        /// <summary>Gets the shared instance.</summary>
        public static NullRunLog Instance { get; } = new();

        /// <inheritdoc />
        public Guid RunId => Guid.Empty;

        /// <inheritdoc />
        public void Step(string step, string message, IReadOnlyDictionary<string, object?>? detail = null)
        {
        }

        /// <inheritdoc />
        public void Progress(double percent)
        {
        }

        /// <inheritdoc />
        public void LlmCall(
            string phase,
            int batch,
            int attempt,
            Guid? userId,
            TimeSpan duration,
            LlmRequest request,
            LlmResult? result,
            string outcome,
            string? error)
        {
        }

        /// <inheritdoc />
        public void SetProvider(
            string provider,
            string model,
            decimal inputCostPerMillion = 0,
            decimal outputCostPerMillion = 0,
            decimal cachedCostPerMillion = 0)
        {
        }

        /// <inheritdoc />
        public void Complete()
        {
        }

        /// <inheritdoc />
        public void Fail(string error)
        {
        }
    }
}
