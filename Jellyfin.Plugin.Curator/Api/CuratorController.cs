using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Services;
using Jellyfin.Plugin.Curator.Services.Categories;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Api
{
    /// <summary>One category as shown on the configuration page.</summary>
    /// <param name="Id">Curator's category ID.</param>
    /// <param name="Name">Category name.</param>
    /// <param name="Description">One-line description.</param>
    /// <param name="MemberCount">Members in the latest run.</param>
    /// <param name="PlaylistCount">Live playlists across users.</param>
    /// <param name="HandedOffCount">Playlists the user took ownership of.</param>
    /// <param name="UpdatedAt">When the definition was last refreshed (UTC).</param>
    /// <param name="ModelId">Model that produced it.</param>
    public sealed record CategorySummary(
        Guid Id,
        string Name,
        string Description,
        int MemberCount,
        int PlaylistCount,
        int HandedOffCount,
        DateTime UpdatedAt,
        string ModelId);

    /// <summary>Current run state for the configuration page.</summary>
    /// <param name="IsRunning">Whether a run is in progress.</param>
    /// <param name="Categories">The stored categories.</param>
    public sealed record CuratorStatus(bool IsRunning, IReadOnlyList<CategorySummary> Categories);

    /// <summary>
    /// Admin API backing the configuration page: run status, the category list,
    /// and the manual-run trigger.
    /// </summary>
    [ApiController]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Route("Curator")]
    public class CuratorController : ControllerBase
    {
        private readonly CuratorRunService _runService;
        private readonly ICategoryStore _categoryStore;
        private readonly ILogger<CuratorController> _logger;

        public CuratorController(
            CuratorRunService runService,
            ICategoryStore categoryStore,
            ILogger<CuratorController> logger)
        {
            _runService = runService;
            _categoryStore = categoryStore;
            _logger = logger;
        }

        /// <summary>
        /// Gets run state and the current categories.
        /// </summary>
        /// <response code="200">Status retrieved.</response>
        /// <returns>The status.</returns>
        [HttpGet("Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<CuratorStatus> GetStatus()
        {
            var categories = _categoryStore.GetAll()
                .Select(category => new CategorySummary(
                    category.Id,
                    category.Name,
                    category.Description,
                    category.Members.Count,
                    category.UserPlaylists.Count(link => link.PlaylistId is not null),
                    category.UserPlaylists.Count(link => link.HandedOff),
                    category.UpdatedAt,
                    category.ModelId))
                .ToArray();

            return new CuratorStatus(_runService.IsRunning, categories);
        }

        /// <summary>
        /// Starts a run in the background. Returns immediately; progress appears
        /// in the server log and in this endpoint's IsRunning flag.
        /// </summary>
        /// <response code="202">Run started.</response>
        /// <response code="409">A run is already in progress.</response>
        /// <returns>An action result.</returns>
        [HttpPost("Run")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public ActionResult StartRun()
        {
            if (_runService.IsRunning)
            {
                return Conflict("A Curator run is already in progress.");
            }

            // Fire and forget: a run can take many minutes, far past any sane
            // HTTP timeout. The run service enforces single-flight itself.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _runService.RunAsync(null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Curator: manual run failed — {Message}", ex.Message);
                }
            });

            return Accepted();
        }

        /// <summary>
        /// Deletes a category definition. Its playlists are left alone — remove
        /// them in Jellyfin if you want them gone.
        /// </summary>
        /// <param name="categoryId">The category ID.</param>
        /// <response code="204">Deleted.</response>
        /// <response code="404">No such category.</response>
        /// <returns>An action result.</returns>
        [HttpDelete("Categories/{categoryId}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult DeleteCategory([FromRoute] Guid categoryId)
        {
            return _categoryStore.Delete(categoryId) ? NoContent() : NotFound();
        }
    }
}
