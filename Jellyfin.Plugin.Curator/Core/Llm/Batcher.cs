using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Core.Llm
{
    /// <summary>
    /// Chunks the reduced library into batches of the configured size.
    /// </summary>
    public static class Batcher
    {
        /// <summary>
        /// Splits <paramref name="records"/> into consecutive batches of at most
        /// <paramref name="batchSize"/> items, preserving order.
        /// </summary>
        /// <param name="records">The reduced library records.</param>
        /// <param name="batchSize">
        /// Maximum items per batch. 0 or less sends the whole library in one batch,
        /// which is what you want whenever the model's context can hold it: a thread
        /// running through items split across two batches is one the model never
        /// gets to see, because each call only ever sees its own slice.
        /// </param>
        /// <returns>The batches, in order.</returns>
        public static IReadOnlyList<IReadOnlyList<MediaItemRecord>> Split(
            IReadOnlyList<MediaItemRecord> records,
            int batchSize)
        {
            ArgumentNullException.ThrowIfNull(records);

            if (batchSize <= 0)
            {
                return records.Count == 0 ? [] : [records];
            }

            var batches = new List<IReadOnlyList<MediaItemRecord>>();
            for (var start = 0; start < records.Count; start += batchSize)
            {
                var count = Math.Min(batchSize, records.Count - start);
                var batch = new MediaItemRecord[count];
                for (var i = 0; i < count; i++)
                {
                    batch[i] = records[start + i];
                }

                batches.Add(batch);
            }

            return batches;
        }
    }
}
