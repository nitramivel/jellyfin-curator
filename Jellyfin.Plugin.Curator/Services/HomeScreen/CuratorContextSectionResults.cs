using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Curator.Configuration;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Context;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Core.Recommendations;
using Jellyfin.Plugin.Curator.Services.Categories;
using Jellyfin.Plugin.Curator.Services.Context;
using Jellyfin.Plugin.Curator.Services.Summaries;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services.HomeScreen
{
    /// <summary>
    /// Answers for the contents of the weather row and the time-of-day row.
    ///
    /// <para>
    /// A separate class from <see cref="CuratorSectionResults"/> rather than a
    /// second method on it, because Home Screen Sections resolves the handler with
    /// <c>Type.GetMethod(string)</c> — which throws on an overload. One public
    /// <c>GetResults</c> per class is a hard constraint of the other plugin, not a
    /// style choice, and everything <see cref="CuratorSectionResults"/> says about
    /// staying public, being constructible from the root container and never
    /// throwing applies here word for word.
    /// </para>
    ///
    /// <para>
    /// Unlike every other Curator row there is <b>no playlist behind this one</b>.
    /// It is computed when the home screen asks, which is the only way it can be
    /// true: a playlist rebuilt on a schedule would show what the weather was doing
    /// at the last refresh, and a row whose entire claim is "this suits right now"
    /// cannot be hours stale. What that costs is the Collection Sections path — that
    /// plugin resolves a row by playlist name and there is no playlist to name — so
    /// these two rows exist only under <c>SectionDelivery.Integrated</c>.
    /// </para>
    ///
    /// <para>
    /// It is affordable because everything expensive was bought earlier. The model's
    /// judgement about when an item suits watching is cached against the item by the
    /// condensing pass; the viewer's own ordering comes from the categories already
    /// stored; the weather comes from a cache that is refreshed in the background and
    /// never fetched here. What is left on this path is set arithmetic over a few
    /// hundred GUIDs and one library lookup for the handful that survive.
    /// </para>
    /// </summary>
    public class CuratorContextSectionResults
    {
        /// <summary>The <c>additionalData</c> value identifying the weather row.</summary>
        public const string WeatherRowKey = "context:weather";

        /// <summary>The <c>additionalData</c> value identifying the time-of-day row.</summary>
        public const string DaypartRowKey = "context:daypart";

        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IDtoService _dtoService;
        private readonly ICategoryStore _categoryStore;
        private readonly ISummaryStore _summaryStore;
        private readonly IWeatherService _weatherService;
        private readonly ILogger<CuratorContextSectionResults> _logger;

        public CuratorContextSectionResults(
            ILibraryManager libraryManager,
            IUserManager userManager,
            IDtoService dtoService,
            ICategoryStore categoryStore,
            ISummaryStore summaryStore,
            IWeatherService weatherService,
            ILogger<CuratorContextSectionResults> logger)
        {
            _libraryManager = libraryManager;
            _userManager = userManager;
            _dtoService = dtoService;
            _categoryStore = categoryStore;
            _summaryStore = summaryStore;
            _weatherService = weatherService;
            _logger = logger;
        }

        /// <summary>
        /// Returns one context row's items, best fit first.
        /// </summary>
        /// <param name="payload">The viewer and which row, from Home Screen Sections.</param>
        /// <returns>The row's items, or an empty result.</returns>
        public QueryResult<BaseItemDto> GetResults(CuratorSectionPayload payload)
        {
            try
            {
                return Resolve(payload);
            }
            catch (Exception ex)
            {
                // The home screen is drawn by one request across every section. A
                // section that throws does not lose its own row, it fails the page.
                _logger.LogError(
                    ex,
                    "Curator: failed to build the '{Row}' context row for user {UserId}",
                    payload?.AdditionalData,
                    payload?.UserId);
                return Empty();
            }
        }

        private QueryResult<BaseItemDto> Resolve(CuratorSectionPayload? payload)
        {
            // Echoed back by the client, so neither field is trusted. The row key has
            // to be one of exactly two strings and the viewer has to exist.
            if (payload is null || payload.UserId.Equals(Guid.Empty))
            {
                return Empty();
            }

            var kind = payload.AdditionalData switch
            {
                WeatherRowKey => ContextRowKind.Weather,
                DaypartRowKey => ContextRowKind.Daypart,
                _ => (ContextRowKind?)null,
            };

            if (kind is not { } rowKind)
            {
                return Empty();
            }

            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.ContextRows)
            {
                // Registration outlives the setting: nothing unregisters a section, so
                // a row switched off since the last restart is still asked for its
                // contents. Empty is the honest answer.
                return Empty();
            }

            var user = _userManager.GetUserById(payload.UserId);
            if (user is null)
            {
                return Empty();
            }

            var affinities = LoadAffinities();
            if (affinities.Count == 0)
            {
                // Either nothing has been classified yet, or everything was judged to
                // suit nothing in particular. The health check catches the first —
                // which is the one an owner needs telling about, since it is what
                // switching on the rows without the judgement looks like. The second
                // is a real answer about a library, and an empty row is the honest
                // rendering of it.
                return Empty();
            }

            var context = CurrentContext(config, payload.UserId);
            var ranked = RankedForViewer(payload.UserId, config);
            if (ranked.Count == 0)
            {
                return Empty();
            }

            var chosen = ContextRanker.Rank(
                ranked, affinities, context, rowKind, Math.Max(0, config.MaxContextRowItems));

            if (chosen.Count == 0)
            {
                _logger.LogDebug(
                    "Curator: nothing in {Viewer}'s library suits {Context}; the {Row} row is empty",
                    payload.UserId,
                    context.Describe(),
                    rowKind);
                return Empty();
            }

            var items = ResolveItems(chosen, user);
            return items.Count == 0
                ? Empty()
                : new QueryResult<BaseItemDto>(_dtoService.GetBaseItemDtos(items, BuildDtoOptions(), user));
        }

        /// <summary>
        /// The conditions this row is being drawn for.
        /// </summary>
        /// <remarks>
        /// The weather reading carries the location's own UTC offset, so a viewer
        /// given a location in another timezone gets <em>their</em> evening rather
        /// than the server's. With no reading at all the server's clock is the only
        /// clock there is, which is right for the ordinary case of a household
        /// watching where its server lives.
        /// </remarks>
        private ViewingContext CurrentContext(PluginConfiguration config, Guid userId)
        {
            var reading = _weatherService.Current(config.LocationFor(userId));

            var utcNow = DateTime.UtcNow;
            var localTime = reading.LocalTimeOfDay(utcNow) ?? DateTime.Now.TimeOfDay;
            var daypart = ContextVocabulary.DaypartFor(localTime);

            return reading.IsUsable
                ? new ViewingContext(reading.Words, daypart)
                : ViewingContext.ClockOnly(daypart);
        }

        /// <summary>
        /// Every item that has been judged, with what it was judged to suit.
        /// </summary>
        /// <remarks>
        /// Read straight off the summary store, which is one JSON file already held
        /// in memory. Items never classified are simply absent, and
        /// <c>ContextRanker</c> treats absence as "unknown" rather than "no" — a
        /// library part-way through its first classifying pass draws a shorter row,
        /// not a wrong one.
        /// </remarks>
        private Dictionary<Guid, ItemContextAffinity> LoadAffinities()
        {
            var affinities = new Dictionary<Guid, ItemContextAffinity>();

            foreach (var (id, summary) in _summaryStore.GetAll())
            {
                if (summary.ContextSourceHash is null)
                {
                    continue;
                }

                if (summary.Weather.Count > 0 || summary.Dayparts.Count > 0)
                {
                    affinities[id] = new ItemContextAffinity(summary.Weather, summary.Dayparts);
                }
            }

            return affinities;
        }

        /// <summary>
        /// The viewer's own ordering over everything Curator has put in front of them.
        /// </summary>
        /// <remarks>
        /// Built from the category store alone — member lists are GUIDs already held
        /// in memory, so this touches no library query and no user data. That is what
        /// makes it safe on a render path.
        /// <para>
        /// Watch history is deliberately <b>not</b> consulted, so an item the viewer
        /// has seen is as eligible as one they have not. The recommendation row
        /// already answers "what next"; this one answers "what suits right now", and
        /// the honest answer to that on a cold wet evening is often something they
        /// have loved before. Reading watch state here would also mean per-item user
        /// data lookups on the path that draws the home screen, which is the one cost
        /// this class exists to avoid.
        /// </para>
        /// </remarks>
        private IReadOnlyList<Guid> RankedForViewer(Guid userId, PluginConfiguration config)
        {
            var categories = new List<RankedCategory>();

            foreach (var category in _categoryStore.GetAll())
            {
                if (!category.UserPlaylists.Exists(link => link.UserId == userId))
                {
                    continue;
                }

                if (category.Members.Count > 0)
                {
                    categories.Add(new RankedCategory(category.Members, category.OwnerUserId == userId));
                }
            }

            if (categories.Count == 0)
            {
                return [];
            }

            // No cap and watched items kept: the context filter is what narrows this,
            // and cutting the list first would hide an item that suits tonight exactly
            // because it sat outside an arbitrary top N.
            return RecommendationRanker.Rank(
                categories,
                new Dictionary<Guid, UserActivity>(),
                new RecommendationOptions(MaxItems: 0, IncludeWatched: true));
        }

        /// <summary>
        /// Resolves the chosen IDs to items, keeping the chosen order and dropping
        /// anything this viewer cannot see.
        /// </summary>
        private List<BaseItem> ResolveItems(IReadOnlyList<Guid> chosen, User user)
        {
            var items = new List<BaseItem>(chosen.Count);

            foreach (var id in chosen)
            {
                var item = _libraryManager.GetItemById(id);
                if (item is not null && item.IsVisible(user))
                {
                    items.Add(item);
                }
            }

            if (items.Count < 2)
            {
                return items;
            }

            // The same backstop the category rows carry: two cuts of one film must
            // not both appear, whatever the stored categories still hold.
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.CollapseDuplicateVersions)
            {
                return items;
            }

            var records = new List<MediaItemRecord>(items.Count);
            var judged = new HashSet<Guid>();
            foreach (var item in items)
            {
                var record = ItemReducer.Reduce(item);
                if (record is not null && judged.Add(record.Id))
                {
                    records.Add(record);
                }
            }

            if (records.Count < 2)
            {
                return items;
            }

            var surviving = DuplicateItems.SurvivingIds(records, config.MatchDuplicatesByProviderId);
            return surviving.Count == records.Count
                ? items
                : items.Where(item => surviving.Contains(item.Id) || !judged.Contains(item.Id)).ToList();
        }

        /// <summary>
        /// The fields and images a home screen card needs. Matches what every other
        /// section provider asks for, so these cards render identically to the rest.
        /// </summary>
        private static DtoOptions BuildDtoOptions()
        {
            return new DtoOptions
            {
                Fields = [ItemFields.PrimaryImageAspectRatio, ItemFields.MediaSourceCount],
                ImageTypes = [ImageType.Primary, ImageType.Backdrop, ImageType.Banner, ImageType.Thumb],
                ImageTypeLimit = 1,
            };
        }

        private static QueryResult<BaseItemDto> Empty()
        {
            return new QueryResult<BaseItemDto>(Array.Empty<BaseItemDto>());
        }
    }
}
