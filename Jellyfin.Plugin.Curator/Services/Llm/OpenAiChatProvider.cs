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

        /// <summary>xAI's API base. OpenAI-compatible, down to the request shape.</summary>
        public const string GrokBaseUrl = "https://api.x.ai/v1";

        private readonly HttpClient _httpClient;
        private readonly string? _apiKey;
        private readonly Uri _endpoint;
        private readonly bool _useLegacyMaxTokens;
        private readonly bool _useStructuredOutputs;
        private readonly TimeSpan? _initialRetryDelay;
        private readonly string _providerName;
        private readonly bool _useConversationRouting;

        private OpenAiChatProvider(
            HttpClient httpClient,
            string model,
            string? apiKey,
            string baseUrl,
            bool useLegacyMaxTokens,
            bool useStructuredOutputs,
            string providerName,
            TimeSpan? initialRetryDelay,
            bool useConversationRouting = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            _httpClient = httpClient;
            ModelId = model;
            _apiKey = apiKey;
            _endpoint = new Uri(baseUrl.TrimEnd('/') + "/chat/completions");
            _useLegacyMaxTokens = useLegacyMaxTokens;
            _useStructuredOutputs = useStructuredOutputs;
            _providerName = providerName;
            _initialRetryDelay = initialRetryDelay;
            _useConversationRouting = useConversationRouting;
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
                useLegacyMaxTokens: false,
                useStructuredOutputs: false,
                providerName: "OpenAI",
                initialRetryDelay: null);
        }

        /// <summary>
        /// Creates a provider for xAI's Grok. Same wire format as OpenAI, a
        /// different host — and structured outputs are on, which is the whole
        /// reason it gets its own entry rather than being configured as a generic
        /// compatible endpoint.
        /// </summary>
        /// <param name="httpClient">The HTTP client.</param>
        /// <param name="model">The model identifier, e.g. grok-4.</param>
        /// <param name="apiKey">The xAI API key.</param>
        /// <param name="baseUrl">Optional base URL override, e.g. for a proxy.</param>
        /// <param name="initialRetryDelay">First backoff step; overridden only by tests.</param>
        /// <returns>The provider.</returns>
        public static OpenAiChatProvider CreateGrok(
            HttpClient httpClient,
            string model,
            string apiKey,
            string? baseUrl = null,
            TimeSpan? initialRetryDelay = null)
        {
            return new OpenAiChatProvider(
                httpClient,
                model,
                apiKey,
                string.IsNullOrWhiteSpace(baseUrl) ? GrokBaseUrl : baseUrl,
                useLegacyMaxTokens: false,
                useStructuredOutputs: true,
                providerName: "Grok",
                initialRetryDelay,
                useConversationRouting: true);
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

            // Structured outputs stay OFF here. This path exists for Ollama, LM
            // Studio, vLLM and anything else speaking the dialect, and a server
            // that does not understand response_format rejects the whole request
            // rather than ignoring the field.
            return new OpenAiChatProvider(
                httpClient,
                model,
                apiKey,
                baseUrl,
                useLegacyMaxTokens: true,
                useStructuredOutputs: false,
                providerName: "Chat completions endpoint",
                initialRetryDelay: null);
        }

        /// <inheritdoc />
        public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var payload = BuildRequestBody(request);

            var body = await TransientHttpRetry.SendAsync(
                _httpClient,
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                    if (!string.IsNullOrEmpty(_apiKey))
                    {
                        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                    }

                    // xAI stores cache entries PER SERVER, so without a routing hint
                    // the calls of one run scatter across the fleet and each lands on
                    // a machine that has never seen the prefix. Measured before this
                    // header: 16 of 18 calls reported 128 cached tokens against a
                    // ~28k identical prefix; the two that hit cost $0.0033 and
                    // $0.0002 against $0.077 for the misses. Grouping a run under one
                    // ID pins its calls to one server.
                    if (_useConversationRouting && !string.IsNullOrEmpty(request.ConversationId))
                    {
                        message.Headers.Add("x-grok-conv-id", request.ConversationId);
                    }

                    message.Content = JsonContent.Create(payload);
                    return message;
                },
                (status, failure) => $"{_providerName} returned {(int)status}: {Truncate(failure)}",
                _initialRetryDelay,
                cancellationToken).ConfigureAwait(false);

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

            long promptTokens = 0;
            long outputTokens = 0;
            long cachedTokens = 0;
            long reasoningTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var prompt))
                {
                    promptTokens = prompt.GetInt64();
                }

                if (usage.TryGetProperty("completion_tokens", out var completion))
                {
                    outputTokens = completion.GetInt64();
                }

                // Optional detail blocks. Absent on plain OpenAI-compatible servers,
                // present on OpenAI and xAI.
                if (usage.TryGetProperty("prompt_tokens_details", out var promptDetails)
                    && promptDetails.TryGetProperty("cached_tokens", out var cached))
                {
                    cachedTokens = cached.GetInt64();
                }

                if (usage.TryGetProperty("completion_tokens_details", out var completionDetails)
                    && completionDetails.TryGetProperty("reasoning_tokens", out var reasoning))
                {
                    reasoningTokens = reasoning.GetInt64();
                }
            }

            // prompt_tokens is the TOTAL input including anything served from cache
            // — the same convention as Gemini and the opposite of Anthropic. The
            // cached span comes off so a cache hit does not read as more input.
            return new LlmResult(
                text,
                Math.Max(0, promptTokens - cachedTokens),
                outputTokens,
                finishReason == "length",
                CacheWriteTokens: 0,
                CacheReadTokens: cachedTokens,
                ThinkingTokens: reasoningTokens);
        }

        private object BuildRequestBody(LlmRequest request)
        {
            var messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt },
            };

            if (!_useStructuredOutputs)
            {
                return _useLegacyMaxTokens
                    ? new { model = ModelId, max_tokens = request.MaxOutputTokens, messages }
                    : (object)new { model = ModelId, max_completion_tokens = request.MaxOutputTokens, messages };
            }

            return new
            {
                model = ModelId,
                max_completion_tokens = request.MaxOutputTokens,
                messages,
                response_format = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = request.Shape switch
                        {
                            ResponseShape.PersonalCategories => "curator_personal_categories",
                            ResponseShape.Summaries => "curator_summaries",
                            ResponseShape.SummariesWithTags => "curator_summaries_tagged",
                            ResponseShape.RecommendationOrder => "curator_recommendation_order",
                            _ => "curator_categories",
                        },
                        strict = true,
                        schema = BuildResponseSchema(request.Shape),
                    },
                },
            };
        }

        /// <summary>
        /// Translates the response contract into JSON Schema as OpenAI's strict
        /// mode wants it: lowercase types, <c>additionalProperties: false</c> on
        /// every object, and every property listed in <c>required</c>.
        /// </summary>
        /// <remarks>
        /// Deliberately a separate builder from the Gemini one. The two dialects
        /// look alike and are not: Gemini wants uppercase type names and rejects
        /// nothing for extra keys, while strict mode here refuses a schema that
        /// omits any property from <c>required</c>. Sharing one builder would mean
        /// one of them being subtly wrong.
        /// <para>
        /// This must stay in step with <see cref="Core.Llm.ProposalParser"/> and
        /// the shapes the prompts describe.
        /// </para>
        /// </remarks>
        private static object BuildResponseSchema(ResponseShape shape)
        {
            if (shape is ResponseShape.Summaries or ResponseShape.SummariesWithTags)
            {
                return BuildSummarySchema(shape == ResponseShape.SummariesWithTags);
            }

            if (shape == ResponseShape.RecommendationOrder)
            {
                return new
                {
                    type = "object",
                    properties = new
                    {
                        order = new
                        {
                            type = "array",
                            description = "Every index from the shortlist, once each, best first.",
                            items = new { type = "integer" },
                        },
                    },
                    required = new[] { "order" },
                    additionalProperties = false,
                };
            }

            var category = new
            {
                type = "object",
                properties = new
                {
                    name = new { type = "string", description = "Short, evocative, at most 40 characters, no colons." },
                    description = new { type = "string", description = "One sentence." },
                    members = new
                    {
                        type = "array",
                        description = "Integer indexes from the item list, strongest belonging first.",
                        items = new { type = "integer" },
                    },
                },
                required = new[] { "name", "description", "members" },
                additionalProperties = false,
            };

            var categories = new
            {
                type = "array",
                items = category,
            };

            // A viewer's pass and the shared pass now ask for the same object. The
            // viewer's pass used to also return a "selected" list of existing category
            // names to put on that viewer's home screen; shared categories now go to
            // every viewer, so there is nothing left to select.

            return new
            {
                type = "object",
                properties = new { categories },
                required = new[] { "categories" },
                additionalProperties = false,
            };
        }

        /// <summary>
        /// The condensed-summary contract, in OpenAI strict-mode dialect.
        /// </summary>
        /// <remarks>
        /// Must stay in step with <see cref="Core.Summaries.SummaryParser"/>. The
        /// character budget is deliberately NOT expressed here: JSON Schema has no
        /// maxLength that strict mode honours for this purpose, so the prompt states
        /// it and the parser enforces it. The schema's job is only to guarantee the
        /// shape.
        /// </remarks>
        private static object BuildSummarySchema(bool includeTags)
        {
            var i = new { type = "integer", description = "The item's integer index from the input list." };
            var text = new { type = "string", description = "The compressed description." };

            // Strict mode requires every declared property, so "t" is added only when
            // the prompt actually asks for it. Declaring it always would demand a tag
            // list from a pass that was never told to produce one; omitting it when
            // the prompt does ask leaves the model with no legal place to put the
            // tags, and it writes them into "s" instead.
            object summary = includeTags
                ? new
                {
                    type = "object",
                    properties = new
                    {
                        i,
                        s = text,
                        t = new
                        {
                            type = "array",
                            description = "Consolidated tags describing what watching it is like; may be empty.",
                            items = new { type = "string" },
                        },
                    },
                    required = new[] { "i", "s", "t" },
                    additionalProperties = false,
                }
                : new
                {
                    type = "object",
                    properties = new { i, s = text },
                    required = new[] { "i", "s" },
                    additionalProperties = false,
                };

            return new
            {
                type = "object",
                properties = new
                {
                    summaries = new
                    {
                        type = "array",
                        items = summary,
                    },
                },
                required = new[] { "summaries" },
                additionalProperties = false,
            };
        }

        private static string Truncate(string body)
        {
            return body.Length <= 500 ? body : body[..500] + "…";
        }
    }
}
