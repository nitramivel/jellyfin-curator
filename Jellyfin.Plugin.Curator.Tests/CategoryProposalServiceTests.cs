using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Services.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    public class CategoryProposalServiceTests
    {
        /// <summary>
        /// Stubbed provider: returns canned responses in sequence and records requests.
        /// No live API calls anywhere in the suite.
        /// </summary>
        private sealed class StubProvider : ILlmProvider
        {
            private readonly Queue<LlmResult> _results;

            public StubProvider(params LlmResult[] results)
            {
                _results = new Queue<LlmResult>(results);
            }

            public List<LlmRequest> Requests { get; } = [];

            public string ModelId => "stub-model";

            public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                return Task.FromResult(_results.Dequeue());
            }
        }

        /// <summary>
        /// Stubbed batch provider. Returns results in reverse submission order and
        /// keyed only by custom id, mirroring the real endpoint's lack of any ordering
        /// guarantee — a caller that matches on position will fail against this.
        /// </summary>
        private sealed class StubBatchProvider : ILlmProvider, IBatchLlmProvider
        {
            private readonly Dictionary<string, BatchLlmResult> _byId;

            public StubBatchProvider(params BatchLlmResult[] results)
            {
                _byId = new Dictionary<string, BatchLlmResult>(StringComparer.Ordinal);
                foreach (var r in results)
                {
                    _byId[r.CustomId] = r;
                }
            }

            public List<BatchLlmRequest> Submitted { get; } = [];

            public string ModelId => "stub-batch-model";

            public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
                => throw new InvalidOperationException("Batch path must not fall back to per-request calls.");

            public Task<IReadOnlyList<BatchLlmResult>> CompleteBatchAsync(
                IReadOnlyList<BatchLlmRequest> requests,
                CancellationToken cancellationToken)
            {
                Submitted.AddRange(requests);
                var ordered = new List<BatchLlmResult>();
                for (var i = requests.Count - 1; i >= 0; i--)
                {
                    if (_byId.TryGetValue(requests[i].CustomId, out var result))
                    {
                        ordered.Add(result);
                    }
                }

                return Task.FromResult<IReadOnlyList<BatchLlmResult>>(ordered);
            }
        }

        private static CategoryProposalService Service() =>
            new(NullLogger<CategoryProposalService>.Instance);

        private static LlmResult Ok(string text, long input = 100, long output = 50) =>
            new(text, input, output, Truncated: false);

        /// <summary>
        /// The model occasionally returns invalid JSON; losing a whole user's
        /// categories to one bad sample is worse than paying for a second call.
        /// </summary>
        [Fact]
        public async Task ProposeAsync_UnparseableResponse_IsRetriedOnce()
        {
            var provider = new StubProvider(
                Ok("not json at all"),
                Ok("""{"categories":[{"name":"Second Time Lucky","members":[0,1]}]}"""));

            var result = await Service().ProposeAsync(
                provider,
                BatcherTests.MakeRecords(2),
                new ProposalRunSettings(BatchSize: 2, MaxOutputTokens: 1000, TokenBudget: 0));

            Assert.Equal(2, provider.Requests.Count);
            Assert.Equal(1, result.BatchesCompleted);
            Assert.Equal(0, result.BatchesSkipped);
            Assert.Equal("Second Time Lucky", Assert.Single(result.Proposals).Name);
        }

        [Fact]
        public async Task ProposeAsync_UnparseableTwice_IsSkippedAfterOneRetry()
        {
            var provider = new StubProvider(Ok("not json"), Ok("still not json"));

            var result = await Service().ProposeAsync(
                provider,
                BatcherTests.MakeRecords(2),
                new ProposalRunSettings(BatchSize: 2, MaxOutputTokens: 1000, TokenBudget: 0));

            // Exactly two calls — a bad batch must not retry forever.
            Assert.Equal(2, provider.Requests.Count);
            Assert.Equal(0, result.BatchesCompleted);
            Assert.Equal(1, result.BatchesSkipped);
        }

        [Fact]
        public async Task ProposeAsync_ParseableFirstTime_DoesNotRetry()
        {
            var provider = new StubProvider(Ok("""{"categories":[]}"""));

            await Service().ProposeAsync(
                provider,
                BatcherTests.MakeRecords(2),
                new ProposalRunSettings(BatchSize: 2, MaxOutputTokens: 1000, TokenBudget: 0));

            Assert.Single(provider.Requests);
        }

        [Fact]
        public async Task ProposeAsync_BatchApi_MapsResultsByCustomIdNotPosition()
        {
            var records = BatcherTests.MakeRecords(4);
            var provider = new StubBatchProvider(
                new BatchLlmResult("batch-0", Ok("""{"categories":[{"name":"First Batch Vibes","members":[0,1]}]}"""), null),
                new BatchLlmResult("batch-1", Ok("""{"categories":[{"name":"Second Batch Vibes","members":[0]}]}"""), null));

            var result = await Service().ProposeAsync(
                provider,
                records,
                new ProposalRunSettings(BatchSize: 2, MaxOutputTokens: 1000, TokenBudget: 0, UseBatchApi: true));

            Assert.Equal(2, result.BatchesCompleted);
            Assert.Equal(0, result.BatchesSkipped);

            // The stub returned these in reverse. If the mapping used position, the
            // second batch's member index 0 would resolve to records[0], not records[2].
            Assert.Equal([records[2].Id], result.Proposals[1].Members);
            Assert.Equal([records[0].Id, records[1].Id], result.Proposals[0].Members);
        }

        [Fact]
        public async Task ProposeAsync_BatchApi_SkipsFailedEntriesWithoutSinkingTheRun()
        {
            var records = BatcherTests.MakeRecords(4);
            var provider = new StubBatchProvider(
                new BatchLlmResult("batch-0", null, "errored"),
                new BatchLlmResult("batch-1", Ok("""{"categories":[{"name":"Survivor","members":[0]}]}"""), null));

            var result = await Service().ProposeAsync(
                provider,
                records,
                new ProposalRunSettings(BatchSize: 2, MaxOutputTokens: 1000, TokenBudget: 0, UseBatchApi: true));

            Assert.Equal(1, result.BatchesCompleted);
            Assert.Equal(1, result.BatchesSkipped);
            Assert.Equal("Survivor", Assert.Single(result.Proposals).Name);
        }

        [Fact]
        public async Task ProposeAsync_BatchApi_MissingEntryIsSkipped()
        {
            var provider = new StubBatchProvider(
                new BatchLlmResult("batch-0", Ok("""{"categories":[]}"""), null));

            var result = await Service().ProposeAsync(
                provider,
                BatcherTests.MakeRecords(4),
                new ProposalRunSettings(BatchSize: 2, MaxOutputTokens: 1000, TokenBudget: 0, UseBatchApi: true));

            Assert.Equal(1, result.BatchesSkipped);
        }

        [Fact]
        public async Task ProposeAsync_BatchApi_NotUsedWhenProviderLacksSupport()
        {
            // StubProvider is ILlmProvider only; the flag must not force the batch path.
            var provider = new StubProvider(Ok("""{"categories":[]}"""));

            var result = await Service().ProposeAsync(
                provider,
                BatcherTests.MakeRecords(2),
                new ProposalRunSettings(BatchSize: 2, MaxOutputTokens: 1000, TokenBudget: 0, UseBatchApi: true));

            Assert.Single(provider.Requests);
            Assert.Equal(1, result.BatchesCompleted);
        }

        [Fact]
        public async Task ProposeAsync_AggregatesProposalsAcrossBatches()
        {
            var records = BatcherTests.MakeRecords(4);
            var provider = new StubProvider(
                Ok("""{"categories":[{"name":"First Batch Vibes","members":[0,1]}]}"""),
                Ok("""{"categories":[{"name":"Second Batch Vibes","members":[0]}]}"""));

            var result = await Service().ProposeAsync(
                provider,
                records,
                new ProposalRunSettings(BatchSize: 2, MaxOutputTokens: 1000, TokenBudget: 0));

            Assert.Equal(2, result.Proposals.Count);
            Assert.Equal(2, result.BatchesCompleted);
            Assert.Equal(0, result.BatchesSkipped);
            // Index 0 of the second batch is the third record overall.
            Assert.Equal([records[2].Id], result.Proposals[1].Members);
            Assert.Equal(200, result.InputTokens);
            Assert.Equal(100, result.OutputTokens);
        }

        [Fact]
        public async Task ProposeAsync_RespectsBatchSize()
        {
            var provider = new StubProvider(
                Ok("""{"categories":[]}"""),
                Ok("""{"categories":[]}"""),
                Ok("""{"categories":[]}"""));

            await Service().ProposeAsync(
                provider,
                BatcherTests.MakeRecords(7),
                new ProposalRunSettings(BatchSize: 3, MaxOutputTokens: 1000, TokenBudget: 0));

            Assert.Equal(3, provider.Requests.Count);
        }

        [Fact]
        public async Task ProposeAsync_StopsWhenTokenBudgetExhausted()
        {
            var provider = new StubProvider(
                Ok("""{"categories":[]}""", input: 600, output: 400),
                Ok("""{"categories":[]}"""));

            var result = await Service().ProposeAsync(
                provider,
                BatcherTests.MakeRecords(4),
                new ProposalRunSettings(BatchSize: 2, MaxOutputTokens: 1000, TokenBudget: 1000));

            Assert.Single(provider.Requests);
            Assert.Equal(1, result.BatchesCompleted);
            Assert.Equal(1, result.BatchesSkipped);
        }

        [Fact]
        public async Task ProposeAsync_UnparseableBatch_IsSkippedNotFatal()
        {
            // Batch 0 fails both its attempt and its retry, so it is skipped; batch 1
            // still runs and its proposals survive.
            var provider = new StubProvider(
                Ok("I refuse to answer in JSON, sorry."),
                Ok("Still not JSON, I'm afraid."),
                Ok("""{"categories":[{"name":"Survivors","members":[0]}]}"""));

            var result = await Service().ProposeAsync(
                provider,
                BatcherTests.MakeRecords(4),
                new ProposalRunSettings(BatchSize: 2, MaxOutputTokens: 1000, TokenBudget: 0));

            Assert.Single(result.Proposals);
            Assert.Equal(1, result.BatchesCompleted);
            Assert.Equal(1, result.BatchesSkipped);
        }

        [Fact]
        public async Task ProposeAsync_PassesPromptsBuiltFromBatch()
        {
            var provider = new StubProvider(Ok("""{"categories":[]}"""));

            await Service().ProposeAsync(
                provider,
                BatcherTests.MakeRecords(2),
                new ProposalRunSettings(BatchSize: 10, MaxOutputTokens: 777, TokenBudget: 0));

            var request = Assert.Single(provider.Requests);
            Assert.Contains("Movie 0", request.UserPrompt, StringComparison.Ordinal);
            Assert.Contains("Movie 1", request.UserPrompt, StringComparison.Ordinal);
            Assert.Equal(777, request.MaxOutputTokens);
            Assert.NotEmpty(request.SystemPrompt);
        }

        [Fact]
        public async Task ProposeAsync_EmptyLibrary_MakesNoCalls()
        {
            var provider = new StubProvider();

            var result = await Service().ProposeAsync(
                provider,
                BatcherTests.MakeRecords(0),
                new ProposalRunSettings(BatchSize: 10, MaxOutputTokens: 1000, TokenBudget: 0));

            Assert.Empty(provider.Requests);
            Assert.Empty(result.Proposals);
        }
    }
}
