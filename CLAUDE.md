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
│   ├── Llm/                  # Batcher, PromptBuilder, ProposalParser
│   ├── Reconciliation/       # Reconciler, StringSimilarity
│   ├── Playlists/            # PlaylistSyncDecision (the ownership decision table)
│   ├── HomeScreen/           # SectionConfigMerger (JSON merge for both integrations)
│   └── Models/               # MediaItemRecord, CategoryProposal, ReconciledCategory,
│                             #   CategoryDefinition, UserActivity
├── Services/                 # Everything that touches Jellyfin or the network
│   ├── CuratorRunService.cs  # The end-to-end run; both entry points call this
│   ├── GenerateCategoriesTask.cs   # IScheduledTask, weekly default
│   ├── Library/              # LibraryScanner, UserActivityProvider
│   ├── Llm/                  # ILlmProvider + Anthropic/Google/Grok/OpenAI/compatible,
│   │                         #   TransientHttpRetry (shared 429/5xx backoff), factory,
│   │                         #   CategoryProposalService (batch loop, token budget)
│   ├── Categories/           # ICategoryStore — one JSON file per category
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
silently misbehaves.

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
   category loses its identity and returns as a new one.
5. **No live LLM calls in tests.** Providers are tested through a stub
   `HttpMessageHandler`; the run pipeline through a stub `ILlmProvider`.
6. **Log token count and estimated cost at INFO every run.** Runs cost money; the
   user must be able to see what a run spent.
7. **Every category limit is told to the model, not only applied to its answer.**
   `CategoryLimits` is the single value both `PromptBuilder` and `Reconciler`
   take — build one per pool and pass the *same instance* to both. Do not unpack
   it into loose ints on the way, and do not add a limit that only one side sees.
   This has broken twice in opposite directions: prompt-3 / filter-6 binned 17 of
   22 proposals on size alone, and a filter capping at 8 categories with no
   target in the prompt got 5 categories covering 10% of the library where the
   other model gave 23 covering 78%. `CategoryLimitsTests` reads the numbers back
   out of the generated prompt and checks them against what the Reconciler
   actually does — a new limit belongs in that theory.
8. **A run log must never break the run it describes.** Every write in
   `Services/Runs/` swallows its own IO failures with a warning. The same applies
   to the prompt pool and the atomic temp-file rename — diagnostics are strictly
   subordinate to the run.
9. **Ask before adding dependencies** beyond the Jellyfin packages and an
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

1. **Category name churn.** Measured across three runs, ZERO category names
   survived to the next run (0 of 16, then 0 of 33). Identity now falls back to
   member similarity so a rename keeps its row, but that fix has not yet been
   observed across two real runs. Check `category.renamed` steps in the run log.
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
