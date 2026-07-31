using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Configuration;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Core.Summaries;
using Jellyfin.Plugin.Curator.Services.Library;
using Jellyfin.Plugin.Curator.Services.Llm;
using Jellyfin.Plugin.Curator.Services.Runs;
using Jellyfin.Plugin.Curator.Services.Summaries;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// The distillation pass end to end, driven by a stub provider.
    ///
    /// <para>
    /// This class only exists because the service takes <c>ILlmProviderFactory</c>
    /// rather than the concrete factory. Before that seam existed there was no way
    /// to run this loop at all, so the recovery behaviour below — the part that was
    /// silently losing 185 items of 212 — could only be tested through the pure
    /// helpers it delegates to, never through the loop that calls them.
    /// </para>
    ///
    /// <para>Hard rule 5: no live calls. Every response here is canned.</para>
    /// </summary>
    public class SummaryDistillServiceTests
    {
        private const string LongOverview =
            "A very long overview, comfortably past the minimum source length, describing a film in the "
            + "unhurried and slightly overwritten register that metadata providers favour when they have "
            + "an entire paragraph to fill and no particular reason to stop writing before the end of it.";

        private static MediaItemRecord Item(int i) => new()
        {
            Id = Guid.NewGuid(),
            Kind = MediaKind.Movie,
            Name = "Film " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Overview = LongOverview,
        };

        /// <summary>Answers every request with a summary for each item it was given.</summary>
        private sealed class StubProvider : ILlmProvider
        {
            private readonly Func<int, LlmResult> _respond;

            public StubProvider(Func<int, LlmResult> respond) => _respond = respond;

            public string ModelId => "stub-model";

            public List<int> RequestSizes { get; } = [];

            public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
            {
                // The prompt numbers items from 0, one JSON object per line.
                var size = request.CacheablePrefix.Split('\n').Count(l => l.TrimStart().StartsWith('{'));
                RequestSizes.Add(size);
                return Task.FromResult(_respond(size));
            }
        }

        private static LlmResult Answer(int count, int answerFor = -1)
        {
            var take = answerFor < 0 ? count : Math.Min(answerFor, count);
            var summaries = Enumerable.Range(0, take)
                .Select(i => new { i, s = "a condensed line for item " + i.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            return new LlmResult(JsonSerializer.Serialize(new { summaries }), 100, 50, false);
        }

        private static LlmResult Truncated()
            => new("{\"summaries\":[{\"i\":0,\"s\":\"cut off mid", 100, 50, true);

        private sealed class StubFactory : ILlmProviderFactory
        {
            private readonly ILlmProvider _provider;

            public StubFactory(ILlmProvider provider) => _provider = provider;

            public ILlmProvider Create(PluginConfiguration config) => _provider;

            public ILlmProvider Create(ModelProfile profile, bool globalEnableThinking) => _provider;
        }

        private sealed class StubScanner : ILibraryScanner
        {
            private readonly IReadOnlyList<MediaItemRecord> _items;

            public StubScanner(IReadOnlyList<MediaItemRecord> items) => _items = items;

            public LibraryHealth Inspect() => new(_items.Count, 0);

            public IReadOnlyList<MediaItemRecord> ScanLibrary(
                bool includeEpisodes,
                string? surfacedCollections = null,
                int maxOverviewLength = ItemReducer.DefaultMaxOverviewLength,
                IReadOnlyDictionary<Guid, CondensedSummary>? condensedSummaries = null,
                bool useCondensedTags = false,
                bool surfaceAllCollections = false) => _items;
        }

        private sealed class StubSummaryStore : ISummaryStore
        {
            public Dictionary<Guid, CondensedSummary> Stored { get; } = [];

            public IReadOnlyDictionary<Guid, CondensedSummary> GetAll() => Stored;

            public void Upsert(IReadOnlyCollection<CondensedSummary> summaries)
            {
                foreach (var summary in summaries)
                {
                    Stored[summary.ItemId] = summary;
                }
            }

            public int Clear()
            {
                var n = Stored.Count;
                Stored.Clear();
                return n;
            }

            public int Prune(IReadOnlyCollection<Guid> liveItemIds) => 0;
        }

        private sealed class StubRunLogStore : IRunLogStore
        {
            public List<string> Triggers { get; } = [];

            public bool TrackedAsCurrent { get; private set; }

            public IRunLog Begin(
                string trigger,
                IReadOnlyDictionary<string, object?> settings,
                bool trackAsCurrent = true)
            {
                Triggers.Add(trigger);
                TrackedAsCurrent |= trackAsCurrent;
                return NullRunLog.Instance;
            }

            public IReadOnlyList<RunLogSummary> List(int limit = 50) => [];

            public RunLogSummary? Current() => null;

            public RunDetail? Detail(Guid runId, IReadOnlyDictionary<Guid, string>? userNames = null) => null;

            public string? ReadRaw(Guid runId) => null;
        }

        private static PluginConfiguration Config(int batchSize) => new()
        {
            ModelProfiles = [new ModelProfile { Id = "p", Name = "stub", Model = "stub-model", ApiKey = "k" }],
            DefaultModelProfileId = "p",
            SummaryBatchSize = batchSize,
            SummaryMinSourceLength = 20,
            CondensedSummaryMaxLength = 100,
        };

        private static (SummaryDistillService Service, StubSummaryStore Store, StubRunLogStore Runs)
            Build(IReadOnlyList<MediaItemRecord> items, ILlmProvider provider)
        {
            var store = new StubSummaryStore();
            var runs = new StubRunLogStore();
            var service = new SummaryDistillService(
                new StubScanner(items),
                store,
                new StubFactory(provider),
                runs,
                NullLogger<SummaryDistillService>.Instance);
            return (service, store, runs);
        }

        [Fact]
        public async Task ACleanPassStoresEverySummary()
        {
            var items = Enumerable.Range(0, 10).Select(Item).ToList();
            var provider = new StubProvider(size => Answer(size));
            var (service, store, _) = Build(items, provider);

            var result = await service.DistillAsync(Config(batchSize: 5), null, force: false, CancellationToken.None);

            Assert.Equal(10, result.Distilled);
            Assert.Equal(0, result.Failed);
            Assert.Equal(10, store.Stored.Count);
        }

        [Fact]
        public async Task ATruncatedBatchIsSplitAndItsItemsAreRecovered()
        {
            // The measured failure, end to end: a request the model cannot answer at
            // this size used to take every item in it down. Fail anything over four
            // items and the loop must halve its way to a size that works.
            var items = Enumerable.Range(0, 8).Select(Item).ToList();
            var provider = new StubProvider(size => size > 4 ? Truncated() : Answer(size));
            var (service, store, _) = Build(items, provider);

            var result = await service.DistillAsync(Config(batchSize: 8), null, force: false, CancellationToken.None);

            Assert.Equal(8, result.Distilled);
            Assert.Equal(0, result.Failed);
            Assert.Equal(8, store.Stored.Count);

            // 8 failed, then 4 and 4 succeeded.
            Assert.Equal([8, 4, 4], provider.RequestSizes);
        }

        [Fact]
        public async Task AnswersCoveringOneItemOfManyGetTheRestRetried()
        {
            // The quiet half: this used to parse cleanly and silently count the other
            // nineteen as failures.
            var items = Enumerable.Range(0, 4).Select(Item).ToList();
            var calls = 0;
            var provider = new StubProvider(size =>
            {
                calls++;
                return calls == 1 ? Answer(size, answerFor: 1) : Answer(size);
            });
            var (service, store, _) = Build(items, provider);

            var result = await service.DistillAsync(Config(batchSize: 4), null, force: false, CancellationToken.None);

            Assert.Equal(4, result.Distilled);
            Assert.Equal(0, result.Failed);
            Assert.Equal(4, store.Stored.Count);
        }

        [Fact]
        public async Task AnItemNothingCanAnswerForIsGivenUpOnWithoutTakingTheBatchWithIt()
        {
            // One poisoned item must not cost the other seven. It fails at every size,
            // so the loop halves down to it and abandons only that one.
            var items = Enumerable.Range(0, 8).Select(Item).ToList();
            var provider = new StubProvider(size => size == 1 ? Truncated() : Answer(size, answerFor: size - 1));
            var (service, store, _) = Build(items, provider);

            var result = await service.DistillAsync(Config(batchSize: 8), null, force: false, CancellationToken.None);

            Assert.True(result.Distilled >= 7, $"expected most items stored, got {result.Distilled}");
            Assert.True(store.Stored.Count >= 7);
        }

        [Fact]
        public async Task ThePassWritesItsOwnRunLogAndDoesNotClaimTheCurrentRun()
        {
            // Diagnosing this pass used to mean grepping the server log. And the
            // status endpoint pairs Current() with the category run's IsRunning, so a
            // distillation claiming it would show the wrong thing in the panel.
            var items = Enumerable.Range(0, 4).Select(Item).ToList();
            var (service, _, runs) = Build(items, new StubProvider(size => Answer(size)));

            await service.DistillAsync(Config(batchSize: 4), null, force: false, CancellationToken.None);

            Assert.Equal("summaries", Assert.Single(runs.Triggers));
            Assert.False(runs.TrackedAsCurrent);
        }

        [Fact]
        public async Task AForcedPassIsRecordedAsSuchInTheRunLog()
        {
            var items = Enumerable.Range(0, 2).Select(Item).ToList();
            var (service, _, runs) = Build(items, new StubProvider(size => Answer(size)));

            await service.DistillAsync(Config(batchSize: 2), null, force: true, CancellationToken.None);

            Assert.Equal("summaries-forced", Assert.Single(runs.Triggers));
        }
    }
}
