using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Services.Categories;
using Jellyfin.Plugin.Curator.Services.Playlists;
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
    /// Answers for the contents of a Curator home screen row.
    ///
    /// <para>
    /// Home Screen Sections calls this by reflection — it finds the assembly, the
    /// class and the method named in the registration, builds an instance through
    /// the server's DI container, and passes one payload. Three consequences shape
    /// this class and none of them are style choices:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>It must stay <c>public</c>, and every constructor argument must be
    /// resolvable from the root container, or the row silently returns nothing.</item>
    /// <item><see cref="GetResults"/> must remain the only public method of that
    /// name — the other side resolves it with <c>Type.GetMethod(string)</c>, which
    /// throws on an overload.</item>
    /// <item>It must never throw. The call sits inside a request that renders the
    /// whole home screen, so an exception here is not a missing row, it is an
    /// error page.</item>
    /// </list>
    ///
    /// <para>
    /// What it does is deliberately almost nothing: resolve the viewer's own
    /// playlist and hand back its items in playlist order. No grouping, no sorting,
    /// no trimming. That is the entire point of owning the row — the ordering is
    /// already correct when it gets here, because <c>MemberOrdering</c> arranged
    /// each viewer's copy of a shared playlist when the run wrote it, and anything
    /// this method did to the list would be undoing that work.
    /// </para>
    /// </summary>
    public class CuratorSectionResults
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IDtoService _dtoService;
        private readonly ICategoryStore _categoryStore;
        private readonly ILogger<CuratorSectionResults> _logger;

        public CuratorSectionResults(
            ILibraryManager libraryManager,
            IUserManager userManager,
            IDtoService dtoService,
            ICategoryStore categoryStore,
            ILogger<CuratorSectionResults> logger)
        {
            _libraryManager = libraryManager;
            _userManager = userManager;
            _dtoService = dtoService;
            _categoryStore = categoryStore;
            _logger = logger;
        }

        /// <summary>
        /// Returns one row's items, in playlist order.
        /// </summary>
        /// <param name="payload">The viewer and the category, from Home Screen Sections.</param>
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
                    "Curator: failed to build home screen row for category '{Category}' and user {UserId}",
                    payload?.AdditionalData,
                    payload?.UserId);
                return Empty();
            }
        }

        private QueryResult<BaseItemDto> Resolve(CuratorSectionPayload? payload)
        {
            // Both fields come back from the client rather than from anything the
            // server remembered, so neither is trusted. The category ID has to
            // parse and has to name a stored category, and the items returned are
            // only ever the ones already in that user's own playlist.
            if (payload is null
                || !Guid.TryParse(payload.AdditionalData, CultureInfo.InvariantCulture, out var categoryId)
                || payload.UserId.Equals(Guid.Empty))
            {
                return Empty();
            }

            var category = _categoryStore.Get(categoryId);
            if (category is null)
            {
                // Registration outlives the category: nothing removes a section
                // from Home Screen Sections' in-memory table, so a category deleted
                // since the last restart is still asked for its contents until the
                // server restarts. An empty row is the honest answer, and the
                // section settings write has already stopped it being drawn.
                _logger.LogDebug("Curator: home screen asked for category {CategoryId}, which no longer exists", categoryId);
                return Empty();
            }

            var user = _userManager.GetUserById(payload.UserId);
            if (user is null)
            {
                return Empty();
            }

            var link = category.UserPlaylists.Find(l => l.UserId == payload.UserId);
            if (link is null)
            {
                // The category exists but is not this viewer's — a personal row
                // belonging to somebody else, or a shared one they are not targeted
                // by. Returning nothing is both correct and the safe direction.
                return Empty();
            }

            var playlist = PlaylistLookup.Find(_libraryManager, category.Id, payload.UserId, link.PlaylistId);
            if (playlist is null)
            {
                _logger.LogDebug(
                    "Curator: no playlist resolved for category '{Category}' and user {UserId}; row is empty",
                    category.Name,
                    payload.UserId);
                return Empty();
            }

            // GetManageableItems keeps the playlist's own order and resolves links
            // that carry a path rather than an item ID, which a playlist the viewer
            // has edited by hand can.
            var items = playlist.GetManageableItems()
                .Select(entry => entry.Item2)
                .Where(item => item is not null && item.IsVisible(user))
                .ToList();

            items = DropDuplicateVersions(items);

            if (items.Count == 0)
            {
                return Empty();
            }

            var dtos = _dtoService.GetBaseItemDtos(items, BuildDtoOptions(), user);
            return new QueryResult<BaseItemDto>(dtos);
        }

        /// <summary>
        /// Drops a second row for a title the playlist already holds, keeping playlist
        /// order otherwise.
        /// </summary>
        /// <remarks>
        /// A backstop, not the mechanism. Duplicates are collapsed before the model
        /// ever sees the library, so a category built since that landed cannot contain
        /// two cuts of one film — but a category stored before it can, and its
        /// playlist keeps showing two posters until the next weekly run rewrites the
        /// definition. Doing it here as well means the row is right on the next home
        /// screen load rather than on the next run.
        /// <para>
        /// Cheap enough to sit on this path: a row is a few dozen items, and reducing
        /// one is field copies. It reuses <see cref="DuplicateItems"/> rather than
        /// asking the question a second way, so the row and the run cannot disagree
        /// about what a duplicate is.
        /// </para>
        /// </remarks>
        private static List<BaseItem> DropDuplicateVersions(List<BaseItem> items)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.CollapseDuplicateVersions || items.Count < 2)
            {
                return items;
            }

            // An item the reducer will not describe — anything that is not a movie,
            // series or episode — has no key to be matched on and is always kept.
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
            if (surviving.Count == records.Count)
            {
                return items;
            }

            var kept = new List<BaseItem>(surviving.Count);
            var drawn = new HashSet<Guid>();
            foreach (var item in items)
            {
                // Kept when it survived, and also when it was never a candidate —
                // the reducer declined to describe it, so nothing here can judge it.
                if ((surviving.Contains(item.Id) || !judged.Contains(item.Id))
                    && drawn.Add(item.Id))
                {
                    kept.Add(item);
                }
            }

            return kept;
        }

        /// <summary>
        /// The fields and images a home screen card needs.
        /// </summary>
        /// <remarks>
        /// Matches what the other section providers ask for, so Curator's cards
        /// render identically to every other row rather than subtly differently.
        /// </remarks>
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
