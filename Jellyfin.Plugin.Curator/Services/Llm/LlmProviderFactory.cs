using System;
using System.Net.Http;
using Jellyfin.Plugin.Curator.Configuration;

namespace Jellyfin.Plugin.Curator.Services.Llm
{
    /// <summary>
    /// Builds the configured <see cref="ILlmProvider"/> from plugin configuration.
    /// </summary>
    public sealed class LlmProviderFactory
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public LlmProviderFactory(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// The named HttpClient used for LLM calls; registered with a long timeout
        /// because large batches take a while.
        /// </summary>
        public const string HttpClientName = "CuratorLlm";

        /// <summary>
        /// Creates the provider selected by configuration.
        /// </summary>
        /// <param name="config">The plugin configuration.</param>
        /// <returns>The provider.</returns>
        /// <exception cref="InvalidOperationException">Configuration is incomplete for the selected provider.</exception>
        public ILlmProvider Create(PluginConfiguration config)
        {
            ArgumentNullException.ThrowIfNull(config);

            if (string.IsNullOrWhiteSpace(config.Model))
            {
                throw new InvalidOperationException("Curator: no model configured. Set one in the plugin configuration.");
            }

            var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            httpClient.Timeout = TimeSpan.FromMinutes(10);

            switch (config.Provider)
            {
                case LlmProviderKind.Anthropic:
                    RequireApiKey(config);
                    return new AnthropicProvider(httpClient, config.Model, config.ApiKey, NullIfEmpty(config.BaseUrl));

                case LlmProviderKind.OpenAi:
                    RequireApiKey(config);
                    return OpenAiChatProvider.CreateOpenAi(httpClient, config.Model, config.ApiKey, NullIfEmpty(config.BaseUrl));

                case LlmProviderKind.OpenAiCompatible:
                    if (string.IsNullOrWhiteSpace(config.BaseUrl))
                    {
                        throw new InvalidOperationException(
                            "Curator: the OpenAI-compatible provider requires a base URL (e.g. http://localhost:11434/v1).");
                    }

                    return OpenAiChatProvider.CreateCompatible(httpClient, config.Model, config.BaseUrl, NullIfEmpty(config.ApiKey));

                default:
                    throw new InvalidOperationException($"Curator: unknown provider {config.Provider}.");
            }
        }

        private static void RequireApiKey(PluginConfiguration config)
        {
            if (string.IsNullOrWhiteSpace(config.ApiKey))
            {
                throw new InvalidOperationException(
                    $"Curator: the {config.Provider} provider requires an API key.");
            }
        }

        private static string? NullIfEmpty(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
