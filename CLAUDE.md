# Curator — Jellyfin plugin

Sends the media library to an LLM, asks what threads run through it, turns the
answers into ordered Jellyfin playlists, and publishes those as home screen rows
through the Collection Sections plugin.

**Scope discipline:** Curator does LLM inference and nothing else. Rule-based
filtering, metadata queries, and external list sources are explicitly out of
scope — that is [SmartLists](https://github.com/jyourstone/jellyfin-smartlists-plugin)'
job, and Curator is designed to sit alongside it. Reject feature requests that
amount to "add a rules engine."

## Development commands

The .NET 9 SDK is installed per-user and is **not on `PATH` by default**:

```bash
export PATH="$HOME/.dotnet:$PATH"     # required first, in every shell

dotnet build Jellyfin.Plugin.Curator.sln -c Release
dotnet test  Jellyfin.Plugin.Curator.sln -c Release    # 326 tests, no network
./build/package.sh                                      # artifacts/Curator_<version>/
VERSION=0.2.0.0 CHANGELOG="..." ./build/release.sh      # zip + manifest.json entry
```

Ubuntu's apt only carries SDK 8 and 10; 9 came from `dot.net/v1/dotnet-install.sh`
into `~/.dotnet`. Target framework is **net9.0** — Jellyfin 10.11.x runs on .NET 9,
*not* .NET 8. Build treats warnings as errors.

There is no local Jellyfin server on the dev machine, so nothing here has been
exercised against a live server (see *Unverified* below). Verification is
`dotnet test` plus compiling against the real 10.11 ABI.

## Releasing

`build/release.sh` builds the zip (plugin files at zip **root**), computes the MD5
Jellyfin verifies on install, and inserts the version into `manifest.json`. Then
create a GitHub release tagged `v<VERSION>` and upload that exact zip — rebuilding
or re-zipping changes the checksum and breaks catalogue installs. Users add
`https://raw.githubusercontent.com/nitramivel/jellyfin-curator/main/manifest.json`
as a plugin repository.

## Project structure

```text
Jellyfin.Plugin.Curator/
├── Core/                     # Pure logic — no Jellyfin services, fully unit-tested
│   ├── ItemReducer.cs        # BaseItem -> MediaItemRecord
│   ├── SeriesActivityRollup.cs   # Episode watch data -> series watch depth
│   ├── RunFailure.cs         # Host teardown vs. a real fault
│   ├── LibraryPathFilter.cs  # Drops items orphaned by a removed library folder
│   ├── CategoryIdentity.cs   # Matches a reconciled category to a stored definition
│   ├── CategoryRetention.cs  # Which stored categories to prune when over a cap
│   ├── Models/CategoryLimits.cs  # The one value the prompt AND the Reconciler read
│   ├── Llm/                  # Batcher, PromptBuilder, ProposalParser,
│   │                         #   ModelProfiles (list migration + resolution),
│   │                         #   JsonResponse (shared model-output unwrapping)
│   ├── Summaries/            # SummaryPlan (what needs distilling, staleness by
│   │                         #   source hash), SummaryPromptBuilder, SummaryParser
│   ├── Reconciliation/       # Reconciler, StringSimilarity
│   ├── Playlists/            # PlaylistSyncDecision (the ownership decision table)
│   ├── Recommendations/      # RecommendationRanker (merge a viewer's categories
│   │                         #   into one ranked list; per-user playlist identity)
│   ├── Scheduling/           # ScheduleSpec + ScheduleTranslator (the page's one
│   │                         #   cadence <-> Jellyfin's trigger list)
│   ├── Health/               # HealthCheck (facts in, findings out — pure)
│   ├── HomeScreen/           # SectionConfigMerger (JSON merge for both integrations)
│   └── Models/               # MediaItemRecord, CategoryProposal, ReconciledCategory,
│                             #   CategoryDefinition, UserActivity
├── Services/                 # Everything that touches Jellyfin or the network
│   ├── CuratorRunService.cs  # The end-to-end run; both entry points call this
│   ├── GenerateCategoriesTask.cs   # IScheduledTask, weekly default
│   ├── DistillSummariesTask.cs     # IScheduledTask, daily default
│   ├── MaintenanceTask.cs          # IScheduledTask, daily; reconcile + prune
│   ├── RefreshRecommendationsTask.cs # IScheduledTask, 6-hourly; per-viewer rows
│   ├── HealthCheckTask.cs          # IScheduledTask, daily; read-only diagnosis
│   ├── Library/              # LibraryScanner, UserActivityProvider
│   ├── Llm/                  # ILlmProvider + Anthropic/Google/Grok/OpenAI/compatible,
│   │                         #   TransientHttpRetry (shared 429/5xx backoff), factory,
│   │                         #   CategoryProposalService (batch loop, token budget)
│   ├── Categories/           # ICategoryStore — one JSON file per category
│   ├── Summaries/            # ISummaryStore (one file for the whole set) +
│   │                         #   SummaryDistillService (the condensing pass)
│   ├── Runs/                 # IRunLogStore — one JSON file per run: every step,
│   │                         #   every prompt and response, written incrementally
│   ├── Playlists/            # CuratorPlaylistService — create/update/delete, tagging
│   └── HomeScreen/           # HomeScreenIntegrationService, API key provider
├── Api/CuratorController.cs  # Admin: Status, Run, runs, delete category + playlists
└── Configuration/            # PluginConfiguration + configPage.html
```

**The Core/Services split is the main architectural rule.** Anything decidable
without a server belongs in `Core/` as a pure function so it can be tested; the
`Services/` layer wires those decisions to Jellyfin. When a bug appears in
service code, first ask whether the logic can move to `Core/` and be pinned by a
test.

## Hard rules

These are invariants, not preferences. Breaking one produces a plugin that
silently misbehaves. (Rules 1-9 predate the model profile list; rule 10 came with
it, rule 11 with condensed summaries, rule 12 with the recommendation playlist,
rules 13-14 with scheduling and tag consolidation, and 15-16 with the task set
and the health check.)

1. **The model never sees Jellyfin GUIDs.** `PromptBuilder` assigns batch-local
   integer indexes; `ProposalParser` discards any index outside `0..n-1` and maps
   survivors back to GUIDs. This is what makes it structurally impossible for the
   model to reference an item the user does not own. Do not "simplify" by sending
   real IDs.
2. **Never resolve our own playlists by name.** Always by stored GUID, with
   recovery via the `CuratorCategory` provider-ID tether. Duplicate playlist names
   are legal in Jellyfin; SmartLists removed exactly this fallback for good reason.
3. **The `curator` tag is the ownership contract.** A playlist without it belongs
   to the user permanently — never modify, delete, or replace it, and never create
   a replacement for that user. Handoff takes precedence over deletion, even when
   the category empties.
4. **Empty category ≠ deleted category.** Remove the Jellyfin playlist, null the
   stored playlist ID, keep the definition so a later run reuses the same identity.
   Identity is name **or** member similarity, not name alone — the model renames
   every thread every run (measured: 0 of 16 then 0 of 33 names survived), and a
   rename must not destroy a row. `CategoryIdentity` uses Jaccard, deliberately
   not the Reconciler's overlap coefficient: that one divides by the smaller set,
   so a six-item category would swallow the identity of the twenty-item category
   containing it.
   The single exception is `CategoryRetention` enforcing a configured cap, where
   the user has asked for a bounded list and something must actually go; a pruned
   category loses its identity and returns as a new one. Retention spends
   **empty categories first** — one holding no playlist is showing nobody
   anything, so it goes before a live row however stale its date looks — then
   oldest-first within each group. A handed-off playlist counts as held.
   `POST /Curator/Playlists/Sync` applies the same judgement on demand: it
   rebuilds a playlist a category has lost, then deletes definitions still
   holding none, then deletes Curator-owned playlists no definition claims.
   Untagged playlists are never touched by any of it — see rule 3.
5. **No live LLM calls in tests.** Providers are tested through a stub
   `HttpMessageHandler`; the run pipeline through a stub `ILlmProvider`.
6. **Log token count and estimated cost at INFO every run.** Runs cost money; the
   user must be able to see what a run spent. **Cache reads are charged, not
   free.** Providers that report cached tokens inside their input count have it
   subtracted before costing, so pricing only `InputTokens` silently drops them
   from the total — measured, that understated one run by 24%, and a fully cached
   run would report about a third of its bill. `CachedInputCostPerMillion` falls
   back to half the input price when blank: conservative, and the right direction
   to err for a number whose only job is telling the owner what a run spent.
   Cache *writes* carry their own premium and are still unpriced, which the
   `RunLogCost` doc comment says out loud.
7. **`BatchSize = 0` means the whole library in one request, and is the default.**
   A thread running through items split across two batches is one the model
   never gets to see: each call only sees its own slice, so the categories it
   proposes can only join up what is in front of it. Raise it off 0 only for a
   model whose context cannot hold the library.
8. **Every category limit is told to the model, not only applied to its answer.**
   `CategoryLimits` is the single value both `PromptBuilder` and `Reconciler`
   take — build one per pool and pass the *same instance* to both. Do not unpack
   it into loose ints on the way, and do not add a limit that only one side sees.
   This has broken twice in opposite directions: prompt-3 / filter-6 binned 17 of
   22 proposals on size alone, and a filter capping at 8 categories with no
   target in the prompt got 5 categories covering 10% of the library where the
   other model gave 23 covering 78%. `CategoryLimitsTests` reads the numbers back
   out of the generated prompt and checks them against what the Reconciler
   actually does — a new limit belongs in that theory.
9. **A run log must never break the run it describes.** Every write in
   `Services/Runs/` swallows its own IO failures with a warning. The same applies
   to the prompt pool and the atomic temp-file rename — diagnostics are strictly
   subordinate to the run.
10. **A model profile is the unit of "how to call a model", and its legacy fields
   are not dead code.** `ModelProfile` carries provider, model, API key, base URL
   **and that profile's prices**; `Core/Llm/ModelProfiles` normalizes the list on
   every read. Pricing lives on the profile because a list you switch between
   turns "remember to change the prices when you change provider" from an
   occasional mistake into the normal case — rule 6 says the cost line must be
   right, and a shared price block cannot be. The pre-profile scalars on
   `PluginConfiguration` (`Provider`, `Model`, `ApiKey`, `BaseUrl`, the three
   `*CostPerMillion`) look unused and **must not be deleted**: XmlSerializer
   silently drops elements it has no property for, so removing them throws away
   the API key of every install that upgrades before it next opens the config
   page. `Normalize` folds them into one profile the first time it sees an empty
   list, and the config page blanks them on the next save so migration happens
   exactly once. Migration is deliberately *only* for an empty list — re-importing
   them afterwards would resurrect a deleted profile on every run.
11. **Condensed summaries are a cache, never a write-back.** The distillation pass
   reads Jellyfin's `Overview`, stores a short rewrite in
   `data/curator/summaries.json`, and substitutes it *on the way out of*
   `LibraryScanner`. Nothing ever writes to the library, so clearing the store
   restores the previous behaviour exactly and the originals cannot be damaged —
   say this plainly in any UI that offers to delete them. Two traps:
   `SummaryDistillService` must scan with `ItemReducer.NoOverviewLimit`, because
   distilling the reducer's 300-character cut would store a compression of the
   first 300 characters forever with nothing downstream able to tell; and
   `SummaryPlan` keys staleness on a hash of the source overview, which is the only
   thing that makes a second pass free and stops a metadata refresh leaving a
   summary describing the wrong film.
12. **The recommendation playlist has no stored definition, so its tether is its
   identity.** `RecommendationRanker.IdentityFor(userId)` derives a stable GUID
   from the user, stamped on the playlist as the usual `CuratorCategory`
   provider ID. That is how it is found again — never by name (rule 2), and
   without a store file. Two consequences that bite: the orphan sweep in
   `RemoveOrphanedPlaylistsAsync` deletes Curator-tagged playlists no definition
   claims, which is *exactly* this playlist's shape, so it self-identifies via
   `IsRecommendationPlaylist` rather than relying on callers to claim it —
   forgetting to claim would delete every viewer's spotlight row. And because
   nothing is stored, handoff needs no flag: a missing ownership tag says the
   viewer took it, on this run and every future one.
13. **The Schedule tab edits Jellyfin's triggers, not Curator's config.**
   `IScheduledTaskWorker.Triggers` is settable and persists on assignment, so the
   tab and Dashboard → Scheduled Tasks are two editors over one store. Saving
   **replaces** a task's triggers: the page offers one cadence and Jellyfin allows
   several of mixed kinds, so keeping a hidden extra would mean the page showed
   something other than what runs. `ScheduleTranslator` is the whole conversion and
   is round-trip tested — what the page saves must be what it reads back, or the
   settings drift every time they are opened.
14. **Two settings govern tags and they are not interchangeable.**
   `MaxTagsPerItem` takes the first N of the **raw** scraped list and defaults to 0;
   `ConsolidateTags` has the distillation pass keep however many genuinely describe
   the item, with `MaxConsolidatedTags` as a **ceiling and never a target** — a
   fixed count is exactly what the raw setting already did badly. Consolidation
   happens in the same model call as the summary, and `SummaryPlan` queues an item
   whose summary is current but whose tags are missing, so switching it on is
   incremental rather than a full redo. An item with no scraped tags is never
   queued: the answer can only ever be empty and queueing it would re-buy a summary
   every pass. When `SendConsolidatedTags` is on the run service raises the
   effective tag cap, because `MaxTagsPerItem` is normally 0 and would otherwise
   substitute the consolidated tags onto every record and then write none of them.
15. **Four scheduled tasks, one job each.** Generate Categories (weekly, the only
   one that costs money), Condense Summaries (daily), Refresh Recommendations
   (6-hourly), Clean Up and Sync (daily), Health Check (daily). The recommendation
   refresh deliberately does **not** live in the maintenance task any more: it
   tracks watch activity and wants a far shorter cadence than reconciling
   playlists does, and having two tasks rebuild the same playlists was duplicate
   work. Everything except Generate Categories is free and calls no model, so
   cadence there is a taste decision rather than a spending one. All four skip or
   no-op while a run is in progress — a run rewrites the same playlists and
   definitions, and racing it loses work.
16. **The health check exists because this plugin fails silently.** Both
   integrations degrade quietly by design, a run dies mid-flight whenever
   installing any plugin tears the host down, and library rows outlive their
   folder. From the outside all of these look identical to "Curator stopped
   working". `Core/Health/HealthCheck` is pure — facts in, findings out — so the
   judgements are testable without a server, and it must stay shy: a panel that
   reports normal operation as a problem gets ignored, which is worse than no
   panel. That is why a late run is not a stalled one and a manual-only schedule
   is never reported at all.
17. **Ask before adding dependencies** beyond the Jellyfin packages and an
   HTTP/JSON stack. Current runtime dependencies: none beyond Jellyfin. Test-only:
   xUnit.

## Verified integration facts

Read from the source of the plugins we integrate with. Several are
counterintuitive; do not "correct" them from memory.

**Collection Sections** (GUID `043b2c48-b3e0-4610-b398-8217b146d1a4`)

- Config is `SectionsConfig[] Sections`, each `{ UniqueId, DisplayText,
  CollectionName, SectionType }`, where `SectionType` serializes as the string
  `"Playlist"` or `"Collection"`.
- It resolves the target list **by name string comparison** against
  `CollectionName` — not by GUID. Playlist names are the join key and must stay
  stable. Renaming a Curator playlist by hand breaks its row.
- Its playlist handler groups episodes into parent series then takes 16, so an
  episode playlist renders as deduplicated **series cards in playlist order**. Its
  collection handler applies **no sort** — hence playlists are the default.
- We write sections by `GET` then `POST /Plugins/{guid}/Configuration`. Saving
  fires its `ConfigurationChanged`, which re-registers every section with Home
  Screen Sections. This is why we go through its config rather than registering
  ourselves.
- The server serializes plugin config as **camelCase** over HTTP while the C# type
  is PascalCase. `SectionConfigMerger` handles both; a naive implementation
  silently creates a second `Sections` array the plugin ignores.

**Home Screen Sections** (GUID `b8298e01-2697-407a-b44d-aa8dc795e850`)

- `PluginInterface.RegisterSection` is **in-memory only and does not persist** —
  anything registered that way vanishes on restart. Never call it directly.
- **Row order and card shape live here, not in Collection Sections.** Its plugin
  config carries `SectionSettings[]`, each `{ SectionId, Enabled,
  AllowUserOverride, LowerLimit, UpperLimit, OrderIndex, ViewMode,
  HideWatchedItems }`, where `ViewMode` is `Landscape` / `Portrait` / `Square`.
  Collection Sections has no fields for either, so a section registered through
  it lands on whatever default this plugin assigns. Curator owns `OrderIndex`
  and `ViewMode`, keyed on `SectionId` (not `UniqueId` as in Collection
  Sections), and leaves every other field on an **existing** entry alone.
- **"Leave every other field alone" does not apply to an entry being created.**
  An absent field is not left alone — it deserializes to the CLR default, so a
  new entry carrying only `SectionId`, `OrderIndex` and `ViewMode` arrives
  `Enabled=false` with `LowerLimit` and `UpperLimit` at 0: a row switched off
  and asking for no items. Measured on the owner's server, 40 of 46 Curator
  rows were in that state while every non-Curator row sat at `Enabled=true`,
  1 and 1. New entries now seed the fields Home Screen Sections sets for
  itself. Both limits at 0 is Curator's fingerprint — that plugin never writes
  it — so `RepairIncompleteEntry` heals exactly that shape and nothing else; a
  row with real limits keeps whatever the user set, `Enabled` included, because
  switching a row off by hand must survive the next run.
- Per-user enablement: `GET`/`POST /ModularHomeViews/UserSettings` with
  `{ UserId, EnabledSections, LockedSections, DefaultEnabledSections }`.
- A section must be **registered before it can be enabled**, or the ID references
  nothing. Order matters.
- The `GET` returns defaults *without* a `UserId` when a user has no saved
  settings, and the `POST` routes on that field — set it explicitly before posting.

**xAI caches per server, so a run needs a routing hint.** Grok's prompt caching
is automatic — there is no `cache_control` — but cache entries live on the
machine that wrote them, so without `x-grok-conv-id` the calls of one run scatter
across the fleet and each lands on a server that has never seen the prefix.
Measured before the header: 16 of 18 calls reported 128 cached tokens against a
byte-identical ~28k prefix, and the two that did hit cost $0.0033 and $0.0002
against $0.077 for the misses. `LlmRequest.ConversationId` carries the run ID and
**every call of a run must use the same value** — a per-user ID would defeat the
point, since the passes share one item list. The header is xAI's alone; the
generic OpenAI-compatible path must not send it. Anthropic is the opposite model:
caching there is explicit, a `cache_control` breakpoint on the item-list block
with a 1-hour TTL, because a run spans about four minutes and the 5-minute
default would be racing it.

**Both integrations degrade gracefully.** Assembly probing detects whether each
plugin is loaded; a missing one logs a clear explanation and returns `false`.
Playlists are still built. Never throw out of home screen integration.

## Jellyfin API notes

- `AuthenticationInfo.AppName` holds the API key name (there is no `.Name`).
- `TaskTriggerInfo` uses `TaskTriggerInfoType.IntervalTrigger` + `IntervalTicks`.
- Playlist members: assign `playlist.LinkedChildren` directly for exact ordering,
  then `UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)`.
- Deleting a playlist: `_libraryManager.DeleteItem(playlist, new DeleteOptions
  { DeleteFileLocation = true }, true)`.
- `Episode.SeriesName`/`SeriesId` are persisted properties — safe to read.
  `Episode.Series` walks parent folders through server statics and cannot run
  outside a live server (it will break tests).
- **A `Series` row's user data does not carry watch history.** Playback is
  recorded against `Episode` rows; `GetUserData(user, series)` returns the
  favorite flag and a manual rating, but `PlayCount` is 0 and `LastPlayedDate`
  is null however much of the show has been watched — "played" for a series is
  derived at DTO-build time from child counts and never persisted where
  `IUserDataManager` can see it. Anything asking "has this user watched this
  show" must aggregate the episodes itself. `Core/SeriesActivityRollup` does
  this; `UserActivityProvider` feeds it. Measured cost of not doing it: a
  library of 211 movies and 86 series produced an activity map holding 210
  movie entries and **3** series entries across six users, so every viewer who
  watches television read as a viewer who had watched nothing.
- Library scan query shape: `Recursive = true`, `IsVirtualItem = false` (excludes
  missing-episode stubs).
- **`IsVirtualItem = false` does not exclude items orphaned by a removed library
  folder.** Jellyfin keeps the database row when a library folder is deleted or
  its mount is renamed: same path, `LocationType=FileSystem`, a media source,
  and it still comes back from `GetItemsResult`. It is indistinguishable from a
  real item and plays back as nothing. Measured on the owner's server, 36 of 298
  movies and series sat under `/storage/`, a mount that no longer exists — 12%
  of what went to the model was unplayable, and three of the ten members of one
  category were ghosts. `LibraryScanner` now filters on
  `Core/LibraryPathFilter.IsInsideLibrary` against
  `ILibraryManager.GetVirtualFolders()`. It fails **open**: with no readable
  roots everything is kept, because emptying the library is a far worse failure
  than a few dead rows.
- **Installing or updating any plugin restarts the host in the same process.**
  Jellyfin sends shutdown notifications, logs `Disposing "CoreAppHost"`, then
  rebuilds and reports `Startup complete` a second or two later — same PID, same
  log file. A background task started beforehand is *not* killed: it keeps
  running against a disposed container and fails at whatever service it touches
  next. Measured 30 Jul 2026: a run started 09:29:22, host disposed 09:31:37 on
  a 0.3.16.0 install, run died 09:31:57 at 53% in `GetUserById` with
  `ObjectDisposedException: 'IServiceProvider'`. It reads exactly like a defect
  in this plugin. `CuratorRunService` cancels on dispose and
  `Core/RunFailure.IsHostTeardown` names what slips through the gap — disposal
  order across singletons is undefined, so cancellation alone cannot close it.
  Do not "fix" a report of this by hunting for a scope leak; check the server
  log for a restart first.

## Config page conventions

`Configuration/configPage.html` follows Jellyfin UI rules the hard way:

- **No ES6 template literals** — string concatenation only.
- Use `class="emby-input"`; `is="emby-input"` causes `htmlFor` errors. (`is=` is
  correct for `emby-select`, `emby-checkbox`, and `emby-button`.)
- Escape every interpolated value — category names come from an LLM.
- Config load/save goes through `ApiClient.getPluginConfiguration` /
  `updatePluginConfiguration`; everything else through `ApiClient.ajax` against
  `Curator/*`.
- `is="emby-checkbox"` is correct for **static** markup only. In rows built by
  `innerHTML`, customized built-in elements upgrade unreliably — one row rendered
  styled-but-unwired and the rest bare. Dynamic rows use plain
  `<input type="checkbox" class="curatorCheck">`.
- **The Model tab edits one profile at a time out of an in-memory list.**
  `modelProfiles` / `activeProfileId` hold the list while the page is open; the
  editor shows only the selected profile, so anything that changes the selection
  must call `captureProfileEditor()` first or the outgoing profile's edits are
  lost. `normalizeProfiles()` is a hand-mirror of
  `Core/Llm/ModelProfiles.Normalize` — change one, change both.
- **A new `ResponseShape` needs a schema in BOTH structured-output providers.**
  `OpenAiChatProvider` and `GoogleProvider` each branch `BuildResponseSchema` on
  the shape, in deliberately separate dialects. They used to hardcode the
  categories schema regardless, so adding a shape without touching them forces the
  model to answer in the wrong JSON — which looks like a parser bug.
- **Settings live on five tabs**: Model (profiles, request, spend), Library
  (what is sent and who for), Categories (the two pools' size and count), Home
  screen (rows and the recommendation playlist), Summaries (the condensing pass
  and its settings). **Two tabs are NOT part of the settings form** and hide the
  save row: Runs, and Schedule — the latter writes through Jellyfin's
  `ITaskManager`, not plugin config, so the form's Save would do nothing for it. Put a new setting where its *subject* is, not where it is
  technically enforced.
- **Option order in a `<select>` is load-bearing.** `setEnumSelect` falls back to
  matching by index when a stored config carries the numeric enum value, so
  provider options must stay in enum order. Change labels freely; never reorder.
- **The page is cached by the browser.** It is an `<EmbeddedResource>` served at
  a URL that does not change between versions, so after any deploy touching it
  you must hard-reload (Ctrl+Shift+R) or you are looking at the old page. This
  has wasted time in three separate sessions. Confirm the new page is really
  installed by grepping the DLL: `grep -ac curatorTabPanel .../Jellyfin.Plugin.Curator.dll`.

## Verified on a live server, and what is still open

The plugin runs end to end against a real 10.11.11 server: scan, propose,
reconcile, build playlists, publish rows. Items 1 and 2 of the old "unverified"
list — the loopback API key header and the Collection Sections config
round-trip — are confirmed working. What remains:

1. ~~**Category name churn.**~~ **Resolved and measured.** Names still never
   survive, but identity falling back to member similarity works: `category.renamed`
   ran 9 → 18 → 20 across three consecutive runs with none created fresh, so renames
   now keep their rows. What remains is *row* churn rather than identity churn — the
   model coins genuinely different threads each run (one run retired 24 categories),
   which `CategoryRetirementGraceRuns` softens by waiting several runs before a
   category loses its row, and `CategoryRetention` orders removal by `MissedRuns`
   so the longest-unproposed go first.
2. ~~**Shared-category distribution.**~~ **Decided by the owner and implemented:
   shared means shared.** Every target user receives every shared category, and
   the viewer pass only invents. The `"selected"` field is gone from the personal
   prompt, from `PersonalParseResult`, and from both structured-output schemas.
   Do not reintroduce opt-in without asking. What it looked like when it was
   opt-in: the model declined 16 of 25 offers, three of eight shared categories
   were built for exactly one user, and that user was the one who had watched
   nothing and received all of them through the thin-history fallback. The
   underlying cause was the series-user-data bug above — it was choosing from
   histories with all television missing.
3. **Grok and the batch API.** No live xAI call has been made from the agent
   side. `AnthropicProvider.CompleteBatchAsync` has still never parsed a real
   completed job and is off by default — the least-tested code in the repo.
4. **Whole-series playlist members** — series go in as `Series` LinkedChildren
   without episode expansion. Home rows render correctly; in-library playback of
   such a playlist may behave oddly.

## Reference

SmartLists is the architectural reference for list storage, GUID ownership,
ordering, and refresh patterns — it solves the same *infrastructure* problems for
a different purpose. It is **AGPL-3.0**: read it for patterns and API usage, never
copy code. Nothing in Curator is copied from it.
