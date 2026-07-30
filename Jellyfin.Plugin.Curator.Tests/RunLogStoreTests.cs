using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Curator.Services.Llm;
using Jellyfin.Plugin.Curator.Services.Runs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    public class RunLogStoreTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "curator-runs-" + Guid.NewGuid().ToString("N"));

        private RunLogStore Store() => new(_root, NullLogger<RunLogStore>.Instance);

        private static readonly Dictionary<string, object?> Settings = new()
        {
            ["model"] = "claude-sonnet-5",
            ["batchSize"] = 150,
        };

        // ---- live snapshot, which is what moves the progress bar ----

        /// <summary>
        /// The configuration page polls this every couple of seconds during a run.
        /// It has to come from memory: a run log carries every prompt in full and
        /// runs to hundreds of kilobytes.
        /// </summary>
        [Fact]
        public void Current_ReportsProgressAndTheLatestStepWhileRunning()
        {
            var store = Store();
            var log = store.Begin("manual", Settings);

            log.Step("library.scanned", "Scanned 297 library item(s)");
            log.Progress(42);

            var snapshot = store.Current();

            Assert.NotNull(snapshot);
            Assert.Equal(log.RunId, snapshot!.RunId);
            Assert.Equal(RunStatus.Running, snapshot.Status);
            Assert.Equal(42, snapshot.Progress);
            Assert.Equal("Scanned 297 library item(s)", snapshot.LastMessage);
            Assert.Equal("library.scanned", snapshot.LastStep);
        }

        /// <summary>
        /// LastStep is the machine name the page maps to a human phase label, and it
        /// has to track the newest step rather than the first.
        /// </summary>
        [Fact]
        public void Current_TracksTheNewestStep()
        {
            var store = Store();
            var log = store.Begin("manual", Settings);

            log.Step("library.scanned", "Scanned");
            log.Step("discovery.reconciled", "Reconciled");

            Assert.Equal("discovery.reconciled", store.Current()!.LastStep);
        }

        [Fact]
        public void Current_IsNullBeforeAnyRunStarts()
        {
            Assert.Null(Store().Current());
        }

        /// <summary>
        /// A finished run is reported from its file like any other. Leaving it here
        /// would keep the progress bar up after the run ended.
        /// </summary>
        [Fact]
        public void Current_IsNullOnceTheRunEnds()
        {
            var store = Store();
            var log = store.Begin("manual", Settings);
            Assert.NotNull(store.Current());

            log.Complete();

            Assert.Null(store.Current());
        }

        [Fact]
        public void Current_IsNullAfterAFailedRun()
        {
            var store = Store();
            var log = store.Begin("manual", Settings);

            log.Fail("something went wrong");

            Assert.Null(store.Current());
        }

        private JsonDocument ReadOnlyFile()
        {
            var file = Assert.Single(Directory.EnumerateFiles(_root, "run_*.json"));
            return JsonDocument.Parse(File.ReadAllText(file));
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            GC.SuppressFinalize(this);
        }

        [Fact]
        public void Begin_WritesTheFileImmediately()
        {
            // A run that dies without finishing is the one most worth a log, so the
            // file has to exist from the start rather than being written at the end.
            var log = Store().Begin("manual", Settings);

            using var document = ReadOnlyFile();
            Assert.Equal("running", document.RootElement.GetProperty("Status").GetString());
            Assert.Equal("manual", document.RootElement.GetProperty("Trigger").GetString());
            Assert.Equal(log.RunId, document.RootElement.GetProperty("RunId").GetGuid());
            Assert.Equal(
                "claude-sonnet-5",
                document.RootElement.GetProperty("Settings").GetProperty("model").GetString());
        }

        [Fact]
        public void FileNameLeadsWithTheTimestampSoTheDirectorySortsChronologically()
        {
            Store().Begin("manual", Settings);

            var name = Path.GetFileName(Assert.Single(Directory.EnumerateFiles(_root)));
            Assert.Matches(@"^run_\d{8}T\d{9}Z_[0-9a-f]{8}\.json$", name);
        }

        [Fact]
        public void Steps_AreRecordedInOrderWithTheirDetail()
        {
            var log = Store().Begin("manual", Settings);

            log.Step("library.scanned", "Scanned 294 items", new Dictionary<string, object?>
            {
                ["itemCount"] = 294,
            });
            log.Step("discovery.reconciled", "7 candidates");

            using var document = ReadOnlyFile();
            var steps = document.RootElement.GetProperty("Steps").EnumerateArray().ToList();
            Assert.Equal(2, steps.Count);
            Assert.Equal(1, steps[0].GetProperty("Seq").GetInt32());
            Assert.Equal("library.scanned", steps[0].GetProperty("Step").GetString());
            Assert.Equal(294, steps[0].GetProperty("Detail").GetProperty("itemCount").GetInt32());
            Assert.Equal(2, steps[1].GetProperty("Seq").GetInt32());
        }

        [Fact]
        public void LlmCall_RecordsBothSidesOfTheExchange()
        {
            var log = Store().Begin("manual", Settings);
            var request = new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, ResponseShape.PersonalCategories);
            var result = new LlmResult("""{"categories":[]}""", 25, 6551, false, 0, 55702);
            var user = Guid.NewGuid();

            log.LlmCall("personal", 0, 1, user, TimeSpan.FromSeconds(42), request, result, "ok", null);

            using var document = ReadOnlyFile();
            var call = Assert.Single(document.RootElement.GetProperty("LlmCalls").EnumerateArray());
            Assert.Equal("personal", call.GetProperty("Phase").GetString());
            Assert.Equal(user, call.GetProperty("UserId").GetGuid());
            Assert.Equal(42000, call.GetProperty("DurationMs").GetInt64());
            Assert.Equal("PersonalCategories", call.GetProperty("Request").GetProperty("Shape").GetString());
            Assert.Equal("SUFFIX", call.GetProperty("Request").GetProperty("VariableSuffix").GetString());
            Assert.Equal("""{"categories":[]}""", call.GetProperty("Response").GetProperty("Text").GetString());
            Assert.Equal(55702, call.GetProperty("Response").GetProperty("CacheReadTokens").GetInt64());
            Assert.Equal("ok", call.GetProperty("Outcome").GetString());
        }

        [Fact]
        public void RepeatedPromptBodies_AreStoredOnceAndReferenced()
        {
            // The item list is byte-identical across every pass of a run — that is
            // what makes prompt caching work — so six passes must not write six
            // copies of a 150 KB prompt.
            var log = Store().Begin("manual", Settings);
            var result = new LlmResult("{}", 1, 1, false);

            for (var i = 0; i < 5; i++)
            {
                log.LlmCall(
                    "personal", 0, 1, Guid.NewGuid(), TimeSpan.Zero,
                    new LlmRequest("SYSTEM", "THE-BIG-ITEM-LIST", "suffix-" + i, 4096),
                    result,
                    "ok",
                    null);
            }

            using var document = ReadOnlyFile();
            var pool = document.RootElement.GetProperty("PromptPool");

            // One entry for the shared system prompt, one for the shared item list.
            Assert.Equal(2, pool.EnumerateObject().Count());

            var calls = document.RootElement.GetProperty("LlmCalls").EnumerateArray().ToList();
            var prefixRef = calls[0].GetProperty("Request").GetProperty("CacheablePrefixRef").GetString()!;
            Assert.All(calls, c => Assert.Equal(
                prefixRef, c.GetProperty("Request").GetProperty("CacheablePrefixRef").GetString()));
            Assert.Equal("THE-BIG-ITEM-LIST", pool.GetProperty(prefixRef).GetString());
        }

        [Fact]
        public void FailedCall_IsRecordedWithNoResponse()
        {
            var log = Store().Begin("manual", Settings);

            log.LlmCall(
                "discovery", 0, 1, null, TimeSpan.Zero,
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096),
                null,
                "error",
                "credit balance too low");

            using var document = ReadOnlyFile();
            var call = Assert.Single(document.RootElement.GetProperty("LlmCalls").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, call.GetProperty("Response").ValueKind);
            Assert.Equal("credit balance too low", call.GetProperty("Error").GetString());

            // The prompt still has to survive — it is the thing worth reading after
            // a provider error.
            Assert.Equal("SUFFIX", call.GetProperty("Request").GetProperty("VariableSuffix").GetString());
        }

        [Fact]
        public void Totals_AccumulateAcrossCalls()
        {
            var log = Store().Begin("manual", Settings);
            var request = new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096);

            log.LlmCall("discovery", 0, 1, null, TimeSpan.Zero, request, new LlmResult("{}", 25, 6551, false, 55702, 0), "ok", null);
            log.LlmCall("personal", 0, 1, null, TimeSpan.Zero, request, new LlmResult("{}", 429, 3637, false, 0, 56016), "ok", null);

            using var document = ReadOnlyFile();
            var totals = document.RootElement.GetProperty("Totals");
            Assert.Equal(454, totals.GetProperty("InputTokens").GetInt64());
            Assert.Equal(10188, totals.GetProperty("OutputTokens").GetInt64());
            Assert.Equal(55702, totals.GetProperty("CacheWriteTokens").GetInt64());
            Assert.Equal(56016, totals.GetProperty("CacheReadTokens").GetInt64());
            Assert.Equal(2, totals.GetProperty("LlmCallCount").GetInt32());
        }

        [Fact]
        public void EstimatedCost_IsAccumulatedWhenPricesAreConfigured()
        {
            var log = Store().Begin("manual", Settings);
            log.SetProvider("Anthropic", "claude-sonnet-5", inputCostPerMillion: 3m, outputCostPerMillion: 15m);
            log.LlmCall(
                "discovery", 0, 1, null, TimeSpan.Zero,
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096),
                new LlmResult("{}", 1_000_000, 1_000_000, false),
                "ok",
                null);

            using var document = ReadOnlyFile();
            Assert.Equal(18m, document.RootElement.GetProperty("Totals").GetProperty("EstimatedCostUsd").GetDecimal());
        }

        [Fact]
        public void EachCall_CarriesItsOwnCostSplitIntoInputAndOutput()
        {
            var log = Store().Begin("manual", Settings);
            log.SetProvider("Anthropic", "claude-sonnet-5", inputCostPerMillion: 3m, outputCostPerMillion: 15m);
            log.LlmCall(
                "discovery", 0, 1, null, TimeSpan.Zero,
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096),
                new LlmResult("{}", 500_000, 200_000, false),
                "ok",
                null);

            using var document = ReadOnlyFile();
            var cost = Assert.Single(document.RootElement.GetProperty("LlmCalls").EnumerateArray())
                .GetProperty("Response").GetProperty("Cost");

            Assert.Equal(1.5m, cost.GetProperty("InputUsd").GetDecimal());
            Assert.Equal(3.0m, cost.GetProperty("OutputUsd").GetDecimal());
            Assert.Equal(4.5m, cost.GetProperty("TotalUsd").GetDecimal());
        }

        [Fact]
        public void RunTotalCost_AgreesWithTheSumOfItsCalls()
        {
            var log = Store().Begin("manual", Settings);
            log.SetProvider("Google", "gemini-2.5-flash", inputCostPerMillion: 0.3m, outputCostPerMillion: 2.5m);
            var request = new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096);

            log.LlmCall("discovery", 0, 1, null, TimeSpan.Zero, request, new LlmResult("{}", 100_000, 10_000, false), "ok", null);
            log.LlmCall("personal", 0, 1, null, TimeSpan.Zero, request, new LlmResult("{}", 400_000, 30_000, false), "ok", null);

            using var document = ReadOnlyFile();
            var calls = document.RootElement.GetProperty("LlmCalls").EnumerateArray()
                .Select(c => c.GetProperty("Response").GetProperty("Cost").GetProperty("TotalUsd").GetDecimal())
                .ToList();
            var total = document.RootElement.GetProperty("Totals").GetProperty("Cost");

            Assert.Equal(calls.Sum(), total.GetProperty("TotalUsd").GetDecimal());
            Assert.Equal(0.15m, total.GetProperty("InputUsd").GetDecimal());
            Assert.Equal(0.10m, total.GetProperty("OutputUsd").GetDecimal());
        }

        [Fact]
        public void PricesAreRecordedAsEnteredInSettings()
        {
            // A cost figure without the rate that produced it is unreadable — the
            // prices are typed by hand and go stale the moment the provider changes.
            var log = Store().Begin("manual", Settings);
            log.SetProvider("Google", "gemini-2.5-flash", inputCostPerMillion: 0.3m, outputCostPerMillion: 2.5m);

            using var document = ReadOnlyFile();
            var pricing = document.RootElement.GetProperty("Pricing");

            Assert.Equal(0.3m, pricing.GetProperty("InputPerMillionUsd").GetDecimal());
            Assert.Equal(2.5m, pricing.GetProperty("OutputPerMillionUsd").GetDecimal());
            Assert.True(pricing.GetProperty("Configured").GetBoolean());
        }

        [Fact]
        public void WithNoPricesConfigured_EveryCostIsNullAndPricingSaysSo()
        {
            var log = Store().Begin("manual", Settings);
            log.SetProvider("Anthropic", "claude-sonnet-5");
            log.LlmCall(
                "discovery", 0, 1, null, TimeSpan.Zero,
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096),
                new LlmResult("{}", 1_000_000, 1_000_000, false),
                "ok",
                null);

            using var document = ReadOnlyFile();
            var call = Assert.Single(document.RootElement.GetProperty("LlmCalls").EnumerateArray());

            Assert.Equal(JsonValueKind.Null, call.GetProperty("Response").GetProperty("Cost").ValueKind);
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("Totals").GetProperty("Cost").ValueKind);
            Assert.False(document.RootElement.GetProperty("Pricing").GetProperty("Configured").GetBoolean());
        }

        [Fact]
        public void EstimatedCost_StaysNullWhenNoPricesAreConfigured()
        {
            // Null, not zero — a run that cost money must not report as free.
            var log = Store().Begin("manual", Settings);
            log.SetProvider("Anthropic", "claude-sonnet-5");
            log.LlmCall(
                "discovery", 0, 1, null, TimeSpan.Zero,
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096),
                new LlmResult("{}", 1_000_000, 1_000_000, false),
                "ok",
                null);

            using var document = ReadOnlyFile();
            Assert.Equal(
                JsonValueKind.Null,
                document.RootElement.GetProperty("Totals").GetProperty("EstimatedCostUsd").ValueKind);
        }

        [Fact]
        public void Complete_MarksTheRunFinishedAtFullProgress()
        {
            var log = Store().Begin("manual", Settings);
            log.Progress(40);
            log.Complete();

            using var document = ReadOnlyFile();
            Assert.Equal("completed", document.RootElement.GetProperty("Status").GetString());
            Assert.Equal(100, document.RootElement.GetProperty("Progress").GetDouble());
            Assert.NotEqual(JsonValueKind.Null, document.RootElement.GetProperty("FinishedAt").ValueKind);
            Assert.True(document.RootElement.GetProperty("DurationSeconds").GetDouble() >= 0);
        }

        [Fact]
        public void Fail_RecordsTheErrorAndLeavesProgressWhereItStopped()
        {
            var log = Store().Begin("manual", Settings);
            log.Progress(40);
            log.Step("discovery.reconciled", "7 candidates");
            log.Fail("Cannot access a disposed object.");

            using var document = ReadOnlyFile();
            Assert.Equal("failed", document.RootElement.GetProperty("Status").GetString());
            Assert.Equal("Cannot access a disposed object.", document.RootElement.GetProperty("Error").GetString());
            Assert.Equal(40, document.RootElement.GetProperty("Progress").GetDouble());
        }

        [Fact]
        public void List_ReturnsNewestFirstWithTheLastMessage()
        {
            // Both runs start inside the same second here, which is the case that
            // used to fall back to comparing two random ID suffixes.
            var store = Store();
            var first = store.Begin("scheduled", Settings);
            first.Complete();

            var second = store.Begin("manual", Settings);
            second.Step("library.scanned", "Scanned 294 library items");

            var runs = store.List();

            Assert.Equal(2, runs.Count);
            Assert.Equal(second.RunId, runs[0].RunId);
            Assert.Equal("Scanned 294 library items", runs[0].LastMessage);
            Assert.Equal("running", runs[0].Status);
            Assert.Equal("completed", runs[1].Status);
            Assert.Equal("scheduled", runs[1].Trigger);
        }

        [Fact]
        public void List_OnAnEmptyOrMissingDirectory_IsEmpty()
        {
            Assert.Empty(Store().List());
        }

        [Fact]
        public void ReadRaw_ReturnsTheStoredJsonVerbatim()
        {
            var store = Store();
            var log = store.Begin("manual", Settings);
            log.Step("library.scanned", "Scanned 294 items");

            var json = store.ReadRaw(log.RunId);

            Assert.NotNull(json);
            var file = Assert.Single(Directory.EnumerateFiles(_root, "run_*.json"));
            Assert.Equal(File.ReadAllText(file), json);
        }

        [Fact]
        public void ReadRaw_UnknownRun_IsNull()
        {
            var store = Store();
            store.Begin("manual", Settings);

            Assert.Null(store.ReadRaw(Guid.NewGuid()));
        }

        [Fact]
        public void PromptsAreStoredReadable_NotEscapedIntoUnicodeSequences()
        {
            // A run log nobody can read is not much of a diagnostic.
            var log = Store().Begin("manual", Settings);
            log.LlmCall(
                "discovery", 0, 1, null, TimeSpan.Zero,
                new LlmRequest("Find the threads — the \"vibes\" — in this library.", "ITEMS", "S", 4096),
                new LlmResult("{}", 1, 1, false),
                "ok",
                null);

            var file = Assert.Single(Directory.EnumerateFiles(_root, "run_*.json"));
            var raw = File.ReadAllText(file);

            Assert.Contains("the threads — the", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("\\u2014", raw, StringComparison.Ordinal);
        }
    }
}
