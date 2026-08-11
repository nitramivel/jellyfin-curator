using System;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Curator.Core
{
    /// <summary>
    /// Reduces Jellyfin <see cref="BaseItem"/>s to the compact <see cref="MediaItemRecord"/>
    /// sent to the LLM. Pure logic — no Jellyfin service dependencies — so it is directly
    /// unit-testable against constructed entities.
    /// </summary>
    public static class ItemReducer
    {
        /// <summary>
        /// The default maximum overview length, in characters. Overviews longer than this
        /// are cut at a word boundary with a trailing ellipsis. Chosen so a typical batch
        /// of a few hundred items stays well inside model context windows.
        /// </summary>
        public const int DefaultMaxOverviewLength = 300;

        /// <summary>
        /// Passed as the maximum length to keep an overview whole.
        /// <para>
        /// Used by the condensed-summary pass, which must read the overview the
        /// metadata provider actually wrote. Distilling a truncation would bake the
        /// cut into the cache permanently — the summary would be a compression of
        /// the first 300 characters forever, and nothing downstream could tell.
        /// </para>
        /// </summary>
        public const int NoOverviewLimit = int.MaxValue;

        /// <summary>
        /// Reduces a library item to its compact record, or returns null for items that
        /// cannot be meaningfully categorized (unsupported kinds, missing names).
        /// </summary>
        /// <param name="item">The library item.</param>
        /// <param name="maxOverviewLength">Maximum overview length in characters.</param>
        /// <returns>The reduced record, or null if the item should be skipped.</returns>
        public static MediaItemRecord? Reduce(BaseItem item, int maxOverviewLength = DefaultMaxOverviewLength)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                return null;
            }

            MediaKind kind;
            switch (item)
            {
                case Movie:
                    kind = MediaKind.Movie;
                    break;
                case Series:
                    kind = MediaKind.Series;
                    break;
                case Episode:
                    kind = MediaKind.Episode;
                    break;
                default:
                    return null;
            }

            var record = new MediaItemRecord
            {
                Id = item.Id,
                Kind = kind,
                Name = item.Name.Trim(),
                Year = item.ProductionYear,
                Genres = item.Genres?.Where(g => !string.IsNullOrWhiteSpace(g)).ToArray() ?? [],
                Tags = item.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? [],
                OfficialRating = NullIfEmpty(item.OfficialRating),
                RuntimeMinutes = TicksToMinutes(item.RunTimeTicks),
                CommunityRating = item.CommunityRating,
                Overview = TruncateOverview(item.Overview, maxOverviewLength),
                PrimaryVersionId = PrimaryVersionOf(item),
                ExternalId = ExternalIdOf(item),
            };

            if (item is Episode episode)
            {
                record = record with
                {
                    // Deliberately the persisted SeriesName/SeriesId properties, not
                    // Episode.Series: that getter walks parent folders through server
                    // statics and is unusable outside a running Jellyfin.
                    SeriesName = NullIfEmpty(episode.SeriesName),
                    SeriesId = episode.SeriesId == Guid.Empty ? null : episode.SeriesId,
                    SeasonNumber = episode.ParentIndexNumber,
                    EpisodeNumber = episode.IndexNumber,
                };
            }

            return record;
        }

        /// <summary>
        /// Truncates an overview at a word boundary, appending an ellipsis.
        /// Returns null for null/whitespace input.
        /// </summary>
        /// <param name="overview">The raw overview text.</param>
        /// <param name="maxLength">Maximum length in characters, including the ellipsis.</param>
        /// <returns>The truncated overview, or null.</returns>
        public static string? TruncateOverview(string? overview, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(overview))
            {
                return null;
            }

            ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);

            var text = overview.Trim();
            if (text.Length <= maxLength)
            {
                return text;
            }

            var cut = text.LastIndexOf(' ', maxLength - 1);
            if (cut <= 0)
            {
                cut = maxLength - 1;
            }

            return text[..cut].TrimEnd() + "…";
        }

        /// <summary>
        /// The providers consulted for an item's external identity, in the order they
        /// are trusted.
        /// </summary>
        /// <remarks>
        /// A fixed order, and the first one present wins, so the answer is a single
        /// string two rows either share or do not. Matching on "any provider ID in
        /// common" would be more forgiving and is deliberately not done: it makes
        /// duplicate detection depend on which scrapers happened to run, so the same
        /// library collapses differently after a metadata refresh. TMDb leads because
        /// it is what Jellyfin's own movie and series scrapers fill in first.
        /// </remarks>
        private static readonly MetadataProvider[] IdentityProviders =
        [
            MetadataProvider.Tmdb,
            MetadataProvider.Imdb,
            MetadataProvider.Tvdb,
        ];

        /// <summary>
        /// The item this one is an alternate version of, or null.
        /// </summary>
        /// <remarks>
        /// Only <see cref="Video"/> carries the link — a Series has no alternate
        /// versions — and Jellyfin stores it as a string, so a value that will not
        /// parse is treated as absent rather than trusted.
        /// </remarks>
        private static Guid? PrimaryVersionOf(BaseItem item)
        {
            if (item is not Video video || string.IsNullOrWhiteSpace(video.PrimaryVersionId))
            {
                return null;
            }

            return Guid.TryParse(video.PrimaryVersionId, out var primary) && primary != Guid.Empty
                ? primary
                : null;
        }

        /// <summary>
        /// The item's identity at its metadata provider, as <c>tmdb:78</c>.
        /// </summary>
        private static string? ExternalIdOf(BaseItem item)
        {
            if (item.ProviderIds is null || item.ProviderIds.Count == 0)
            {
                return null;
            }

            foreach (var provider in IdentityProviders)
            {
                var value = item.GetProviderId(provider);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return provider.ToString().ToLowerInvariant() + ":" + value.Trim();
                }
            }

            return null;
        }

        private static int? TicksToMinutes(long? ticks)
        {
            if (ticks is not > 0)
            {
                return null;
            }

            return (int)Math.Round(ticks.Value / (double)TimeSpan.TicksPerMinute);
        }

        private static string? NullIfEmpty(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
