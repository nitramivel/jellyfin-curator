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

        private static CategoryProposalService Service() =>
            new(NullLogger<CategoryProposalService>.Instance);

        private static LlmResult Ok(string text, long input = 100, long output = 50) =>
            new(text, input, output, Truncated: false);

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
            var provider = new StubProvider(
                Ok("I refuse to answer in JSON, sorry."),
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
