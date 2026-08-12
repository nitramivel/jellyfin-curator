using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Curator.Core.Context
{
    /// <summary>
    /// A handful of row titles a model wrote for one set of conditions.
    /// </summary>
    /// <param name="Condition">
    /// The conditions these describe, as <see cref="ContextTitles.ConditionKey"/>
    /// builds it. This is the cache key, and it is what makes the feature almost
    /// free: there are only so many distinct conditions a place ever produces.
    /// </param>
    /// <param name="Titles">The titles, in the order the model returned them.</param>
    /// <param name="Rotation">
    /// How many times this set has been drawn from, so the next draw can move on
    /// rather than showing the same title every rainy evening for a year.
    /// </param>
    /// <param name="LastUsedUtc">
    /// When this was last drawn from. The only input to culling by age — a
    /// condition a place stopped producing ages out instead of sitting in the store
    /// forever.
    /// </param>
    /// <param name="ModelId">The model that wrote them, for the settings snapshot.</param>
    public sealed record ContextTitleSet(
        string Condition,
        IReadOnlyList<string> Titles,
        int Rotation,
        DateTime LastUsedUtc,
        string? ModelId = null);

    /// <summary>
    /// Naming the context row for the moment it is drawn in, and keeping the store
    /// of names from growing forever.
    ///
    /// <para>
    /// The economics are the whole design. A title is bought once <b>per set of
    /// conditions</b>, not per refresh: "rain, cold at evening" is a key, and the
    /// five titles bought for it the first time serve every cold wet evening
    /// afterwards for nothing. A place produces on the order of seventy distinct
    /// sky-and-hour combinations, so the cost curve flattens within a season and
    /// then stays flat — against two calls per refresh forever, which is what
    /// titling on the clock would mean.
    /// </para>
    ///
    /// <para>
    /// Rotation is what stops that thrift reading as staleness. Each draw moves one
    /// place along the set, so a rainy evening in March is not labelled identically
    /// to a rainy evening in November, and two viewers under one sky do not see the
    /// same words.
    /// </para>
    /// </summary>
    public static class ContextTitles
    {
        /// <summary>
        /// How long an unused set of titles is kept before culling.
        /// </summary>
        /// <remarks>
        /// A year, because the conditions this is keyed on are seasonal. Culling a
        /// snowy-evening set in July because it has gone six months unused would
        /// re-buy it every winter — which is the failure to avoid, since the whole
        /// point of the cache is that a condition is paid for once. What ages out is
        /// a condition the place genuinely stopped producing.
        /// </remarks>
        public const int DefaultRetentionDays = 365;

        /// <summary>How many titles to ask a model for in one call.</summary>
        /// <remarks>
        /// Enough that a condition does not repeat within a season, few enough that
        /// the model does not start reaching — asked for twenty variations on one
        /// rainy evening, the last ten are noticeably worse than the first five.
        /// </remarks>
        public const int DefaultTitlesPerCondition = 5;

        /// <summary>
        /// The cache key for one moment: the sky and the hour together.
        /// </summary>
        /// <remarks>
        /// Weather words are sorted, so <c>rain, cold</c> and <c>cold, rain</c> are
        /// one condition rather than two — the order Open-Meteo happens to report
        /// them in is not a fact about the weather, and treating it as one would
        /// double the number of conditions bought.
        /// <para>
        /// The daypart is part of the key because it is part of the title: a row
        /// called "rainy night cozy vibes" cannot be reused at eleven in the morning.
        /// That multiplies the conditions by four, which is affordable precisely
        /// because each is bought once and then rotated.
        /// </para>
        /// <para>
        /// An empty weather half is a legitimate key, not a broken one: with no
        /// reading the row is drawn from the clock alone, and that moment deserves a
        /// title of its own rather than borrowing a rainy one.
        /// </para>
        /// </remarks>
        /// <param name="context">The conditions.</param>
        /// <returns>A stable key, such as <c>cold,rain|evening</c>.</returns>
        public static string ConditionKey(ViewingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var words = context.Weather
                .Select(w => w.Trim().ToLowerInvariant())
                .Where(w => w.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(w => w, StringComparer.Ordinal);

            return string.Join(',', words) + "|" + ContextVocabulary.WordFor(context.Daypart);
        }

        /// <summary>
        /// Whether a condition key still means something.
        /// </summary>
        /// <remarks>
        /// The half of culling that is not about time: a key naming a word the
        /// vocabulary no longer has can never be matched again, so it is dead the
        /// moment the vocabulary changes rather than a year later. Keys written in
        /// the old two-row shape — before the sky and the hour became one row — are
        /// dead for the same reason, and are culled on the first pass after the
        /// upgrade rather than lingering for a year.
        /// </remarks>
        /// <param name="condition">The stored key.</param>
        /// <returns>Whether any live condition could produce it.</returns>
        public static bool IsLiveCondition(string? condition)
        {
            if (string.IsNullOrWhiteSpace(condition))
            {
                return false;
            }

            var bar = condition.IndexOf('|', StringComparison.Ordinal);
            if (bar < 0)
            {
                return false;
            }

            var weather = condition[..bar];
            var daypart = condition[(bar + 1)..];

            if (!ContextVocabulary.IsDaypart(daypart))
            {
                return false;
            }

            return weather.Length == 0
                || weather.Split(',', StringSplitOptions.RemoveEmptyEntries).All(ContextVocabulary.IsWeather);
        }

        /// <summary>
        /// Picks a title out of a set, and reports the set as it should be stored back.
        /// </summary>
        /// <remarks>
        /// The viewer's own offset is mixed into the position so two people under one
        /// sky do not read the same words on the same evening — which is what would
        /// otherwise happen the moment per-viewer rows exist, since they share a
        /// condition and therefore a set.
        /// </remarks>
        /// <param name="set">The stored set.</param>
        /// <param name="userOffset">A stable per-viewer offset; 0 for a shared row.</param>
        /// <param name="utcNow">The current time, stamped onto the returned set.</param>
        /// <returns>The chosen title and the set to store, or null when the set is empty.</returns>
        public static (string Title, ContextTitleSet Updated)? Draw(
            ContextTitleSet set,
            int userOffset,
            DateTime utcNow)
        {
            ArgumentNullException.ThrowIfNull(set);

            if (set.Titles.Count == 0)
            {
                return null;
            }

            // Non-negative regardless of what the offset is derived from: a hash can
            // be negative, and a negative index throws rather than wrapping.
            var position = (int)(((long)set.Rotation + userOffset) % set.Titles.Count);
            if (position < 0)
            {
                position += set.Titles.Count;
            }

            var updated = set with
            {
                Rotation = set.Rotation == int.MaxValue ? 0 : set.Rotation + 1,
                LastUsedUtc = utcNow,
            };

            return (set.Titles[position], updated);
        }

        /// <summary>
        /// A stable offset for a viewer, so their place in the rotation does not move
        /// between refreshes.
        /// </summary>
        /// <param name="userId">The viewer.</param>
        /// <returns>A small non-negative offset.</returns>
        public static int OffsetFor(Guid userId)
        {
            // Deliberately not GetHashCode: string hashing is randomized per process
            // in .NET, so a viewer's title would change on every server restart.
            var bytes = userId.ToByteArray();
            var sum = 0;
            foreach (var b in bytes)
            {
                sum = ((sum * 31) + b) % 1_000_003;
            }

            return sum;
        }

        /// <summary>
        /// Culls sets that can no longer apply.
        /// </summary>
        /// <remarks>
        /// Two reasons to drop one, different in kind. A set naming a word the
        /// vocabulary has lost, or written in a shape nothing asks for any more, is
        /// dead immediately. A set that merely has not been drawn from is dropped
        /// only after a long time, because these conditions are seasonal and an eager
        /// rule would re-buy every winter what it culled every summer.
        /// <para>
        /// Never touches a set drawn from in this pass, whatever the clock says: the
        /// caller stamps <c>LastUsedUtc</c> before pruning, so the current conditions
        /// cannot be culled out from under the row using them.
        /// </para>
        /// </remarks>
        /// <param name="sets">The stored sets.</param>
        /// <param name="utcNow">The current time.</param>
        /// <param name="retentionDays">How long an unused set is kept. 0 or less keeps them forever.</param>
        /// <returns>The sets to keep, and why the rest went.</returns>
        public static (IReadOnlyList<ContextTitleSet> Kept, int Expired, int Obsolete) Prune(
            IReadOnlyList<ContextTitleSet> sets,
            DateTime utcNow,
            int retentionDays = DefaultRetentionDays)
        {
            ArgumentNullException.ThrowIfNull(sets);

            var kept = new List<ContextTitleSet>(sets.Count);
            var expired = 0;
            var obsolete = 0;

            foreach (var set in sets)
            {
                if (!IsLiveCondition(set.Condition) || set.Titles.Count == 0)
                {
                    obsolete++;
                    continue;
                }

                if (retentionDays > 0 && utcNow - set.LastUsedUtc > TimeSpan.FromDays(retentionDays))
                {
                    expired++;
                    continue;
                }

                kept.Add(set);
            }

            return (kept, expired, obsolete);
        }

        /// <summary>
        /// The moment written out for a model to read.
        /// </summary>
        /// <param name="context">The conditions.</param>
        /// <returns>Something like <c>an evening with rain and hard cold</c>.</returns>
        public static string Describe(ViewingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            // The article is part of each phrase rather than a prefix, because they
            // do not all take the same one: "an evening", but "the small hours".
            // Bolting "a " on the front produced "a small hours of the night", which
            // went into the prompt verbatim.
            var daypart = context.Daypart switch
            {
                Daypart.Morning => "a morning",
                Daypart.Afternoon => "an afternoon",
                Daypart.Evening => "an evening",
                _ => "the small hours of the night",
            };

            if (!context.HasWeather)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{daypart}, with the weather unknown");
            }

            var weather = string.Join(" and ", context.Weather.Select(WeatherPhrase));
            return string.Create(CultureInfo.InvariantCulture, $"{daypart} with {weather}");
        }

        private static string WeatherPhrase(string word) => word switch
        {
            ContextVocabulary.Clear => "a clear bright sky",
            ContextVocabulary.Cloudy => "a grey overcast sky",
            ContextVocabulary.Rain => "rain",
            ContextVocabulary.Storm => "a thunderstorm",
            ContextVocabulary.Snow => "snow",
            ContextVocabulary.Fog => "fog",
            ContextVocabulary.Hot => "real heat",
            ContextVocabulary.Cold => "hard cold",
            _ => word,
        };
    }
}
