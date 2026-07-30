using System;
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
    /// Plugin configuration. Category definitions are NOT stored here — they live as
    /// individual JSON files in the plugin data directory behind ICategoryStore.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets the LLM provider.
        /// </summary>
        public LlmProviderKind Provider { get; set; } = LlmProviderKind.Anthropic;

        /// <summary>
        /// Gets or sets the model identifier sent to the provider.
        /// </summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the provider API key.
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets an optional base URL override (Ollama, LM Studio, vLLM, OpenRouter, proxies).
        /// Empty means the provider's default endpoint.
        /// </summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the number of library items sent per LLM request.
        /// </summary>
        public int BatchSize { get; set; } = 150;

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
        /// </summary>
        public int MaxSharedCategories { get; set; } = 10;

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
        /// </summary>
        public int MaxCategoryMembers { get; set; } = 20;

        /// <summary>
        /// Gets or sets the hard token cap per run (input + output). 0 disables the cap.
        /// </summary>
        public long TokenBudget { get; set; } = 2_000_000;

        /// <summary>
        /// Gets or sets the maximum output tokens requested per LLM call.
        /// </summary>
        public int MaxOutputTokens { get; set; } = 16000;

        /// <summary>
        /// Gets or sets the provider's input price in USD per million tokens,
        /// used only for the estimated-cost log line. 0 logs token counts without cost.
        /// </summary>
        public decimal InputCostPerMillion { get; set; }

        /// <summary>
        /// Gets or sets the provider's output price in USD per million tokens,
        /// used only for the estimated-cost log line. 0 logs token counts without cost.
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
        /// Gets or sets the smallest personal category kept, in members. Also the
        /// number the per-viewer prompt asks the model to meet. Minimum 2.
        /// <para>
        /// Keep this at or below <see cref="MinSharedCategorySize"/>. A shared
        /// category is drawn from the whole library and can reasonably be asked for
        /// four members; a personal one is grounded in one viewer's history, and a
        /// viewer with a handful of watched items cannot honestly support a category
        /// that size. Holding both to the shared floor silently threw away most
        /// invented categories.
        /// </para>
        /// </summary>
        public int MinPersonalCategorySize { get; set; } = 2;

        /// <summary>
        /// Gets or sets the most personal categories kept per user. 0 means no cap.
        /// </summary>
        public int MaxPersonalCategories { get; set; } = 6;

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
