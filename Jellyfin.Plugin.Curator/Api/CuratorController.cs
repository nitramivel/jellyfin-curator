using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Core.Models;
using Jellyfin.Plugin.Curator.Core.Scheduling;
using Jellyfin.Plugin.Curator.Services;
using Jellyfin.Plugin.Curator.Services.Categories;
using Jellyfin.Plugin.Curator.Services.Health;
using Jellyfin.Plugin.Curator.Services.Playlists;
using Jellyfin.Plugin.Curator.Services.Runs;
using Jellyfin.Plugin.Curator.Services.Summaries;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
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
    /// <param name="CreatedAt">When the definition was first created (UTC).</param>
    /// <param name="SourceProposalCount">Batch proposals that produced it in the latest run.</param>
    /// <param name="Users">Per-user playlist links.</param>
    /// <param name="SourceProposals">The individual proposals that merged into it.</param>
    /// <param name="OwnerUserId">The user this category belongs to, or null when shared.</param>
    /// <param name="OwnerUserName">That user's name, or null when shared or deleted.</param>
    public sealed record CategorySummary(
        Guid Id,
        string Name,
        string Description,
        int MemberCount,
        int PlaylistCount,
        int HandedOffCount,
        DateTime UpdatedAt,
        string ModelId,
        DateTime CreatedAt,
        int SourceProposalCount,
        IReadOnlyList<CategoryUserLink> Users,
        IReadOnlyList<CategorySourceProposal> SourceProposals,
        Guid? OwnerUserId,
        string? OwnerUserName);

    /// <summary>One user's playlist link for a category, for the config page.</summary>
    /// <param name="UserId">The Jellyfin user.</param>
    /// <param name="UserName">That user's name, or null if the account no longer exists.</param>
    /// <param name="PlaylistId">The playlist GUID, or null while the category is empty for them.</param>
    /// <param name="HandedOff">Whether the user took ownership, making the playlist permanently theirs.</param>
    public sealed record CategoryUserLink(
        Guid UserId,
        string? UserName,
        Guid? PlaylistId,
        bool HandedOff);

    /// <summary>One member item of a category, resolved against the library.</summary>
    /// <param name="ItemId">The Jellyfin item ID.</param>
    /// <param name="Name">The item's name, or null if it is no longer in the library.</param>
    /// <param name="Year">Production year, when known.</param>
    /// <param name="Kind">Item type, e.g. Movie or Series.</param>
    /// <param name="Position">1-based position in the category's confidence ordering.</param>
    public sealed record CategoryMember(
        Guid ItemId,
        string? Name,
        int? Year,
        string? Kind,
        int Position);

    /// <summary>Categories to delete in one call.</summary>
    /// <param name="CategoryIds">The category IDs.</param>
    public sealed record DeleteCategoriesRequest(IReadOnlyList<Guid> CategoryIds);

    /// <summary>The outcome of a bulk delete.</summary>
    /// <param name="Deleted">How many definitions were removed.</param>
    /// <param name="NotFound">How many IDs matched nothing.</param>
    public sealed record DeleteCategoriesResult(int Deleted, int NotFound);

    /// <summary>Current run state for the configuration page.</summary>
    /// <param name="IsRunning">Whether a run is in progress.</param>
    /// <param name="Categories">The stored categories.</param>
    /// <param name="CurrentRunId">The run in progress, for following it through the Runs endpoints.</param>
    /// <param name="CurrentRun">
    /// Live progress, tokens and cost for the run in progress, or null when nothing
    /// is running. Read from memory, so the page can poll it as fast as it likes.
    /// </param>
    public sealed record CuratorStatus(
        bool IsRunning,
        IReadOnlyList<CategorySummary> Categories,
        Guid? CurrentRunId,
        RunLogSummary? CurrentRun);

    /// <summary>One Curator scheduled task and its current cadence.</summary>
    /// <param name="Key">The task key, e.g. CuratorGenerateCategories.</param>
    /// <param name="Name">Display name.</param>
    /// <param name="Description">What the task does.</param>
    /// <param name="Mode">Manual, Interval, Daily or Weekly.</param>
    /// <param name="IntervalHours">Hours between runs, when Interval.</param>
    /// <param name="TimeOfDayMinutes">Minutes past midnight, when Daily or Weekly.</param>
    /// <param name="DayOfWeek">Day, when Weekly.</param>
    /// <param name="State">Jellyfin's task state, e.g. Idle or Running.</param>
    /// <param name="LastRun">When it last finished, if ever.</param>
    /// <param name="LastStatus">How that run ended, if ever.</param>
    public sealed record CuratorTaskSchedule(
        string Key,
        string Name,
        string Description,
        string Mode,
        double IntervalHours,
        int TimeOfDayMinutes,
        int DayOfWeek,
        string State,
        DateTime? LastRun,
        string? LastStatus);

    /// <summary>A cadence change for one task.</summary>
    /// <param name="Key">The task key.</param>
    /// <param name="Mode">Manual, Interval, Daily or Weekly.</param>
    /// <param name="IntervalHours">Hours between runs, when Interval.</param>
    /// <param name="TimeOfDayMinutes">Minutes past midnight, when Daily or Weekly.</param>
    /// <param name="DayOfWeek">Day, when Weekly.</param>
    public sealed record ScheduleUpdate(
        string Key,
        string Mode,
        double IntervalHours,
        int TimeOfDayMinutes,
        int DayOfWeek);

    /// <summary>Coverage of the condensed-summary cache, for the Summaries tab.</summary>
    /// <param name="IsRunning">Whether a distillation pass is in progress.</param>
    /// <param name="Progress">How far that pass has got, 0-100.</param>
    /// <param name="StoredCount">How many items have a condensed summary.</param>
    /// <param name="OriginalChars">Total length of the overviews those summaries replaced.</param>
    /// <param name="CondensedChars">Total length of the summaries themselves.</param>
    /// <param name="UsedInRuns">Whether category runs are currently sending them.</param>
    /// <param name="LastResult">What the last completed pass did, or null if none has run.</param>
    public sealed record SummaryStatus(
        bool IsRunning,
        double Progress,
        int StoredCount,
        long OriginalChars,
        long CondensedChars,
        bool UsedInRuns,
        SummaryRunResult? LastResult);

    /// <summary>One stored summary beside the overview it replaced.</summary>
    /// <param name="ItemId">The Jellyfin item ID.</param>
    /// <param name="Title">The item's title at distillation time.</param>
    /// <param name="Text">The condensed summary.</param>
    /// <param name="Tags">Consolidated tags, however many the model judged applied.</param>
    /// <param name="Length">Length of the condensed summary.</param>
    /// <param name="SourceLength">Length of the overview it replaced.</param>
    /// <param name="ModelId">The model that produced it.</param>
    /// <param name="CreatedAt">When it was produced (UTC).</param>
    public sealed record SummarySample(
        Guid ItemId,
        string Title,
        string Text,
        IReadOnlyList<string> Tags,
        int Length,
        int SourceLength,
        string? ModelId,
        DateTime CreatedAt);

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
        private readonly IRunLogStore _runLogStore;
        private readonly ISummaryStore _summaryStore;
        private readonly SummaryDistillService _distillService;
        private readonly HealthService _healthService;
        private readonly ICuratorPlaylistService _playlistService;
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ITaskManager _taskManager;
        private readonly ILogger<CuratorController> _logger;

        public CuratorController(
            CuratorRunService runService,
            ICategoryStore categoryStore,
            IRunLogStore runLogStore,
            ISummaryStore summaryStore,
            SummaryDistillService distillService,
            HealthService healthService,
            ICuratorPlaylistService playlistService,
            IUserManager userManager,
            ILibraryManager libraryManager,
            ITaskManager taskManager,
            ILogger<CuratorController> logger)
        {
            _runService = runService;
            _categoryStore = categoryStore;
            _runLogStore = runLogStore;
            _summaryStore = summaryStore;
            _distillService = distillService;
            _healthService = healthService;
            _playlistService = playlistService;
            _userManager = userManager;
            _libraryManager = libraryManager;
            _taskManager = taskManager;
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
            // Resolve names once rather than per link; a category can carry a link
            // for a user who has since been deleted, which surfaces as a null name.
            var userNames = _userManager.GetUsers().ToDictionary(u => u.Id, u => u.Username);

            var categories = _categoryStore.GetAll()
                .Select(category => new CategorySummary(
                    category.Id,
                    category.Name,
                    category.Description,
                    category.Members.Count,
                    category.UserPlaylists.Count(link => link.PlaylistId is not null),
                    category.UserPlaylists.Count(link => link.HandedOff),
                    category.UpdatedAt,
                    category.ModelId,
                    category.CreatedAt,
                    category.SourceProposalCount,
                    category.UserPlaylists
                        .Select(link => new CategoryUserLink(
                            link.UserId,
                            userNames.GetValueOrDefault(link.UserId),
                            link.PlaylistId,
                            link.HandedOff))
                        .ToArray(),
                    category.SourceProposals,
                    category.OwnerUserId,
                    category.OwnerUserId is { } owner ? userNames.GetValueOrDefault(owner) : null))
                .ToArray();

            return new CuratorStatus(
                _runService.IsRunning,
                categories,
                _runService.CurrentRunId,
                _runLogStore.Current());
        }

        /// <summary>
        /// Lists recorded runs, newest first. Each run's full record — every step
        /// and every LLM exchange — is fetched separately by ID.
        /// </summary>
        /// <param name="limit">The most to return.</param>
        /// <response code="200">Runs listed.</response>
        /// <returns>The run summaries.</returns>
        [HttpGet("Runs")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IReadOnlyList<RunLogSummary>> GetRuns([FromQuery] int limit = 25)
        {
            return Ok(_runLogStore.List(Math.Clamp(limit, 1, 200)));
        }

        /// <summary>
        /// Gets one run's whole record, including every prompt and response.
        /// </summary>
        /// <remarks>
        /// Returned as raw stored JSON rather than a re-serialized model: the file
        /// is the artifact, and passing it through unchanged means what the page
        /// shows is exactly what is on disk.
        /// </remarks>
        /// <param name="runId">The run ID.</param>
        /// <response code="200">The run record.</response>
        /// <response code="404">No such run.</response>
        /// <returns>The run document.</returns>
        /// <summary>
        /// Reconciles stored categories against the playlists that actually exist,
        /// without an LLM call and without spending anything.
        /// </summary>
        /// <remarks>
        /// Rebuilds a playlist a category has lost, deletes definitions left holding
        /// none, and deletes Curator-owned playlists no definition claims. Playlists
        /// without the ownership tag are never touched.
        /// </remarks>
        /// <response code="200">Sync ran; the body says what changed.</response>
        /// <returns>What changed.</returns>
        [HttpPost("Playlists/Sync")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<PlaylistSyncResult>> SyncPlaylists()
        {
            return Ok(await _runService.SyncPlaylistsAsync(HttpContext.RequestAborted).ConfigureAwait(false));
        }

        /// <summary>
        /// Re-publishes the home screen rows from the stored categories, without an
        /// LLM call and without spending anything.
        /// </summary>
        /// <response code="200">Sync ran; the body says whether it succeeded.</response>
        /// <returns>Whether the integration reported success.</returns>
        [HttpPost("HomeScreen/Sync")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<bool>> SyncHomeScreen()
        {
            return Ok(await _runService.SyncHomeScreenAsync(HttpContext.RequestAborted).ConfigureAwait(false));
        }

        /// <summary>
        /// One run reduced to what the configuration page shows when its row is
        /// expanded.
        /// </summary>
        /// <remarks>
        /// Deliberately not the run file, which carries every prompt and response in
        /// full and runs to hundreds of kilobytes. Fetch that from
        /// <c>Runs/{runId}</c> when you actually want it.
        /// </remarks>
        /// <param name="runId">The run.</param>
        /// <response code="200">The run detail.</response>
        /// <response code="404">No such run.</response>
        /// <returns>The detail.</returns>
        [HttpGet("Runs/{runId}/Detail")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<RunDetail> GetRunDetail([FromRoute] Guid runId)
        {
            var names = _userManager.GetUsers().ToDictionary(u => u.Id, u => u.Username);
            return _runLogStore.Detail(runId, names) is { } detail
                ? Ok(detail)
                : NotFound();
        }

        [HttpGet("Runs/{runId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult GetRun([FromRoute] Guid runId)
        {
            return _runLogStore.ReadRaw(runId) is { } json
                ? Content(json, "application/json")
                : NotFound();
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
            //
            // SuppressFlow stops the request's ExecutionContext — and with it the
            // ambient HttpContext and its scoped IServiceProvider — from flowing into
            // the background task. Without it the task outlives the request that
            // started it, and dies part-way through with ObjectDisposedException
            // ('IServiceProvider') the next time Jellyfin resolves a pooled DbContext,
            // leaving categories written but their playlists never built.
            //
            // What it does NOT cover is the host itself going away. Installing or
            // updating any plugin makes Jellyfin dispose CoreAppHost and rebuild it
            // in the same process, and a run started beforehand keeps executing
            // against disposed services until it touches one. That produces the same
            // ObjectDisposedException from a completely different cause, and this
            // comment previously claimed immunity from it — which cost an
            // investigation. CuratorRunService handles that case: it cancels on
            // dispose, and RunFailure.IsHostTeardown names what slips through.
            using (ExecutionContext.SuppressFlow())
            {
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
            }

            return Accepted();
        }

        /// <summary>
        /// Rebuilds every viewer's recommendation playlist now.
        /// </summary>
        /// <remarks>
        /// Synchronous, unlike a run or a distil pass: no network calls happen, so
        /// it finishes well inside an HTTP timeout and the page shows the result.
        /// </remarks>
        /// <response code="200">Refreshed.</response>
        /// <returns>How many playlists were rebuilt, or -1 when a run is in progress.</returns>
        [HttpPost("Recommendations/Refresh")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<int>> RefreshRecommendations()
        {
            return await _runService.RefreshRecommendationsAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Reports the plugin's health: the ways it goes quietly wrong.
        /// </summary>
        /// <response code="200">Checked.</response>
        /// <returns>The findings, most severe first.</returns>
        [HttpGet("Health")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<HealthReport> GetHealth()
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return Conflict("Curator configuration is unavailable.");
            }

            return _healthService.Check(config);
        }

        /// <summary>
        /// Lists Curator's scheduled tasks and how often each is set to run.
        /// </summary>
        /// <response code="200">Schedules listed.</response>
        /// <returns>The tasks.</returns>
        [HttpGet("Schedules")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IReadOnlyList<CuratorTaskSchedule>> GetSchedules()
        {
            return CuratorWorkers().Select(worker =>
            {
                var spec = ScheduleTranslator.FromTriggers(worker.Triggers);
                return new CuratorTaskSchedule(
                    worker.ScheduledTask.Key,
                    worker.Name,
                    worker.Description,
                    spec.Mode.ToString(),
                    Math.Round(spec.IntervalHours, 2),
                    spec.TimeOfDayMinutes,
                    (int)spec.DayOfWeek,
                    worker.State.ToString(),
                    worker.LastExecutionResult?.EndTimeUtc,
                    worker.LastExecutionResult?.Status.ToString());
            }).ToList();
        }

        /// <summary>
        /// Sets how often Curator's scheduled tasks run.
        /// </summary>
        /// <remarks>
        /// Writes through Jellyfin's own task manager, so the dashboard's Scheduled
        /// Tasks page shows the same values and either page can edit them. Saving
        /// <em>replaces</em> a task's triggers rather than adding to them — the
        /// editor offers one cadence, so keeping a second hidden trigger would mean
        /// the page showed something other than what runs.
        /// </remarks>
        /// <param name="updates">The cadences to apply.</param>
        /// <response code="200">Schedules updated.</response>
        /// <returns>The tasks as they now stand.</returns>
        [HttpPost("Schedules")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IReadOnlyList<CuratorTaskSchedule>> UpdateSchedules(
            [FromBody] IReadOnlyList<ScheduleUpdate> updates)
        {
            ArgumentNullException.ThrowIfNull(updates);

            var workers = CuratorWorkers().ToDictionary(w => w.ScheduledTask.Key, StringComparer.Ordinal);

            foreach (var update in updates)
            {
                if (update is null || !workers.TryGetValue(update.Key, out var worker))
                {
                    continue;
                }

                if (!Enum.TryParse<ScheduleMode>(update.Mode, ignoreCase: true, out var mode))
                {
                    mode = ScheduleMode.Manual;
                }

                var spec = new ScheduleSpec(
                    mode,
                    update.IntervalHours,
                    update.TimeOfDayMinutes,
                    (DayOfWeek)Math.Clamp(update.DayOfWeek, 0, 6));

                worker.Triggers = [.. ScheduleTranslator.ToTriggers(spec)];

                _logger.LogInformation(
                    "Curator: '{Task}' is now set to {Mode}",
                    worker.Name,
                    spec.Normalized().Mode);
            }

            return GetSchedules();
        }

        /// <summary>
        /// Runs the cleanup and sync pass now.
        /// </summary>
        /// <response code="200">Maintenance finished.</response>
        /// <returns>What it did.</returns>
        [HttpPost("Maintenance")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<MaintenanceResult>> RunMaintenance()
        {
            // Not fire-and-forget, unlike a run or a distil pass: this makes no
            // network calls, so it finishes well inside an HTTP timeout and the page
            // can show the result directly.
            return await _runService.RunMaintenanceAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Curator's own scheduled tasks, in the order the settings page shows them.
        /// </summary>
        private IEnumerable<IScheduledTaskWorker> CuratorWorkers()
        {
            return _taskManager.ScheduledTasks
                .Where(w => string.Equals(w.ScheduledTask.Category, "Curator", StringComparison.Ordinal))
                .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reports coverage of the condensed-summary cache and what the last pass did.
        /// </summary>
        /// <response code="200">Status retrieved.</response>
        /// <returns>The summary status.</returns>
        [HttpGet("Summaries/Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<SummaryStatus> GetSummaryStatus()
        {
            var stored = _summaryStore.GetAll();
            var config = Plugin.Instance?.Configuration;

            var originalChars = stored.Values.Sum(s => (long)s.SourceLength);
            var condensedChars = stored.Values.Sum(s => (long)(s.Text?.Length ?? 0));

            return new SummaryStatus(
                _distillService.IsRunning,
                Math.Round(_distillService.Progress, 1),
                stored.Count,
                originalChars,
                condensedChars,
                config?.UseCondensedSummaries ?? false,
                _distillService.LastResult);
        }

        /// <summary>
        /// Lists stored summaries alongside the overview each replaced.
        /// </summary>
        /// <param name="limit">How many to return.</param>
        /// <response code="200">Summaries listed.</response>
        /// <returns>The summaries, newest first.</returns>
        [HttpGet("Summaries")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IReadOnlyList<SummarySample>> GetSummaries([FromQuery] int limit = 40)
        {
            var capped = Math.Clamp(limit, 1, 500);

            var samples = _summaryStore.GetAll().Values
                .OrderByDescending(s => s.CreatedAt)
                .Take(capped)
                .Select(s => new SummarySample(
                    s.ItemId,
                    s.Title ?? "(unknown)",
                    s.Text,
                    s.Tags,
                    s.Text?.Length ?? 0,
                    s.SourceLength,
                    s.ModelId,
                    s.CreatedAt))
                .ToList();

            return samples;
        }

        /// <summary>
        /// Starts a condensed-summary pass.
        /// </summary>
        /// <remarks>
        /// Fire and forget for the same reason a category run is: a first pass over
        /// a large library is many minutes of paid calls, far past any HTTP timeout.
        /// Progress is readable from <c>Summaries/Status</c>.
        /// </remarks>
        /// <param name="force">Redo every item rather than only new and changed ones.</param>
        /// <param name="profileId">
        /// The model profile to use for this pass, overriding the saved choice.
        /// Lets the picker on the Summaries tab take effect without saving first;
        /// blank or unknown falls back to the configured profile.
        /// </param>
        /// <response code="202">Pass started.</response>
        /// <response code="409">A pass is already in progress.</response>
        /// <returns>An action result.</returns>
        [HttpPost("Summaries/Distill")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public ActionResult StartDistill(
            [FromQuery] bool force = false,
            [FromQuery] string? profileId = null)
        {
            if (_distillService.IsRunning)
            {
                return Conflict("A Curator summary pass is already in progress.");
            }

            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return Conflict("Curator configuration is unavailable.");
            }

            // See StartRun for why the execution context is suppressed: without it
            // this task inherits the request's scoped IServiceProvider and dies on
            // the first pooled DbContext it touches after the response is sent.
            using (ExecutionContext.SuppressFlow())
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _distillService
                            .DistillAsync(config, null, force, CancellationToken.None, profileId)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Curator: summary pass failed — {Message}", ex.Message);
                    }
                });
            }

            return Accepted();
        }

        /// <summary>
        /// Deletes every stored condensed summary.
        /// </summary>
        /// <remarks>
        /// Safe by construction: the summaries are a Curator-side cache and the
        /// library's own overviews were never modified, so this restores the
        /// original prompt behaviour exactly rather than losing anything.
        /// </remarks>
        /// <response code="200">Summaries cleared.</response>
        /// <returns>How many were removed.</returns>
        [HttpDelete("Summaries")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<int> ClearSummaries()
        {
            var removed = _summaryStore.Clear();
            _logger.LogInformation("Curator: cleared {Removed} condensed summary/summaries", removed);
            return removed;
        }

        /// <summary>
        /// Lists one category's member items, in the stored confidence order.
        /// </summary>
        /// <remarks>
        /// Deliberately separate from Status: resolving every member of every
        /// category would be hundreds of library lookups on a poll that runs every
        /// five seconds during a run. The config page calls this only when a member
        /// list is actually expanded.
        /// </remarks>
        /// <param name="categoryId">The category ID.</param>
        /// <response code="200">Members listed.</response>
        /// <response code="404">No such category.</response>
        /// <returns>The members.</returns>
        [HttpGet("Categories/{categoryId}/Members")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<IReadOnlyList<CategoryMember>> GetCategoryMembers([FromRoute] Guid categoryId)
        {
            if (_categoryStore.Get(categoryId) is not { } category)
            {
                return NotFound();
            }

            var members = new List<CategoryMember>(category.Members.Count);
            for (var i = 0; i < category.Members.Count; i++)
            {
                var itemId = category.Members[i];

                // A member can outlive the library item it points at; surface that
                // as a null name rather than dropping the row, so the ordering the
                // model produced stays legible.
                var item = _libraryManager.GetItemById(itemId);
                members.Add(new CategoryMember(
                    itemId,
                    item?.Name,
                    item?.ProductionYear,
                    item?.GetBaseItemKind().ToString(),
                    i + 1));
            }

            return members;
        }

        /// <summary>
        /// Deletes a category and the playlists it built.
        /// </summary>
        /// <remarks>
        /// Playlists a user has taken ownership of — those without the `curator`
        /// tag, and those already marked handed off — are left in place. Deleting a
        /// Curator category is not a licence to delete someone's own list.
        /// </remarks>
        /// <param name="categoryId">The category ID.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <response code="204">Deleted.</response>
        /// <response code="404">No such category.</response>
        /// <returns>An action result.</returns>
        [HttpDelete("Categories/{categoryId}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> DeleteCategory(
            [FromRoute] Guid categoryId,
            CancellationToken cancellationToken)
        {
            if (_categoryStore.Get(categoryId) is not { } category)
            {
                return NotFound();
            }

            await _playlistService.RemoveCategoryPlaylistsAsync(category, cancellationToken).ConfigureAwait(false);
            return _categoryStore.Delete(categoryId) ? NoContent() : NotFound();
        }

        /// <summary>
        /// Deletes several categories and their playlists in one call. Unknown IDs
        /// are counted rather than failing the request, so a stale page cannot block
        /// the whole batch.
        /// </summary>
        /// <param name="request">The category IDs.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <response code="200">Deleted; the body reports what happened.</response>
        /// <returns>An action result.</returns>
        [HttpPost("Categories/Delete")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<DeleteCategoriesResult>> DeleteCategories(
            [FromBody] DeleteCategoriesRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var deleted = 0;
            var missing = 0;
            foreach (var id in request.CategoryIds)
            {
                if (_categoryStore.Get(id) is not { } category)
                {
                    missing++;
                    continue;
                }

                await _playlistService.RemoveCategoryPlaylistsAsync(category, cancellationToken).ConfigureAwait(false);
                if (_categoryStore.Delete(id))
                {
                    deleted++;
                }
                else
                {
                    missing++;
                }
            }

            _logger.LogInformation(
                "Curator: deleted {Deleted} category definition(s) and their playlists from the config page ({Missing} not found)",
                deleted,
                missing);

            return new DeleteCategoriesResult(deleted, missing);
        }
    }
}
