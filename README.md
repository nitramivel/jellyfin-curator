<div align="center">

# 🎬 Curator

**A Jellyfin plugin that asks an LLM what your library has in common —<br/>and builds the answers into home screen rows.**

[![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11.x-aa5cc3?logo=jellyfin&logoColor=white)](https://jellyfin.org)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![Status](https://img.shields.io/badge/status-in%20development-orange)](#-installation)

*Dumb & Perfect · Cerebral Sci-Fi · Comfort Rewatch ·<br/>Movies That Look Better Than They Are · Quietly Devastating · Saturday Afternoon Cable*

</div>

---

Curator is a scheduled task. It sends every movie and show in your library to a model you configure, asks it to find the threads running through them, and turns each thread into an ordered Jellyfin playlist — surfaced on your home screen as a row via Collection Sections.

The categories it produces are the ones a query can't express.

> **Status:** running against a live Jellyfin 10.11.11 server. The full pipeline — scan, propose, reconcile, build playlists, publish home screen rows — completes from the scheduled task or the configuration page's **Generate categories now** button, and every run writes a complete record you can read afterwards.
>
> Still being tuned: how stable category names are between runs, and how shared categories should be distributed to users who have watched very little.

## 🎯 Scope

Curator does exactly one thing: **LLM-inferred vibe categories.**

It deliberately does *not* build categories from metadata fields or external data sources. If you want "Directed by Wes Anderson," "Unwatched Action Movies," or "2026 Emmy Winners," those are rule-based and external-list problems that [SmartLists](https://github.com/jyourstone/jellyfin-smartlists-plugin) already solves well. Curator is designed to sit alongside it, not replace it.

The bet is that the interesting gap in the Jellyfin ecosystem isn't *more ways to filter* — it's the categories that require taste.

## 🧠 How it works

```mermaid
flowchart LR
    A["📚 Scan<br/><i>library → compact records</i>"] --> B["📦 Batch"]
    B --> C["🤖 Propose<br/><i>discovery + per-viewer passes</i>"]
    C --> D["🔀 Reconcile<br/><i>merge · filter · cap</i>"]
    D --> E["🎵 Build<br/><i>ordered playlists</i>"]
    E --> F["🏠 Home screen rows"]
    S["✍️ Condense<br/><i>overviews → tone, cached</i>"] -.-> A
```

**1. Scan.** Every movie and series in the library is collected and reduced to a compact record: title, year, genres, tags, official rating, runtime, community rating, a truncated overview, and its Jellyfin item ID. No file paths or account details are ever sent. Watch history stays local too, with one opt-out exception: when **personalized playlists** are enabled (the default for playlist output), each target user's watch activity — played state, play count, favorites, personal rating — is attached so the model can shape categories to their taste. Television counts: Jellyfin records playback against episodes and leaves a series' own watch history empty, so episode plays are rolled up onto the parent series and reach the model as watch depth ("140 of 201 episodes"). Without that, anyone who mostly watches TV reads as someone who has watched nothing. Turn the toggle off and nothing about viewing behavior leaves the server.

**2. Batch.** By default the whole library goes in one request. A thread running through items split across two batches is one the model never gets to see — each call only sees its own slice — so batching is a workaround for a context that cannot hold the library, not something to want. Set a batch size only if you hit context limits.

**3. Propose.** Each batch is sent to the model, which returns proposed categories: a name, a one-line description, and the list of item IDs that belong to it. The model may only reference IDs supplied in that batch. Any ID it returns that wasn't in the input is discarded — this is what prevents it from confidently adding a movie you don't own.

**4. Reconcile.** Because each batch only sees a slice of the library, proposals overlap and duplicate across batches. A reconciliation pass merges near-identical categories (fuzzy name match plus member overlap), drops categories below a minimum size, caps the total number of categories, and produces the final set.

> Both limits are **written into the prompt as well as applied afterwards**, so the model aims at the numbers it will be judged by. Raising the floor asks for fewer, broader categories rather than quietly binning most of what it returns; the ceilings tell the model how many categories to reach for and how large to let each grow. A cap the model cannot see is one it cannot aim at — given no target count, one model returned 23 categories covering 78% of the library while another returned 5 covering 10%, from an identical prompt.

> Categories are recognised across runs by their **members as well as their name**. Models reword the same theme every run — "Reality Coming Undone" becomes "Reality Is Optional" becomes "Glitch in the Reality Engine" — and matching on the name alone made every run tear down and rebuild every home screen row. A renamed category now keeps its playlist, its position and its watch state, and simply changes its label.

Curator also sets each row's placement in Home Screen Sections: an **order index** you choose (500 by default — one lane, leaving the arrangement around it to you), and a card shape from the category's size, landscape below the **portrait row threshold** and portrait at or above it (default 10). Rows sharing an index are shuffled by Home Screen Sections on every load, which is why the weather and time-of-day rows can be given a lane of their own. Everything else about a row (enabled, limits, hide-watched) is left as you set it.

**5. Build.** Each surviving category becomes a Jellyfin playlist, ordered by the model's confidence ranking. Curator then registers a matching home screen section and enables it for your users.

Shared categories go to **everyone**. They were once opt-in — each viewer's pass named the ones it wanted — and that collapsed in practice: the model declined 16 of 25 offers, and three of eight shared categories ended up built for a single user. A category drawn from the whole library belongs to the whole household; the per-viewer pass earns its keep by inventing, not by vetoing.

They are still personalized, just not by removal. **Each viewer's own copy of a shared playlist is ordered for them** — Jellyfin playlists are per-user, so the same row can lead with different items for different people, and Collection Sections renders the first 16 in playlist order. A favourite rises, something rated 2 out of 10 sinks, and anything the viewer has no opinion about keeps exactly the order the model gave it. It is a nudge rather than a re-sort: the model ranked members by how strongly each belongs to the thread, and a favourite sitting thirtieth in a thread it barely belongs to rises without leading the row. Because this happens inside the playlist, it needs no client support at all — it works in Infuse and everything else, unlike the home screen rows.

Items orphaned by a removed or renamed library folder never reach the model. Jellyfin keeps their database rows — same path, same media source — so they are indistinguishable from real items and play back as nothing; on one server 36 of 298 items were such ghosts, and they were landing in real playlists.

## ⚙️ Configuration

### Model

Set in the plugin's configuration page:

Curator keeps a **list of model profiles** rather than one set of credentials. A profile is everything needed to call one model — provider, model id, its own API key, an optional base URL, **its own prices**, and whether it thinks — and different parts of a run can be pointed at different profiles.

Pricing lives on the profile because a list you switch between turns "remember to change the prices when you change provider" from an occasional mistake into the normal case.

| Per-profile setting | Description |
|---|---|
| **Provider** | Anthropic (Claude), Google (Gemini), xAI (Grok), OpenAI (GPT), or any OpenAI-compatible endpoint (Ollama, LM Studio, vLLM, OpenRouter). Google and Grok constrain the model to this plugin's exact response shape, so a malformed answer cannot lose a batch — prefer their own entries over reaching them through the OpenAI-compatible endpoint, which gives that up |
| **Model** | The model identifier to use |
| **API key** | Stored in the plugin configuration; optional for local OpenAI-compatible servers |
| **Base URL** | Optional override for self-hosted or proxied endpoints; required for the OpenAI-compatible provider (e.g. `http://localhost:11434/v1`) |
| **Let this model think** | Follow the global setting, always, or never. Thinking counts against the output cap, so this is worth setting per profile: on a measured distillation pass it took most of the budget and cut three batches off mid-JSON, while the discovery pass with thinking *off* returned one usable category instead of twenty. Keeping the same model as two profiles — one thinking, one not — is how a run reasons where reasoning pays and stops where it does not |
| **Input / output cost per million** | That profile's prices, used only for the estimated-cost log line; leave at 0 to log token counts alone |
| **Cached input cost per million** | What the provider charges for input served from its prompt cache. **Blank means half the input price** — deliberately conservative, since it errs high rather than reporting a run as cheaper than it was. Anthropic bills cache reads at a tenth of the input rate; others are nearer a half, so set this explicitly on Anthropic |

**Which model runs which pass.** A run is two different jobs, and they need not use the same model:

| Assignment | What it covers |
|---|---|
| **Discovery pass** | One call over the whole library, looking for the threads that run through it. The hard half of the job, and a single call per run — so a stronger model here costs one call's worth |
| **Per-viewer passes** | One call for each viewer with enough history, **every run** — five of six calls on a measured run. This is the setting that actually moves the bill, and the narrower job |
| **Summaries** | The condensing pass, on the Summaries tab |
| **Recommendation ordering** | Only used if you switch model-ranked recommendations on |

Leave any of them blank to use the default profile.

| Request setting | Description |
|---|---|
| **Batch size** | Items per request. **0 (the default) sends the whole library in one request** — raise it off 0 only if you hit context limits |
| **Max output tokens** | Output cap per request. Raise it if batch responses get truncated. **Thinking counts against this**, which is the usual reason a response gets cut off |
| **Token budget** | Hard cap per run, so a large library can't run up an unexpected bill |

| Category setting | Description |
|---|---|
| **Items per row** | The size range for a row, `6` to `25` by default. The two pools sit side by side on the tab, each asked the same two questions — how big is a row, and how many rows — with a sentence underneath reading the numbers back. Both ends are written into the prompt, so the model aims at the size it will be judged by. **The floor is the number that actually moves row length** — on a measured run all 60 categories came back at 5–10 items against a ceiling of 20, so the model sits near the floor it is given and the ceiling goes unused |
| **Items per personal row — fewest / most** | The same range for categories invented for a single viewer. Kept separate because a personal category is grounded in one person's history rather than the whole library, so this is the end to lower if invented categories start being padded or discarded |
| **New rows per run** | How many categories **one run** may propose. Told to the model as well as applied afterwards: a cap the model cannot see is one it cannot aim at — given no target count, one model returned 23 categories covering 78% of the library while another returned 5 covering 10%, from an identical prompt |
| **Rows kept in total / per viewer** | How large a library of rows may accumulate **across** runs, as opposed to how many one run may propose. Set these above the per-run numbers to let good threads build up: with the two equal, every full run deletes something to make room, and a row deleted by the cap loses its identity and returns as a brand-new row rather than the one you had. The tab warns you when the two numbers are set that way |
| **Min watched items to personalize** | How many items a user must have watched before they get a personalization pass. Defaults to 2; users below it are skipped before the request is sent and receive the shared categories instead. 0 personalizes everyone |
| **Send every collection an item belongs to** | On by default. Each item carries the full list of collections holding it, so the model sees how you have already grouped your library. The trade-off is that a franchise collection ("Marvel", "Star Wars Collection") reads as a ready-made category — the one shape the prompt spends a paragraph telling the model not to propose, and which it now names directly rather than relying on the input being pre-filtered. Turn it off to send only the collections you name below |
| **Collections to tell the model about** | Only used when the box above is unticked. Comma-separated names, `Oscar Nominees, Oscar Winners` by default |
| **Portrait row threshold** | Rows with at least this many items render as portrait posters; shorter rows render as landscape. Default 10. 0 makes every row portrait |

**Grok** talks the OpenAI wire format at `https://api.x.ai/v1`, with `response_format: json_schema` in strict mode — so valid JSON is an API guarantee, as with Gemini. Needs `grok-2-1212` or newer for that. Cached input and reasoning tokens are read from the usage detail blocks, so cache hits and thinking spend show up in the run log. Rate limits and transient 5xx are retried with backoff.

OpenAI's prompt caching is automatic above about 1,000 tokens, and Curator sends a `prompt_cache_key` so a run's calls route to the same cache — without it a 139,000-token prompt, byte-identical across six calls, reported zero cached tokens on two consecutive runs.

xAI's prompt caching is automatic and needs no configuration, but **cache entries live on the server that wrote them** — so every call of a run is tagged with the run's ID via the `x-grok-conv-id` header to pin them all to one machine. Without it the calls scatter across the fleet and each lands on a server that has never seen the item list: measured on a real library, 16 of 18 calls reported 128 cached tokens against a byte-identical 28,000-token prefix, while the two that happened to land warm cost a fortieth of the ones that missed.

The Google provider is built for unattended runs: the response schema makes valid JSON an API guarantee, safety filtering is turned off (the prompt is a list of your own films and their synopses, and a blocked response costs a whole pass), rate limits and transient 5xx are retried with backoff honouring `Retry-After`, and thinking tokens are reported separately so a truncated response tells you whether to raise the output cap or shrink the batch. Implicit context caching works without configuration — the item list is sent as a stable leading part, and cache hits show up in the run log.

Retention is enforced on what is **kept**, using the "rows to keep" numbers rather than the per-run caps. When a pool is over its cap the excess is deleted — definition and playlists together — so lowering a cap takes effect on the categories already stored. Categories holding no playlist go first: one showing nobody anything is the cheapest thing in the pool to lose, however recent it looks. After that it is oldest-first, meaning least recently produced by a run rather than earliest created — a category the model re-proposes every time is the last thing to go.

> [!IMPORTANT]
> **A note on what gets sent.** Curator transmits your library's titles and metadata to whatever provider you configure — and, when personalized playlists are enabled, each target user's watch activity too. With a hosted provider, that means a third party sees a list of everything you own and how you watch it. If that's not acceptable, disable personalization, or point Curator at a local model using the base URL override — then nothing leaves your network.

### Condensed summaries and tags

Metadata providers write overviews for a reader, not for a model: a paragraph of plot mechanics where what matters is tone. The **Summaries** tab runs a separate pass that rewrites each overview as one short, tone-carrying phrase and caches the result.

It is a **cache, never a write-back**. Nothing is written to your library, so clearing the store restores the previous behaviour exactly and the originals cannot be damaged. Staleness is keyed on a hash of the source overview, so a second pass over an unchanged library is free and a metadata refresh cannot leave a summary describing the wrong film.

| Setting | Description |
|---|---|
| **Use condensed summaries** | Substitute the short rewrite for the provider's overview on the way to the model |
| **Max length** | The character budget for one summary. The prompt asks for a *complete* phrase inside it rather than a sentence cut off at the limit |
| **Minimum source length** | Overviews shorter than this are left alone — distilling them would spend a call to make the prompt no smaller |
| **Consolidate tags while condensing** | Scraped tag lists average ~18 values an item and are mostly production trivia. This keeps only the ones describing what watching the thing is *like*. Done in the same call as the summary, and **after** it: the model writes the rewrite first, then keeps the tags that agree with the reading it just committed to, so one judgement drives both. Decided separately you get a summary calling something quietly devastating beside a tag list saying "action" |
| **Most tags to keep per item** | A ceiling, never a target — a title with one clear texture should come back with one tag |
| **Let it coin a tag when nothing in the list fits** | Off by default, for consistency rather than quality: free coinage produces near-synonyms (*melancholy, melancholic, wistful, quietly sad*) describing four films as four textures instead of one. The scraped list is a shared vocabulary imposed for free |

A batch the model answers badly is **split and retried**, not written off. An unusable answer is halved — usually the output cap cutting the JSON mid-object, which a smaller request simply does not hit — and a partial answer is retried for the items it missed. Bounded at three attempts, because every retry is a paid call.

### Recommendation playlist

One long playlist per viewer, ordered most-recommended first, built by merging the categories that viewer already has. Intended for a spotlight banner such as the Media Bar plugin, or any row that takes a playlist name.

**Which items appear costs nothing** — no model call. Every category already carries the model's own ranking of its members, so the information needed is already bought and stored. What the merge adds is the two things one category cannot express: that an item turning up in several of a viewer's threads is a stronger signal than topping any one of them, and that a recommendation is mainly about what they have *not* watched. A viewer's own categories count 1.6× a shared one.

| Setting | Description |
|---|---|
| **Playlist name** | Every viewer gets a playlist with this same name — Jellyfin playlists are per-user, so one name in one setting serves everybody. **This is the name to give the Media Bar plugin** |
| **Length** | How many items the playlist holds. A spotlight bar cycles, so a long list keeps it from repeating |
| **Include watched** | Keep items already played, always sorted below everything unwatched |
| **Have a model choose the order** | Off by default, and **the only part of this playlist that costs money**. Membership is unaffected; a model reads the top of the shortlist and decides what this viewer should see first — leading with the strongest fit and varying the mood as the row goes rather than stacking six bleak films together, which a weighted sum cannot do. **One call per eligible viewer per refresh**, against a task that runs 6-hourly by default. If a call fails or the answer is unusable the weighted order is kept, so the worst case is a wasted call rather than a broken row |
| **How many to order** | Only the top of the list is sent, 30 by default. A row is seen a few items at a time, so ordering the head buys nearly all the value for a fraction of the tokens |

### The weather and time-of-day row

One more home screen row, for the moment you are actually in — the weather outside and the hour of the day answered *together*. A cold wet Tuesday evening is not the same shelf as a bright Sunday morning, and the row's title says so: *Rainy Night Cozy Vibes*, *Cloudy Morning Stories*.

The match is **graded rather than strict**, and that is not a detail. Demanding both halves empties the row exactly when it is most wanted: on the library this was built against, "cloudy and morning" described a single film and "rain and morning" described none, because only six items suit a morning at all. So something suiting both leads, then the weather, then the hour. What tops the row genuinely fits the moment; the grading is what keeps it drawable on a bright Tuesday morning as well as a wet Friday night. Measured after the change, every sky-and-hour combination fills a full row.

A thin row is also **topped up** with things suiting the hour either side — evening reaches late night and afternoon, never morning — placed below every genuine match. A row that already has enough is left strictly alone, so a well-stocked evening is never diluted to rescue a starved morning.

With per-viewer locations each person gets their own row, titled for their own sky.

These are **worked out when the home screen asks for them**, not rebuilt on a schedule — so they match the actual hour and the actual sky rather than whatever was true at the last refresh. That is also why they have no playlist behind them, and why they need **Row source: Curator**; Collection Sections can only show a row by naming a playlist.

**Neither row costs a model call.** The judgement — *what should the weather be doing while someone watches this* — is bought once by the condensing pass, in the same call that writes the short description, and cached against the item. Drawing the row is set arithmetic over words already stored.

Every weather code Open-Meteo reports is mapped — clear, cloudy, rain, thunderstorms, snow, fog, plus hot and cold from the temperature — and freezing rain counts as rain with the cold carried separately. Rare conditions get a **stand-in**: `storm` is a word few films earn, so rather than show nothing during a thunderstorm the row falls back to rain-suited titles, ranked strictly below anything that genuinely suits thunder. That only happens when the exact condition cannot fill a row, so a well-stocked one is never diluted.

If you also run [Concierge](https://github.com/nitramivel/jellyfin-concierge), Curator will read the tone descriptions it has already generated and send them alongside each overview — *lonely and heartbreaking*, *stylish and unsettling*. That is better input for "what sky does this suit" than a plot synopsis, and it costs nothing because the judgement was already bought. Purely additive: an item Concierge has not seen is described exactly as before, and nothing breaks if it is absent or removed.

The model answers from a **closed vocabulary**: `clear`, `cloudy`, `rain`, `storm`, `snow`, `fog`, `hot`, `cold`, and `morning` / `afternoon` / `evening` / `latenight`. Closed because a row asking "is it raining" can only match a word that means the same thing on every item — left open, one film comes back *drizzly*, the next *overcast*, and the row matches neither. It is also told to be sparing, and that **most items should get nothing**: a broad comedy suits any hour and any sky, and saying so is the correct answer.

Weather comes from [Open-Meteo](https://open-meteo.com) — no account, no API key, nothing to expire. Give it a place name and it is geocoded once; conditions are re-read every half hour in the background, never while a home screen is being drawn.

| Setting | Description |
|---|---|
| **Judge when an item suits watching** *(Summaries tab)* | Buys the judgement. Rides along in the condensing call, so it costs output tokens and no extra input, and switching it on costs one pass over the items not yet judged rather than a full redo |
| **Show the weather and time-of-day row** *(Home screen tab)* | Publishes the row. Separate from the setting above for the same reason *Send consolidated tags* is separate from building them — classify first, look at what came back, then put rows on everybody's home screen |
| **Whose weather** | One place for the whole server, or each viewer's own. Per-viewer also moves the *time-of-day* row onto each viewer's own clock, since the timezone comes back with the forecast |
| **Location** | A place name — `Pittsburgh`, or `Pittsburgh, Pennsylvania` if the plain name is ambiguous. Also the fallback for any viewer without one of their own |
| **Row length** | 20 by default, and shorter than the recommendation playlist on purpose: these rows make a narrow claim, and a long one dilutes it with everything that merely qualifies |
| **Row titles** | Keep the names you typed, or let a model write them. See below |
| **Row order index** | Which lane the rows sit in. The context rows can have one of their own — worth doing, since rows sharing a lane get shuffled on every load |

Press **Test the weather lookup** on the Home screen tab to confirm the requests are getting through: it calls Open-Meteo right then, bypassing the cache, and reports the place it matched, the conditions, the temperature and the local time. Without it, "the server has no outbound internet" and "nothing in the library suits today" look identical from the settings page.

#### Model-written row titles

Optional, and off by default. Instead of a fixed *Picks for the Weather*, a model names the row for the conditions — and the mistake it has to avoid is restating them, because "Rainy Evening Picks" is not a title, it is the label it replaced. The prompt spends most of its length on that: reach for what the weather *does* to a person, not what it is.

**The cost is bounded by your weather, not by time.** Titles are bought once per set of conditions — `rain + cold` is a key — several at a time, cached, and rotated on each use. A place produces on the order of thirty distinct conditions, so spending flattens within a few weeks and then effectively stops. Two viewers under the same sky are offset in the rotation so they do not read the same words, and a failed call or an unseen condition falls back to the name you typed.

Unused sets are **culled automatically** after a year, and sets naming a condition Curator no longer recognises go immediately. The retention is a year rather than a month because these conditions are seasonal: culling the snowy-evening titles every July would re-buy them every December, which is exactly what the cache exists to prevent.

When each viewer has their own location, each gets their **own pair of rows** — their own sections, enabled only for them, titled for their own sky. That is the only way two people can read two different titles: Home Screen Sections keeps no per-user display text, so two titles means two sections.

A row with fewer than three matching items is not drawn at all — a single card reads less like a thin row than a broken one. If the weather cannot be read the weather row is simply left out rather than quietly falling back to the clock, and the health check tells you if you have switched the rows on without switching on the judgement that fills them.

One design note worth knowing, because it explains the scheduled task: a row's **title belongs to its registration**, so a name that tracks the sky has to be re-registered when the sky turns over — that is what **Refresh Context Rows** does, hourly. Its *contents* are still assembled when the home screen asks, but from the conditions the task pinned, so the title and the cards can never contradict each other.

### Scheduled tasks

Seven tasks, one job each, all editable from the **Schedule** tab (which writes through Jellyfin's own task manager, so **Dashboard → Scheduled Tasks** shows the same values and either page can edit them):

| Task | Default | Costs money |
|---|---|---|
| **Generate Categories** | Weekly | **Yes** — the only one that calls a model as a matter of course |
| **Condense Summaries** | Daily | Only for items not already distilled |
| **Refresh Recommendations** | 6-hourly | Only if model-ranked ordering is on |
| **Clean Up and Sync** | Daily | No |
| **Health Check** | Daily | No |
| **Publish Home Screen Rows** | Every server start | No |
| **Refresh Context Rows** | Hourly, and every server start | Only the first time a set of weather conditions is seen, and only with model-written titles on |

All of them skip or no-op while a run is in progress — a run rewrites the same playlists and definitions, and racing it loses work.

### Health check

This plugin fails quietly by design: both home screen integrations degrade silently, a run dies mid-flight whenever installing any plugin tears the host down, and library rows outlive their folders. From the outside all of those look identical to "Curator stopped working".

It runs daily, and there is a **Health check now** button on the Schedule tab beside *Clean up and sync now* — it reads only and calls no model, so it is free to press as often as you like. The line beneath the buttons says when it last ran and what it found.

The health panel reports what it can actually diagnose — a prerequisite plugin gone, a run that has stopped happening, ghost items, model profiles without keys, categories holding no playlist, tag consolidation producing nothing, a distillation pass that lost most of its items, the row-publishing task having lost its startup trigger, and the weather and time-of-day rows being switched on with nothing classified to fill them. It is deliberately shy: a panel that reports normal operation as a problem gets ignored, so a late run is not a stalled one and a manual-only schedule is never reported at all.

### Output

| Setting | Description |
|---|---|
| **Output type** | Playlists (default) or collections |
| **Personalized playlists** | Attach each target user's watch activity so their playlists reflect their taste. Playlists only; runs the model once per user, so cost scales with user count — see **min watched items to personalize** for the floor that keeps dormant accounts out of that multiplier |
| **Send one row per title** | On by default. A director's cut and a theatrical cut are two items in Jellyfin and one film to a viewer — sent as two they arrive with identical titles, years, genres and overviews, so the model puts both in the same category and the row shows the same poster twice. Keeps the copy with the longest runtime and folds the other's watch history onto it. Two rows are one title when you have merged them in Jellyfin yourself, when they carry the same metadata-provider ID, or when kind, title **and** year all agree — nothing here is a similarity test, so *Freaky Friday* (2003) and *Freaky Friday* (1995) stay two different films. Applied to the rows as they are drawn too, so a category built before you turned this on stops showing two posters immediately rather than at the next run |
| **Treat the same TMDb/IMDb ID as the same title** | On by default, and what catches the case title and year cannot: *Blade Runner* (1982) and *Blade Runner: The Final Cut* (2007) agree on neither field and are one film, and their TMDb IDs say so. Turn it off only for a library whose provider IDs are known to be wrong |
| **Include episodes** | Allow the model to select individual episodes, not just whole series |
| **Target users** | Which users get playlists generated for them (empty = all users) |
| **Auto-enable sections** | Enable newly created sections on the home screen automatically |

### Run logs

Every paid pass writes a complete record to `{data}/curator/runs/run_<timestamp>_<id>.json`, separate from the category files — the category run and the distillation pass alike, distinguished by the trigger recorded in the file. One file per run, containing the settings it ran under, every step in order, and **every LLM prompt and response in full** — including the attempts that failed, which are usually the interesting ones. The file is written as the run progresses, so a run that dies part-way still leaves everything up to the point it stopped.

Repeated prompt bodies are stored once and referenced by hash: the item list is byte-identical across every pass of a run, so a six-pass run records it once rather than six times. The newest 50 runs are kept and older files are rotated away. API keys are never written to a run log.

Each run log carries costs at three levels: the **prices it was costed at** (recorded in the file, so a figure is never readable without the rate that produced it), a **per-call** input/cached/output/total, and a **run total**. With no prices set, every cost is `null` rather than `0` — a run that cost money must not read as free.

Cache reads are charged, not free. Providers that report cached tokens inside their input count have it subtracted before costing, so pricing input alone drops them from the total silently — on one measured run that understated the bill by 24%, and on a fully cached run it would report about a third of it. Cache *writes* still carry a premium that is not priced, so the total remains an estimate; the token counts sit beside it precisely so the arithmetic can be redone by hand.

They are readable over the API too — `GET /Curator/Runs` for the list, `GET /Curator/Runs/{runId}` for one run's whole record.

Runs happen weekly by default via the **Generate Categories** scheduled task (grouped under *Curator*) (adjust the schedule under **Dashboard → Scheduled Tasks**), or on demand from the **Generate categories now** button on the configuration page. Only one run happens at a time.

While a run is going the page shows a progress bar with a live cost breakdown, and the last five runs sit beneath it as expandable rows — the scan, the discovery pass, one line per viewer, what happened to the category set, and every model call with its duration, tokens, cache reads and cost. The complete run file is one link away.

Two maintenance buttons sit alongside, both free and neither calling a model:

- **Re-sync home screen rows** rebuilds the rows from the categories already stored, for when they have drifted.
- **Re-sync playlists** reconciles stored categories against the playlists that actually exist: it rebuilds a playlist a category has lost, deletes categories left holding none, and deletes Curator playlists no category claims. Playlists you have untagged are never touched.

## 🎵 Playlists, collections, and ordering

**Curator defaults to playlists, and you should probably leave it that way.**

Collection Sections renders a collection row by taking the first 16 children in whatever order Jellyfin returns them — there is no ordering hook. You cannot control which 16 of a 40-item category appear.

Playlists preserve explicit order, so Curator controls both the sequence and, by extension, which items make the visible cut.

The usual objection to playlists is that adding a series inserts its episodes individually. That objection doesn't apply here: Collection Sections' playlist handler groups episodes back into their parent series before rendering, so a playlist of individual episodes displays as **series cards**, deduplicated, in playlist order. Inside the library UI the playlist still shows episodes — that's the tradeoff — but the home screen row looks correct.

This also unlocks episode-level categories that collections simply cannot express: **Bottle Episodes**, **The Halloween Ones**, **Episodes That Broke People**.

Note that Jellyfin playlists are user-scoped. One category becomes one playlist per targeted user, and Curator tracks the per-user playlist IDs internally.

## 🏷️ Ownership and tagging

Every playlist and collection Curator creates is tagged in its metadata as plugin-created, alongside the run timestamp and the model that generated it.

This means:

- Curator resolves its own lists by stored GUID on subsequent runs, updating them rather than creating duplicates.
- Lists you created by hand are never modified or deleted, even if the name collides with one Curator would generate.
- You can purge everything Curator has made in a single action without touching the rest of your library.
- Removing the tag from a list hands it to you permanently — Curator will stop managing it.

When a category stops matching anything, Curator removes the Jellyfin playlist but keeps the category definition, and recreates the playlist if the category becomes populated again.

## 🏠 Home screen integration

Curator registers its categories as Collection Sections entries and can enable them for users automatically, so a new category appears on the home screen without anyone visiting a settings page.

If you'd rather approve categories before they go live, disable **Auto-enable sections** and they'll appear in Modular Home settings as available-but-off.

## 📦 Installation

### Prerequisites

1. A Jellyfin **10.11.x** server.
2. [Home Screen Sections](https://github.com/IAmParadox27/jellyfin-plugin-home-sections) installed and working — follow its guide first, including enabling modular home in your user settings.
3. [Collection Sections](https://github.com/IAmParadox27/jellyfin-plugin-collection-sections) installed on top of it.
4. An API key for Anthropic, Google, xAI, or OpenAI, **or** a local OpenAI-compatible endpoint (Ollama, LM Studio, vLLM).

### Install from the plugin catalogue (recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and add:

   ```
   https://raw.githubusercontent.com/nitramivel/jellyfin-curator/main/manifest.json
   ```

2. Switch to the **Catalog** tab — Curator now appears under General. Click it and press **Install**.
3. Restart Jellyfin when prompted.
4. Open Curator's configuration page, choose a provider and model, and enter your API key (or base URL for a local endpoint).
5. Run the **Generate Categories** scheduled task manually for a first pass, and watch the server log — every run logs its token count and estimated cost at INFO.

### Install manually (folder drop)

1. Grab a packaged build — either from the repository's releases, or build one yourself (next section). Either way you end up with a folder containing `Jellyfin.Plugin.Curator.dll` and `meta.json`.
2. Copy that folder into your server's plugin directory as `plugins/Curator_<version>`:

   | Setup | Plugin directory |
   |---|---|
   | Podman/Docker (config dir mounted at `/config`) | `<your config mount>/plugins/Curator_0.1.0.0/` |
   | Linux package | `/var/lib/jellyfin/plugins/Curator_0.1.0.0/` |
   | Windows | `%ProgramData%\Jellyfin\Server\plugins\Curator_0.1.0.0\` |

3. Make sure the files are readable by the Jellyfin user (for containers, match the UID the container runs as).
4. Restart Jellyfin. **Dashboard → Plugins** should now list Curator as Active, then configure and run as in the catalogue steps above.

### Building from source

Requires the .NET 9 SDK.

```bash
git clone https://github.com/nitramivel/jellyfin-curator
cd jellyfin-curator
dotnet test                # full test suite; no network access needed
./build/package.sh         # produces artifacts/Curator_<version>/ ready to copy
```

`build/package.sh` accepts `VERSION` and `TARGET_ABI` environment variables if you need to pin either. Releasing a new catalogue version is `VERSION=x.y.z.w CHANGELOG="..." ./build/release.sh`, which builds the zip, computes its checksum, and updates `manifest.json` — then upload the zip to a GitHub release tagged `vx.y.z.w`.

## ⚠️ Caveats

- **LLM output is not deterministic.** Two runs over the same library will not produce identical categories. This is arguably the point, but it means categories churn between runs. To keep a playlist you love exactly as it is, remove its `curator` tag — Curator hands it to you permanently and never touches it again.
- **Cost scales with library size.** A 3,000-item library is a lot of tokens per run. Set a token budget and start with a wide schedule.
- **The model can be wrong about your media.** It sees a title and an overview, not the film. It will occasionally group things confidently and incorrectly.
- **Collection Sections resolves rows by name.** Renaming a Curator playlist by hand will break its home screen section.
- **Installing any plugin restarts Jellyfin's host in-process.** A Curator run started beforehand keeps executing against a container that no longer exists and is abandoned part-way. Curator reports this as what it is rather than as a stack trace, and nothing is left half-built — but don't start a run and then install a plugin.

## 🙏 Credits

Home screen integration builds on [Home Screen Sections](https://github.com/IAmParadox27/jellyfin-plugin-home-sections) and [Collection Sections](https://github.com/IAmParadox27/jellyfin-plugin-collection-sections) by [IAmParadox27](https://github.com/IAmParadox27). Architectural patterns — list storage, ownership by GUID, ordering strategies, refresh queueing — were informed by [SmartLists](https://github.com/jyourstone/jellyfin-smartlists-plugin) by [jyourstone](https://github.com/jyourstone).
