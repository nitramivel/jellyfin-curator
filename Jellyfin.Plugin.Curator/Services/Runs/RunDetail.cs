using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.Curator.Services.Runs
{
    /// <summary>What the library scan found.</summary>
    /// <param name="ItemCount">Items reduced and sent to the model.</param>
    /// <param name="IncludeEpisodes">Whether episodes were in the item list.</param>
    public sealed record RunDetailLibrary(int ItemCount, bool IncludeEpisodes);

    /// <summary>What the shared discovery pass produced.</summary>
    /// <param name="ProposalCount">Categories the model proposed.</param>
    /// <param name="CandidateCount">Categories that survived reconciliation.</param>
    /// <param name="BatchesSkipped">Batches whose response could not be parsed.</param>
    public sealed record RunDetailDiscovery(int ProposalCount, int CandidateCount, int BatchesSkipped);

    /// <summary>One viewer's pass.</summary>
    /// <param name="UserId">The viewer.</param>
    /// <param name="Name">Their display name, when it could be resolved.</param>
    /// <param name="Skipped">Whether they were skipped before the LLM call.</param>
    /// <param name="WatchedCount">Items they had watched.</param>
    /// <param name="SeriesWithHistory">How many of those were series, after the episode rollup.</param>
    /// <param name="PersonalCount">Categories invented for them.</param>
    public sealed record RunDetailUser(
        Guid UserId,
        string? Name,
        bool Skipped,
        int WatchedCount,
        int SeriesWithHistory,
        int PersonalCount);

    /// <summary>What happened to the category set.</summary>
    /// <param name="Built">Categories built this run.</param>
    /// <param name="Renamed">Categories the model renamed but which kept their row.</param>
    /// <param name="Retired">Categories not proposed this run; playlists removed.</param>
    /// <param name="Pruned">Categories deleted for being over a cap.</param>
    public sealed record RunDetailCategories(int Built, int Renamed, int Retired, int Pruned);

    /// <summary>One LLM exchange, without the prompt bodies.</summary>
    /// <param name="Seq">Call order within the run.</param>
    /// <param name="Phase">"discovery" or "personal".</param>
    /// <param name="UserName">Whose pass this was, on a personal call.</param>
    /// <param name="Attempt">1 for the first try, 2 for the retry.</param>
    /// <param name="DurationMs">Wall-clock time.</param>
    /// <param name="Outcome">"ok", "unparseable", or "error".</param>
    /// <param name="InputTokens">Uncached input tokens.</param>
    /// <param name="OutputTokens">Output tokens.</param>
    /// <param name="CacheReadTokens">Input tokens served from cache.</param>
    /// <param name="CostUsd">What the call cost, or null when unpriced.</param>
    public sealed record RunDetailCall(
        int Seq,
        string Phase,
        string? UserName,
        int Attempt,
        long DurationMs,
        string Outcome,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        decimal? CostUsd);

    /// <summary>
    /// A run reduced to what the configuration page shows when a run row is
    /// expanded.
    /// </summary>
    /// <remarks>
    /// Deliberately not the run file. That carries every prompt and response in
    /// full — 233 KB on the owner's server, 128 KB of it prompt pool alone — and
    /// none of it belongs in a panel that opens on a click. This projection of the
    /// same file runs to single-digit kilobytes. The whole file is still one link
    /// away for anyone debugging a bad run.
    /// </remarks>
    /// <param name="RunId">The run.</param>
    /// <param name="Trigger">What started it.</param>
    /// <param name="Status">Its outcome.</param>
    /// <param name="StartedAt">When it began.</param>
    /// <param name="FinishedAt">When it ended, when it did.</param>
    /// <param name="DurationSeconds">How long it took.</param>
    /// <param name="Provider">The provider.</param>
    /// <param name="Model">The model.</param>
    /// <param name="Totals">Tokens and cost for the whole run.</param>
    /// <param name="Library">The library scan.</param>
    /// <param name="Discovery">The shared discovery pass.</param>
    /// <param name="Users">One entry per viewer considered.</param>
    /// <param name="Categories">What happened to the category set.</param>
    /// <param name="Calls">Every LLM exchange, prompts stripped.</param>
    /// <param name="Error">Why it failed, when it did.</param>
    /// <param name="FileSizeBytes">Size of the full run file, for the download link.</param>
    public sealed record RunDetail(
        Guid RunId,
        string Trigger,
        string Status,
        DateTime StartedAt,
        DateTime? FinishedAt,
        double? DurationSeconds,
        string Provider,
        string Model,
        RunLogTotals Totals,
        RunDetailLibrary? Library,
        RunDetailDiscovery? Discovery,
        IReadOnlyList<RunDetailUser> Users,
        RunDetailCategories Categories,
        IReadOnlyList<RunDetailCall> Calls,
        string? Error,
        long FileSizeBytes);

    /// <summary>
    /// Reduces a stored run document to <see cref="RunDetail"/>.
    /// </summary>
    /// <remarks>
    /// Pure, so it is pinned by tests rather than only exercised through the API.
    /// Step detail arrives as <see cref="JsonElement"/> because the document was
    /// deserialized from disk into <c>object?</c> values; every read here is
    /// defensive, since a step written by a newer version must degrade to a missing
    /// number rather than throw a run detail page away.
    /// </remarks>
    public static class RunDetailProjection
    {
        /// <summary>
        /// Builds the detail view of one run.
        /// </summary>
        /// <param name="document">The stored run document.</param>
        /// <param name="userNames">Display names by user ID, for the per-viewer rows.</param>
        /// <param name="fileSizeBytes">Size of the run file on disk.</param>
        /// <returns>The projection.</returns>
        public static RunDetail Project(
            RunLogDocument document,
            IReadOnlyDictionary<Guid, string>? userNames = null,
            long fileSizeBytes = 0)
        {
            ArgumentNullException.ThrowIfNull(document);

            RunDetailLibrary? library = null;
            RunDetailDiscovery? discovery = null;
            var users = new List<RunDetailUser>();
            var built = 0;
            var renamed = 0;
            var retired = 0;
            var pruned = 0;

            foreach (var step in document.Steps)
            {
                switch (step.Step)
                {
                    case "library.scanned":
                        library = new RunDetailLibrary(
                            Int(step, "itemCount") ?? 0,
                            Bool(step, "includeEpisodes") ?? false);
                        break;

                    case "discovery.reconciled":
                        discovery = new RunDetailDiscovery(
                            Int(step, "proposalCount") ?? 0,
                            Int(step, "candidateCount") ?? 0,
                            Int(step, "batchesSkipped") ?? 0);
                        break;

                    case "user.skipped":
                        users.Add(new RunDetailUser(
                            Guid(step, "userId"),
                            Name(userNames, Guid(step, "userId")),
                            Skipped: true,
                            Int(step, "watchedCount") ?? 0,
                            SeriesWithHistory: 0,
                            PersonalCount: 0));
                        break;

                    case "user.pass":
                        users.Add(new RunDetailUser(
                            Guid(step, "userId"),
                            Name(userNames, Guid(step, "userId")),
                            Skipped: false,
                            Int(step, "watchedCount") ?? 0,

                            // Absent on runs recorded before the episode rollup
                            // landed; those read as zero rather than breaking.
                            Int(step, "seriesWithHistory") ?? 0,
                            Int(step, "proposedCount") ?? 0));
                        break;

                    case "category.built": built++; break;
                    case "category.renamed": renamed++; break;
                    case "category.retired": retired++; break;
                    case "category.pruned": pruned++; break;
                    default: break;
                }
            }

            var calls = document.LlmCalls.Select(call => new RunDetailCall(
                call.Seq,
                call.Phase,
                call.UserId is { } id ? Name(userNames, id) : null,
                call.Attempt,
                call.DurationMs,
                call.Outcome,
                call.Response?.InputTokens ?? 0,
                call.Response?.OutputTokens ?? 0,
                call.Response?.CacheReadTokens ?? 0,
                call.Response?.Cost?.TotalUsd)).ToArray();

            return new RunDetail(
                document.RunId,
                document.Trigger,
                document.Status,
                document.StartedAt,
                document.FinishedAt,
                document.DurationSeconds,
                document.Provider,
                document.Model,
                document.Totals,
                library,
                discovery,
                users,
                new RunDetailCategories(built, renamed, retired, pruned),
                calls,
                document.Error,
                fileSizeBytes);
        }

        private static string? Name(IReadOnlyDictionary<Guid, string>? names, Guid id)
            => names is not null && names.TryGetValue(id, out var name) ? name : null;

        private static object? Raw(RunStep step, string key)
            => step.Detail is not null && step.Detail.TryGetValue(key, out var value) ? value : null;

        private static int? Int(RunStep step, string key)
        {
            var raw = Raw(step, key);
            return raw switch
            {
                int i => i,
                long l => (int)l,
                JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt32(out var n) => n,
                _ => null,
            };
        }

        private static bool? Bool(RunStep step, string key)
        {
            var raw = Raw(step, key);
            return raw switch
            {
                bool b => b,
                JsonElement { ValueKind: JsonValueKind.True } => true,
                JsonElement { ValueKind: JsonValueKind.False } => false,
                _ => null,
            };
        }

        private static Guid Guid(RunStep step, string key)
        {
            var raw = Raw(step, key);
            var text = raw switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
                _ => null,
            };

            return System.Guid.TryParse(text, CultureInfo.InvariantCulture, out var id)
                ? id
                : System.Guid.Empty;
        }
    }
}
