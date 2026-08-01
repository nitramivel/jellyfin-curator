using System;
using Jellyfin.Plugin.Curator.Core.HomeScreen;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Curator.Configuration
{
    /// <summary>
    /// The LLM provider to use for category generation.
    /// </summary>
    public enum LlmProviderKind
    {
        /// <summary>Anthropic Messages API.</summary>
        Anthropic = 0,

        /// <summary>OpenAI Chat Completions API.</summary>
        OpenAi = 1,

        /// <summary>Any OpenAI-compatible endpoint (Ollama, LM Studio, vLLM, OpenRouter, ...).</summary>
        OpenAiCompatible = 2,

        /// <summary>Google Gemini, natively — the generateContent API with a response schema.</summary>
        Google = 3,

        /// <summary>xAI Grok. OpenAI-shaped wire format, with structured outputs on.</summary>
        Grok = 4,
    }

    /// <summary>
    /// The kind of Jellyfin list Curator creates for each category.
    /// </summary>
    public enum OutputKind
    {
        /// <summary>Ordered, user-scoped playlists (default; supports ordering).</summary>
        Playlist = 0,

        /// <summary>Server-wide collections (no ordering control in Collection Sections).</summary>
        Collection = 1,
    }

    /// <summary>
    /// Whether a particular pass lets the model think before answering.
    /// </summary>
    /// <remarks>
    /// Option order is load-bearing — the config page's <c>setEnumSelect</c> falls
    /// back to matching by index when a stored config carries the numeric value, so
    /// these may be relabelled but never reordered.
    /// </remarks>
    public enum ThinkingMode
    {
        /// <summary>Follow the global <see cref="PluginConfiguration.EnableThinking"/>.</summary>
        Inherit = 0,

        /// <summary>Think, whatever the global setting says.</summary>
        On = 1,

        /// <summary>Do not think, whatever the global setting says.</summary>
        Off = 2,
    }

    /// <summary>
    /// Plugin configuration. Category definitions are NOT stored here — they live as
    /// individual JSON files in the plugin data directory behind ICategoryStore.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets the saved model profiles — each a provider, a model, its own
        /// API key, and its own prices. See <see cref="ModelProfile"/>.
        /// <para>
        /// The list is the source of truth for how Curator calls a model. The legacy
        /// scalar fields below are kept only so an existing install's credential is
        /// not lost on upgrade; <c>Core/Llm/ModelProfiles.Normalize</c> folds them
        /// into a single profile the first time it sees an empty list.
        /// </para>
        /// </summary>
        public ModelProfile[] ModelProfiles { get; set; } = Array.Empty<ModelProfile>();

        /// <summary>
        /// Gets or sets the <see cref="ModelProfile.Id"/> of the profile used for
        /// every LLM call.
        /// <para>
        /// Named "default" rather than "active" because it is the fallback each task
        /// resolves to when nothing more specific is assigned. Per-task overrides are
        /// intended to sit alongside this, not replace it — a task with no assignment
        /// of its own must always land here.
        /// </para>
        /// </summary>
        public string DefaultModelProfileId { get; set; } = string.Empty;

        // ---------------------------------------------------------------------
        // Legacy single-profile settings.
        //
        // Superseded by ModelProfiles. They are NOT dead code and must not be
        // deleted: XmlSerializer silently drops elements it has no property for,
        // so removing these would throw away the API key of every install that
        // upgrades before it next opens the config page. Normalize() reads them
        // once, writes the profile, and nothing else in the plugin looks at them.
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets or sets the pre-profile LLM provider. Migration source only —
        /// read <see cref="ModelProfile.Provider"/> instead.
        /// </summary>
        public LlmProviderKind Provider { get; set; } = LlmProviderKind.Anthropic;

        /// <summary>
        /// Gets or sets the pre-profile model identifier. Migration source only —
        /// read <see cref="ModelProfile.Model"/> instead.
        /// </summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the pre-profile provider API key. Migration source only —
        /// read <see cref="ModelProfile.ApiKey"/> instead.
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the pre-profile base URL override. Migration source only —
        /// read <see cref="ModelProfile.BaseUrl"/> instead.
        /// </summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the number of library items sent per LLM request. 0 sends
        /// the whole library in a single request.
        /// <para>
        /// Prefer 0 whenever the model's context can hold the library. A thread
        /// running through items split across two batches is one the model never gets
        /// to see: each call only ever sees its own slice, and the categories it
        /// proposes can only join up what is in front of it.
        /// </para>
        /// </summary>
        public int BatchSize { get; set; } = 0;

        // ---------------------------------------------------------------------
        // Category size and count.
        //
        // Four knobs, two pools, one rule each way: a *floor* on how small a
        // category may be, and a *ceiling* on how many survive. The pools are
        // separate because they are drawn from different evidence — a shared
        // category comes from the whole library, a personal one from one viewer's
        // history — so the same numbers do not suit both.
        //
        // The floors are not only applied after the fact: each is written into the
        // prompt that asks for that kind of category, so the model aims at the
        // number it will be judged by. They were previously independent, and the
        // gap was expensive — the prompt asked for 3 members while the filter
        // demanded 6, and a measured run had 17 of 22 proposals binned on size
        // alone. Change a floor and the instruction changes with it.
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets or sets the smallest shared category kept, in members. Also the
        /// number the discovery prompt asks the model to meet. Minimum 2.
        /// <para>
        /// The low end of the shared pool's size range, paired with
        /// <see cref="MaxSharedCategorySize"/>. This is the number that actually
        /// moves row length: measured on a 263-item library, every one of 60
        /// categories came back between 5 and 10 members against a ceiling of 20, so
        /// the model sits near the floor it is given and the ceiling goes unused.
        /// </para>
        /// </summary>
        public int MinSharedCategorySize { get; set; } = 6;

        /// <summary>
        /// Gets or sets the most shared categories kept per run. 0 means no cap.
        /// <para>
        /// Told to the model as well as applied afterwards. A ceiling the model
        /// cannot see is one it cannot aim at: with no target count in the prompt,
        /// one model read "find the threads" as be exhaustive and another as
        /// satisfy the constraint — 23 categories covering 78% of the library
        /// against 5 covering 10%, from an identical prompt. The floor decides what
        /// is worth keeping, this decides how many survive.
        /// </para>
        /// <para>
        /// This is a <em>per-run</em> number: how many threads one discovery pass may
        /// propose. How many are kept in the store across runs is
        /// <see cref="MaxStoredSharedCategories"/>, which is a different question.
        /// </para>
        /// </summary>
        public int MaxSharedCategories { get; set; } = 10;

        /// <summary>
        /// Gets or sets how many shared categories the store keeps in total. 0
        /// inherits <see cref="MaxSharedCategories"/>.
        /// <para>
        /// Separate from the per-run cap because they answer different questions. The
        /// per-run number is how many threads the model is asked for in one pass; this
        /// is how large a library of them is allowed to accumulate. Tying them
        /// together caps the collection at one pass's worth, so every run over the
        /// number deletes something — and a category deleted by the cap loses its
        /// identity and comes back as a new row (hard rule 6), which is what makes the
        /// home screen churn. Measured on a single run with the two tied: 35
        /// categories pruned, 21 renamed, 49 un-proposed and held on grace.
        /// </para>
        /// <para>
        /// Set it above the per-run cap to let good threads accumulate across runs.
        /// Below it is legal but pointless: the run would propose more than the store
        /// is allowed to hold and immediately delete the excess.
        /// </para>
        /// </summary>
        public int MaxStoredSharedCategories { get; set; } = 0;

        /// <summary>
        /// Gets the shared retention cap actually applied.
        /// </summary>
        public int EffectiveStoredSharedCategories
            => MaxStoredSharedCategories > 0 ? MaxStoredSharedCategories : MaxSharedCategories;

        /// <summary>
        /// Gets or sets how many consecutive runs a category may go un-proposed
        /// before it loses its home screen row. 0 retires it immediately.
        /// <para>
        /// The model coins largely different threads each run — measured on a real
        /// library, one run matched 20 categories to existing definitions only
        /// through member similarity and retired 24 more. Retiring on the first miss
        /// means a row disappears and usually returns the next week, so the home
        /// screen flickers without the taste having changed. Waiting a couple of runs
        /// costs nothing: a category that really has gone still loses its row, just
        /// later.
        /// </para>
        /// </summary>
        public int CategoryRetirementGraceRuns { get; set; } = 2;

        /// <summary>
        /// Gets or sets the most items one category may contain. 0 means no limit.
        /// <para>
        /// Applies to both pools — a category is a row on a home screen either way,
        /// and Collection Sections renders only the first 16 of one. Members arrive
        /// ranked strongest-first, so the excess is trimmed off the tail rather than
        /// the category being discarded; a forty-item thread becomes the best twenty
        /// of itself. Told to the model too, so it aims at a size rather than
        /// having its answer quietly shortened.
        /// </para>
        /// <para>
        /// This is the fallback ceiling. <see cref="MaxSharedCategorySize"/> and
        /// <see cref="MaxPersonalCategorySize"/> override it per pool when set.
        /// </para>
        /// </summary>
        public int MaxCategoryMembers { get; set; } = 25;

        /// <summary>
        /// Gets or sets the most items one shared category may contain. 0 inherits
        /// <see cref="MaxCategoryMembers"/>.
        /// <para>
        /// Paired with <see cref="MinSharedCategorySize"/> this is the size range a
        /// shared row is asked for and held to. It exists because the two pools draw
        /// on different amounts of material: a thread through the whole library can
        /// carry thirty items, where one drawn from a single viewer's history often
        /// cannot. Note 0 means *inherit* here, not *no limit* — the no-limit answer
        /// is 0 on <see cref="MaxCategoryMembers"/>, which this then inherits.
        /// </para>
        /// <para>
        /// Defaults to a real number rather than to inherit, because this and
        /// <see cref="MinSharedCategorySize"/> are the pair the owner is meant to
        /// read as "between 6 and 25 items" — a box showing 0 does not say that.
        /// The consequence is that an install upgrading past this change picks the
        /// new ceiling up immediately, where its floor stays whatever was saved:
        /// stored values beat code defaults, and only the floor was ever stored.
        /// </para>
        /// </summary>
        public int MaxSharedCategorySize { get; set; } = 25;

        /// <summary>
        /// Gets or sets the member count at or above which a home screen row renders
        /// as portrait posters rather than landscape thumbs.
        /// <para>
        /// Landscape cards are wide, so a short row fills the screen; portrait cards
        /// are narrow and fit more across, which suits a row with enough in it to be
        /// worth scrolling. The right split depends on the screen and the taste, so
        /// it is a setting. 0 makes every row portrait; a number above
        /// <see cref="MaxCategoryMembers"/> makes every row landscape.
        /// </para>
        /// </summary>
        public int PortraitThreshold { get; set; } = SectionConfigMerger.DefaultPortraitThreshold;

        /// <summary>
        /// Gets or sets the hard token cap per run (input + output). 0 disables the cap.
        /// </summary>
        public long TokenBudget { get; set; } = 2_000_000;

        /// <summary>
        /// Gets or sets the maximum output tokens requested per LLM call.
        /// </summary>
        public int MaxOutputTokens { get; set; } = 16000;

        /// <summary>
        /// Gets or sets the pre-profile input price in USD per million tokens.
        /// Migration source only — read <see cref="ModelProfile.InputCostPerMillion"/>
        /// instead, so the price follows the profile it belongs to.
        /// </summary>
        public decimal InputCostPerMillion { get; set; }

        /// <summary>
        /// Gets or sets the pre-profile cache-read price in USD per million tokens.
        /// Migration source only — read <see cref="ModelProfile.CachedInputCostPerMillion"/>
        /// instead. Blank falls back to half the input price.
        /// <para>
        /// Cache reads are discounted, not free, and every provider discounts them
        /// differently — Anthropic bills a tenth of the input rate, others nearer a
        /// half. Curator used to leave them out of the total entirely, which
        /// understated any run served largely from cache. Half the input price is a
        /// deliberately conservative default: it errs high rather than reporting a
        /// run as cheaper than it was.
        /// </para>
        /// </summary>
        public decimal CachedInputCostPerMillion { get; set; }

        /// <summary>
        /// Gets or sets the pre-profile output price in USD per million tokens.
        /// Migration source only — read <see cref="ModelProfile.OutputCostPerMillion"/>
        /// instead.
        /// </summary>
        public decimal OutputCostPerMillion { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether playlist runs include each target
        /// user's watch activity (played, play count, favorite, rating) in what is
        /// sent to the LLM, producing user-specific categories. Only applies when
        /// <see cref="OutputType"/> is <see cref="OutputKind.Playlist"/>. Note this
        /// multiplies LLM cost by the number of target users and shares viewing
        /// behavior with the configured provider.
        /// </summary>
        public bool PersonalizedPlaylists { get; set; } = true;

        /// <summary>
        /// Gets or sets the kind of Jellyfin list created per category.
        /// </summary>
        public OutputKind OutputType { get; set; } = OutputKind.Playlist;

        /// <summary>
        /// Gets or sets a value indicating whether the model may select individual
        /// episodes rather than only whole series.
        /// </summary>
        public bool IncludeEpisodes { get; set; } = false;

        /// <summary>
        /// Gets or sets how many tags per item are sent to the model. 0 sends none.
        /// <para>
        /// Note this reads the opposite way to <see cref="MaxSharedCategories"/>, where 0
        /// means "no cap": here 0 means "no tags". Scraped tag lists are dominated by
        /// production trivia (aftercreditsstinger, duringcreditsstinger) that pushes
        /// the model toward the metadata-shaped categories the system prompt tells it
        /// to avoid, so off is the better default. Raise it to feed a few back in.
        /// </para>
        /// </summary>
        public int MaxTagsPerItem { get; set; } = 0;

        /// <summary>
        /// Gets or sets the <see cref="ModelProfile.Id"/> used for the shared
        /// discovery pass. Blank uses <see cref="DefaultModelProfileId"/>.
        /// <para>
        /// This is the hard half of the job: one pass over the whole library looking
        /// for threads that run through it. It is also a single call, so a expensive
        /// model here costs one call's worth.
        /// </para>
        /// </summary>
        public string DiscoveryModelProfileId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the <see cref="ModelProfile.Id"/> used for the per-viewer
        /// passes. Blank uses <see cref="DefaultModelProfileId"/>.
        /// <para>
        /// These are the many calls: one per eligible viewer, every run. On a
        /// measured six-call run, five were viewer passes — so this is the setting
        /// that actually moves the bill, and the pass is a narrower job than
        /// discovery besides.
        /// </para>
        /// </summary>
        public string PersonalModelProfileId { get; set; } = string.Empty;

        // ---------------------------------------------------------------------
        // Recommendation playlist.
        //
        // One long, ranked playlist per viewer, built by merging the categories
        // they already have. Costs no model call: every category already carries
        // the model's own ordering of its members, so the ranking is bought and
        // paid for by the time this runs. Intended for a spotlight row — the Media
        // Bar plugin and anything else that takes a playlist name.
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets or sets a value indicating whether each target user gets a ranked
        /// recommendation playlist.
        /// </summary>
        public bool RecommendationPlaylists { get; set; } = true;

        /// <summary>
        /// Gets or sets the name given to every viewer's recommendation playlist.
        /// <para>
        /// The same name for all of them, deliberately. Jellyfin playlists are
        /// user-scoped, so each viewer sees only their own — which means a consumer
        /// that resolves one playlist by name, as Media Bar and Collection Sections
        /// both do, shows every viewer their own list from a single setting.
        /// Renaming this renames the playlists on the next run; anything pointing at
        /// the old name has to be repointed.
        /// </para>
        /// </summary>
        public string RecommendationPlaylistName { get; set; } = "Recommended for You";

        /// <summary>
        /// Gets or sets how many items a recommendation playlist may hold. 0 means
        /// no cap.
        /// </summary>
        public int MaxRecommendations { get; set; } = 75;

        /// <summary>
        /// Gets or sets a value indicating whether items the viewer has already
        /// played appear in their recommendations.
        /// <para>
        /// When on they are kept but always sort below everything unwatched, so the
        /// head of the list stays discovery and the tail becomes rewatch fodder.
        /// Turning it off makes a shorter, strictly-unseen list.
        /// </para>
        /// </summary>
        public bool RecommendationsIncludeWatched { get; set; } = true;

        /// <summary>
        /// Gets or sets whether a model re-orders each viewer's shortlist. Off by
        /// default, because it is the only part of this playlist that costs money.
        /// </summary>
        /// <remarks>
        /// Selection stays arithmetic either way: which items are in play is a sum
        /// over categories already bought, and a model adds nothing to it. What this
        /// buys is the ordering, which is the part arithmetic is bad at — "what
        /// should this person see first tonight" is a judgement about a spread of
        /// moods rather than a sum of weights.
        /// <para>
        /// Read the cost before switching it on: <b>one call per eligible viewer per
        /// refresh</b>, and the refresh task runs every six hours by default. Six
        /// viewers is 24 calls a day against a task that is currently free. Lower the
        /// cadence on the Schedule tab, or point
        /// <see cref="RecommendationModelProfileId"/> at something cheap, or both.
        /// </para>
        /// </remarks>
        public bool ModelRankedRecommendations { get; set; } = false;

        /// <summary>
        /// Gets or sets the <see cref="ModelProfile.Id"/> used to re-order
        /// recommendations. Blank uses <see cref="DefaultModelProfileId"/>.
        /// </summary>
        /// <remarks>
        /// Worth pointing somewhere cheap. Ordering a shortlist of titles is a much
        /// smaller job than finding threads through a library, and this is the
        /// highest-frequency call Curator makes.
        /// </remarks>
        public string RecommendationModelProfileId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets how many of the top-ranked candidates are sent to be
        /// re-ordered. 0 sends the whole playlist.
        /// </summary>
        /// <remarks>
        /// The cost control. A recommendation row is looked at a few items at a time,
        /// so the ordering that matters is the top of it — sending 30 candidates and
        /// leaving the tail in the weighted ranker's order buys nearly all the value
        /// of sending 200. Anything beyond this keeps its existing order and is
        /// appended.
        /// </remarks>
        public int MaxRecommendationsToRank { get; set; } = 30;

        // ---------------------------------------------------------------------
        // Condensed summaries.
        //
        // Overviews are about two thirds of every prompt, and the same overview is
        // re-sent on every run forever. Distilling each one down once and caching
        // the result trades a single up-front cost for a permanently smaller prompt.
        // The distilled text is stored beside the categories and never written back
        // to Jellyfin, so the library's own overviews are untouched.
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets or sets a value indicating whether runs send the condensed summary
        /// in place of the Jellyfin overview, where one exists.
        /// <para>
        /// Off by default, because with an empty summary store it would change
        /// nothing and with a half-built one it would send a library described two
        /// different ways. Turn it on once the Summaries tab reports full coverage.
        /// </para>
        /// </summary>
        public bool UseCondensedSummaries { get; set; } = false;

        /// <summary>
        /// Gets or sets the target length of a condensed summary, in characters.
        /// <para>
        /// Told to the model and enforced on its answer, the same contract
        /// <see cref="CategoryLimits"/> keeps for categories. 90 is about a third of
        /// the measured average overview and still holds a clause about tone.
        /// </para>
        /// </summary>
        public int CondensedSummaryMaxLength { get; set; } = 90;

        /// <summary>
        /// Gets or sets how many items are distilled per LLM request. 0 sends them all
        /// in one request.
        /// <para>
        /// Unlike the category <see cref="BatchSize"/>, batching costs nothing here:
        /// each item is summarized independently, so a batch boundary cannot hide a
        /// connection the way it can when the model is looking for threads. Batches
        /// exist only to keep any one response inside the output cap, and to make a
        /// failure lose one batch rather than the whole pass.
        /// </para>
        /// </summary>
        public int SummaryBatchSize { get; set; } = 40;

        /// <summary>
        /// Gets or sets the overview length below which an item is left alone.
        /// <para>
        /// Distilling an overview that is already shorter than the target spends a
        /// model call to make the prompt no smaller, and risks throwing away detail
        /// for nothing.
        /// </para>
        /// </summary>
        public int SummaryMinSourceLength { get; set; } = 140;

        /// <summary>
        /// Gets or sets a value indicating whether the distillation pass also
        /// consolidates each item's scraped tags.
        /// <para>
        /// Done in the same model call as the summary, so switching it on costs one
        /// pass over the items that do not have consolidated tags yet rather than a
        /// whole second pipeline.
        /// </para>
        /// </summary>
        public bool ConsolidateTags { get; set; } = false;

        /// <summary>
        /// Gets or sets the most consolidated tags one item may keep.
        /// <para>
        /// A ceiling, never a target. The point of consolidation is that the count
        /// varies with the item — a title with one clear texture keeps one tag and a
        /// dense one keeps several — which is exactly what the old
        /// <see cref="MaxTagsPerItem"/> could not do, since taking the first N keeps
        /// whatever the scraper happened to order first regardless of whether it
        /// means anything.
        /// </para>
        /// </summary>
        public int MaxConsolidatedTags { get; set; } = 6;

        /// <summary>
        /// Gets or sets a value indicating whether runs send the consolidated tags
        /// alongside each item.
        /// <para>
        /// Separate from <see cref="ConsolidateTags"/> on purpose: building them and
        /// sending them are different decisions, and the useful order is to build,
        /// look at what came back on the Summaries tab, and only then start paying
        /// prompt tokens for them.
        /// </para>
        /// </summary>
        public bool SendConsolidatedTags { get; set; } = false;

        /// <summary>
        /// Gets or sets whether tag consolidation may coin a word the scraped list
        /// does not contain. Off by default.
        /// </summary>
        /// <remarks>
        /// The reason it is off: a tag is only worth anything if the same tag means
        /// the same thing across items, and free coinage produces near-synonyms —
        /// "melancholy", "melancholic", "wistful", "quietly sad" — which describe
        /// four films as four separate textures instead of one. Keeping the scraped
        /// wording is a shared vocabulary imposed for free.
        /// <para>
        /// The reason to turn it on: the scraped list is written by a metadata
        /// provider that never watched anything, so when nothing in it names what the
        /// summary just said, the alternative to coining a word is dropping the
        /// texture entirely. Constrained in the prompt to a last resort and to the
        /// plainest available wording, which is what keeps the vocabulary from
        /// fragmenting.
        /// </para>
        /// </remarks>
        public bool AllowInventedTags { get; set; } = false;

        /// <summary>
        /// Gets or sets the <see cref="ModelProfile.Id"/> used for distillation.
        /// Blank uses <see cref="DefaultModelProfileId"/>.
        /// <para>
        /// The first per-task model assignment. Distillation is a mechanical rewrite
        /// of one paragraph at a time — it does not need the model that finds threads
        /// across a whole library — so pointing it at a cheaper profile is the whole
        /// point of the profile list.
        /// </para>
        /// </summary>
        public string SummaryModelProfileId { get; set; } = string.Empty;


        /// <summary>
        /// Gets or sets the collection names whose membership is sent to the model,
        /// comma-separated. Empty sends none.
        /// <para>
        /// An item in one of these is labelled with it in the item list, so the model
        /// can see that a film won an Oscar and weigh that as evidence about the film.
        /// Keep the list short and meaningful: naming a franchise collection here
        /// invites exactly the metadata-shaped categories the system prompt otherwise
        /// tells the model to avoid, which is why this is a chosen list rather than
        /// every collection on the server.
        /// </para>
        /// <para>
        /// Ignored entirely when <see cref="SurfaceAllCollections"/> is on, which is
        /// the default. Kept populated regardless, so turning that off returns to the
        /// curated list rather than to nothing.
        /// </para>
        /// </summary>
        public string SurfacedCollections { get; set; } = "Oscar Nominees, Oscar Winners";

        /// <summary>
        /// Gets or sets a value indicating whether every collection an item belongs to
        /// is sent, rather than only those named in <see cref="SurfacedCollections"/>.
        /// <para>
        /// On by default. The whitelist exists because a franchise collection is the
        /// one kind of membership that reads as a ready-made category — "Marvel",
        /// "Star Wars Collection" — and the system prompt spends a paragraph telling
        /// the model not to propose exactly that. Sending everything trades that risk
        /// for completeness: the owner's own grouping of a title is evidence about it,
        /// and a whitelist can only carry the groupings someone remembered to type in.
        /// The prompt names the risk directly rather than relying on the input being
        /// pre-filtered.
        /// </para>
        /// </summary>
        public bool SurfaceAllCollections { get; set; } = true;

        /// <summary>
        /// Gets or sets the smallest personal category kept, in members. Also the
        /// number the per-viewer prompt asks the model to meet. Minimum 2.
        /// <para>
        /// Defaults to the same value as <see cref="MinSharedCategorySize"/> and is
        /// deliberately a separate setting: a personal category is grounded in one
        /// viewer's history rather than the whole library, so this is the knob to
        /// lower if invented categories start being discarded on size. The two were
        /// once 6 and 2, which made a personal row a much thinner thing than a shared
        /// one for no reason the owner had asked for; both now start at 4.
        /// </para>
        /// </summary>
        public int MinPersonalCategorySize { get; set; } = 6;

        /// <summary>
        /// Gets or sets the most items one personal category may contain. 0 inherits
        /// <see cref="MaxCategoryMembers"/>.
        /// <para>
        /// The other half of the personal pool's size range, and the one worth
        /// lowering: a viewer's own history is a smaller pool than the library, so a
        /// personal row asked for as many items as a shared one is a row the model
        /// has to pad to fill. Same 0-inherits rule and same defaulting reasoning as
        /// <see cref="MaxSharedCategorySize"/>.
        /// </para>
        /// </summary>
        public int MaxPersonalCategorySize { get; set; } = 25;

        /// <summary>
        /// Gets the ceiling actually applied to a shared category.
        /// </summary>
        /// <remarks>
        /// Get-only, so it is computed rather than stored — an inherited ceiling must
        /// follow <see cref="MaxCategoryMembers"/> when that changes rather than
        /// having frozen a copy of it at the moment the setting was saved.
        /// </remarks>
        public int EffectiveSharedCategorySize
            => MaxSharedCategorySize > 0 ? MaxSharedCategorySize : MaxCategoryMembers;

        /// <summary>
        /// Gets the ceiling actually applied to a personal category.
        /// </summary>
        public int EffectivePersonalCategorySize
            => MaxPersonalCategorySize > 0 ? MaxPersonalCategorySize : MaxCategoryMembers;

        /// <summary>
        /// Gets or sets the most personal categories kept per user. 0 means no cap.
        /// <para>
        /// Defaults to the same value as <see cref="MaxSharedCategories"/>, and is
        /// likewise kept separate so the two pools can still be capped independently.
        /// </para>
        /// </summary>
        public int MaxPersonalCategories { get; set; } = 10;

        /// <summary>
        /// Gets or sets how many personal categories the store keeps per viewer in
        /// total. 0 inherits <see cref="MaxPersonalCategories"/>.
        /// <para>
        /// The per-viewer counterpart of <see cref="MaxStoredSharedCategories"/>, and
        /// the one where the churn showed worst: 27 of the 35 categories pruned on a
        /// measured run were personal, because five viewers each proposed a full
        /// pass's worth against a store capped at the same number.
        /// </para>
        /// </summary>
        public int MaxStoredPersonalCategories { get; set; } = 0;

        /// <summary>
        /// Gets the per-viewer retention cap actually applied.
        /// </summary>
        public int EffectiveStoredPersonalCategories
            => MaxStoredPersonalCategories > 0 ? MaxStoredPersonalCategories : MaxPersonalCategories;

        /// <summary>
        /// Gets or sets how many items a user must have watched before they get a
        /// personalization pass of their own. 0 personalizes every user.
        /// <para>
        /// Personalization costs one full library prompt per user, paid whether or
        /// not the user has any history to shape it. Someone who has watched
        /// nothing gives the model nothing to work from, so the pass buys either
        /// silence or invention. Users below this floor are skipped before the call
        /// and receive the shared categories instead, which cost nothing extra.
        /// </para>
        /// </summary>
        public int MinWatchedForPersonalization { get; set; } = 2;

        /// <summary>
        /// Gets or sets a value indicating whether the model may think before
        /// answering.
        /// <para>
        /// On by default. Turning it off does NOT simply save tokens: recent models
        /// tend to write their reasoning into the visible response instead, which
        /// both wastes the output budget and degrades the answer — a discovery pass
        /// returned one usable category instead of twenty with thinking disabled.
        /// Leave this on unless the output budget is tight, and raise
        /// <see cref="MaxOutputTokens"/> rather than turning it off.
        /// </para>
        /// </summary>
        public bool EnableThinking { get; set; } = true;

        /// <summary>
        /// Gets or sets the users playlists are generated for. Empty means all users.
        /// </summary>
        public Guid[] TargetUsers { get; set; } = Array.Empty<Guid>();

        /// <summary>
        /// Gets or sets a value indicating whether newly created home screen sections
        /// are enabled for target users automatically.
        /// </summary>
        public bool AutoEnableSections { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether batches are submitted through the
        /// provider's asynchronous batch endpoint (Anthropic only) at half the token
        /// price, instead of one blocking request each.
        /// <para>
        /// This trades against prompt caching rather than adding to it. Batch requests
        /// are processed in parallel, so the per-user passes over a batch race each
        /// other and none can read a cache entry the others are still writing — the
        /// discount is reliable, the cache hits are not. It also removes the mid-run
        /// token-budget brake, since every request is committed up front.
        /// </para>
        /// </summary>
        public bool UseBatchApi { get; set; } = false;
    }
}
