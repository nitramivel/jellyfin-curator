using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Core.Context;

namespace Jellyfin.Plugin.Curator.Services.Context
{
    /// <summary>
    /// What one context row was registered as, and the conditions it was registered
    /// for.
    ///
    /// <para>
    /// This is the record that keeps a row honest. The title is fixed when the
    /// section is registered and the contents are worked out when the home screen
    /// asks — so without something pinning them together, a row titled for rain at
    /// 17:00 would fill itself from a clear sky at 20:00 and quietly contradict its
    /// own name. The row reads its conditions from here rather than from the clock,
    /// so the cards always answer the question the title asked.
    /// </para>
    /// </summary>
    /// <param name="SectionId">The registered section.</param>
    /// <param name="UserId">The viewer this row belongs to, or empty for a shared row.</param>
    /// <param name="Kind">Which of the two rows.</param>
    /// <param name="Weather">The weather words the row was registered for.</param>
    /// <param name="Daypart">The daypart the row was registered for.</param>
    /// <param name="Title">The title it was registered under.</param>
    /// <param name="Place">The resolved place name, for the config page and the log.</param>
    /// <param name="RefreshedAtUtc">When the task last wrote this.</param>
    public sealed record ContextRowSnapshot(
        string SectionId,
        Guid UserId,
        ContextRowKind Kind,
        IReadOnlyList<string> Weather,
        Daypart Daypart,
        string Title,
        string Place,
        DateTime RefreshedAtUtc)
    {
        /// <summary>
        /// The conditions this row was registered for, as the ranker wants them.
        /// </summary>
        /// <returns>The pinned context.</returns>
        public ViewingContext Context() => new(Weather, Daypart);
    }

    /// <summary>
    /// Stores the model-written row titles and what each row is currently showing.
    /// </summary>
    /// <remarks>
    /// One file for both, because they are written together by one pass and read
    /// together by one row — and because the titles are the only thing here worth
    /// paying for twice if it were lost.
    /// </remarks>
    public interface IContextRowStore
    {
        /// <summary>
        /// Every stored set of titles, keyed by its condition.
        /// </summary>
        /// <returns>The sets.</returns>
        IReadOnlyDictionary<string, ContextTitleSet> GetTitles();

        /// <summary>
        /// Writes the titles back, replacing the stored set entirely.
        /// </summary>
        /// <param name="titles">The sets to keep.</param>
        void SaveTitles(IReadOnlyCollection<ContextTitleSet> titles);

        /// <summary>
        /// Every row's current snapshot, keyed by section ID.
        /// </summary>
        /// <returns>The snapshots.</returns>
        IReadOnlyDictionary<string, ContextRowSnapshot> GetSnapshots();

        /// <summary>
        /// Replaces every snapshot. Rows absent from the list are forgotten, which is
        /// how a viewer who is no longer targeted stops having a row on file.
        /// </summary>
        /// <param name="snapshots">The snapshots to keep.</param>
        void SaveSnapshots(IReadOnlyCollection<ContextRowSnapshot> snapshots);
    }
}
