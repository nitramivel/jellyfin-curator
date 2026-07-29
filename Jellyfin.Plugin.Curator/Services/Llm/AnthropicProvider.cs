using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Curator.Services.Llm
{
    /// <summary>
    /// Anthropic Messages API provider (POST {base}/messages).
    /// </summary>
    public sealed class AnthropicProvider : ILlmProvider
    {
        /// <summary>The default API base, including the version segment.</summary>
        public const string DefaultBaseUrl = "https://api.anthropic.com/v1";

        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly Uri _endpoint;

        public AnthropicProvider(HttpClient httpClient, string model, string apiKey, string? baseUrl = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            _httpClient = httpClient;
            ModelId = model;
            _apiKey = apiKey;
            var basePart = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');
            _endpoint = new Uri(basePart + "/messages");
        }

        /// <inheritdoc />
        public string ModelId { get; }

        /// <inheritdoc />
        public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            message.Headers.Add("x-api-key", _apiKey);
            message.Headers.Add("anthropic-version", "2023-06-01");
            message.Content = JsonContent.Create(new
            {
                model = ModelId,
                max_tokens = request.MaxOutputTokens,
                system = request.SystemPrompt,
                messages = new[]
                {
                    new { role = "user", content = request.UserPrompt },
                },
            });

            using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Anthropic API returned {(int)response.StatusCode}: {Truncate(body)}");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var stopReason = root.TryGetProperty("stop_reason", out var stop) ? stop.GetString() : null;
            if (stopReason == "refusal")
            {
                throw new InvalidOperationException("Anthropic declined the request (stop_reason: refusal).");
            }

            var text = string.Empty;
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var type)
                        && type.ValueEquals("text")
                        && block.TryGetProperty("text", out var textElement))
                    {
                        text += textElement.GetString();
                    }
                }
            }

            long inputTokens = 0;
            long outputTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input_tokens", out var input))
                {
                    inputTokens = input.GetInt64();
                }

                if (usage.TryGetProperty("output_tokens", out var output))
                {
                    outputTokens = output.GetInt64();
                }
            }

            return new LlmResult(text, inputTokens, outputTokens, stopReason == "max_tokens");
        }

        private static string Truncate(string body)
        {
            return body.Length <= 500 ? body : body[..500] + "…";
        }
    }
}
