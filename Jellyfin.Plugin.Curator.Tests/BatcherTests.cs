using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Llm;
using Jellyfin.Plugin.Curator.Core.Models;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    public class BatcherTests
    {
        internal static IReadOnlyList<MediaItemRecord> MakeRecords(int count)
        {
            return Enumerable.Range(0, count)
                .Select(i => new MediaItemRecord
                {
                    Id = new Guid(i + 1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]),
                    Kind = MediaKind.Movie,
                    Name = $"Movie {i}",
                })
                .ToArray();
        }

        [Fact]
        public void Split_EvenDivision_ProducesEqualBatches()
        {
            var batches = Batcher.Split(MakeRecords(10), 5);

            Assert.Equal(2, batches.Count);
            Assert.All(batches, b => Assert.Equal(5, b.Count));
        }

        [Fact]
        public void Split_Remainder_ProducesShortFinalBatch()
        {
            var batches = Batcher.Split(MakeRecords(7), 3);

            Assert.Equal(3, batches.Count);
            Assert.Equal(3, batches[0].Count);
            Assert.Equal(3, batches[1].Count);
            Assert.Single(batches[2]);
        }

        [Fact]
        public void Split_PreservesOrder()
        {
            var records = MakeRecords(5);

            var batches = Batcher.Split(records, 2);

            Assert.Equal(records.Select(r => r.Id), batches.SelectMany(b => b).Select(r => r.Id));
        }

        [Fact]
        public void Split_Empty_ProducesNoBatches()
        {
            Assert.Empty(Batcher.Split(MakeRecords(0), 10));
        }

        /// <summary>
        /// 0 means "the whole library in one request", which is the setting to prefer
        /// whenever the model's context can hold it: a thread running through items
        /// split across two batches is one the model never gets to see, because each
        /// call only ever sees its own slice.
        /// </summary>
        [Fact]
        public void Split_ZeroBatchSize_SendsEverythingInOneBatch()
        {
            var records = MakeRecords(297);

            var batches = Batcher.Split(records, 0);

            Assert.Equal(297, Assert.Single(batches).Count);
        }

        [Fact]
        public void Split_NegativeBatchSize_IsTreatedAsUnlimited()
        {
            Assert.Equal(3, Assert.Single(Batcher.Split(MakeRecords(3), -1)).Count);
        }

        [Fact]
        public void Split_ZeroBatchSizeWithNoRecords_ProducesNoBatches()
        {
            Assert.Empty(Batcher.Split(MakeRecords(0), 0));
        }
    }
}
