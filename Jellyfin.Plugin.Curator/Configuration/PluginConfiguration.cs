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

        /// <summary>
        /// Gets or sets the ceiling on how many categories a run may produce after reconciliation.
        /// </summary>
        public int MaxCategories { get; set; } = 15;

        /// <summary>
        /// Gets or sets the minimum member count below which a category is discarded.
        /// </summary>
        public int MinCategorySize { get; set; } = 5;

        /// <summary>
        /// Gets or sets the hard token cap per run (input + output). 0 disables the cap.
        /// </summary>
        public long TokenBudget { get; set; } = 2_000_000;

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
        /// Gets or sets the users playlists are generated for. Empty means all users.
        /// </summary>
        public Guid[] TargetUsers { get; set; } = Array.Empty<Guid>();

        /// <summary>
        /// Gets or sets a value indicating whether newly created home screen sections
        /// are enabled for target users automatically.
        /// </summary>
        public bool AutoEnableSections { get; set; } = true;
    }
}
