using System;
using Jellyfin.Plugin.Curator.Core.Context;
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
    /// Where a context row's title comes from.
    /// </summary>
    /// <remarks>
    /// Option order is load-bearing — relabel freely, never reorder.
    /// </remarks>
    public enum ContextRowTitleMode
    {
        /// <summary>The name typed into the settings, unchanged. Free.</summary>
        Fixed = 0,

        /// <summary>
        /// A model writes a few titles for each set of conditions, cached and
        /// rotated. Costs one call the first time a condition is seen and nothing
        /// afterwards.
        /// </summary>
        Model = 1,
    }

    /// <summary>
    /// Whose weather the context row is drawn for.
    /// </summary>
    /// <remarks>
    /// Option order is load-bearing for the same reason <see cref="SectionDelivery"/>
    /// says: relabel freely, never reorder.
    /// </remarks>
    public enum WeatherLocationMode
    {
        /// <summary>
        /// One place for the whole server. The normal case — a household watches in
        /// one house, and it is raining on all of them.
        /// </summary>
        Single = 0,

        /// <summary>
        /// Each viewer's own place, falling back to the server's for anyone who has
        /// not been given one. For a library shared across households.
        /// </summary>
        PerUser = 1,
    }

    /// <summary>
    /// One viewer's weather location.
    /// </summary>
    /// <remarks>
    /// A class with a parameterless constructor and settable properties because
    /// XmlSerializer writes the plugin configuration: it cannot serialize a record,
    /// an init-only property or a tuple, and it fails at runtime rather than at
    /// compile time when handed one.
    /// </remarks>
    public class UserWeatherLocation
    {
        /// <summary>Gets or sets the viewer.</summary>
        public Guid UserId { get; set; }

        /// <summary>Gets or sets the place name, as typed — e.g. "Pittsburgh".</summary>
        public string Location { get; set; } = string.Empty;
    }

    /// <summary>
    /// How Curator's categories reach the home screen.
    /// </summary>
    /// <remarks>
    /// Option order is load-bearing — the config page's <c>setEnumSelect</c> falls
    /// back to matching by index when a stored config carries the numeric value, so
    /// these may be relabelled but never reordered.
    /// </remarks>
    public enum SectionDelivery
    {
        /// <summary>
        /// Curator registers its own sections with Home Screen Sections and answers
        /// for their contents itself. Falls back to
        /// <see cref="CollectionSections"/> for a sync that cannot register.
        /// </summary>
        Integrated = 0,

        /// <summary>
        /// Sections are written into the Collection Sections plugin's configuration
        /// and it answers for their contents. The original path, kept as an escape
        /// hatch.
        /// </summary>
        CollectionSections = 1,
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
        /// identity and comes back as a new row (hard rule 7), which is what makes the
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
        /// Gets or sets whether two library rows for the same title are collapsed to
        /// one before the model sees them. On by default.
        /// </summary>
        /// <remarks>
        /// A director's cut and a theatrical cut are two items in Jellyfin and one
        /// film to a viewer. Sent as two they arrive with identical titles, years,
        /// genres and overviews, so the model puts both in the same category and the
        /// row shows the same poster twice.
        /// <para>
        /// Matching is exact and never fuzzy: Jellyfin's own alternate-version link
        /// first, then the item's metadata-provider ID, then kind, title and year
        /// together. On the library this was built against, "Freaky Friday" exists as
        /// 2003 and 1995, and a title-only rule would merge two genuinely different
        /// films. The longest runtime wins, so the fuller cut is the one kept, and
        /// watch activity from the dropped row is folded onto it rather than lost.
        /// </para>
        /// </remarks>
        public bool CollapseDuplicateVersions { get; set; } = true;

        /// <summary>
        /// Gets or sets whether two rows carrying the same metadata-provider ID count
        /// as one title. On by default, and only consulted when
        /// <see cref="CollapseDuplicateVersions"/> is on.
        /// </summary>
        /// <remarks>
        /// This is the setting that catches the case title and year cannot: "Blade
        /// Runner" (1982) beside "Blade Runner: The Final Cut" (2007) agree on neither
        /// field and are one film, and their TMDb IDs say so. It is exposed at all
        /// because it inherits whatever the scrapers got wrong — a provider ID stamped
        /// on the wrong film merges two titles here, and nothing downstream would
        /// notice. Switch it off only for a library known to be in that state.
        /// </remarks>
        public bool MatchDuplicatesByProviderId { get; set; } = true;

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
        /// Gets or sets a value indicating whether each viewer also gets a films-only
        /// and a television-only recommendation list beside the combined one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The combined list is built either way and never stops being built. That is
        /// deliberate: consumers that take a single playlist name — the Media Bar
        /// plugin at the top of the page, Collection Sections — have one slot, and a
        /// split that quietly emptied it would take the feature away from the place
        /// it is most visible. So this adds two lists rather than replacing one.
        /// </para>
        /// <para>
        /// It costs no extra model call. The ranking is done once and the per-type
        /// lists are filters over that one order, so hard rule 15 holds unchanged:
        /// the re-rank, when it is on at all, is still one call per viewer.
        /// </para>
        /// </remarks>
        public bool SplitRecommendationsByType { get; set; }

        /// <summary>
        /// Gets or sets the name given to every viewer's films-only recommendation
        /// playlist. Used only when <see cref="SplitRecommendationsByType"/> is on.
        /// </summary>
        public string RecommendationMoviePlaylistName { get; set; } = "Recommended Movies for You";

        /// <summary>
        /// Gets or sets the name given to every viewer's television-only
        /// recommendation playlist. Used only when
        /// <see cref="SplitRecommendationsByType"/> is on.
        /// </summary>
        public string RecommendationShowPlaylistName { get; set; } = "Recommended Shows for You";

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

        // Viewing context: what the weather is doing and what time it is.

        /// <summary>
        /// Gets or sets whether the condensing pass also judges when an item suits
        /// watching — the weather outside and the part of the day. Off by default.
        /// </summary>
        /// <remarks>
        /// This is the paid half of the context rows, and it is deliberately folded
        /// into the pass that already reads every overview rather than given a pass of
        /// its own. The judgement wanted here is the same one the rewrite makes, asked
        /// about the room instead of the film, so it belongs in the same call — and a
        /// second pass would re-send the whole item list to ask one more question,
        /// paying the library's input tokens twice for a few words of output.
        /// <para>
        /// It follows that this does nothing on its own: it rides on
        /// <see cref="UseCondensedSummaries"/>' pass, so the Condense Summaries task
        /// has to be running for anything to be classified. Switching it on queues
        /// only the items not yet judged, never the whole library — see
        /// <c>SummaryPlan</c>'s context hash.
        /// </para>
        /// </remarks>
        public bool ClassifyViewingContext { get; set; } = false;

        /// <summary>
        /// Gets or sets whether the condensing pass may read tone descriptions the
        /// Concierge search plugin has already generated, and send them alongside
        /// each overview. On by default, and a no-op when that plugin is absent.
        /// </summary>
        /// <remarks>
        /// It is free — the judgement was bought for a different purpose and is
        /// sitting on disk — and it is better input than an overview for both halves
        /// of this pass. An overview describes the <em>premise</em>; the rewrite is
        /// about tone and the context judgement is about mood, and neither is about
        /// premise. Concierge's themes are explicitly "what watching it feels like",
        /// which is the question being asked.
        /// <para>
        /// Never a replacement for the overview, always an addition — so an item the
        /// other plugin has not seen is described exactly as it was before. Reading
        /// stops at two field names and fails open, so nothing here can break a pass
        /// if that plugin changes, is removed, or was never installed.
        /// </para>
        /// </remarks>
        public bool UseExternalThemes { get; set; } = true;

        /// <summary>
        /// Gets or sets whether the two context rows are published to the home screen.
        /// Off by default.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="ClassifyViewingContext"/> for the reason
        /// <see cref="SendConsolidatedTags"/> is separate from
        /// <see cref="ConsolidateTags"/>: buying the judgement and acting on it are
        /// different decisions, and the useful order is to classify, look at what came
        /// back, and only then put a row on everybody's home screen.
        /// <para>
        /// <b>One</b> row, answering the weather and the hour together — one per
        /// viewer when each has their own location, otherwise one shared. It was two,
        /// and the reason it is not is measured rather than aesthetic: on a 202-item
        /// library "cloudy and morning" described a single film and "rain and
        /// morning" described none, so a pair of strict rows was absent every
        /// morning. One graded row is drawable whenever either half has something.
        /// </para>
        /// <para>
        /// It exists only under <see cref="SectionDelivery.Integrated"/>: its
        /// contents are assembled when the home screen asks, and there is no playlist
        /// behind it to hand to Collection Sections, which resolves a row by name.
        /// </para>
        /// </remarks>
        public bool ContextRows { get; set; } = false;

        /// <summary>The name the single context row falls back to.</summary>
        public const string DefaultContextRowName = "Right For Now";

        /// <summary>
        /// Gets or sets the context row's name under
        /// <see cref="ContextRowTitleMode.Fixed"/>, and its fallback under
        /// <see cref="ContextRowTitleMode.Model"/>.
        /// </summary>
        /// <remarks>
        /// Still load-bearing when a model is writing the titles: a failed call, a
        /// condition never yet seen, or an answer that would not parse all fall back
        /// to this rather than leaving a row unlabelled.
        /// </remarks>
        public string ContextRowName { get; set; } = DefaultContextRowName;

        // ---------------------------------------------------------------------
        // The two row names from when weather and time of day were separate rows.
        //
        // Superseded by ContextRowName and NOT dead code: XmlSerializer silently
        // drops elements it has no property for, so deleting these would discard
        // whatever the owner had typed into them the first time the config page
        // saved after an upgrade. They cost nothing to keep; see hard rule 13 for
        // the expensive version of this lesson.
        // ---------------------------------------------------------------------

        /// <summary>Gets or sets the pre-merge weather row name. Migration source only.</summary>
        public string WeatherRowName { get; set; } = "Picks for the Weather";

        /// <summary>Gets or sets the pre-merge time-of-day row name. Migration source only.</summary>
        public string DaypartRowName { get; set; } = "Picks for the Hour";

        /// <summary>
        /// Gets or sets whether a model writes the context row titles.
        /// </summary>
        /// <remarks>
        /// A row's display text is fixed when its section is registered — Home Screen
        /// Sections keeps no per-user or per-request title — so a title that tracks
        /// the sky means <em>re-registering the section</em> whenever conditions turn
        /// over. That is what the Refresh Context Rows task is for, and it is why
        /// this cannot be decided on the render path with everything else about
        /// these rows.
        /// </remarks>
        public ContextRowTitleMode ContextRowTitleMode { get; set; } = ContextRowTitleMode.Fixed;

        /// <summary>
        /// Gets or sets the <see cref="ModelProfile.Id"/> used to write row titles.
        /// Blank uses <see cref="DefaultModelProfileId"/>.
        /// </summary>
        /// <remarks>
        /// Worth pointing somewhere cheap. Naming a shelf is the smallest job any
        /// pass here asks for — a few dozen words in, a few dozen out — and it is
        /// bought once per set of conditions rather than per refresh.
        /// </remarks>
        public string ContextTitleModelProfileId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets how many titles are bought for each set of conditions.
        /// </summary>
        /// <remarks>
        /// The whole set arrives in one call, so this is variety per unit of spend
        /// rather than a multiplier on it. Above about eight the model starts
        /// reaching and the last few are noticeably worse than the first.
        /// </remarks>
        public int ContextTitlesPerCondition { get; set; } = ContextTitles.DefaultTitlesPerCondition;

        /// <summary>
        /// Gets or sets how many days an unused set of titles is kept before it is
        /// culled. 0 keeps them forever.
        /// </summary>
        /// <remarks>
        /// A year by default, because these conditions are seasonal: culling the
        /// snowy-evening titles in July because they have gone six months unused
        /// would re-buy them every winter, which is exactly what the cache exists to
        /// prevent. Titles naming a word the vocabulary no longer has are dropped
        /// immediately regardless — those can never match again, so waiting a year
        /// serves nobody.
        /// </remarks>
        public int ContextTitleRetentionDays { get; set; } = ContextTitles.DefaultRetentionDays;

        /// <summary>
        /// Gets or sets how many items a context row may hold.
        /// </summary>
        /// <remarks>
        /// Smaller than the recommendation playlist on purpose. A context row is a
        /// narrow claim — these suit a cold wet evening — and a long one dilutes it
        /// with everything that merely qualifies. Below
        /// <c>ContextRanker.MinimumRowLength</c> matches the row is not drawn at all.
        /// </remarks>
        public int MaxContextRowItems { get; set; } = 20;

        /// <summary>
        /// Gets or sets whether the weather row uses one location for the server or
        /// each viewer's own.
        /// </summary>
        public WeatherLocationMode WeatherLocationMode { get; set; } = WeatherLocationMode.Single;

        /// <summary>
        /// Gets or sets the server's weather location, as a place name to look up.
        /// </summary>
        /// <remarks>
        /// A place name rather than coordinates, because it is what the owner can type
        /// without going and finding anything. It is geocoded once and the result is
        /// cached for the life of the process, so the lookup is not on any hot path.
        /// Also the fallback for any viewer with no location of their own, whichever
        /// mode is selected.
        /// </remarks>
        public string WeatherLocation { get; set; } = "Pittsburgh";

        /// <summary>
        /// Gets or sets each viewer's own weather location. Only consulted under
        /// <see cref="WeatherLocationMode.PerUser"/>; a viewer absent from this list
        /// falls back to <see cref="WeatherLocation"/>.
        /// </summary>
        public UserWeatherLocation[] UserWeatherLocations { get; set; } = Array.Empty<UserWeatherLocation>();

        /// <summary>
        /// Gets or sets the Home Screen Sections order index Curator's category rows
        /// claim. 500 by default.
        /// </summary>
        /// <remarks>
        /// Worth knowing what this number does, because it is not only position:
        /// Home Screen Sections <b>shuffles the rows sharing an order index</b> before
        /// returning them, so every Curator row in one lane lands somewhere different
        /// on every home screen load. That is the cost of the one-lane default — it
        /// keeps Curator out of the way of your other rows, at the price of its own
        /// order being arbitrary.
        /// </remarks>
        public int SectionOrderIndex { get; set; } = SectionConfigMerger.OrderIndex;

        /// <summary>
        /// Gets or sets the order index the two context rows claim. 0 inherits
        /// <see cref="SectionOrderIndex"/>.
        /// </summary>
        /// <remarks>
        /// Separate because these two are not like the others. They are about right
        /// now, they are the same two rows every day, and there are exactly two — so
        /// putting them in their own lane is the one case where Curator has a real
        /// basis for ranking its own rows, and it stops them being shuffled into the
        /// middle of fifty category rows where nobody scrolls.
        /// </remarks>
        public int ContextRowOrderIndex { get; set; }

        /// <summary>
        /// The order index the context rows should actually use.
        /// </summary>
        /// <remarks>
        /// Read this rather than the raw setting, so an inherited lane tracks
        /// <see cref="SectionOrderIndex"/> instead of freezing a copy of whatever it
        /// was when the box was last saved.
        /// </remarks>
        public int EffectiveContextRowOrderIndex
            => ContextRowOrderIndex > 0 ? ContextRowOrderIndex : SectionOrderIndex;

        /// <summary>
        /// The place name to use for one viewer.
        /// </summary>
        /// <param name="userId">The viewer.</param>
        /// <returns>Their place name, or the server's when they have none.</returns>
        public string LocationFor(Guid userId)
        {
            if (WeatherLocationMode == WeatherLocationMode.PerUser && UserWeatherLocations is not null)
            {
                foreach (var entry in UserWeatherLocations)
                {
                    if (entry is not null
                        && entry.UserId == userId
                        && !string.IsNullOrWhiteSpace(entry.Location))
                    {
                        return entry.Location.Trim();
                    }
                }
            }

            return WeatherLocation?.Trim() ?? string.Empty;
        }

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
        /// Gets or sets how Curator's categories reach the home screen.
        /// <para>
        /// <see cref="SectionDelivery.Integrated"/> is the default and answers for
        /// its own rows: it resolves each viewer's playlist by the stored GUID and
        /// returns that playlist's items in playlist order, so per-viewer ordering
        /// reaches the screen and no row depends on a name comparison.
        /// <see cref="SectionDelivery.CollectionSections"/> restores the original
        /// path for an install where the integrated one misbehaves. Integrated
        /// falls back to it on its own when registration fails, so this setting is
        /// for the failures a machine cannot see — rows that render but render
        /// wrongly.
        /// </para>
        /// <para>
        /// Home Screen Sections is required either way: only the plugin that
        /// resolves a row's contents changes.
        /// </para>
        /// </summary>
        public SectionDelivery SectionDelivery { get; set; } = SectionDelivery.Integrated;

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
