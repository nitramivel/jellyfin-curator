using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Core.Models;
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
            foreach (var itemId in itemIds)
            {
                var item = _libraryManager.GetItemById(itemId);
                if (item is null)
                {
                    continue;
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

            return result;
        }
    }
}
