using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Curator.Services.Runs
{
    /// <summary>
    /// How a run ended, or that it has not.
    /// </summary>
    public static class RunStatus
    {
        /// <summary>The run is in progress.</summary>
        public const string Running = "running";

        /// <summary>The run finished normally.</summary>
        public const string Completed = "completed";

        /// <summary>The run threw.</summary>
        public const string Failed = "failed";

        /// <summary>
        /// The file was found still marked running with no process behind it —
        /// the server was restarted or the plugin reloaded mid-run.
        /// </summary>
        public const string Abandoned = "abandoned";
    }

    /// <summary>
    /// One recorded step of a run: the narrative of what happened, in order.
    /// </summary>
    /// <param name="Seq">1-based position in the run.</param>
    /// <param name="At">When it happened (UTC).</param>
    /// <param name="Step">A stable machine-readable name, e.g. "library.scanned".</param>
    /// <param name="Message">A one-line human summary.</param>
    /// <param name="Detail">Arbitrary structured payload for this step.</param>
    public sealed record RunStep(
        int Seq,
        DateTime At,
        string Step,
        string Message,
        IReadOnlyDictionary<string, object?>? Detail);

    /// <summary>
    /// The prompt sent for one LLM call. Large repeated bodies are stored once in
    /// <see cref="RunLogDocument.PromptPool"/> and referenced by hash here, so a
    /// six-pass run does not write the same 150 KB item list six times.
    /// </summary>
    /// <param name="SystemPromptRef">Pool key for the system prompt.</param>
    /// <param name="CacheablePrefixRef">Pool key for the reusable user-prompt prefix.</param>
    /// <param name="VariableSuffix">The per-request tail, stored inline because it differs every call.</param>
    /// <param name="MaxOutputTokens">The output cap requested.</param>
    /// <param name="Shape">The response shape asked for.</param>
    public sealed record RunLogPrompt(
        string SystemPromptRef,
        string CacheablePrefixRef,
        string VariableSuffix,
        int MaxOutputTokens,
        string Shape);

    /// <summary>
    /// What came back from one LLM call.
    /// </summary>
    /// <param name="Text">The model's raw output, verbatim and untrimmed.</param>
    /// <param name="InputTokens">Uncached input tokens billed.</param>
    /// <param name="OutputTokens">Output tokens billed.</param>
    /// <param name="CacheReadTokens">Input tokens served from cache.</param>
    /// <param name="CacheWriteTokens">Input tokens written to cache.</param>
    /// <param name="Truncated">Whether the output cap cut the answer off.</param>
    /// <param name="ThinkingTokens">
    /// The share of the output spent reasoning, where the provider separates it.
    /// Read this alongside <paramref name="Truncated"/>: a truncated response whose
    /// thinking approaches the output cap was starved, and the fix is a bigger cap
    /// rather than a smaller batch.
    /// </param>
    /// <param name="Cost">What this one call cost, or null when no prices are configured.</param>
    public sealed record RunLogResponse(
        string Text,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheWriteTokens,
        bool Truncated,
        long ThinkingTokens = 0,
        RunLogCost? Cost = null);

    /// <summary>
    /// What one call, or a whole run, cost at the configured prices.
    /// </summary>
    /// <param name="InputUsd">Uncached input tokens at the input price.</param>
    /// <param name="OutputUsd">Output tokens at the output price.</param>
    /// <param name="TotalUsd">The two added together.</param>
    /// <remarks>
    /// Cached tokens are counted at the plain input price, because the plugin
    /// carries one input price and providers bill cache reads and writes at their
    /// own multipliers — Anthropic reads at a tenth and writes at a premium. A run
    /// with heavy cache traffic is therefore an estimate, and on the generous side
    /// for reads. The token counts sit beside these figures precisely so the
    /// arithmetic can be redone by hand when it matters.
    /// </remarks>
    public sealed record RunLogCost(
        decimal InputUsd,
        decimal OutputUsd,
        decimal TotalUsd);

    /// <summary>
    /// The prices a run was costed at, recorded verbatim from configuration.
    /// </summary>
    /// <param name="InputPerMillionUsd">Input price as entered in settings.</param>
    /// <param name="OutputPerMillionUsd">Output price as entered in settings.</param>
    /// <param name="Configured">
    /// Whether either price was actually set. When false every cost in the file is
    /// null rather than zero — a run that cost money must never read as free.
    /// </param>
    public sealed record RunLogPricing(
        decimal InputPerMillionUsd,
        decimal OutputPerMillionUsd,
        bool Configured);

    /// <summary>
    /// One complete LLM exchange, including the failed attempts — a retry after a
    /// malformed response appears here as two calls, and the discarded first one is
    /// usually the interesting half.
    /// </summary>
    /// <param name="Seq">1-based position among this run's calls.</param>
    /// <param name="At">When the call was made (UTC).</param>
    /// <param name="Phase">"discovery", "personal", or "discovery-batch".</param>
    /// <param name="Batch">Which batch of the library this covers.</param>
    /// <param name="Attempt">1 for the first try, 2 for the retry.</param>
    /// <param name="UserId">The viewer this pass is for, on personal calls.</param>
    /// <param name="DurationMs">Wall-clock time for the call.</param>
    /// <param name="Request">What was sent.</param>
    /// <param name="Response">What came back, or null when the call itself threw.</param>
    /// <param name="Outcome">"ok", "unparseable", or "error".</param>
    /// <param name="Error">The failure message, when there was one.</param>
    public sealed record RunLogCall(
        int Seq,
        DateTime At,
        string Phase,
        int Batch,
        int Attempt,
        Guid? UserId,
        long DurationMs,
        RunLogPrompt Request,
        RunLogResponse? Response,
        string Outcome,
        string? Error);

    /// <summary>
    /// Everything a run spent.
    /// </summary>
    public sealed class RunLogTotals
    {
        /// <summary>Gets or sets uncached input tokens billed.</summary>
        public long InputTokens { get; set; }

        /// <summary>Gets or sets output tokens billed.</summary>
        public long OutputTokens { get; set; }

        /// <summary>Gets or sets input tokens served from the prompt cache.</summary>
        public long CacheReadTokens { get; set; }

        /// <summary>Gets or sets input tokens written to the prompt cache.</summary>
        public long CacheWriteTokens { get; set; }

        /// <summary>Gets or sets how many LLM calls were made.</summary>
        public int LlmCallCount { get; set; }

        /// <summary>
        /// Gets or sets the whole run's cost, broken into input and output.
        /// Null when no prices are configured, rather than a misleading zero.
        /// </summary>
        public RunLogCost? Cost { get; set; }

        /// <summary>
        /// Gets or sets the estimated cost in USD — the same figure as
        /// <see cref="Cost"/>'s total, kept as a flat number for readers that only
        /// want the one value.
        /// </summary>
        public decimal? EstimatedCostUsd { get; set; }
    }

    /// <summary>
    /// The whole record of one run, as written to
    /// <c>{DataPath}/curator/runs/run_{timestamp}_{id}.json</c>.
    ///
    /// This is a diagnostic artifact and a data source for run tracking on the
    /// configuration page, so it is written incrementally: the file exists and is
    /// valid JSON from the moment a run starts, and <see cref="Status"/> and
    /// <see cref="Progress"/> are current as of the last completed step. A reader
    /// polling it during a run sees the run advance.
    /// </summary>
    public sealed class RunLogDocument
    {
        /// <summary>The document layout version, so a reader can tell old files apart.</summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>Gets or sets the schema version.</summary>
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>Gets or sets the run's unique ID.</summary>
        public Guid RunId { get; set; }

        /// <summary>Gets or sets what started the run: "manual" or "scheduled".</summary>
        public string Trigger { get; set; } = string.Empty;

        /// <summary>Gets or sets the run status; see <see cref="RunStatus"/>.</summary>
        public string Status { get; set; } = RunStatus.Running;

        /// <summary>Gets or sets progress from 0 to 100.</summary>
        public double Progress { get; set; }

        /// <summary>Gets or sets when the run started (UTC).</summary>
        public DateTime StartedAt { get; set; }

        /// <summary>Gets or sets when the run ended (UTC), or null while it runs.</summary>
        public DateTime? FinishedAt { get; set; }

        /// <summary>Gets or sets how long the run took, once finished.</summary>
        public double? DurationSeconds { get; set; }

        /// <summary>Gets or sets the failure message when the run threw.</summary>
        public string? Error { get; set; }

        /// <summary>Gets or sets the provider used.</summary>
        public string Provider { get; set; } = string.Empty;

        /// <summary>Gets or sets the model used.</summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the prices this run was costed at, exactly as entered in
        /// settings. Recorded here as well as in <see cref="Settings"/> so a cost
        /// figure can never be read without the rate that produced it — the prices
        /// are typed by hand and are wrong the moment the provider changes.
        /// </summary>
        public RunLogPricing? Pricing { get; set; }

        /// <summary>
        /// Gets or sets the settings that shaped this run. Recorded because the
        /// question asked of a run log is usually "what was it set to when this
        /// happened", and the live configuration has moved on by then.
        /// </summary>
        public IReadOnlyDictionary<string, object?> Settings { get; set; }
            = new Dictionary<string, object?>();

        // These four are settable rather than get-only because System.Text.Json
        // does not populate get-only collection properties when deserializing, and
        // these files are read back to build the run list.

        /// <summary>Gets or sets the running totals.</summary>
        public RunLogTotals Totals { get; set; } = new();

        /// <summary>Gets or sets the ordered narrative of the run.</summary>
        public List<RunStep> Steps { get; set; } = [];

        /// <summary>Gets or sets every LLM exchange, including retried attempts.</summary>
        public List<RunLogCall> LlmCalls { get; set; } = [];

        /// <summary>
        /// Gets or sets the deduplicated prompt bodies, keyed by content hash. The
        /// item list is byte-identical across every pass of a run by design — that
        /// is what makes prompt caching work — so storing it once keeps a run log
        /// readable instead of a megabyte of the same text repeated.
        /// </summary>
        public Dictionary<string, string> PromptPool { get; set; } = [];
    }

    /// <summary>
    /// A run as listed for the configuration page: the header of a
    /// <see cref="RunLogDocument"/> without its prompts or responses.
    /// </summary>
    /// <param name="RunId">The run's ID.</param>
    /// <param name="Trigger">What started it.</param>
    /// <param name="Status">How it ended, or that it is running.</param>
    /// <param name="Progress">Progress from 0 to 100.</param>
    /// <param name="StartedAt">When it started (UTC).</param>
    /// <param name="FinishedAt">When it ended (UTC), or null.</param>
    /// <param name="DurationSeconds">How long it took, once finished.</param>
    /// <param name="Model">The model used.</param>
    /// <param name="Provider">The provider used.</param>
    /// <param name="Totals">What it spent.</param>
    /// <param name="StepCount">How many steps were recorded.</param>
    /// <param name="Error">The failure message, when it failed.</param>
    /// <param name="LastMessage">The most recent step's summary — what it is doing now.</param>
    /// <param name="LastStep">The most recent step's machine name, for mapping to a label.</param>
    public sealed record RunLogSummary(
        Guid RunId,
        string Trigger,
        string Status,
        double Progress,
        DateTime StartedAt,
        DateTime? FinishedAt,
        double? DurationSeconds,
        string Model,
        string Provider,
        RunLogTotals Totals,
        int StepCount,
        string? Error,
        string? LastMessage,
        string? LastStep = null);
}
