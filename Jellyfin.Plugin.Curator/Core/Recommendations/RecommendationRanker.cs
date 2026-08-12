using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Core.Recommendations
{
    /// <summary>
    /// One of a viewer's categories, as input to the ranker.
    /// </summary>
    /// <param name="Members">Members in the model's confidence order, strongest first.</param>
    /// <param name="IsPersonal">Whether this category was invented for this viewer rather than shared.</param>
    public sealed record RankedCategory(IReadOnlyList<Guid> Members, bool IsPersonal);

    /// <summary>
    /// Knobs for <see cref="RecommendationRanker"/>.
    /// </summary>
    /// <param name="MaxItems">How many items the finished list may hold. 0 means no cap.</param>
    /// <param name="IncludeWatched">
    /// Whether items the viewer has already played appear at all. When true they are
    /// kept but always sort below everything unwatched.
    /// </param>
    public sealed record RecommendationOptions(int MaxItems, bool IncludeWatched);

    /// <summary>
    /// Merges a viewer's categories into one long list ordered most-recommended
    /// first.
    ///
    /// <para>
    /// This spends no model call. Every category already carries the model's own
    /// ranking of its members — the discovery and viewer passes are both told to
    /// order by how strongly an item belongs — so the information needed to rank a
    /// viewer's whole library is already bought and stored. What this adds is the
    /// two things a single category cannot express: that an item turning up in
    /// several of a viewer's threads is a stronger signal than topping any one of
    /// them, and that a recommendation is mainly about what they have not watched.
    /// </para>
    /// </summary>
    public static class RecommendationRanker
    {
        /// <summary>
        /// How much more a category invented for this viewer counts than one shared
        /// with everybody. Personal categories are drawn from their own history, so
        /// they describe this person rather than the library.
        /// </summary>
        private const double PersonalWeight = 1.6;

        private const double SharedWeight = 1.0;

        /// <summary>
        /// How much of a category's weight position can take away. At 0.6 the
        /// strongest member scores full weight and the weakest still scores 0.4 of
        /// it — being in the thread at all matters more than where in it, which is
        /// what stops one long category dominating the head of the list.
        /// </summary>
        private const double PositionFalloff = 0.6;

        /// <summary>A favourite the viewer has already seen leads the watched tail.</summary>
        private const double FavouriteBoost = 0.5;

        /// <summary>
        /// Ranks a viewer's recommendations.
        /// </summary>
        /// <param name="categories">The viewer's categories, personal and shared.</param>
        /// <param name="activity">The viewer's watch activity, keyed by item.</param>
        /// <param name="options">Length and watched-item handling.</param>
        /// <returns>Item IDs, most recommended first.</returns>
        public static IReadOnlyList<Guid> Rank(
            IReadOnlyList<RankedCategory> categories,
            IReadOnlyDictionary<Guid, UserActivity> activity,
            RecommendationOptions options)
        {
            ArgumentNullException.ThrowIfNull(categories);
            ArgumentNullException.ThrowIfNull(activity);
            ArgumentNullException.ThrowIfNull(options);

            var scores = new Dictionary<Guid, double>();

            foreach (var category in categories)
            {
                if (category.Members.Count == 0)
                {
                    continue;
                }

                var weight = category.IsPersonal ? PersonalWeight : SharedWeight;
                for (var i = 0; i < category.Members.Count; i++)
                {
                    // Scores accumulate rather than taking a maximum: appearing in
                    // three of someone's threads is the signal worth surfacing, and
                    // a max would throw exactly that away.
                    var positional = 1.0 - (PositionFalloff * i / category.Members.Count);
                    var id = category.Members[i];
                    scores[id] = scores.GetValueOrDefault(id) + (weight * positional);
                }
            }

            if (scores.Count == 0)
            {
                return [];
            }

            var ranked = new List<(Guid Id, bool Unwatched, double Score)>(scores.Count);
            foreach (var (id, score) in scores)
            {
                activity.TryGetValue(id, out var watched);
                var unwatched = !HasBeenPlayed(watched);

                if (!unwatched && !options.IncludeWatched)
                {
                    continue;
                }

                // A favourite is worth leading the watched tail with, but must never
                // outrank something unseen: the tier decides that, not the score.
                var adjusted = watched?.IsFavorite == true ? score + FavouriteBoost : score;
                ranked.Add((id, unwatched, adjusted));
            }

            // Unwatched first as a hard tier, then score, then the ID. The ID
            // tie-break is what makes the order stable between runs — without it two
            // items on an identical score could swap places every time the playlist
            // was rebuilt, which reads as the row reshuffling itself at random.
            ranked.Sort((a, b) =>
            {
                if (a.Unwatched != b.Unwatched)
                {
                    return a.Unwatched ? -1 : 1;
                }

                var byScore = b.Score.CompareTo(a.Score);
                return byScore != 0 ? byScore : a.Id.CompareTo(b.Id);
            });

            IEnumerable<Guid> ordered = ranked.Select(r => r.Id);
            if (options.MaxItems > 0)
            {
                ordered = ordered.Take(options.MaxItems);
            }

            return [.. ordered];
        }

        /// <summary>
        /// Whether the viewer has actually watched this, counting a series by its
        /// episodes.
        /// </summary>
        /// <remarks>
        /// A series' own user data never carries watch history — playback is
        /// recorded against episodes — so <c>PlayCount</c> on a show is 0 however
        /// much of it has been seen. Reading only that would put every series a
        /// viewer has worked through into their recommendations as if it were new.
        /// </remarks>
        private static bool HasBeenPlayed(UserActivity? activity)
        {
            if (activity is null)
            {
                return false;
            }

            if (activity.EpisodesPlayed is { } episodes)
            {
                return episodes > 0;
            }

            return activity.Played || activity.PlayCount > 0;
        }

        /// <summary>
        /// The stable identity for a viewer's recommendation playlist.
        /// </summary>
        /// <remarks>
        /// Derived from the user ID so it is the same on every run without anything
        /// being stored. That is what lets the playlist be found again by its
        /// provider-ID tether rather than by its name — hard rule 3 — even though,
        /// unlike a category, there is no definition file to keep an ID in.
        /// </remarks>
        /// <param name="userId">The viewer.</param>
        /// <returns>A deterministic ID for that viewer's recommendations.</returns>
        public static Guid IdentityFor(Guid userId)
        {
            return IdentityFor(userId, RecommendationScope.Combined);
        }

        /// <summary>
        /// The stable identity for one of a viewer's recommendation playlists.
        /// </summary>
        /// <remarks>
        /// Each scope needs an identity of its own or the three lists would share a
        /// tether and overwrite one another. The combined scope's seed is unchanged
        /// from when it was the only one, which is what stops an upgrade orphaning
        /// every existing "Recommended for You": its tether is the only record that
        /// playlist is Curator's, and a new seed would leave the old one untethered,
        /// unclaimed by any definition, and looking exactly like the orphan the
        /// sweep in <c>RemoveOrphanedPlaylistsAsync</c> deletes. Never renumber it.
        /// </remarks>
        /// <param name="userId">The viewer.</param>
        /// <param name="scope">Which of that viewer's lists.</param>
        /// <returns>A deterministic ID for that viewer's list.</returns>
        public static Guid IdentityFor(Guid userId, RecommendationScope scope)
        {
            var prefix = scope switch
            {
                RecommendationScope.Movies => "curator-recommendations:movies:",
                RecommendationScope.Shows => "curator-recommendations:shows:",
                _ => "curator-recommendations:",
            };

            var seed = Encoding.UTF8.GetBytes(prefix + userId.ToString("N"));
            var hash = SHA256.HashData(seed);
            return new Guid(hash.AsSpan(0, 16));
        }

        /// <summary>
        /// Every scope, so a caller can sweep the ones it is not building.
        /// </summary>
        public static IReadOnlyList<RecommendationScope> AllScopes { get; } =
        [
            RecommendationScope.Combined,
            RecommendationScope.Movies,
            RecommendationScope.Shows,
        ];
    }
}
