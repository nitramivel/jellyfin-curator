namespace Jellyfin.Plugin.Curator.Configuration
{
    /// <summary>
    /// One saved way of calling a model: the provider, the model identifier, the
    /// credential, and what that combination costs.
    /// <para>
    /// A profile is deliberately self-contained. Pricing lives here rather than on
    /// <see cref="PluginConfiguration"/> because a profile that carried someone
    /// else's prices would report every run at the wrong number — the config page
    /// already warned owners to change the prices by hand whenever they changed
    /// provider, and a list you are meant to switch between turns that occasional
    /// mistake into the normal case. Switching profile switches the price with it.
    /// </para>
    /// <para>
    /// Everything about *how* a call is shaped — thinking, output cap, batching,
    /// token budget — stays global on <see cref="PluginConfiguration"/>. Those are
    /// properties of the run, not of the credential.
    /// </para>
    /// </summary>
    /// <remarks>
    /// A mutable class with a parameterless constructor, not a record: Jellyfin
    /// persists plugin configuration with <see cref="System.Xml.Serialization.XmlSerializer"/>,
    /// which requires both.
    /// </remarks>
    public class ModelProfile
    {
        /// <summary>
        /// Gets or sets the stable identifier for this profile.
        /// <para>
        /// Referenced by <see cref="PluginConfiguration.DefaultModelProfileId"/>, and
        /// by the per-task assignments planned on top of this list. It must survive
        /// renaming and reordering, so nothing may key a profile by its name or its
        /// position — both are things the owner can change at will.
        /// </para>
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the display name shown in the profile list, e.g. "Grok 4.5"
        /// or "Local Llama". Free text; duplicates are legal and harmless because
        /// <see cref="Id"/> is what anything actually resolves against.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the LLM provider this profile calls.
        /// </summary>
        public LlmProviderKind Provider { get; set; } = LlmProviderKind.Anthropic;

        /// <summary>
        /// Gets or sets the model identifier sent to the provider.
        /// </summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the provider API key. Stored in plaintext in the plugin
        /// configuration file, exactly as the single key it replaces was.
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets an optional base URL override. Empty means the provider's
        /// default endpoint; required for <see cref="LlmProviderKind.OpenAiCompatible"/>.
        /// </summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets this profile's input price in USD per million tokens, used
        /// only for the estimated-cost log line. 0 logs token counts without cost.
        /// </summary>
        public decimal InputCostPerMillion { get; set; }

        /// <summary>
        /// Gets or sets this profile's cache-read price in USD per million tokens.
        /// Blank falls back to half <see cref="InputCostPerMillion"/>.
        /// </summary>
        public decimal CachedInputCostPerMillion { get; set; }

        /// <summary>
        /// Gets or sets this profile's output price in USD per million tokens, used
        /// only for the estimated-cost log line. 0 logs token counts without cost.
        /// </summary>
        public decimal OutputCostPerMillion { get; set; }
    }
}
