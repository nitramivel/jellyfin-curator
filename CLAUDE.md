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
│   ├── CollectionSurfacing.cs    # Which collections ride along on an item
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
silently misbehaves. Rule 3 is the newest and was the most expensive to learn:
every one of these was a real failure on a real server before it was a rule.

1. **The model never sees Jellyfin GUIDs.** `PromptBuilder` assigns batch-local
   integer indexes; `ProposalParser` discards any index outside `0..n-1` and maps
   survivors back to GUIDs. This is what makes it structurally impossible for the
   model to reference an item the user does not own. Do not "simplify" by sending
   real IDs.
2. **Never resolve our own playlists by name.** Always by stored GUID, with
   recovery via the `CuratorCategory` provider-ID tether. Duplicate playlist names
   are legal in Jellyfin; SmartLists removed exactly this fallback for good reason.
3. **A category's audience is `CategoryAudience.For(OwnerUserId, targetUsers)` —
   never the raw target list.** Shared goes to everyone targeted; personal goes to
   its one owner, and to nobody at all if that owner is no longer targeted. The
   run got this right where it built categories and wrong in the reconcile pass,
   which walked every stored definition and handed all of them the full list — so
   once that pass became a nightly task, every viewer ended up on every row.
   Measured: 102 definitions, 80 personal, all six users on each. `SyncCategoryAsync`
   is now **authoritative** about its audience: a link held by anyone outside it is
   run through the same ownership table with `hasMembers: false`, so it is deleted
   if Curator still owns it, handed off if the tag has gone, and left alone forever
   if it was handed off before. That is what repairs a store this has already
   spread, without a migration.
4. **Shared rows go to everyone; only their order is personalized.** Making them
   opt-in was tried and collapsed — a category no viewer picked went unbuilt for
   the whole household, and on a real library the model declined 16 of 25 offers
   because it was choosing from watch histories missing all television. So the
   viewer's pass earns its keep by inventing, not by vetoing. `MemberOrdering`
   reorders each viewer's own copy of a shared playlist instead: no row can ever
   be taken from anyone, and because Jellyfin playlists are per-user it needs no
   client support, unlike anything routed through the home screen plugins. It
   **nudges** — the model ranked members by belonging and that stays the primary
   signal; a favourite thirty places down rises but must not lead the row.
   Per-viewer row *order* is not available at all: Home Screen Sections keeps
   `OrderIndex` in its global `SectionSettings`, and the only per-user structure is
   `EnabledSections`, which is a set. Hiding a row per viewer is possible there and
   is deliberately not done — it is the same veto in a smaller blast radius.
5. **The `curator` tag is the ownership contract.** A playlist without it belongs
   to the user permanently — never modify, delete, or replace it, and never create
   a replacement for that user. Handoff takes precedence over deletion, even when
   the category empties.
6. **Empty category ≠ deleted category.** Remove the Jellyfin playlist, null the
   stored playlist ID, keep the definition so a later run reuses the same identity.
   Identity is name **or** member similarity, not name alone — the model renames
   every thread every run (measured: 0 of 16 then 0 of 33 names survived), and a
   rename must not destroy a row. `CategoryIdentity` uses Jaccard, deliberately
   not the Reconciler's overlap coefficient: that one divides by the smaller set,
   so a six-item category would swallow the identity of the twenty-item category
   containing it.
   **Both passes are now told what already exists.** The viewer's pass always was;
   the discovery pass — the one that coins the library-wide rows — was told
   nothing, which is why 0 of 16 then 0 of 33 names survived a run and identity had
   to be rescued on member overlap every time. `BuildDiscoverySuffix` lists the
   stored shared rows and asks the model to reuse a name when it means the same
   thread. Phrase that as permission, never instruction: a pass told too firmly to
   reuse names returns only the names it was given and stops finding anything,
   which is a frozen home screen instead of a churning one. It also has to say that
   reusing a name is not a promise about members, or the model either returns stale
   rows or avoids reuse entirely.
   The single exception is `CategoryRetention` enforcing a configured cap, where
   the user has asked for a bounded list and something must actually go; a pruned
   category loses its identity and returns as a new one. **That cap is
   `MaxStored{Shared,Personal}Categories`, not `Max{Shared,Personal}Categories`** —
   how many the store keeps across runs, against how many one run may propose. They
   were a single number, which capped the collection at one pass's worth and made
   every full run delete something: measured, 35 pruned / 21 renamed / 49 held on
   grace in one run. Read `EffectiveStored*Categories`, where **0 means inherit the
   per-run cap** (and inheriting an uncapped per-run number is itself uncapped).
   Setting the store cap *below* the per-run cap is legal and pointless — the run
   proposes more than the store may hold and deletes the excess immediately. Retention spends
   **empty categories first** — one holding no playlist is showing nobody
   anything, so it goes before a live row however stale its date looks — then
   oldest-first within each group. A handed-off playlist counts as held.
   `POST /Curator/Playlists/Sync` applies the same judgement on demand: it
   rebuilds a playlist a category has lost, then deletes definitions still
   holding none, then deletes Curator-owned playlists no definition claims.
   Untagged playlists are never touched by any of it — see rule 5.
7. **No live LLM calls in tests.** Providers are tested through a stub
   `HttpMessageHandler`; the run pipeline through a stub `ILlmProvider`.
   **Orchestration services take `ILlmProviderFactory`, not the concrete factory.**
   That interface is the only seam that makes the second half of this rule
   achievable — with the concrete type there is nothing to substitute, and the only
   testable parts are whatever pure logic can be lifted out from under the service.
   `SummaryDistillServiceTests` is what it buys: the split-and-retry loop driven end
   to end against canned responses, asserting a failing 8-item request becomes
   `[8, 4, 4]` and loses nothing.
8. **Log token count and estimated cost at INFO every run.** Runs cost money; the
   user must be able to see what a run spent. **Cache reads are charged, not
   free.** Providers that report cached tokens inside their input count have it
   subtracted before costing, so pricing only `InputTokens` silently drops them
   from the total — measured, that understated one run by 24%, and a fully cached
   run would report about a third of its bill. `CachedInputCostPerMillion` falls
   back to half the input price when blank: conservative, and the right direction
   to err for a number whose only job is telling the owner what a run spent.
   Cache *writes* carry their own premium and are still unpriced, which the
   `RunLogCost` doc comment says out loud.
9. **`BatchSize = 0` means the whole library in one request, and is the default.**
   A thread running through items split across two batches is one the model
   never gets to see: each call only sees its own slice, so the categories it
   proposes can only join up what is in front of it. Raise it off 0 only for a
   model whose context cannot hold the library.
10. **Every category limit is told to the model, not only applied to its answer.**
   `CategoryLimits` is the single value both `PromptBuilder` and `Reconciler`
   take — build one per pool and pass the *same instance* to both. Do not unpack
   it into loose ints on the way, and do not add a limit that only one side sees.
   This has broken twice in opposite directions: prompt-3 / filter-6 binned 17 of
   22 proposals on size alone, and a filter capping at 8 categories with no
   target in the prompt got 5 categories covering 10% of the library where the
   other model gave 23 covering 78%. `CategoryLimitsTests` reads the numbers back
   out of the generated prompt and checks them against what the Reconciler
   actually does — a new limit belongs in that theory.
   Each pool now carries **both** ends of its size range:
   `MaxSharedCategorySize` / `MaxPersonalCategorySize` against the two existing
   floors. On those two, **0 means inherit `MaxCategoryMembers`, not no limit** —
   the no-limit answer is 0 on `MaxCategoryMembers` itself, which an inheriting
   pool then inherits. Read `Effective{Shared,Personal}CategorySize` and never the
   raw setting: they are get-only so an inherited ceiling tracks the fallback
   instead of freezing a copy of it. A pool ceiling below its own floor is
   discarded by `EffectiveMaxMembers` rather than honoured — the floor is
   load-bearing and wins.
   **The shipped range is 6-25**, and the ceilings default to 25 rather than to
   0-inherit so the two boxes read as one range. That makes upgrades asymmetric,
   which is deliberate but surprising: the floors were stored on every existing
   install and so survive, the ceilings never were and so jump to 25 at once.
   **The floor is the number that actually moves row length** — measured on a
   263-item library, all 60 categories came back at 5-10 members against a
   ceiling of 20, so the model sits near the floor it is given and the ceiling
   goes unused. Reach for the floor first when rows are too short.
11. **Every paid pass writes a run log.** The category run always did; the
   distillation pass did not, and diagnosing it meant grepping tens of megabytes of
   server log — which is how a pass losing 185 items of 212 went unnoticed. It now
   calls `Begin` with trigger `summaries` and **`trackAsCurrent: false`**: the
   status endpoint pairs `Current()` with the *category* run's `IsRunning`, so a
   second kind of run claiming that snapshot shows the progress panel something
   that is not its own.
   **A run log must never break the run it describes.** Every write in
   `Services/Runs/` swallows its own IO failures with a warning. The same applies
   to the prompt pool and the atomic temp-file rename — diagnostics are strictly
   subordinate to the run.
12. **A model profile is the unit of "how to call a model", and its legacy fields
   are not dead code.** `ModelProfile` carries provider, model, API key, base URL,
   **that profile's prices, and whether it thinks**; `Core/Llm/ModelProfiles` normalizes the list on
   every read. Pricing lives on the profile because a list you switch between
   turns "remember to change the prices when you change provider" from an
   occasional mistake into the normal case — rule 8 says the cost line must be
   right, and a shared price block cannot be. The pre-profile scalars on
   `PluginConfiguration` (`Provider`, `Model`, `ApiKey`, `BaseUrl`, the three
   `*CostPerMillion`) look unused and **must not be deleted**: XmlSerializer
   silently drops elements it has no property for, so removing them throws away
   the API key of every install that upgrades before it next opens the config
   page. `Normalize` folds them into one profile the first time it sees an empty
   list, and the config page blanks them on the next save so migration happens
   exactly once. Migration is deliberately *only* for an empty list — re-importing
   them afterwards would resurrect a deleted profile on every run.
13. **Condensed summaries are a cache, never a write-back.** The distillation pass
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
14. **Recommendation selection is arithmetic; only the order may cost money.**
   `RecommendationRanker` decides *which* items appear and calls no model — the
   information is already bought, since every category carries the model's own
   ordering of its members. `ModelRankedRecommendations` (off by default) adds one
   call per eligible viewer per refresh to reorder the top
   `MaxRecommendationsToRank` of the shortlist, on `RecommendationModelProfileId`.
   Two things that must stay true: the re-rank never changes membership, and
   `RecommendationParser` treats the answer as a **preference over** the shortlist
   rather than a replacement — anything the model omits, repeats or invents leaves
   the weighted order in place. Dropping an index here would silently delete an
   item from somebody's home screen row, which is why this parser appends what it
   cannot read instead of discarding it like every other parser here. A failed
   call costs the call and nothing else.
   **The recommendation playlist has no stored definition, so its tether is its
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
15. **The Schedule tab edits Jellyfin's triggers, not Curator's config.**
   `IScheduledTaskWorker.Triggers` is settable and persists on assignment, so the
   tab and Dashboard → Scheduled Tasks are two editors over one store. Saving
   **replaces** a task's triggers: the page offers one cadence and Jellyfin allows
   several of mixed kinds, so keeping a hidden extra would mean the page showed
   something other than what runs. `ScheduleTranslator` is the whole conversion and
   is round-trip tested — what the page saves must be what it reads back, or the
   settings drift every time they are opened.
16. **Two settings govern tags and they are not interchangeable.**
   `MaxTagsPerItem` takes the first N of the **raw** scraped list and defaults to 0;
   `ConsolidateTags` has the distillation pass keep however many genuinely describe
   the item, with `MaxConsolidatedTags` as a **ceiling and never a target** — a
   fixed count is exactly what the raw setting already did badly. Consolidation
   happens in the same model call as the summary **and after it**: the whole scraped
   list goes out uncapped, the model writes the rewrite first, then keeps the tags
   that agree with the reading it just committed to. That ordering is the feature,
   not an implementation detail — decided separately the two halves disagree, and a
   summary calling something quietly devastating ends up beside a tag list saying
   "action". It only holds because `s` is declared before `t` in both schemas
   (Google needs the explicit `propertyOrdering`), which `LlmProviderTests` pins.
   `SummaryPlan` queues an item whose summary is current but whose tags are
   missing, so switching it on is incremental rather than a full redo.
   `AllowInventedTags` (off) lets the model coin a word when nothing scraped names
   what the rewrite said. Off by default for consistency, not quality: a tag is
   worth something only if the same tag means the same thing across items, and free
   coinage yields *melancholy / melancholic / wistful / quietly sad* as four
   textures instead of one. The prompt therefore constrains coinage to a last
   resort and to the plainest wording that fits.
   **A batch the model answers badly is split and retried, never written off.**
   `Core/Summaries/SummaryRetryPlan` holds the decision, bounded at
   `MaxAttempts = 3` because every retry is a paid call. Two distinct failures, and
   they want opposite handling: an answer that will not parse (almost always the
   output cap cutting the JSON mid-object) is halved, while an answer that parses
   but covers only part of the batch is retried for the remainder — *unless* it
   covered less than half, in which case the remainder is halved too, since asking
   again for 19 of 20 is the request that just failed. Measured before this
   existed: a 212-item pass stored 27 and wrote off 185, after paying for all of
   them. Note **thinking counts against `MaxOutputTokens`**, which is what put
   those batches over the cap in the first place — the retry recovers the items but
   the cap is the actual lever. An item with no scraped tags is never
   queued: the answer can only ever be empty and queueing it would re-buy a summary
   every pass. When `SendConsolidatedTags` is on the run service raises the
   effective tag cap, because `MaxTagsPerItem` is normally 0 and would otherwise
   substitute the consolidated tags onto every record and then write none of them.
17. **Four scheduled tasks, one job each.** Generate Categories (weekly, the only
   one that costs money), Condense Summaries (daily), Refresh Recommendations
   (6-hourly), Clean Up and Sync (daily), Health Check (daily). The recommendation
   refresh deliberately does **not** live in the maintenance task any more: it
   tracks watch activity and wants a far shorter cadence than reconciling
   playlists does, and having two tasks rebuild the same playlists was duplicate
   work. Everything except Generate Categories is free and calls no model, so
   cadence there is a taste decision rather than a spending one. All four skip or
   no-op while a run is in progress — a run rewrites the same playlists and
   definitions, and racing it loses work.
18. **The health check exists because this plugin fails silently.** Both
   integrations degrade quietly by design, a run dies mid-flight whenever
   installing any plugin tears the host down, and library rows outlive their
   folder. From the outside all of these look identical to "Curator stopped
   working". `Core/Health/HealthCheck` is pure — facts in, findings out — so the
   judgements are testable without a server, and it must stay shy: a panel that
   reports normal operation as a problem gets ignored, which is worse than no
   panel. That is why a late run is not a stalled one and a manual-only schedule
   is never reported at all. Two findings exist because this plugin failed exactly
   these ways and said nothing: `summaries.notags` (consolidation on, a decent
   sample stored, zero tags anywhere — the schema bug, which ran for weeks) and
   `summaries.failing` (the last pass lost more than half its items). Both need a
   real sample before firing, because a handful of items whose scraped tags were all
   trivia genuinely produce nothing.
19. **A run may call two models, so nothing may assume there is one.**
   `DiscoveryModelProfileId` and `PersonalModelProfileId` name the profile each
   pass uses; blank means the default, so an install that has chosen nothing
   behaves exactly as it always did. The split exists because the two passes are
   different jobs at different volumes — discovery is one call over the whole
   library, the viewer passes are one call *each, every run* (five of six calls on
   a measured run), doing a narrower job. That is the setting that moves the bill.
   Three things this breaks if done casually:
   - **Resolve both passes from one `ModelProfiles.Normalize` result**, via the
     `Resolve(NormalizedProfiles, id)` overload. Normalizing per resolve is not
     idempotent on a pre-profile-list install: the migrated profile is synthesized
     afresh each call **with a new id**, so two resolves of one profile compare as
     two by reference *and* by id, and the run builds a second identical provider
     and reports itself as mixed. Pinned by `ModelProfilesTests`.
   - **The run total is summed from the per-call costs**, not recomputed from the
     aggregate token totals as it was while one price covered everything — no
     single rate can price a mixed run. `IRunLog.LlmCall` takes an optional
     `RunLogPricing` for the pass's own rates, falling back to the run's; its
     cached rate falls back to half of **its own** input rate, never the other
     model's. Decimal addition is exact at these magnitudes, so the parts still
     agree with the whole, which is what the old approach was protecting.
   - **Attribute output to the model that produced it.** Personal categories are
     stored against the viewer pass's `ModelId`, and the run-log settings snapshot
     names both passes — one model reported for a two-model run is how a bug
     report gets read wrongly.
20. **Ask before adding dependencies** beyond the Jellyfin packages and an
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
- **Collection membership is a link, not a parent.** A `BoxSet` holds its items
  in `LinkedChildren`, so querying children by parent ID returns nothing — it has
  to be read off each BoxSet and inverted, which is what `ResolveCollections`
  does. Every collection an item belongs to is sent by default
  (`SurfaceAllCollections`); `SurfacedCollections` names a subset and applies only
  when that is off. The mode switch is pure in `Core/CollectionSurfacing`, with
  one rule worth keeping: an empty name list means **send none**, never "send
  everything" — clearing the box must not silently invert. Sending everything
  means franchises reach the model, and a franchise is a ready-made metadata
  category of exactly the kind the system prompt forbids, so both prompts name
  that risk explicitly rather than relying on the input being pre-filtered.
  `PromptBuilderTests` pins that wording.
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
  `Curator/*`. **A JSON body goes in `data`, never `content`.** `ApiClient.ajax`
  reads the body off `data` and silently ignores any other key, so a POST written
  with `content` is sent with no body at all: `[FromBody]` fails model binding,
  ASP.NET returns 400 before the action runs, and **nothing reaches the server
  log** — the Schedule tab shipped this way and its "check the server log" error
  pointed at a log with no entry to find. If an endpoint appears never to be
  called, check the body key before anything else.
- `is="emby-checkbox"` is correct for **static** markup only. In rows built by
  `innerHTML`, customized built-in elements upgrade unreliably — one row rendered
  styled-but-unwired and the rest bare. Dynamic rows use plain
  `<input type="checkbox" class="curatorCheck">`.
- **Anything listing profiles must be rebuilt whenever the list changes.** There
  are now three such pickers — the Summaries tab's, and the Model tab's two
  per-pass ones — and `renderProfiles()` refreshes all of them via
  `syncSummaryProfileSelect()` / `syncPassProfileSelects()`. Miss one and a rename
  two rows up leaves a picker showing a name the profile no longer has, silently
  saving the wrong id. Blank is a **real value** on these: it means "follow the
  default profile", not "unset".
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
- **Prompt caching needs a routing hint, and each vendor spells it differently.**
  Cache entries live on the server that wrote them, so without one the calls of a
  run scatter and each lands somewhere that has never seen the prefix. xAI takes
  the `x-grok-conv-id` **header**; OpenAI takes a `prompt_cache_key` **body
  field**; both are given the run id, so a run's calls pin together. Measured on
  the same 263-item library: Grok served 82k of 147k input from cache, while
  OpenAI reported **zero** cached tokens on a byte-identical 139k prompt across
  two runs eight minutes apart. Send neither to `CreateCompatible` — Ollama, LM
  Studio, vLLM and OpenRouter are entitled to reject a body field they do not
  know, and this one class drives all of them.
- **Never send an empty content block.** Anthropic rejects the whole request —
  `messages: text content blocks must be non-empty` — and Google gains nothing
  from an empty part. Both builders guarded an empty *prefix* and not an empty
  *suffix*, which two passes hand over by design: the distillation pass and the
  recommendation re-rank put their whole prompt in the prefix. Measured: 195
  items, every batch 400ing, 0 distilled, on the first run that used Anthropic for
  that pass. A request with neither half now throws rather than reaching a
  provider. **The cache marker goes on the prefix only when there is also a
  suffix** — the split exists to mark what repeats across calls, so a caller with
  no suffix is saying nothing repeats, and marking it anyway pays the 2x write
  premium on every batch of a pass whose prompt is different every time.
- **The schema and the prompt must ask for the same fields.** Strict mode is a
  grammar, not a hint: a field the prompt requests and the schema omits has *no
  legal position in the output*, and the model does not error — it writes the
  field into the previous string. Measured: the summary schema declared only
  `i`/`s` with `additionalProperties:false` while the prompt asked for `t`
  whenever tag consolidation was on, and 17 of 232 stored summaries ended
  `…viciously sharp','t':[` while **every** item came back with an empty tag list.
  That is why tags are a separate shape (`SummariesWithTags`) rather than an
  optional property — strict mode requires every declared property, so one schema
  cannot serve both prompts. `LlmProviderTests` pins both directions for both
  providers. Symptoms of this class of bug look like model stupidity or a parser
  fault; check the schema against the prompt first.
- **Settings live on five tabs**: Model (profiles, request, spend), Library
  (what is sent and who for), Categories (the two pools' size and count), Home
  screen (rows and the recommendation playlist), Summaries (the condensing pass
  and its settings). **Two tabs are NOT part of the settings form** and hide the
  save row: Runs, and Schedule — the latter writes through Jellyfin's
  `ITaskManager`, not plugin config, so the form's Save would do nothing for it. Put a new setting where its *subject* is, not where it is
  technically enforced.
- **The Categories tab shows resolved numbers, never the stored sentinel.** `0`
  means *inherit* on `MaxStored*Categories` and on the per-pool size ceilings, but
  *no limit* on the per-run counts — two meanings of the same digit in adjacent
  boxes, which is exactly how that tab became unreadable. The page resolves on
  load and writes explicit values back, so the sentinels now survive only for
  configs written before this and for callers reading `Effective*` in C#. It
  follows that **`MaxCategoryMembers` has no field any more**: it is read as a
  fallback when loading and never written, so the stored value is left alone.
  Do not re-add it as an input — it only ever applied when another box was 0.
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
