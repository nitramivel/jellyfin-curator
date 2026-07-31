using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jellyfin.Plugin.Curator.Services.Llm;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services.Runs
{
    /// <summary>
    /// Default <see cref="IRunLogStore"/>: one JSON file per run at
    /// <c>{DataPath}/curator/runs/run_{yyyyMMddTHHmmssZ}_{shortId}.json</c>,
    /// deliberately separate from the category files so a run log can be deleted,
    /// shipped, or diffed without touching plugin state.
    /// <para>
    /// The timestamp leads the filename so the directory sorts chronologically in
    /// any file listing, which is how anyone actually reads these.
    /// </para>
    /// </summary>
    public sealed class RunLogStore : IRunLogStore
    {
        /// <summary>
        /// How many run files to keep. Runs are frequent during tuning and each
        /// carries the full prompt text, so the directory is rotated rather than
        /// left to grow without limit.
        /// </summary>
        private const int RetainedRuns = 50;

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,

            // Prompts are prose full of quotes, apostrophes and em dashes. The
            // default encoder escapes them into \u sequences, which turns a run log
            // into something nobody can read.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly string _basePath;
        private readonly ILogger<RunLogStore> _logger;

        /// <summary>
        /// The run in flight, so the configuration page can follow it without
        /// re-reading its file. A run log carries every prompt in full and runs to
        /// hundreds of kilobytes; polling that off disk every couple of seconds to
        /// move a progress bar would cost more than the bar is worth.
        /// </summary>
        private volatile RunLog? _current;

        public RunLogStore(IServerApplicationPaths applicationPaths, ILogger<RunLogStore> logger)
            : this(Path.Combine(applicationPaths.DataPath, "curator", "runs"), logger)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RunLogStore"/> class rooted
        /// at an explicit directory. Used directly by tests.
        /// </summary>
        /// <param name="basePath">The directory holding the run files.</param>
        /// <param name="logger">The logger.</param>
        public RunLogStore(string basePath, ILogger<RunLogStore> logger)
        {
            _basePath = basePath;
            _logger = logger;
        }

        /// <inheritdoc />
        public IRunLog Begin(
            string trigger,
            IReadOnlyDictionary<string, object?> settings,
            bool trackAsCurrent = true)
        {
            ArgumentNullException.ThrowIfNull(settings);

            var startedAt = DateTime.UtcNow;
            var runId = Guid.NewGuid();
            var document = new RunLogDocument
            {
                RunId = runId,
                Trigger = trigger,
                Status = RunStatus.Running,
                StartedAt = startedAt,
                Settings = settings,
            };

            // Milliseconds, not seconds: two runs can start inside the same second
            // and the ID suffix is random, so a second-precision name would leave
            // their order down to a coin flip in any filename-ordered listing.
            var path = Path.Combine(
                _basePath,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"run_{startedAt:yyyyMMdd'T'HHmmssfff}Z_{runId.ToString("N")[..8]}.json"));

            var log = new RunLog(document, path, _logger);

            // Written immediately so the file exists for the whole life of the run,
            // not only once it ends — a run that dies without ever finishing is
            // exactly the one worth having a log of.
            log.Flush();
            if (trackAsCurrent)
            {
                _current = log;
            }
            Prune();
            return log;
        }

        /// <inheritdoc />
        public RunLogSummary? Current()
        {
            return _current?.Snapshot();
        }

        /// <inheritdoc />
        public IReadOnlyList<RunLogSummary> List(int limit = 50)
        {
            if (!Directory.Exists(_basePath))
            {
                return [];
            }

            var documents = new List<RunLogDocument>();
            foreach (var file in EnumerateRunFiles().Take(limit))
            {
                if (TryRead(file) is { } document)
                {
                    documents.Add(document);
                }
            }

            // Ordered on the recorded start time rather than the filename: the name
            // is only a convenience for reading the directory, and the document is
            // what actually knows when the run began.
            documents.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));

            var summaries = new List<RunLogSummary>(documents.Count);
            foreach (var document in documents)
            {
                var lastStep = document.Steps.Count > 0 ? document.Steps[^1] : null;

                // A file still marked running when nothing is running was orphaned
                // by a restart. Report that rather than leaving a ghost run that the
                // configuration page would wait on forever.
                var status = document.Status == RunStatus.Running && IsStale(document)
                    ? RunStatus.Abandoned
                    : document.Status;

                summaries.Add(new RunLogSummary(
                    document.RunId,
                    document.Trigger,
                    status,
                    document.Progress,
                    document.StartedAt,
                    document.FinishedAt,
                    document.DurationSeconds,
                    document.Model,
                    document.Provider,
                    document.Totals,
                    document.Steps.Count,
                    document.Error,
                    lastStep?.Message,
                    lastStep?.Step));
            }

            return summaries;
        }

        /// <inheritdoc />
        public RunDetail? Detail(Guid runId, IReadOnlyDictionary<Guid, string>? userNames = null)
        {
            var path = FindRunFile(runId);
            if (path is null)
            {
                return null;
            }

            var document = TryRead(path);
            if (document is null)
            {
                return null;
            }

            long size = 0;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                // Only feeds the "download the full log" link's size hint.
            }

            return RunDetailProjection.Project(document, userNames, size);
        }

        /// <summary>
        /// Locates a run's file. The ID is in the filename, but only its first eight
        /// characters, so a filename match still has to be confirmed against the
        /// document itself.
        /// </summary>
        private string? FindRunFile(Guid runId)
        {
            if (!Directory.Exists(_basePath))
            {
                return null;
            }

            var shortId = runId.ToString("N")[..8];
            foreach (var file in EnumerateRunFiles().Where(f => Path.GetFileName(f).Contains(shortId, StringComparison.Ordinal)))
            {
                try
                {
                    if (TryParseRunId(File.ReadAllText(file)) == runId)
                    {
                        return file;
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Curator: could not read run log {Path}", file);
                }
            }

            return null;
        }

        /// <inheritdoc />
        public string? ReadRaw(Guid runId)
        {
            if (!Directory.Exists(_basePath))
            {
                return null;
            }

            // The ID is in the filename, but only its first 8 characters, so a match
            // there still has to be confirmed against the document itself.
            var shortId = runId.ToString("N")[..8];
            foreach (var file in EnumerateRunFiles().Where(f => Path.GetFileName(f).Contains(shortId, StringComparison.Ordinal)))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    if (TryParseRunId(json) == runId)
                    {
                        return json;
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Curator: could not read run log {Path}", file);
                }
            }

            return null;
        }

        private IEnumerable<string> EnumerateRunFiles()
        {
            return Directory.EnumerateFiles(_basePath, "run_*.json")
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal);
        }

        /// <summary>
        /// Whether a document still marked running is really just abandoned. A live
        /// run touches its file on every step, so a long silence means the process
        /// behind it is gone.
        /// </summary>
        private static bool IsStale(RunLogDocument document)
        {
            var last = document.Steps.Count > 0 ? document.Steps[^1].At : document.StartedAt;
            return DateTime.UtcNow - last > TimeSpan.FromMinutes(30);
        }

        private static Guid? TryParseRunId(string json)
        {
            try
            {
                using var parsed = JsonDocument.Parse(json);
                return parsed.RootElement.TryGetProperty("RunId", out var id) && id.TryGetGuid(out var value)
                    ? value
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private RunLogDocument? TryRead(string path)
        {
            try
            {
                return JsonSerializer.Deserialize<RunLogDocument>(File.ReadAllText(path), SerializerOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning(ex, "Curator: skipping unreadable run log {Path}", path);
                return null;
            }
        }

        private void Prune()
        {
            try
            {
                if (!Directory.Exists(_basePath))
                {
                    return;
                }

                foreach (var file in EnumerateRunFiles().Skip(RetainedRuns))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Curator: could not prune old run logs in {Path}", _basePath);
            }
        }

        /// <summary>
        /// The live recorder for one run. Serialized by its own lock: progress can
        /// be reported from a different thread than the one walking the pipeline.
        /// </summary>
        private sealed class RunLog : IRunLog
        {
            private readonly RunLogDocument _document;
            private readonly string _path;
            private readonly ILogger _logger;
            private readonly object _lock = new();
            private int _stepSeq;
            private int _callSeq;
            private decimal _costInput;
            private decimal _costCached;
            private decimal _costOutput;
            private bool _anyCallPriced;
            private decimal _inputCostPerMillion;
            private decimal _cachedCostPerMillion;
            private decimal _outputCostPerMillion;

            public RunLog(RunLogDocument document, string path, ILogger logger)
            {
                _document = document;
                _path = path;
                _logger = logger;
            }

            public Guid RunId => _document.RunId;

            /// <summary>
            /// A consistent read of the live document, under the same lock every
            /// writer takes. Returns null once the run has ended, so a finished run
            /// is reported from its file like any other rather than lingering here.
            /// </summary>
            public RunLogSummary? Snapshot()
            {
                lock (_lock)
                {
                    if (_document.Status != RunStatus.Running)
                    {
                        return null;
                    }

                    var lastStep = _document.Steps.Count > 0 ? _document.Steps[^1] : null;
                    return new RunLogSummary(
                        _document.RunId,
                        _document.Trigger,
                        _document.Status,
                        _document.Progress,
                        _document.StartedAt,
                        _document.FinishedAt,
                        _document.DurationSeconds,
                        _document.Model,
                        _document.Provider,
                        _document.Totals,
                        _document.Steps.Count,
                        _document.Error,
                        lastStep?.Message,
                        lastStep?.Step);
                }
            }

            public void Step(string step, string message, IReadOnlyDictionary<string, object?>? detail = null)
            {
                lock (_lock)
                {
                    _document.Steps.Add(new RunStep(++_stepSeq, DateTime.UtcNow, step, message, detail));
                    Write();
                }
            }

            public void Progress(double percent)
            {
                lock (_lock)
                {
                    _document.Progress = percent;

                    // Deliberately not flushed: progress moves far more often than
                    // anything else and the next step writes it out anyway. A reader
                    // polling the file is never more than one step behind.
                }
            }

            public void LlmCall(
                string phase,
                int batch,
                int attempt,
                Guid? userId,
                TimeSpan duration,
                LlmRequest request,
                LlmResult? result,
                string outcome,
                string? error,
                RunLogPricing? pricing = null)
            {
                ArgumentNullException.ThrowIfNull(request);

                lock (_lock)
                {
                    var prompt = new RunLogPrompt(
                        Pool(request.SystemPrompt),
                        Pool(request.CacheablePrefix),
                        request.VariableSuffix,
                        request.MaxOutputTokens,
                        request.Shape.ToString());

                    RunLogResponse? response = null;
                    if (result is not null)
                    {
                        response = new RunLogResponse(
                            result.Text,
                            result.InputTokens,
                            result.OutputTokens,
                            result.CacheReadTokens,
                            result.CacheWriteTokens,
                            result.Truncated,
                            result.ThinkingTokens,
                            Cost(result.InputTokens, result.CacheReadTokens, result.OutputTokens, pricing));

                        _document.Totals.InputTokens += result.InputTokens;
                        _document.Totals.OutputTokens += result.OutputTokens;
                        _document.Totals.CacheReadTokens += result.CacheReadTokens;
                        _document.Totals.CacheWriteTokens += result.CacheWriteTokens;

                        // Summed from the per-call figures rather than recomputed
                        // from the running token totals. It used to be the other way
                        // round, which was both simpler and correct while every call
                        // shared one price — but a run can now put its discovery pass
                        // on one model and its viewer passes on another, and no single
                        // rate can price a mixed run from aggregate tokens. Decimal
                        // addition is exact at these magnitudes, so the parts still
                        // agree with the whole.
                        if (response.Cost is { } callCost)
                        {
                            _costInput += callCost.InputUsd;
                            _costCached += callCost.CachedUsd;
                            _costOutput += callCost.OutputUsd;
                            _anyCallPriced = true;
                        }

                        _document.Totals.Cost = _anyCallPriced
                            ? new RunLogCost(
                                _costInput,
                                _costCached,
                                _costOutput,
                                _costInput + _costCached + _costOutput)
                            : null;
                        _document.Totals.EstimatedCostUsd = _document.Totals.Cost?.TotalUsd;
                    }

                    _document.Totals.LlmCallCount++;
                    _document.LlmCalls.Add(new RunLogCall(
                        ++_callSeq,
                        DateTime.UtcNow,
                        phase,
                        batch,
                        attempt,
                        userId,
                        (long)duration.TotalMilliseconds,
                        prompt,
                        response,
                        outcome,
                        error));

                    Write();
                }
            }

            public void SetProvider(
                string provider,
                string model,
                decimal inputCostPerMillion = 0,
                decimal outputCostPerMillion = 0,
                decimal cachedCostPerMillion = 0)
            {
                lock (_lock)
                {
                    _document.Provider = provider;
                    _document.Model = model;
                    _inputCostPerMillion = inputCostPerMillion;
                    _outputCostPerMillion = outputCostPerMillion;

                    // Blank means "half the input price" — the common shape of a
                    // cache-read discount, and a far better default than free.
                    _cachedCostPerMillion = cachedCostPerMillion > 0
                        ? cachedCostPerMillion
                        : inputCostPerMillion / 2m;

                    _document.Pricing = new RunLogPricing(
                        inputCostPerMillion,
                        _cachedCostPerMillion,
                        outputCostPerMillion,
                        inputCostPerMillion > 0 || outputCostPerMillion > 0);
                    Write();
                }
            }

            public void Complete()
            {
                Finish(RunStatus.Completed, null);
            }

            public void Fail(string error)
            {
                Finish(RunStatus.Failed, error);
            }

            public void Flush()
            {
                lock (_lock)
                {
                    Write();
                }
            }

            private void Finish(string status, string? error)
            {
                lock (_lock)
                {
                    var finishedAt = DateTime.UtcNow;
                    _document.Status = status;
                    _document.Error = error;
                    _document.FinishedAt = finishedAt;
                    _document.DurationSeconds = (finishedAt - _document.StartedAt).TotalSeconds;
                    if (status == RunStatus.Completed)
                    {
                        _document.Progress = 100;
                    }

                    Write();
                }
            }

            /// <summary>
            /// Prices a token count pair, or returns null when no price is set.
            /// </summary>
            /// <remarks>
            /// Null rather than zero throughout: a run that cost real money must
            /// never be recorded as free because nobody typed the rates in.
            /// </remarks>
            private RunLogCost? Cost(
                long inputTokens,
                long cachedTokens,
                long outputTokens,
                RunLogPricing? pricing = null)
            {
                // A call may carry its own rates, because the pass it belongs to may
                // be running on a different model from the rest of the run. Without
                // them it falls back to the run's, which is every call of a run that
                // uses one model.
                var inputRate = pricing?.InputPerMillionUsd ?? _inputCostPerMillion;
                var outputRate = pricing?.OutputPerMillionUsd ?? _outputCostPerMillion;
                var cachedRate = pricing is null
                    ? _cachedCostPerMillion
                    : (pricing.CachedPerMillionUsd > 0 ? pricing.CachedPerMillionUsd : pricing.InputPerMillionUsd / 2m);

                if (inputRate <= 0 && outputRate <= 0)
                {
                    return null;
                }

                var input = inputTokens * inputRate / 1_000_000m;
                var cached = cachedTokens * cachedRate / 1_000_000m;
                var output = outputTokens * outputRate / 1_000_000m;
                return new RunLogCost(input, cached, output, input + cached + output);
            }

            /// <summary>
            /// Interns a prompt body and returns its reference. Identical bodies —
            /// which is every pass's copy of the item list — collapse to one entry.
            /// </summary>
            private string Pool(string text)
            {
                if (string.IsNullOrEmpty(text))
                {
                    return string.Empty;
                }

                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
                var key = "sha256:" + hash;
                _document.PromptPool.TryAdd(key, text);
                return key;
            }

            /// <summary>
            /// Rewrites the whole document atomically. Callers hold the lock.
            /// </summary>
            /// <remarks>
            /// A run log must never be able to break the run it is describing, so
            /// every failure here is swallowed with a warning. Temp-file-plus-rename
            /// means a reader polling mid-run never sees a half-written file.
            /// </remarks>
            private void Write()
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    var tempPath = _path + ".tmp";
                    File.WriteAllText(tempPath, JsonSerializer.Serialize(_document, SerializerOptions));
                    File.Move(tempPath, _path, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    _logger.LogWarning(ex, "Curator: could not write run log {Path}", _path);
                }
            }
        }
    }
}
