using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services.Library
{
    /// <summary>
    /// Default <see cref="IUserActivityProvider"/> backed by <see cref="IUserDataManager"/>.
    /// </summary>
    public class UserActivityProvider : IUserActivityProvider
    {
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ILogger<UserActivityProvider> _logger;

        public UserActivityProvider(
            IUserManager userManager,
            ILibraryManager libraryManager,
            IUserDataManager userDataManager,
            ILogger<UserActivityProvider> logger)
        {
            _userManager = userManager;
            _libraryManager = libraryManager;
            _userDataManager = userDataManager;
            _logger = logger;
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<Guid, UserActivity> GetActivity(Guid userId, IReadOnlyCollection<Guid> itemIds)
        {
            ArgumentNullException.ThrowIfNull(itemIds);

            var user = _userManager.GetUserById(userId);
            if (user is null)
            {
                _logger.LogWarning("Curator: user {UserId} not found; running without personalization", userId);
                return new Dictionary<Guid, UserActivity>();
            }

            var now = DateTime.UtcNow;
            var result = new Dictionary<Guid, UserActivity>(itemIds.Count);
            var seriesIds = new HashSet<Guid>();
            foreach (var itemId in itemIds)
            {
                var item = _libraryManager.GetItemById(itemId);
                if (item is null)
                {
                    continue;
                }

                // Collected before the signal check below: a series' own row is
                // usually signal-free precisely because its watch history lives on
                // the episodes, and that is the case the rollup exists to fix.
                if (item is Series)
                {
                    seriesIds.Add(itemId);
                }

                var data = _userDataManager.GetUserData(user, item);
                if (data is null)
                {
                    continue;
                }

                // Only carry items the user has actually interacted with — untouched
                // items are the default state and would just bloat the prompt.
                var hasSignal = data.Played || data.PlayCount > 0 || data.IsFavorite || data.Rating.HasValue;
                if (!hasSignal)
                {
                    continue;
                }

                int? daysSinceLastPlayed = null;
                if (data.LastPlayedDate is { } lastPlayed)
                {
                    daysSinceLastPlayed = Math.Max(0, (int)(now - lastPlayed).TotalDays);
                }

                result[itemId] = new UserActivity
                {
                    Played = data.Played,
                    PlayCount = data.PlayCount,
                    IsFavorite = data.IsFavorite,
                    UserRating = data.Rating is { } rating ? (float)rating : null,
                    DaysSinceLastPlayed = daysSinceLastPlayed,
                };
            }

            return RollUpEpisodes(user, seriesIds, result, now);
        }

        /// <summary>
        /// Replaces the watch half of every series entry with the aggregate of its
        /// episodes' user data.
        /// </summary>
        /// <remarks>
        /// Without this a television viewer looks like a viewer of nothing: playback is
        /// recorded against episode rows, and Curator's scan carries series. The
        /// episodes are queried here rather than in the scan because the scan's output
        /// is the model's item list, and an 86-series library would drown it.
        /// </remarks>
        private IReadOnlyDictionary<Guid, UserActivity> RollUpEpisodes(
            Jellyfin.Database.Implementations.Entities.User user,
            HashSet<Guid> seriesIds,
            Dictionary<Guid, UserActivity> gathered,
            DateTime now)
        {
            if (seriesIds.Count == 0)
            {
                return gathered;
            }

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false,
            };

            var episodes = _libraryManager.GetItemsResult(query).Items;
            var flattened = new List<SeriesActivityRollup.EpisodeActivity>(episodes.Count);

            foreach (var item in episodes)
            {
                // SeriesId is a persisted property; Episode.Series walks parent folders
                // through server statics and must not be used here.
                if (item is not Episode episode || !seriesIds.Contains(episode.SeriesId))
                {
                    continue;
                }

                var data = _userDataManager.GetUserData(user, episode);
                if (data is null)
                {
                    continue;
                }

                int? days = null;
                if (data.LastPlayedDate is { } lastPlayed)
                {
                    days = Math.Max(0, (int)(now - lastPlayed).TotalDays);
                }

                flattened.Add(new SeriesActivityRollup.EpisodeActivity(
                    episode.SeriesId,
                    data.Played,
                    data.PlayCount,
                    data.IsFavorite,
                    days));
            }

            var rolled = SeriesActivityRollup.Apply(gathered, seriesIds, flattened);

            _logger.LogInformation(
                "Curator: rolled {Episodes} episode records up onto {Series} series for user {UserId}",
                flattened.Count,
                seriesIds.Count,
                user.Id);

            return rolled;
        }
    }
}
