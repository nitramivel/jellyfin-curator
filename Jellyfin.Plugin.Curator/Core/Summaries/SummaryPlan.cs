using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Core.Summaries
{
    /// <summary>
    /// Decides which items a distillation pass actually has to pay for.
    ///
    /// <para>
    /// This is the whole economics of the feature. A first pass distills the
    /// library; every pass after it should cost almost nothing, because only items
    /// that are new, or whose overview has been rewritten underneath us, need
    /// touching again. Getting this wrong in the cheap direction leaves stale
    /// summaries describing the wrong film forever; getting it wrong in the
    /// expensive direction re-buys the whole library every week.
    /// </para>
    /// </summary>
    public static class SummaryPlan
    {
        /// <summary>
        /// Why an item is in the work list, or why it was left out.
        /// </summary>
        public enum SummaryReason
        {
            /// <summary>No summary has ever been stored for this item.</summary>
            Missing = 0,

            /// <summary>The overview has changed since the stored summary was made.</summary>
            Stale = 1,

            /// <summary>The caller asked for everything to be redone.</summary>
            Forced = 2,

            /// <summary>
            /// The summary is current but tag consolidation has not been run for this
            /// item — the state every stored summary is in the first time tags are
            /// switched on.
            /// </summary>
            TagsMissing = 3,

            /// <summary>
            /// The stored text carries a leaked JSON field fragment, so it is redone
            /// however well its hash matches.
            /// </summary>
            Corrupt = 4,

            /// <summary>
            /// The summary is current but the item has never been judged for when it
            /// suits watching — the state every stored summary is in the first time
            /// context classification is switched on.
            /// </summary>
            ContextMissing = 5,
        }

        /// <summary>One item that needs distilling, and why.</summary>
        /// <param name="Item">The library item.</param>
        /// <param name="Reason">Why it is in the list.</param>
        public sealed record SummaryTask(MediaItemRecord Item, SummaryReason Reason);

        /// <summary>
        /// What a pass would do.
        /// </summary>
        /// <param name="Work">Items to distill, in library order.</param>
        /// <param name="UpToDate">Items whose stored summary still matches their overview.</param>
        /// <param name="TooShort">Items skipped because their overview is already short enough.</param>
        /// <param name="NoOverview">Items skipped because they have no overview at all.</param>
        public sealed record Plan(
            IReadOnlyList<SummaryTask> Work,
            int UpToDate,
            int TooShort,
            int NoOverview);

        /// <summary>
        /// Works out which items need distilling.
        /// </summary>
        /// <param name="items">The scanned library.</param>
        /// <param name="existing">Summaries already stored, keyed by item ID.</param>
        /// <param name="minSourceLength">
        /// Overviews shorter than this are left alone: distilling them would spend a
        /// call to make the prompt no smaller.
        /// </param>
        /// <param name="force">Redo every item that has an overview, ignoring what is stored.</param>
        /// <param name="consolidateTags">
        /// Whether tags are being consolidated as well. When true, an item whose
        /// summary is current but whose tags were never consolidated — or whose
        /// scraped tags have changed since — is queued again.
        /// <para>
        /// This is what makes switching tags on incremental rather than a full
        /// redo: only the items actually missing tags are paid for.
        /// </para>
        /// </param>
        /// <param name="classifyContext">
        /// Whether the pass is also judging when an item suits watching. When true, an
        /// item whose summary is current but which has never been judged — or was
        /// judged from an overview since rewritten — is queued again, so switching the
        /// feature on is incremental rather than a full redo.
        /// </param>
        /// <returns>The plan.</returns>
        public static Plan Create(
            IReadOnlyList<MediaItemRecord> items,
            IReadOnlyDictionary<Guid, CondensedSummary> existing,
            int minSourceLength,
            bool force = false,
            bool consolidateTags = false,
            bool classifyContext = false)
        {
            ArgumentNullException.ThrowIfNull(items);
            ArgumentNullException.ThrowIfNull(existing);

            var work = new List<SummaryTask>();
            var upToDate = 0;
            var tooShort = 0;
            var noOverview = 0;

            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Overview))
                {
                    noOverview++;
                    continue;
                }

                // Measured against the raw overview, not the truncated one the
                // reducer produces: the question is whether the source is worth
                // rewriting, and truncation has already thrown away the evidence.
                if (item.Overview.Length < minSourceLength)
                {
                    tooShort++;
                    continue;
                }

                if (force)
                {
                    work.Add(new SummaryTask(item, SummaryReason.Forced));
                    continue;
                }

                if (!existing.TryGetValue(item.Id, out var stored))
                {
                    work.Add(new SummaryTask(item, SummaryReason.Missing));
                    continue;
                }

                // Before the hash check, because a corrupt summary is corrupt whether
                // or not its source moved. Its hash matches by construction — it was
                // distilled from the overview it still describes — so nothing else in
                // this method would ever pick it up again.
                if (SummaryParser.CarriesFieldFragment(stored.Text))
                {
                    work.Add(new SummaryTask(item, SummaryReason.Corrupt));
                    continue;
                }

                if (!string.Equals(stored.SourceHash, HashOverview(item.Overview), StringComparison.Ordinal))
                {
                    work.Add(new SummaryTask(item, SummaryReason.Stale));
                    continue;
                }

                // Tags are consolidated in the same call as the summary, so an item
                // needing only tags still costs a full entry in a batch. It is
                // queued anyway: the alternative is a second pass over the same
                // items, which would cost more.
                if (consolidateTags && NeedsTags(item, stored))
                {
                    work.Add(new SummaryTask(item, SummaryReason.TagsMissing));
                    continue;
                }

                // Same bargain as tags: judged in the same call as the summary, so an
                // item needing only this still costs a full entry in a batch, and a
                // second pass over the same items would cost more.
                if (classifyContext && NeedsContext(item, stored))
                {
                    work.Add(new SummaryTask(item, SummaryReason.ContextMissing));
                    continue;
                }

                upToDate++;
            }

            return new Plan(work, upToDate, tooShort, noOverview);
        }

        /// <summary>
        /// Whether this item's tags still need consolidating.
        /// </summary>
        /// <remarks>
        /// An item with no scraped tags at all is never queued for tags: there is
        /// nothing to consolidate, and queueing it would re-buy a summary every
        /// single pass for an answer that can only ever be empty.
        /// </remarks>
        private static bool NeedsTags(MediaItemRecord item, CondensedSummary stored)
        {
            if (item.Tags.Count == 0)
            {
                return false;
            }

            return stored.TagSourceHash is null
                || !string.Equals(stored.TagSourceHash, HashTags(item.Tags), StringComparison.Ordinal);
        }

        /// <summary>
        /// Whether this item still needs judging for when it suits watching.
        /// </summary>
        /// <remarks>
        /// Keyed on its own hash of the same overview the summary is keyed on. That
        /// looks redundant and is not: an item stored before this feature existed has
        /// a matching summary hash and no context at all, so without a second hash it
        /// would read as current forever and switching the setting on would appear to
        /// do nothing.
        /// <para>
        /// An item judged to suit nothing in particular stores empty lists <em>and</em>
        /// the hash, so it is not re-bought every pass. That is the whole reason the
        /// hash rather than the lists is the test — most of the library is expected to
        /// come back empty, and treating empty as unanswered would re-buy most of the
        /// library every time.
        /// </para>
        /// </remarks>
        private static bool NeedsContext(MediaItemRecord item, CondensedSummary stored)
        {
            return stored.ContextSourceHash is null
                || !string.Equals(stored.ContextSourceHash, HashOverview(item.Overview), StringComparison.Ordinal);
        }

        /// <summary>
        /// Hashes a scraped tag list so a re-scrape can be detected.
        /// </summary>
        /// <param name="tags">The raw tags.</param>
        /// <returns>A short hex digest.</returns>
        public static string HashTags(IReadOnlyList<string> tags)
        {
            ArgumentNullException.ThrowIfNull(tags);

            // Order-insensitive: metadata providers do not promise a stable order,
            // and a reshuffle is not a change worth paying a model to re-read.
            var ordered = tags
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length > 0)
                .OrderBy(t => t, StringComparer.Ordinal);

            // Separated by a unit separator, which no tag can contain, so ["ab","c"]
            // and ["a","bc"] cannot hash alike.
            return HashOverview(string.Join('\u001f', ordered));
        }

        /// <summary>
        /// Hashes an overview so a later pass can tell whether it has been rewritten.
        /// </summary>
        /// <remarks>
        /// Truncated to 16 hex characters. This is change detection against a
        /// metadata scraper, not a security boundary — 64 bits makes an accidental
        /// collision across a few hundred items vanishingly unlikely, and the whole
        /// store is disposable if one ever happened.
        /// </remarks>
        /// <param name="overview">The overview text.</param>
        /// <returns>A short hex digest.</returns>
        public static string HashOverview(string? overview)
        {
            var text = (overview ?? string.Empty).Trim();
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
        }
    }
}
