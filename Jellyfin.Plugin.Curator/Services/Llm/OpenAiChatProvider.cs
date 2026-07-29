using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Curator.Services.Llm
{
    /// <summary>
    /// Chat Completions provider (POST {base}/chat/completions) covering both the
    /// official OpenAI API and any OpenAI-compatible server (Ollama, LM Studio,
    /// vLLM, OpenRouter). The two differ only in the output-cap parameter name:
    /// the official API uses max_completion_tokens; compatible servers broadly
    /// support the legacy max_tokens.
    /// </summary>
    public sealed class OpenAiChatProvider : ILlmProvider
    {
        /// <summary>The official OpenAI API base, including the version segment.</summary>
        public const string DefaultBaseUrl = "https://api.openai.com/v1";

        private readonly HttpClient _httpClient;
        private readonly string? _apiKey;
        private readonly Uri _endpoint;
        private readonly bool _useLegacyMaxTokens;

        private OpenAiChatProvider(HttpClient httpClient, string model, string? apiKey, string baseUrl, bool useLegacyMaxTokens)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            _httpClient = httpClient;
            ModelId = model;
            _apiKey = apiKey;
            _endpoint = new Uri(baseUrl.TrimEnd('/') + "/chat/completions");
            _useLegacyMaxTokens = useLegacyMaxTokens;
        }

        /// <inheritdoc />
        public string ModelId { get; }

        /// <summary>
        /// Creates a provider for the official OpenAI API.
        /// </summary>
        /// <param name="httpClient">The HTTP client.</param>
        /// <param name="model">The model identifier.</param>
        /// <param name="apiKey">The API key.</param>
        /// <param name="baseUrl">Optional base URL override, e.g. for a proxy.</param>
        /// <returns>The provider.</returns>
        public static OpenAiChatProvider CreateOpenAi(HttpClient httpClient, string model, string apiKey, string? baseUrl = null)
        {
            return new OpenAiChatProvider(
                httpClient,
                model,
                apiKey,
                string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl,
                useLegacyMaxTokens: false);
        }

        /// <summary>
        /// Creates a provider for a generic OpenAI-compatible endpoint. A base URL
        /// is required (e.g. "http://localhost:11434/v1" for Ollama); the API key
        /// is optional since local servers often need none.
        /// </summary>
        /// <param name="httpClient">The HTTP client.</param>
        /// <param name="model">The model identifier.</param>
        /// <param name="baseUrl">The endpoint base, including the version segment.</param>
        /// <param name="apiKey">Optional API key.</param>
        /// <returns>The provider.</returns>
        public static OpenAiChatProvider CreateCompatible(HttpClient httpClient, string model, string baseUrl, string? apiKey = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
            return new OpenAiChatProvider(httpClient, model, apiKey, baseUrl, useLegacyMaxTokens: true);
        }

        /// <inheritdoc />
        public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            if (!string.IsNullOrEmpty(_apiKey))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            var messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt },
            };

            message.Content = _useLegacyMaxTokens
                ? JsonContent.Create(new { model = ModelId, max_tokens = request.MaxOutputTokens, messages })
                : JsonContent.Create(new { model = ModelId, max_completion_tokens = request.MaxOutputTokens, messages });

            using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Chat completions endpoint returned {(int)response.StatusCode}: {Truncate(body)}");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var text = string.Empty;
            string? finishReason = null;
            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("message", out var responseMessage)
                    && responseMessage.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    text = content.GetString() ?? string.Empty;
                }

                if (choice.TryGetProperty("finish_reason", out var finish))
                {
                    finishReason = finish.GetString();
                }
            }

            long inputTokens = 0;
            long outputTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var prompt))
                {
                    inputTokens = prompt.GetInt64();
                }

                if (usage.TryGetProperty("completion_tokens", out var completion))
                {
                    outputTokens = completion.GetInt64();
                }
            }

            return new LlmResult(text, inputTokens, outputTokens, finishReason == "length");
        }

        private static string Truncate(string body)
        {
            return body.Length <= 500 ? body : body[..500] + "…";
        }
    }
}
