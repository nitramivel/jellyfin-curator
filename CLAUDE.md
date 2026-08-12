# Curator — Jellyfin plugin

Sends the media library to an LLM, asks what threads run through it, turns the
answers into ordered Jellyfin playlists, and publishes those as home screen rows
through the Home Screen Sections plugin.

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
│   ├── Context/              # ViewingContext (the closed weather/daypart
│   │                         #   vocabulary, WeatherReading), WeatherCodes (WMO
│   │                         #   code + temperature -> words), ContextRanker,
│   │                         #   ContextTitles (condition keys, rotation, culling),
│   │                         #   ContextTitlePromptBuilder
│   ├── Health/               # HealthCheck (facts in, findings out — pure)
│   ├── Usage/                # UsageRollup + models — every billable call reduced
│   │                         #   to cost by model, by pass and by day
│   ├── HomeScreen/           # SectionConfigMerger (JSON merge for both integrations),
│   │                         #   SectionRegistration (the payload that registers a
│   │                         #   row directly with Home Screen Sections)
│   └── Models/               # MediaItemRecord, CategoryProposal, ReconciledCategory,
│                             #   CategoryDefinition, UserActivity
├── Services/                 # Everything that touches Jellyfin or the network
│   ├── CuratorRunService.cs  # The end-to-end run; both entry points call this
│   ├── GenerateCategoriesTask.cs   # IScheduledTask, weekly default
│   ├── DistillSummariesTask.cs     # IScheduledTask, daily default
│   ├── MaintenanceTask.cs          # IScheduledTask, daily; reconcile + prune
│   ├── RefreshRecommendationsTask.cs # IScheduledTask, 6-hourly; per-viewer rows
│   ├── HealthCheckTask.cs          # IScheduledTask, daily; read-only diagnosis
│   ├── Context/              # IWeatherService + OpenMeteoWeatherService (no API
│   │                         #   key; caches coordinates for the process and
│   │                         #   conditions for 30 min, refreshed off the render path),
│   │                         #   ContextRowService (reads weather, titles the rows,
│   │                         #   publishes them), IContextRowStore (titles + snapshots)
│   ├── Library/              # LibraryScanner, UserActivityProvider
│   ├── Llm/                  # ILlmProvider + Anthropic/Google/Grok/OpenAI/compatible,
│   │                         #   TransientHttpRetry (shared 429/5xx backoff), factory,
│   │                         #   CategoryProposalService (batch loop, token budget)
│   ├── Categories/           # ICategoryStore — one JSON file per category
│   ├── Summaries/            # ISummaryStore (one file for the whole set) +
│   │                         #   SummaryDistillService (the condensing pass)
│   ├── Runs/                 # IRunLogStore — one JSON file per run: every step,
│   │                         #   every prompt and response, written incrementally
│   ├── Playlists/            # CuratorPlaylistService — create/update/delete, tagging;
│   │                         #   PlaylistLookup (the one by-GUID/by-tether resolver)
│   ├── PublishHomeScreenRowsTask.cs # IScheduledTask, startup; re-registers the rows
│   ├── RefreshContextRowsTask.cs    # IScheduledTask, hourly + startup; re-titles
│   │                                #   and republishes the two context rows
│   └── HomeScreen/           # HomeScreenIntegrationService (picks the path),
│                             #   HomeScreenSectionRegistrar (reflects into the other
│                             #   plugin), CuratorSectionResults (answers for a row),
│                             #   CuratorContextSectionResults (answers for the two
│                             #   weather/time rows, computed at render time),
│                             #   API key provider
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
2. **Two library rows for one title are collapsed before the model sees them,
   and every test used is an exact equality.** `DuplicateItems` asks three
   questions in order: Jellyfin's own alternate-version link (`Video.PrimaryVersionId`,
   read into `MediaItemRecord.PrimaryVersionId`), then the item's metadata-provider
   ID (`ExternalId`, `tmdb:78`), then kind + trimmed lowercase title + year.
   Do not add fuzzy matching or strip "director's cut" from titles — the failure
   mode is worse than the problem: "Freaky Friday" exists as 2003 and 1995, and a
   title-only rule silently removes a film.
   **Title and year alone were never enough, and the reason is that they answer
   the wrong question.** Two cuts of a film are usually the case where the titles
   *disagree* — "Blade Runner" (1982) beside "Blade Runner: The Final Cut" (2007)
   is one film under two titles and two years. The provider ID catches exactly
   that while keeping Freaky Friday apart, because those are two TMDb entries.
   An alternate version keys on **whatever its primary keys on**, so a merged pair
   joins up even when title, year and provider ID all disagree; `VersionRootOf`
   walks that link, and a cycle settles on the lowest ID **in the cycle** rather
   than in everything walked to reach it — otherwise two members of one cycle
   answer differently and the group splits. The longest runtime wins, except that
   a row Jellyfin considers an alternate always loses to one it does not: every
   other client draws the primary and hides the alternate behind a version picker,
   so keeping the alternate puts a card on the home screen that appears nowhere
   else on the server. **Fold the activity** through the alias map — history is
   recorded against whichever row was played, so collapsing without folding makes
   a film the viewer has seen read as unseen.
   **The collapse is also applied on the way to the screen**, in both results
   classes, via `DuplicateItems.SurvivingIds`. The run-time collapse only fixes
   categories built after it; a category stored last week holds both IDs and its
   playlist keeps showing two posters until the next weekly run. The backstop is
   the same pure function over the same keys deliberately — a second opinion about
   what a duplicate is would be a second answer to disagree with.
   Note `MediaItemRecord` now carries two fields the model must never see.
   `PromptBuilder` writes its fields one at a time rather than reflecting over the
   record, which is the only thing keeping rule 1 true of them.
3. **Never resolve our own playlists by name.** Always by stored GUID, with
   recovery via the `CuratorCategory` provider-ID tether. Duplicate playlist names
   are legal in Jellyfin; SmartLists removed exactly this fallback for good reason.
   `Services/Playlists/PlaylistLookup` is the single implementation, shared by the
   service that writes playlists and the home screen row that reads them, so the
   rule cannot hold in one and drift in the other.
   **This is also why Curator owns its home screen rows.** Collection Sections
   resolves a row's playlist by name string, and Curator gives all six of a shared
   category's per-user playlists the *same* name by design — there is no field to
   pass a GUID through, so the fragility this rule forbids internally was being
   imposed from outside. A registered section carries the category GUID in its
   `additionalData`, and `CuratorSectionResults` resolves from there.
4. **A category's audience is `CategoryAudience.For(OwnerUserId, targetUsers)` —
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
5. **Shared rows go to everyone; only their order is personalized.** Making them
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
6. **The `curator` tag is the ownership contract.** A playlist without it belongs
   to the user permanently — never modify, delete, or replace it, and never create
   a replacement for that user. Handoff takes precedence over deletion, even when
   the category empties.
7. **Empty category ≠ deleted category.** Remove the Jellyfin playlist, null the
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
   Untagged playlists are never touched by any of it — see rule 6.
8. **No live LLM calls in tests.** Providers are tested through a stub
   `HttpMessageHandler`; the run pipeline through a stub `ILlmProvider`.
   **Orchestration services take `ILlmProviderFactory`, not the concrete factory.**
   That interface is the only seam that makes the second half of this rule
   achievable — with the concrete type there is nothing to substitute, and the only
   testable parts are whatever pure logic can be lifted out from under the service.
   `SummaryDistillServiceTests` is what it buys: the split-and-retry loop driven end
   to end against canned responses, asserting a failing 8-item request becomes
   `[8, 4, 4]` and loses nothing.
9. **Log token count and estimated cost at INFO every run.** Runs cost money; the
   user must be able to see what a run spent. **Cache reads are charged, not
   free.** Providers that report cached tokens inside their input count have it
   subtracted before costing, so pricing only `InputTokens` silently drops them
   from the total — measured, that understated one run by 24%, and a fully cached
   run would report about a third of its bill. `CachedInputCostPerMillion` falls
   back to half the input price when blank: conservative, and the right direction
   to err for a number whose only job is telling the owner what a run spent.
   Cache *writes* carry their own premium and are still unpriced, which the
   `RunLogCost` doc comment says out loud.
   **Every call records the model it went to.** `RunLogCall.Model`/`.Provider`
   (schema 2) exist because the document's headline model is discovery's, so a
   mixed run could not say what each model cost — the Usage tab's whole subject.
   Callers pass a `RunLogModel` (provider + model + that profile's rates); the
   store resolves a null one to the run's own and writes it down resolved, so no
   reader reinvents the fallback. Schema 1 files carry no per-call model and are
   attributed to the run's, which is exactly right for the single-model runs of
   the time.
   **The Usage tab is the readable form of this rule**, over `Core/Usage`
   (pure: calls in, breakdown out) and `IRunLogStore.Usage`. Two invariants there
   and both exist because the alternative misleads: an **unpriced call is counted
   and reported, never costed at zero** — a run made before the rates were typed
   in still cost money — and a **wasted call is still charged**, since an answer
   that would not parse was billed exactly like one that did. An unrecognised
   phase is shown under its own raw name rather than swept into "other": a pass
   that starts spending unnoticed is the thing this is for.
10. **`BatchSize = 0` means the whole library in one request, and is the default.**
   A thread running through items split across two batches is one the model
   never gets to see: each call only sees its own slice, so the categories it
   proposes can only join up what is in front of it. Raise it off 0 only for a
   model whose context cannot hold the library.
11. **Every category limit is told to the model, not only applied to its answer.**
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
12. **Every paid pass writes a run log.** The category run always did; the
   distillation pass did not, and diagnosing it meant grepping tens of megabytes of
   server log — which is how a pass losing 185 items of 212 went unnoticed. It now
   calls `Begin` with trigger `summaries` and **`trackAsCurrent: false`**: the
   status endpoint pairs `Current()` with the *category* run's `IsRunning`, so a
   second kind of run claiming that snapshot shows the progress panel something
   that is not its own.
   **The recommendation re-rank was the last pass spending money in silence.**
   It called the provider and only wrote a `Step`, and the six-hourly task that
   drives it passed **no run log at all** — so `ModelRankedRecommendations` on a
   six-user library bought up to twenty calls a day that nothing recorded. It now
   records an `LlmCall` under phase `rerank` with its own profile's model and
   rates, including the attempt that threw, and `RefreshRecommendationsAsync`
   opens a log with trigger `recommendations`.
   **A pass that opens a run log must also call `SetProvider`.** Per-call models are
   what the Usage tab reads, so it was right all along — but the Runs tab reads the
   *run's* headline model and renders a blank one as "unknown model", which is what
   every recommendation run looked like beside the category and summary runs that
   both set it. One profile drives every call of that pass, so its headline is exact
   rather than a stand-in for a mixed run. Blank is not a neutral default here; it
   is a run that cannot say what it spent money on.
   **Open that log only when the pass will actually buy something.** Selection is
   arithmetic (rule 15), so the usual shape of that task is free — and a free pass
   logging itself four times a day would evict the category runs from a directory
   that keeps the last fifty. Gate on `ModelRankedRecommendations`.
   **A run log must never break the run it describes.** Every write in
   `Services/Runs/` swallows its own IO failures with a warning. The same applies
   to the prompt pool and the atomic temp-file rename — diagnostics are strictly
   subordinate to the run.
13. **A model profile is the unit of "how to call a model", and its legacy fields
   are not dead code.** `ModelProfile` carries provider, model, API key, base URL,
   **that profile's prices, and whether it thinks**; `Core/Llm/ModelProfiles` normalizes the list on
   every read. Pricing lives on the profile because a list you switch between
   turns "remember to change the prices when you change provider" from an
   occasional mistake into the normal case — rule 9 says the cost line must be
   right, and a shared price block cannot be. The pre-profile scalars on
   `PluginConfiguration` (`Provider`, `Model`, `ApiKey`, `BaseUrl`, the three
   `*CostPerMillion`) look unused and **must not be deleted**: XmlSerializer
   silently drops elements it has no property for, so removing them throws away
   the API key of every install that upgrades before it next opens the config
   page. `Normalize` folds them into one profile the first time it sees an empty
   list, and the config page blanks them on the next save so migration happens
   exactly once. Migration is deliberately *only* for an empty list — re-importing
   them afterwards would resurrect a deleted profile on every run.
14. **Condensed summaries are a cache, never a write-back.** The distillation pass
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
15. **Recommendation selection is arithmetic; only the order may cost money.**
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
16. **The Schedule tab edits Jellyfin's triggers, not Curator's config.**
   `IScheduledTaskWorker.Triggers` is settable and persists on assignment, so the
   tab and Dashboard → Scheduled Tasks are two editors over one store. Saving
   **replaces** a task's triggers: the page offers one cadence and Jellyfin allows
   several of mixed kinds, so keeping a hidden extra would mean the page showed
   something other than what runs. `ScheduleTranslator` is the whole conversion and
   is round-trip tested — what the page saves must be what it reads back, or the
   settings drift every time they are opened.
   **A startup trigger is the one thing a save must never replace.** It is not a
   cadence, so no box on the page can express it and `FromTriggers` reports a
   startup-only task as Manual — which is true of its *recurring* schedule and not
   of the task. Saving that back deleted the trigger. Measured: on 2 Aug 2026 one
   save wrote all six tasks, Publish Home Screen Rows went from `[StartupTrigger]`
   to `[]`, and after the 00:30 restart the next day every Curator row on the home
   screen was **absent** — rule 22's failure, reached through this page. Save
   through `ToTriggers(spec, existing)`, which carries startup triggers across, and
   report `HasStartupTrigger` on the DTO so the row says "Also runs at every server
   start" instead of a bare "Never". The page never sends it back; it is preserved
   server-side, because a control the editor does not offer is not the editor's to
   delete.
   **Interval triggers starve on a server that restarts.** Jellyfin arms one at
   `max(lastEnd, lastStart, now + 1min) + interval`, so an overdue task does not
   catch up — it waits a *fresh whole interval* from whenever the timer was armed,
   and every server start arms it again. Installing or updating any plugin restarts
   the host, so a server touched more often than the interval runs that task never.
   Measured on the owner's server: four Curator tasks on 12h and 48h intervals went
   three days without firing across restarts at 00:30 and 06:17, while every
   daily-triggered task on the same server fired on time. Daily and Weekly are
   absolute wall-clock times and are immune, which is why every shipped default is
   one of those. The Schedule tab says so next to the interval box; do not remove
   that note, and prefer Daily/Weekly when choosing a new task's default.
17. **Two settings govern tags and they are not interchangeable.**
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
18. **Seven scheduled tasks, one job each.** Generate Categories (weekly, the only
   one that costs money), Condense Summaries (daily), Refresh Recommendations
   (6-hourly), Clean Up and Sync (daily), Health Check (daily), Publish Home
   Screen Rows (**every server start**), Refresh Context Rows (hourly, plus
   startup). The recommendation
   refresh deliberately does **not** live in the maintenance task any more: it
   tracks watch activity and wants a far shorter cadence than reconciling
   playlists does, and having two tasks rebuild the same playlists was duplicate
   work. Everything except Generate Categories is free and calls no model, so
   cadence there is a taste decision rather than a spending one. All four skip or
   no-op while a run is in progress — a run rewrites the same playlists and
   definitions, and racing it loses work.
19. **The health check exists because this plugin fails silently.** Both
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
   **`context.unclassified` is the fourth**, and the cleanest illustration of the
   panel's subject: the setting that *publishes* the two context rows and the
   setting that *buys* their contents are deliberately separate (rule 23), so
   turning on only the first leaves two rows that never appear, with every service
   healthy and no log line naming a cause. It counts items carrying a
   `ContextSourceHash`, never items with non-empty affinity lists — most of a
   correctly classified library is expected to come back empty, and counting the
   lists would report a working install as broken.
   **`homescreen.nostartuptrigger` is the third**, and the purest example of what
   this panel is for: Publish Home Screen Rows lost its startup trigger, and after
   the next restart all 53 rows were absent while every playlist behind them stayed
   healthy and no log line anywhere named the cause. It fires **only** when Curator
   owns its rows — under the Collection Sections path that plugin re-registers them
   from its own config, so the trigger is not load-bearing and saying otherwise
   would be crying wolf. It also fails **healthy**: a task whose triggers cannot be
   read is not evidence of a missing one.
20. **A run may call two models, so nothing may assume there is one.**
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
21. **Ask before adding dependencies** beyond the Jellyfin packages and an
   HTTP/JSON stack. Current runtime dependencies: none beyond Jellyfin. Test-only:
   xUnit. **Open-Meteo is a network dependency and not a package one** — plain
   HTTP and JSON, no account, no API key, nothing to expire. That last part is why
   it was chosen over the alternatives: a credential is something the owner has to
   obtain before the feature works at all and replace when it lapses, and a weather
   row silently going blank because a key expired is precisely the quiet failure
   rule 19 exists to chase. It is also the only outbound call Curator makes that is
   not to a model provider, so it degrades to "no weather row" and never to an
   error. **This is why the home screen integration is reflection and HTTP rather
   than a project reference.** Neither plugin is on NuGet, so referencing one means
   vendoring somebody else's DLL — and worse, a plugin assembly whose reference
   cannot be resolved does not degrade, it fails to load. Curator would cease to
   exist on a server that had not installed the other plugin, instead of logging
   that rows are unavailable and carrying on building playlists.
22. **A section registered with Home Screen Sections lives in memory and is never
   written down.** Its `RegisterSection` puts the handler in a dictionary; restart
   the server and every Curator row is *absent* — not empty, not broken, gone.
   Under the old path this never came up, because Collection Sections stored the
   rows in its own config and re-registered them on its own startup task. Owning
   the row means owning that job, which is what `PublishHomeScreenRowsTask` is for,
   and it is a genuinely new way for the home screen to be wrong: if that task does
   not run, the rows do not come back. It retries, because plugins start in no
   defined order and the other plugin's entry point throws rather than queues when
   asked too early.
   Two more consequences of that dictionary, both load-bearing:
   - **Registration is keyed on the section ID, and last write wins.** Both plugins
     register Curator's rows under the same `curator-<guid>` IDs, so leaving them in
     Collection Sections' config makes the two race. The integrated path therefore
     *clears* Curator's entries out of that config — `MergeSections` with an empty
     desired list, which already only touches entries carrying our prefix.
   - **Nothing unregisters.** A category deleted since the last restart is still
     asked for its contents. That is handled by the section settings write and the
     per-user enabled list dropping it, so the row is never drawn; the handler
     returning empty for an unknown category is the backstop, not the mechanism.

23. **The context row is bought once and computed on every render, and the two
   halves are separate settings for a reason.** `ClassifyViewingContext` rides on
   the *existing* condensing call — the model writes the rewrite, then the tags,
   then judges what weather and what part of the day the thing suits — so it costs
   output tokens and no extra input. A pass of its own would re-send the whole item
   list to ask one more question. `ContextRows` then publishes **one** home screen row
   — the weather and the hour answered together — computed **when the home screen
   asks**, in `CuratorContextSectionResults`, from the cached affinities plus a
   cached weather reading.
   **It was two rows, and the reason it is one is measured rather than aesthetic.**
   A row demanding both halves is empty exactly when it is most wanted: on the
   owner's 202-item library, `cloudy + morning` described **one** film and
   `rain + morning` and `storm + morning` described none, because only 6 items
   suit a morning at all. So `ContextRanker` grades instead of filtering — both
   halves scores `WeatherWeight + DaypartWeight`, weather alone scores more than
   the hour alone (four dayparts, the busiest holding a third of a library, make
   the hour the weaker signal), and a stand-in sky scores least. Measured after:
   every sky-and-hour combination fills a 20-item row, and `cloudy + morning` went
   from 1 item to 20. The row is also drawn from the clock alone when the weather
   cannot be read — unlike the weather row it replaced, it has a second half to
   stand on, so a server with no outbound access loses precision rather than the
   row. That is the only way a
   row whose whole claim is "this suits right now" can be true; a playlist rebuilt
   on a schedule would show the weather at the last refresh.
   What it costs is the Collection Sections path — that plugin resolves a row by
   playlist name and these have no playlist — so **the context rows exist only under
   `Integrated`**, and `SyncSectionsAsync` keeps them out of the `desired` list that
   feeds Collection Sections while including them in the section-settings write.
   Four things that will break this if done casually:
   - **Every WMO code Open-Meteo documents maps to a word, and a stand-in exists
     for the rare ones.** A code falling through the mapping produces no sky word,
     a reading with no words is treated as no reading, and the weather row silently
     vanishes in exactly that weather — so `ViewingContextTests` pins all 28 codes
     individually. The subtler failure is the row being *drawable in theory and
     empty in practice*: `storm` is a word few films earn, so a strict thunderstorm
     row shows nothing on the one evening the feature should shine.
     `ContextVocabulary.RelatedTo` supplies stand-ins (storm→rain→cloudy,
     snow→cold→cloudy), and `ContextRanker` consults them **only when the exact
     matches cannot fill a row**, appending them *below* every exact match. Rain may
     stand in for thunder; it may not outrank it. Clear and cloudy reach for
     nothing — if the two commonest skies cannot fill a row, nothing can, and
     reaching further stops the row meaning anything.
   - **The vocabulary is closed, and closed in three places at once.** The prompt
     lists the words, both structured-output schemas constrain the arrays to them
     with `enum`, and `SummaryParser` drops anything else without mapping it onto a
     neighbour. "drizzly" is not a more precise "rain" — a row matches by string
     equality, so a coined word is a judgement that was paid for and silently
     discarded. Adding a word means touching `ContextVocabulary`, and the rest
     follows from it.
   - **Two switches means four response shapes**, and rule 17's schema trap applies
     unchanged: strict mode requires every declared property, so a field the prompt
     asks for and the schema omits gets written into the previous string.
     `SummaryShapes` is the single place that maps switches to a shape; both
     providers read it, and `LlmProviderTests` pins all four in both dialects.
     Context fields come **last** in `propertyOrdering` so the rewrite is committed
     to before the judgement is made from it.
   - **`ContextSourceHash` is what makes switching it on incremental**, and it is a
     second hash of the same overview the summary is keyed on. That looks redundant
     and is not: without it every stored summary reads as current and the setting
     appears to do nothing. An item judged to suit *nothing* stores empty lists
     **and** the hash — most of the library is expected to land there by design, and
     treating empty as unanswered would re-buy most of the library every pass.
   - **Nothing on the render path may fetch, block, or throw.** Home Screen Sections
     calls `GetResults` synchronously inside the one request that draws the whole
     page. `IWeatherService.Current` answers from cache and starts a background
     refresh; the ranking is set arithmetic over GUIDs already in memory, reading
     no user data and issuing no library query until the surviving handful are
     resolved. Watch history is deliberately not consulted — the recommendation row
     answers "what next", this one answers "what suits now", and the honest answer
     to that is often something they have loved before.
   **A row's display text belongs to its registration**, which is what
   `RefreshContextRowsTask` exists for. Home Screen Sections keeps no per-user and
   no per-request title, so a name that tracks the sky is a name that has to be
   *re-registered* when the sky turns over — and that cannot happen on the render
   path, which may not write into another plugin or buy anything. Hourly plus a
   startup trigger: the startup one is what makes the interval safe under rule 16,
   since a restart re-arms it immediately.
   Four things around that, all load-bearing:
   - **The title and the cards must answer the same question.** The title is fixed
     at registration and the contents are assembled at render, so reading the
     weather twice lets them drift — a row titled for rain at five filling itself
     from a clear sky at eight. `ContextRowService` writes a `ContextRowSnapshot`
     naming the exact conditions each row was registered for, and
     `CuratorContextSectionResults` takes its context from there, never from the
     clock. Live conditions are the fallback only before the task has ever run.
   - **Titles are bought per set of conditions, not per refresh.** One call gets
     several, cached under a key like `cold,rain|evening` and rotated on each draw,
     with the viewer's own offset mixed in so two people under one sky do not read
     the same words. A place produces around thirty conditions, so the cost
     flattens within weeks — against two calls per refresh forever, which is what
     titling on the clock would mean. Culling drops a set naming a lost vocabulary
     word *immediately* and an unused one only after a year, because these
     conditions are seasonal and an eager rule re-buys every winter what it culled
     every summer. The hour is part of the key because it is part of the title —
     "Rainy Night Cozy Vibes" cannot be reused at eleven in the morning — which
     multiplies the conditions by four and is affordable for exactly the reason
     above. An empty weather half is a legitimate key, not a broken one.
   - **Per-viewer rows exist only under per-viewer locations.** A title is a
     property of the section, so two viewers can only read two titles by looking at
     two sections — `ContextSectionIdFor(userId)` builds those, and each is enabled
     for its own viewer alone. Under one location the weather and the hour are
     identical for everyone and N copies of one row would be N registrations for no
     difference.
     **Nothing unregisters a section**, so the retired `context:weather` and
     `context:daypart` rows are still in the other plugin's table until the next
     restart and are still asked for their contents. `ContextRowKey` deliberately
     does not match them, so they answer empty and are not drawn; the section
     settings write removing them from every viewer's enabled list is the
     mechanism, and this is the backstop.
   - **`SectionScope` is not decoration.** Both merges in `SectionConfigMerger`
     *remove* Curator entries absent from the list they were handed. Category rows
     are published by a run; context rows several times a day. A context sync
     claiming the whole `curator-` prefix would therefore delete every category row
     from the section settings and from every viewer's enabled list, several times
     a day, silently. `Categories` and `Context` are disjoint and each pass claims
     exactly its own.
   The weather cache is in memory like the registrations are, so both startup
   paths refresh it: otherwise the first person to open Jellyfin after a restart is
   the one who does not get the feature. `GET /Curator/Context/Weather` is the
   diagnostic — the one place an Open-Meteo call happens in the foreground and
   bypasses the cache, because a stale reading answering "are the requests getting
   through" would report success for a network that has been failing for hours.

24. **Another plugin's cache may be read, never depended on.** `UseExternalThemes`
   lets the condensing pass read `data/concierge/enrichment.json` — the Concierge
   search plugin's own index — and send each item's `Themes` alongside its
   overview. The case for it is that an overview describes the **premise**, while
   both halves of this pass are about something else: the rewrite is about tone and
   the context judgement is about mood. Concierge's themes are documented as "what
   watching it feels like" and read like it (*lonely and heartbreaking*, *stylish
   and unsettling*), so they are better input for this question than the synopsis
   is — and they have already been paid for. Measured on the owner's library: 4,599
   enriched items, **95% overlap** with Curator's summarised set, 8.3 themes each,
   parsed from 9 MB in 117 ms and cached on the file's write time.
   The constraints are what keep this from becoming a dependency hard rule 21
   forbids:
   - **Additive, never a replacement.** Every item still goes out with its own
     overview. An item the other plugin has not seen produces a byte-identical
     prompt to the one it produced before, which `ConciergeThemeSourceTests` pins —
     otherwise 5% of the library would be quietly degraded by a feature meant to
     improve it.
   - **Two field names, not a shared type.** It reads with `JsonDocument` and
     depends on `ItemId` and `Enrichment.Themes` only. No Concierge type is
     referenced, so that plugin can be absent, uninstalled mid-pass, or rewrite its
     file, and the worst case is the pass costing what it cost before. Every
     failure — missing, corrupt, unrecognised shape — returns empty at `Debug`, not
     a warning: an install without the other plugin is the normal case.
   - **Notes come after the overview in the prompt**, so the model reads the source
     material first and these as commentary on it. The prompt says not to quote them
     back and that the overview wins where they disagree — the two ways borrowed
     judgement goes wrong are parroting and displacing.

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
- **It reads playlists from a cache built once at server startup and never
  refreshed** (`LibraryCache.CachedPlaylists`, filled by its `StartupService`, and
  only ever added to when the key is absent). So a row shows the playlist *as it
  was at the last restart*. This is the measured cause of the bug that prompted
  Curator to own its rows: "Quietly Devastating Portraits" rendered 7 cards in the
  wrong order for a 10-member category whose six per-user playlists each held
  exactly 10, and which Infuse showed correctly. Note what it is **not** — the
  `GroupBy` and the `Take(16)` were the obvious suspects and both are innocent
  here: the members are films, so each groups to itself, and 10 is under 16. Do
  not re-investigate those.
  Its HTTP controller does the same work against live data with a cap of 32, but
  registration uses the in-process reflection path, so the cached one is what runs.
- We write sections by `GET` then `POST /Plugins/{guid}/Configuration`. Saving
  fires its `ConfigurationChanged`, which re-registers every section with Home
  Screen Sections. This is why we go through its config rather than registering
  ourselves.
- The server serializes plugin config as **camelCase** over HTTP while the C# type
  is PascalCase. `SectionConfigMerger` handles both; a naive implementation
  silently creates a second `Sections` array the plugin ignores.

**Home Screen Sections** (GUID `b8298e01-2697-407a-b44d-aa8dc795e850`)

- `PluginInterface.RegisterSection(JObject)` is **in-memory only and does not
  persist** — anything registered that way vanishes on restart, which rule 22
  covers. Curator calls it, by reflection, and the whole contract below was read
  out of the 2.5.11.0 DLL and then exercised against it:
  - The payload is `SectionRegisterPayload` and Curator sends **camelCase** keys
    `id`, `displayText`, `limit`, `additionalData`, `resultsAssembly`,
    `resultsClass`, `resultsMethod` — the same shape Collection Sections sends.
  - `limit` is an **instance count, not an item count**. Above 1 the plugin asks a
    section for several copies of itself (that is how "Because You Watched" becomes
    three rows). A category is one row, so it is always 1.
  - Contents come either from an HTTP `resultsEndpoint` or from the
    assembly/class/method triple. **Use the triple.** The endpoint form builds a
    bare `HttpClient` with no credentials, so it can only call something anonymous,
    and it reads the server URL off a *private* property. The triple is resolved
    with `ActivatorUtilities.CreateInstance` against the server's root container,
    so the named class must be public, must be registered, and every constructor
    argument must resolve from that container.
  - The method is found with `Type.GetMethod(string)` — which **throws on an
    overload** — and its result is cast with `as QueryResult<BaseItemDto>`, so a
    wrong return type yields an empty row and no error. It takes one parameter,
    deserialized by Newtonsoft from the plugin's own `HomeScreenSectionPayload`,
    which carries only `UserId` and `AdditionalData`. Curator declares a
    structurally identical `CuratorSectionPayload` rather than referencing theirs.
    `SectionRegistrationTests` pins all of this from our side.
  - `AdditionalData` is **echoed back by the client**, not remembered by the
    server, so it is untrusted input on the way in. `CuratorSectionResults`
    validates it and only ever returns items already in that viewer's own playlist.
- **Rows in one `OrderIndex` group are shuffled** — `CacheSectionsForUser` calls
  `Shuffle` on each group before returning it, so every Curator row sharing a lane
  appears in a different position on every home screen load. Row *order*, not item
  order; the contents are unaffected. **Now the owner's call, as it should be:**
  `SectionOrderIndex` (default 500) sets the lane for category rows and
  `ContextRowOrderIndex` (0 inherits) gives the two context rows one of their own.
  The one-lane default still stands for categories — Curator has no basis for
  ranking one category above another on a stranger's home screen — but the context
  rows are the exception that proves it: there are exactly two, they are the same
  two every day, and a shared lane shuffles them into the middle of fifty rows
  where nobody scrolls. `DesiredSection` carries its own index so a single merge
  can write both lanes.
- `GetInfo` on a plugin-defined section hardcodes `ViewMode = Landscape`, but
  `SectionToInfo` overwrites it from `SectionSettings` before returning. **So the
  section settings write is what actually sets card shape, in both integration
  modes** — owning the row does not remove that write.
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

**`SectionDelivery` picks which plugin answers for a row, and defaults to
`Integrated`.** The fallback to `CollectionSections` is for what a machine cannot
detect — rows that render, but render wrongly. Registration failing *is*
detectable, so `Integrated` falls back on its own for that sync and says so in the
log; the registrar reports "none of N accepted" as unavailable rather than as a
count of zero precisely so that fallback can fire. Keep the old path working for a
release or two, and only remove it once a few restarts have been survived.

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
- **A hand-styled form control needs an OPAQUE background and `color-scheme`.**
  Dropping `is="emby-*"` means dropping Jellyfin's theming with it, and the
  browser's own default for a `<select>` or an `<input>` is a *light* field. A
  translucent background composites over that white base rather than over the dark
  page, so the Schedule tab's `rgba(255,255,255,0.08)` plus `color: inherit` —
  near-white text, from the dark theme — rendered every control on the tab white on
  white, readable only under the hover highlight. Worse, the dropdown popup and the
  time picker are painted by the browser and ignore those rules entirely; that is
  what `color-scheme: dark` is for, with explicit `option` colours for the browsers
  that still paint the list from the page's palette. Use `#101010`, Jellyfin's own
  base — the same value the Usage tab's palette was validated against.
- **Controller responses come back PascalCase; plugin configuration comes back
  camelCase.** Two different serializer paths on one server, and the page talks to
  both: `Curator/*` action results arrive as `IsRunning` and `Skipped`, while
  `GET /Plugins/{guid}/Configuration` arrives camelCase (which is why
  `SectionConfigMerger.FindProperty` tolerates both on the C# side). Guessing wrong
  **does not throw** — the field reads `undefined` and the page reports failure for
  a call that succeeded. That shipped: the weather Test button said "No reading.
  Check the server log" about a lookup that had worked, pointing at a log with
  nothing in it, and the same mistake had been sitting unnoticed in `syncRows`
  reporting every successful row sync as "Nothing was synced". Read every response
  field through the `field()` helper, which tries both; `ConfigPageContractTests`
  fails the build on a direct `result.someField` read and on a name no API record
  exposes.
- **A diagnostic must carry its reason out AND log it.** Everything in
  `Services/Context` swallows its failures by design — a background refresh that
  cannot reach the internet must be silent and harmless — so the Test button on top
  of it could only ever say "nothing came back". A wrong place name, no outbound
  DNS, a proxy answering 403 and a rate limit are four problems with four different
  fixes and are indistinguishable from a config page. Hence `ProbeAsync` beside
  `RefreshAsync`: same work, but the reason survives the trip. The rule generalises
  — if a button exists to answer *what is wrong*, the layer under it needs a path
  that does not discard the answer.
  **Carrying it out is not enough on its own**, which the first version of this got
  wrong: the reason went onto the response and nowhere else, so when the *page* was
  the broken part the only copy of the answer went somewhere unreadable and the log
  the message told the owner to check was empty. The endpoint now logs the outcome
  as well as returning it. A diagnostic leaves a trace on the server whatever the
  client does with it.
- **Anything listing profiles must be rebuilt whenever the list changes.** There
  are now four such pickers — the Summaries tab's, the Model tab's two per-pass
  ones, and the Home screen tab's title picker — and `renderProfiles()` refreshes
  all of them via `syncSummaryProfileSelect()` / `syncPassProfileSelects()`. Miss one and a rename
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
- **`SectionDelivery` is on the Home screen tab as "Row source"**, above Output
  type. Both are `setEnumSelect` selects, so their option order is load-bearing in
  the way described below.
- **The Usage tab draws its own SVG.** No chart library — the page takes no
  dependencies (rule 21) and a config page is not the place to start. The
  categorical colours are declared once as CSS custom properties on
  `.curatorUsage` and read back in JS, so there is a single source of truth; they
  are the dark steps of the project's data-viz palette, **validated as a set
  against Jellyfin's own `#101010`** for lightness band, chroma floor, adjacent
  CVD separation, normal-vision floor and 3:1 contrast. Assign them in fixed
  order and never cycle: a ninth model folds into "Other" rather than getting a
  generated hue nobody with a colour-vision deficiency could tell from slot 3.
  Colour is keyed on the **model name**, not its rank, so changing the date range
  cannot repaint the survivors. Two SVG traps, both load-bearing: a `transparent`
  fill is *not painted* and so receives no pointer events without
  `pointer-events: all`, and the bars must be `pointer-events: none` or they
  swallow the hover meant for the band behind them.
- **A dynamic list of per-user text boxes follows the `.curatorCheck` rule.**
  `renderUserLocations` builds the weather-location boxes with `innerHTML` and
  plain `<input type="text" class="emby-input">`, because customized built-in
  elements upgrade unreliably in generated markup. Every user gets a box rather
  than only those already carrying a value — an empty box that says what it falls
  back to beats an add-a-row button — and a blank one is **omitted** on save
  rather than stored as an empty string, so "no location" stays one state.
- **Settings live on five tabs**: Model (profiles, request, spend), Library
  (what is sent and who for), Categories (the two pools' size and count), Home
  screen (rows, the recommendation playlist, and the two context rows), Summaries
  (the condensing pass, tag consolidation, and the context judgement that fills
  those rows). Note the context feature deliberately straddles two tabs, which is
  rule "put a new setting where its *subject* is" working as intended: what is
  bought is a property of the condensing pass, and what is shown is a home screen
  row. **Three tabs are NOT part of the settings form** and hide the
  save row: Runs, Schedule and Usage — Schedule writes through Jellyfin's
  `ITaskManager` rather than plugin config, and Usage only reads, so the form's
  Save would do nothing for either. Put a new setting where its *subject* is, not
  where it is technically enforced.
- **The Schedule tab's running badge and the Runs tab's progress bar read one
  object.** `refreshStatus` stores each `Curator/Status` payload in `lastStatus`
  and both renderers draw from it; the badge never polls for itself. A second
  request would be a second source of truth, and the two panels would disagree
  for a couple of seconds every time a run started or stopped. `renderSchedules`
  rebuilds the rows from scratch, so it has to call `applyScheduleRunState()`
  afterwards or the badge is wiped on every save. Only Generate Categories can
  claim it — it is the one task whose progress Curator reports itself; the rest
  are Jellyfin's to report under Dashboard → Scheduled Tasks.
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
- **The page is cached by the browser**, and this is now the single most expensive
  recurring trap in the project — four sessions and counting. It is an
  `<EmbeddedResource>` served at a URL that never changes between versions, and
  Jellyfin sends it with **no cache headers at all**, so a browser may hold an old
  copy indefinitely. Worse, the dashboard fetches it by AJAX, so Ctrl+Shift+R on
  the dashboard does not reliably re-fetch it — **a private window is the only
  certain bypass**.
  The failure mode is what makes it costly: an old page against a new server fails
  in exactly the way that release fixed, while the owner reasonably believes they
  are testing the fix, and every subsequent diagnosis chases a server-side ghost.
  That happened with the weather Test button — the endpoint returned
  `{"Ok":true,...}` on the live server while the page insisted there was no
  reading.
  **The version badge beside the page title is the tell.** `Curator/Version` is
  fetched on load and rendered next to the heading; a page cached from before that
  existed renders nothing there, so a *missing* version is the signal. Check that
  first, before anything server-side.
  To confirm what the server is really serving, ask it rather than the browser:
  `curl -H 'Authorization: MediaBrowser Token="KEY", Client="c", Device="d", DeviceId="i", Version="1"' 'http://HOST:8096/web/ConfigurationPage?name=Curator'`
  and grep that. Grepping the DLL (`grep -ac curatorTabPanel …dll`) proves what is
  installed, not what the browser is showing.

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
5. **The weather and time-of-day rows.** Everything decidable without a server is
   pinned by tests — the vocabulary, the WMO mapping, the ranking, the condition
   keys and their rotation and culling, the store round-trip, every response shape
   in both schema dialects, that the two section scopes are disjoint, and that
   `CuratorContextSectionResults` declares exactly one public `GetResults`. What
   they cannot show is a row rendering, and four unknowns remain.
   No live Open-Meteo call has been made from this machine, so the geocoding and
   forecast response shapes come from documentation rather than observation —
   `GET /Curator/Context/Weather` and the Test button on the Home screen tab exist
   to settle that in one click on a real server. No model has yet been asked to
   judge context, so how sparing the prompt makes it is unmeasured; the failure to
   watch for is the inverse of the tag bug — a model answering "rain" about
   everything makes both rows meaningless, and the fix is the prompt, not the
   ranker. No model has been asked for a row *title* either, and the specific risk
   there is that it hands back the label it was meant to replace ("Rainy Evening
   Picks"), which the prompt forbids in as many words but nothing has verified.
   And `enum` on an array's `items` is used in both dialects against documented
   support, never against a live call.
6. **Curator's own home screen rows.** The registration contract was read out of
   the 2.5.11.0 DLL and then exercised against it — the payload Curator builds
   deserializes into that plugin's `SectionRegisterPayload` with every field
   populated, and its `HomeScreenSectionPayload` deserializes into
   `CuratorSectionPayload` with the category GUID round-tripping. What that
   *cannot* show is a row rendering. Still to verify on the server: all ten cards
   in playlist order, two viewers seeing one shared row differently, rows
   surviving a restart, and the card shape still following `PortraitThreshold`.
   One transitional wrinkle: on the first startup after upgrading, Collection
   Sections' own startup task may re-register Curator's rows from its config
   before Curator clears them, so that boot can still show the old behaviour. It
   self-heals — once cleared, the entries are gone.

## Reference

SmartLists is the architectural reference for list storage, GUID ownership,
ordering, and refresh patterns — it solves the same *infrastructure* problems for
a different purpose. It is **AGPL-3.0**: read it for patterns and API usage, never
copy code. Nothing in Curator is copied from it.
