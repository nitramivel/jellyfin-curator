using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Core
{
    /// <summary>
    /// Decides whether a user has watched enough for a personalization pass to be
    /// worth its tokens.
    ///
    /// A personal category is invented from what someone has actually seen. A
    /// viewer with no history gives the model nothing to work from, so the pass
    /// costs a full library prompt and returns either nothing or categories
    /// invented out of thin air. Ruling those users out before the call is the
    /// only saving available — the prompt is the cost, and it is paid on send.
    ///
    /// Ineligible users are not dropped from the run: they receive the shared
    /// categories from the library-wide discovery pass, which cost nothing extra.
    /// </summary>
    public static class PersonalizationEligibility
    {
        /// <summary>
        /// Counts the items a user has actually watched.
        /// <para>
        /// Favorites and ratings are deliberately not counted. They are carried in
        /// the prompt and do shape the answer, but the setting is a floor on watch
        /// history specifically, and a shelf of favorites nobody has played is not
        /// evidence of taste the model can ground a category in.
        /// </para>
        /// </summary>
        /// <param name="activity">The user's activity, keyed by item ID.</param>
        /// <returns>The number of distinct items played at least once.</returns>
        public static int CountWatched(IReadOnlyDictionary<Guid, UserActivity>? activity)
        {
            if (activity is null)
            {
                return 0;
            }

            var watched = 0;
            foreach (var entry in activity.Values)
            {
                if (entry.Played || entry.PlayCount > 0)
                {
                    watched++;
                }
            }

            return watched;
        }

        /// <summary>
        /// Determines whether a user should get their own personalization pass.
        /// </summary>
        /// <param name="watchedCount">The user's watched item count, from <see cref="CountWatched"/>.</param>
        /// <param name="minimumWatched">The configured floor. 0 or less personalizes everyone.</param>
        /// <returns>True when the user has watched enough to personalize.</returns>
        public static bool IsEligible(int watchedCount, int minimumWatched)
        {
            if (minimumWatched <= 0)
            {
                return true;
            }

            return watchedCount >= minimumWatched;
        }
    }
}
