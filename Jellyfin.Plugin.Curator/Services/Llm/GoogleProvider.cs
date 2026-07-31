using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Curator.Services.Llm
{
    /// <summary>
    /// Google Gemini provider (POST {base}/models/{model}:generateContent).
    /// <para>
    /// Gemini is also reachable through the OpenAI-compatible provider, but that
    /// path gives up the one thing worth having here: <c>responseSchema</c>. The
    /// model is constrained to the exact object the parser expects, so the failure
    /// mode that cost this plugin whole runs — a stray quote or a trailing sentence
    /// making the JSON unparseable — cannot occur. Prefer this over the
    /// compatibility endpoint.
    /// </para>
    /// </summary>
    public sealed class GoogleProvider : ILlmProvider
    {
        /// <summary>The default API base, including the version segment.</summary>
        public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta";


        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly Uri _endpoint;
        private readonly bool _enableThinking;
        private readonly TimeSpan _initialRetryDelay;

        /// <param name="httpClient">The HTTP client.</param>
        /// <param name="model">The model identifier.</param>
        /// <param name="apiKey">The API key.</param>
        /// <param name="baseUrl">Optional base URL override.</param>
        /// <param name="enableThinking">Whether the model may think before answering.</param>
        /// <param name="initialRetryDelay">
        /// First backoff step for transient failures. Overridden only by tests, so
        /// exercising the retry path does not cost them the real five seconds.
        /// </param>
        public GoogleProvider(
            HttpClient httpClient,
            string model,
            string apiKey,
            string? baseUrl = null,
            bool enableThinking = true,
            TimeSpan? initialRetryDelay = null)
        {
            _initialRetryDelay = initialRetryDelay ?? TransientHttpRetry.DefaultInitialDelay;
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            _httpClient = httpClient;

            // Google's own docs write model ids both ways ("gemini-2.5-flash" and
            // "models/gemini-2.5-flash"); the path segment already supplies the
            // prefix, so accept either rather than 404 on the second.
            ModelId = model.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? model["models/".Length..]
                : model;

            _apiKey = apiKey;
            _enableThinking = enableThinking;
            var basePart = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');
            _endpoint = new Uri(basePart + "/models/" + ModelId + ":generateContent");
        }

        /// <inheritdoc />
        public string ModelId { get; }

        /// <inheritdoc />
        public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var body = await SendWithRetriesAsync(request, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            return ParseResponse(document.RootElement);
        }

        /// <summary>
        /// Posts the request, retrying the transient failures Gemini produces in
        /// normal use. See <see cref="TransientHttpRetry"/> for why, and for the
        /// line between transient and permanent.
        /// </summary>
        private Task<string> SendWithRetriesAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            var payload = BuildRequestBody(request);

            return TransientHttpRetry.SendAsync(
                _httpClient,
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                    message.Headers.Add("x-goog-api-key", _apiKey);
                    message.Content = JsonContent.Create(payload);
                    return message;
                },
                (status, body) => $"Google API returned {(int)status}: {Truncate(body)}",
                _initialRetryDelay,
                cancellationToken);
        }

        private object BuildRequestBody(LlmRequest request)
        {
            return new
            {
                systemInstruction = new
                {
                    parts = new[] { new { text = request.SystemPrompt } },
                },

                // Two parts rather than one concatenated string: it keeps the item
                // list at a stable prefix boundary, which is what Gemini's implicit
                // context caching keys on across the per-user passes.
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = BuildParts(request),
                    },
                },
                safetySettings = SafetySettings,
                generationConfig = BuildGenerationConfig(request),
            };
        }

        /// <summary>
        /// Turns the content filters off for every category Gemini lets us set.
        /// </summary>
        /// <remarks>
        /// Unlike the other providers, Gemini applies safety blocking by default,
        /// and it applies it to <em>our</em> input: the prompt is a list of the
        /// user's own films and series with their synopses. A library containing
        /// horror, true crime, war films or anything with an adult certificate will
        /// trip a filter sooner or later, and when it does the whole batch comes
        /// back with no candidate — one blocked response ends a pass that has
        /// already been paid for.
        /// <para>
        /// Nothing here is generative in the risky sense: the model is naming
        /// groupings of media the user already owns and has chosen to catalogue.
        /// Blocking it protects nobody and only makes the plugin unreliable in
        /// proportion to how interesting the library is.
        /// </para>
        /// </remarks>
        private static readonly object[] SafetySettings =
        [
            new { category = "HARM_CATEGORY_HARASSMENT", threshold = "OFF" },
            new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "OFF" },
            new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "OFF" },
            new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "OFF" },
        ];

        private static object[] BuildParts(LlmRequest request)
        {
            if (string.IsNullOrEmpty(request.CacheablePrefix))
            {
                return [new { text = request.VariableSuffix }];
            }

            return
            [
                new { text = request.CacheablePrefix },
                new { text = request.VariableSuffix },
            ];
        }

        private object BuildGenerationConfig(LlmRequest request)
        {
            // Thinking is left at the model's own default when enabled rather than
            // pinned to a budget — Gemini 2.5 sizes it dynamically, and the plugin's
            // setting is a yes/no. When disabled, budget 0 turns it off; note the Pro
            // models refuse a zero budget and will return 400, which is the API
            // telling you the same thing this plugin's own docs do: leave it on.
            if (_enableThinking)
            {
                return new
                {
                    maxOutputTokens = request.MaxOutputTokens,
                    responseMimeType = "application/json",
                    responseSchema = BuildResponseSchema(request.Shape),
                };
            }

            return new
            {
                maxOutputTokens = request.MaxOutputTokens,
                responseMimeType = "application/json",
                responseSchema = BuildResponseSchema(request.Shape),
                thinkingConfig = new { thinkingBudget = 0 },
            };
        }

        /// <summary>
        /// Translates the response contract into Gemini's schema dialect: an OpenAPI
        /// subset whose type names are the uppercase proto enum values, with the
        /// non-standard <c>propertyOrdering</c> fixing generation order.
        /// </summary>
        /// <remarks>
        /// This must stay in step with <see cref="Core.Llm.ProposalParser"/> and the
        /// shapes the prompts describe. The schema is a second enforcement point, not
        /// the only one: every other provider still depends on the prose contract, so
        /// changing one without the other silently splits the two apart.
        /// </remarks>
        private static object BuildResponseSchema(ResponseShape shape)
        {
            if (shape == ResponseShape.Summaries)
            {
                return BuildSummarySchema();
            }

            var category = new
            {
                type = "OBJECT",
                properties = new
                {
                    name = new { type = "STRING", description = "Short, evocative, at most 40 characters, no colons." },
                    description = new { type = "STRING", description = "One sentence." },
                    members = new
                    {
                        type = "ARRAY",
                        description = "Integer indexes from the item list, strongest belonging first.",
                        items = new { type = "INTEGER" },
                    },
                },
                required = new[] { "name", "description", "members" },
                propertyOrdering = new[] { "name", "description", "members" },
            };

            // A viewer's pass and the shared pass now ask for the same object. The
            // viewer's pass used to also return a "selected" list of existing category
            // names to put on that viewer's home screen; shared categories now go to
            // every viewer, so there is nothing left to select.
            return new
            {
                type = "OBJECT",
                properties = new
                {
                    categories = new { type = "ARRAY", items = category },
                },
                required = new[] { "categories" },
                propertyOrdering = new[] { "categories" },
            };
        }

        /// <summary>
        /// The condensed-summary contract in Gemini's dialect.
        /// </summary>
        /// <remarks>
        /// Separate from the OpenAI builder for the reason the class comment already
        /// gives: the dialects look alike and are not. Uppercase type names here, no
        /// <c>additionalProperties</c>, and <c>propertyOrdering</c> so the index is
        /// generated before the text — a model that writes the summary first has to
        /// hold the index in mind across the whole sentence, and that is where
        /// off-by-one answers come from.
        /// </remarks>
        private static object BuildSummarySchema()
        {
            var summary = new
            {
                type = "OBJECT",
                properties = new
                {
                    i = new { type = "INTEGER", description = "The item's integer index from the input list." },
                    s = new { type = "STRING", description = "The compressed description." },
                },
                required = new[] { "i", "s" },
                propertyOrdering = new[] { "i", "s" },
            };

            return new
            {
                type = "OBJECT",
                properties = new
                {
                    summaries = new { type = "ARRAY", items = summary },
                },
                required = new[] { "summaries" },
                propertyOrdering = new[] { "summaries" },
            };
        }

        private static LlmResult ParseResponse(JsonElement root)
        {
            // A prompt rejected outright never reaches a candidate.
            if (root.TryGetProperty("promptFeedback", out var feedback)
                && feedback.TryGetProperty("blockReason", out var blockReason))
            {
                throw new InvalidOperationException(
                    $"Google blocked the request (blockReason: {blockReason.GetString()}).");
            }

            var text = string.Empty;
            string? finishReason = null;

            if (root.TryGetProperty("candidates", out var candidates)
                && candidates.ValueKind == JsonValueKind.Array
                && candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];
                if (candidate.TryGetProperty("finishReason", out var finish))
                {
                    finishReason = finish.GetString();
                }

                if (candidate.TryGetProperty("content", out var content)
                    && content.TryGetProperty("parts", out var parts)
                    && parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        // Thought summaries come back as parts too, flagged; they are
                        // commentary, not the answer, and must not reach the parser.
                        if (part.TryGetProperty("thought", out var thought)
                            && thought.ValueKind == JsonValueKind.True)
                        {
                            continue;
                        }

                        if (part.TryGetProperty("text", out var textElement)
                            && textElement.ValueKind == JsonValueKind.String)
                        {
                            text += textElement.GetString();
                        }
                    }
                }
            }

            // SAFETY and RECITATION discard the candidate's content, so an empty
            // answer with one of those reasons is a refusal, not an empty library.
            if (finishReason is "SAFETY" or "RECITATION" or "PROHIBITED_CONTENT" or "BLOCKLIST")
            {
                throw new InvalidOperationException($"Google declined the request (finishReason: {finishReason}).");
            }

            long promptTokens = 0;
            long outputTokens = 0;
            long thoughtTokens = 0;
            long cacheRead = 0;
            if (root.TryGetProperty("usageMetadata", out var usage))
            {
                if (usage.TryGetProperty("promptTokenCount", out var prompt))
                {
                    promptTokens = prompt.GetInt64();
                }

                if (usage.TryGetProperty("candidatesTokenCount", out var output))
                {
                    outputTokens = output.GetInt64();
                }

                // Thinking is billed as output but reported separately, and is left
                // out of candidatesTokenCount. Adding it keeps the cost line honest.
                if (usage.TryGetProperty("thoughtsTokenCount", out var thoughts))
                {
                    thoughtTokens = thoughts.GetInt64();
                }

                if (usage.TryGetProperty("cachedContentTokenCount", out var cached))
                {
                    cacheRead = cached.GetInt64();
                }
            }

            // Unlike Anthropic, promptTokenCount is the TOTAL input including the
            // cached span. LlmResult.InputTokens means the uncached remainder, so the
            // cached portion comes off here — otherwise a cache hit would read as an
            // input-token increase in the cost log.
            var uncachedInput = Math.Max(0, promptTokens - cacheRead);

            return new LlmResult(
                text,
                uncachedInput,
                outputTokens + thoughtTokens,
                finishReason == "MAX_TOKENS",
                CacheWriteTokens: 0,
                CacheReadTokens: cacheRead,
                ThinkingTokens: thoughtTokens);
        }

        private static string Truncate(string body)
        {
            return body.Length <= 500 ? body : body[..500] + "…";
        }
    }
}
