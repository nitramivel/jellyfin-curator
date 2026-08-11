using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Core.Context;
using Jellyfin.Plugin.Curator.Services.Llm;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    public class LlmProviderTests
    {
        /// <summary>
        /// Captures the outgoing request and returns a canned response. All provider
        /// tests run against this — no live API calls.
        /// </summary>
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _statusCode;
            private readonly string _responseBody;

            public StubHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
            {
                _responseBody = responseBody;
                _statusCode = statusCode;
            }

            public HttpRequestMessage? Request { get; private set; }

            public string? RequestBody { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Request = request;
                RequestBody = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(_statusCode)
                {
                    Content = new StringContent(_responseBody),
                };
            }
        }

        /// <param name="Status">The status to return.</param>
        /// <param name="Body">The response body.</param>
        /// <param name="RetryAfterSeconds">A Retry-After header to set, when the case needs one.</param>
        private sealed record Reply(HttpStatusCode Status, string Body = "{}", int? RetryAfterSeconds = null);

        /// <summary>
        /// Plays a queued sequence of replies and counts the sends, for the retry
        /// paths. The last reply repeats once the queue is drained.
        /// </summary>
        /// <remarks>
        /// A fresh <see cref="HttpResponseMessage"/> is built per send rather than
        /// replaying one instance: the provider disposes each response it reads, so
        /// a repeated instance would come back disposed on the second attempt.
        /// </remarks>
        private sealed class SequenceHandler : HttpMessageHandler
        {
            private readonly Queue<Reply> _replies;
            private Reply _last = null!;

            public SequenceHandler(params Reply[] replies)
            {
                _replies = new Queue<Reply>(replies);
            }

            public int SendCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                SendCount++;
                if (_replies.Count > 0)
                {
                    _last = _replies.Dequeue();
                }

                var response = new HttpResponseMessage(_last.Status)
                {
                    Content = new StringContent(_last.Body),
                };

                if (_last.RetryAfterSeconds is { } seconds)
                {
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
                }

                return Task.FromResult(response);
            }
        }

        /// <summary>A retry delay short enough that tests do not wait on it.</summary>
        private static readonly TimeSpan NoDelay = TimeSpan.FromMilliseconds(1);

        private static readonly LlmRequest Request = new("SYSTEM", "ITEMS", "SUFFIX", 4096);

        private const string AnthropicResponse =
            """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5",
             "content":[{"type":"text","text":"{\"categories\":[]}"}],
             "stop_reason":"end_turn",
             "usage":{"input_tokens":1234,"output_tokens":56}}
            """;

        private const string GoogleResponse =
            """
            {"candidates":[{"content":{"parts":[{"text":"{\"categories\":[]}"}],"role":"model"},
                            "finishReason":"STOP","index":0}],
             "usageMetadata":{"promptTokenCount":1234,"candidatesTokenCount":56,"totalTokenCount":1290},
             "modelVersion":"gemini-2.5-flash"}
            """;

        private const string OpenAiResponse =
            """
            {"id":"chatcmpl-1","object":"chat.completion",
             "choices":[{"index":0,"message":{"role":"assistant","content":"{\"categories\":[]}"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1234,"completion_tokens":56}}
            """;

        [Fact]
        public async Task Anthropic_SendsCorrectRequestShape()
        {
            var handler = new StubHandler(AnthropicResponse);
            var provider = new AnthropicProvider(new HttpClient(handler), "claude-opus-5", "sk-test");

            await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal("https://api.anthropic.com/v1/messages", handler.Request!.RequestUri!.ToString());
            Assert.Equal("sk-test", Assert.Single(handler.Request.Headers.GetValues("x-api-key")));
            Assert.Equal("2023-06-01", Assert.Single(handler.Request.Headers.GetValues("anthropic-version")));

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.Equal("claude-opus-5", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(4096, body.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Equal("SYSTEM", body.RootElement.GetProperty("system").GetString());

            // Thinking is on by default. It is the output CAP that has to accommodate
            // it — disabling it makes recent models write their reasoning into the
            // visible response instead, which is worse on both counts.
            Assert.Equal(
                "adaptive",
                body.RootElement.GetProperty("thinking").GetProperty("type").GetString());

            var message = Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray());
            Assert.Equal("user", message.GetProperty("role").GetString());

            var blocks = message.GetProperty("content").EnumerateArray().ToList();
            Assert.Equal(2, blocks.Count);
            Assert.Equal("ITEMS", blocks[0].GetProperty("text").GetString());
            Assert.Equal("SUFFIX", blocks[1].GetProperty("text").GetString());

            // Only the reusable half is marked, and with a TTL long enough to
            // survive the gap between per-user passes over the same batch.
            var cacheControl = blocks[0].GetProperty("cache_control");
            Assert.Equal("ephemeral", cacheControl.GetProperty("type").GetString());
            Assert.Equal("1h", cacheControl.GetProperty("ttl").GetString());
            Assert.False(blocks[1].TryGetProperty("cache_control", out _));
        }

        [Fact]
        public async Task Anthropic_ThinkingDisabled_IsSentExplicitly()
        {
            var handler = new StubHandler(AnthropicResponse);
            var provider = new AnthropicProvider(
                new HttpClient(handler), "claude-sonnet-5", "sk-test", baseUrl: null, enableThinking: false);

            await provider.CompleteAsync(Request, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.Equal(
                "disabled",
                body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        }

        [Fact]
        public async Task Anthropic_EmptyCacheablePrefix_SendsSingleUnmarkedBlock()
        {
            var handler = new StubHandler(AnthropicResponse);
            var provider = new AnthropicProvider(new HttpClient(handler), "claude-opus-5", "sk-test");

            await provider.CompleteAsync(new LlmRequest("SYSTEM", string.Empty, "SUFFIX", 4096), CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var message = Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray());
            var block = Assert.Single(message.GetProperty("content").EnumerateArray());
            Assert.Equal("SUFFIX", block.GetProperty("text").GetString());
            Assert.False(block.TryGetProperty("cache_control", out _));
        }

        [Fact]
        public async Task Anthropic_ParsesCacheUsage()
        {
            const string cached =
                """
                {"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-5",
                 "content":[{"type":"text","text":"{\"categories\":[]}"}],
                 "stop_reason":"end_turn",
                 "usage":{"input_tokens":12,"output_tokens":56,
                          "cache_creation_input_tokens":0,"cache_read_input_tokens":4321}}
                """;

            var provider = new AnthropicProvider(
                new HttpClient(new StubHandler(cached)), "claude-sonnet-5", "sk-test");

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal(12, result.InputTokens);
            Assert.Equal(0, result.CacheWriteTokens);
            Assert.Equal(4321, result.CacheReadTokens);
        }

        [Fact]
        public async Task OpenAi_ConcatenatesPrefixAndSuffix()
        {
            var handler = new StubHandler(OpenAiResponse);
            var provider = OpenAiChatProvider.CreateOpenAi(new HttpClient(handler), "gpt-4o", "sk-test");

            await provider.CompleteAsync(Request, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var userMessage = body.RootElement.GetProperty("messages").EnumerateArray()
                .First(m => m.GetProperty("role").GetString() == "user");

            // No cache_control equivalent here, so the two halves just concatenate.
            Assert.Equal("ITEMSSUFFIX", userMessage.GetProperty("content").GetString());
        }

        [Fact]
        public async Task Anthropic_ParsesTextAndUsage()
        {
            var provider = new AnthropicProvider(
                new HttpClient(new StubHandler(AnthropicResponse)), "claude-opus-5", "sk-test");

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal("""{"categories":[]}""", result.Text);
            Assert.Equal(1234, result.InputTokens);
            Assert.Equal(56, result.OutputTokens);
            Assert.False(result.Truncated);
        }

        [Fact]
        public async Task Anthropic_MaxTokensStop_IsReportedAsTruncated()
        {
            var response = AnthropicResponse.Replace("\"end_turn\"", "\"max_tokens\"", StringComparison.Ordinal);
            var provider = new AnthropicProvider(
                new HttpClient(new StubHandler(response)), "claude-opus-5", "sk-test");

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.True(result.Truncated);
        }

        [Fact]
        public async Task Anthropic_Refusal_Throws()
        {
            var response = AnthropicResponse.Replace("\"end_turn\"", "\"refusal\"", StringComparison.Ordinal);
            var provider = new AnthropicProvider(
                new HttpClient(new StubHandler(response)), "claude-opus-5", "sk-test");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.CompleteAsync(Request, CancellationToken.None));
        }

        [Fact]
        public async Task Anthropic_HttpError_ThrowsWithStatus()
        {
            var provider = new AnthropicProvider(
                new HttpClient(new StubHandler("""{"error":{"message":"bad key"}}""", HttpStatusCode.Unauthorized)),
                "claude-opus-5",
                "sk-test");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(
                () => provider.CompleteAsync(Request, CancellationToken.None));
            Assert.Contains("401", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Anthropic_BaseUrlOverride_IsUsed()
        {
            var handler = new StubHandler(AnthropicResponse);
            var provider = new AnthropicProvider(
                new HttpClient(handler), "claude-opus-5", "sk-test", "https://proxy.example.com/v1/");

            await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal("https://proxy.example.com/v1/messages", handler.Request!.RequestUri!.ToString());
        }

        [Fact]
        public async Task OpenAi_SendsBearerAndMaxCompletionTokens()
        {
            var handler = new StubHandler(OpenAiResponse);
            var provider = OpenAiChatProvider.CreateOpenAi(new HttpClient(handler), "gpt-test", "sk-test");

            await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal("https://api.openai.com/v1/chat/completions", handler.Request!.RequestUri!.ToString());
            Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
            Assert.Equal("sk-test", handler.Request.Headers.Authorization.Parameter);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.Equal(4096, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
            Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
            var messages = body.RootElement.GetProperty("messages");
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            Assert.Equal("SYSTEM", messages[0].GetProperty("content").GetString());
            Assert.Equal("user", messages[1].GetProperty("role").GetString());
        }

        [Fact]
        public async Task Compatible_SendsLegacyMaxTokensToConfiguredBase()
        {
            var handler = new StubHandler(OpenAiResponse);
            var provider = OpenAiChatProvider.CreateCompatible(
                new HttpClient(handler), "llama3", "http://localhost:11434/v1");

            await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal("http://localhost:11434/v1/chat/completions", handler.Request!.RequestUri!.ToString());
            Assert.Null(handler.Request.Headers.Authorization);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.Equal(4096, body.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.False(body.RootElement.TryGetProperty("max_completion_tokens", out _));
        }

        [Fact]
        public async Task OpenAi_ParsesTextAndUsage()
        {
            var provider = OpenAiChatProvider.CreateOpenAi(
                new HttpClient(new StubHandler(OpenAiResponse)), "gpt-test", "sk-test");

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal("""{"categories":[]}""", result.Text);
            Assert.Equal(1234, result.InputTokens);
            Assert.Equal(56, result.OutputTokens);
            Assert.False(result.Truncated);
        }

        [Fact]
        public async Task OpenAi_LengthFinish_IsReportedAsTruncated()
        {
            var response = OpenAiResponse.Replace("\"stop\"", "\"length\"", StringComparison.Ordinal);
            var provider = OpenAiChatProvider.CreateOpenAi(
                new HttpClient(new StubHandler(response)), "gpt-test", "sk-test");

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.True(result.Truncated);
        }

        [Fact]
        public async Task Google_SendsCorrectRequestShape()
        {
            var handler = new StubHandler(GoogleResponse);
            var provider = new GoogleProvider(new HttpClient(handler), "gemini-2.5-flash", "AIza-test");

            await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal(
                "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent",
                handler.Request!.RequestUri!.ToString());
            Assert.Equal("AIza-test", Assert.Single(handler.Request.Headers.GetValues("x-goog-api-key")));

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var root = body.RootElement;

            var systemPart = Assert.Single(
                root.GetProperty("systemInstruction").GetProperty("parts").EnumerateArray());
            Assert.Equal("SYSTEM", systemPart.GetProperty("text").GetString());

            var content = Assert.Single(root.GetProperty("contents").EnumerateArray());
            Assert.Equal("user", content.GetProperty("role").GetString());

            // Kept as two parts so the reusable half stays at a stable prefix
            // boundary for implicit context caching.
            var parts = content.GetProperty("parts").EnumerateArray().ToList();
            Assert.Equal(2, parts.Count);
            Assert.Equal("ITEMS", parts[0].GetProperty("text").GetString());
            Assert.Equal("SUFFIX", parts[1].GetProperty("text").GetString());

            var generation = root.GetProperty("generationConfig");
            Assert.Equal(4096, generation.GetProperty("maxOutputTokens").GetInt32());
            Assert.Equal("application/json", generation.GetProperty("responseMimeType").GetString());
        }

        [Fact]
        public async Task Google_ModelIdWithModelsPrefix_IsNotDoubled()
        {
            var handler = new StubHandler(GoogleResponse);
            var provider = new GoogleProvider(new HttpClient(handler), "models/gemini-2.5-pro", "AIza-test");

            await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal(
                "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:generateContent",
                handler.Request!.RequestUri!.ToString());
            Assert.Equal("gemini-2.5-pro", provider.ModelId);
        }

        [Fact]
        public async Task Google_DiscoverySchema_MatchesTheParserContract()
        {
            var handler = new StubHandler(GoogleResponse);
            var provider = new GoogleProvider(new HttpClient(handler), "gemini-2.5-flash", "AIza-test");

            await provider.CompleteAsync(Request, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var schema = body.RootElement.GetProperty("generationConfig").GetProperty("responseSchema");

            Assert.Equal("OBJECT", schema.GetProperty("type").GetString());
            Assert.Equal("categories", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());

            var categories = schema.GetProperty("properties").GetProperty("categories");
            Assert.Equal("ARRAY", categories.GetProperty("type").GetString());

            // The item shape ProposalParser reads back: a name, a description, and
            // integer indexes into the batch.
            var category = categories.GetProperty("items");
            var categoryProps = category.GetProperty("properties");
            Assert.Equal("STRING", categoryProps.GetProperty("name").GetProperty("type").GetString());
            Assert.Equal("STRING", categoryProps.GetProperty("description").GetProperty("type").GetString());
            Assert.Equal("ARRAY", categoryProps.GetProperty("members").GetProperty("type").GetString());
            Assert.Equal(
                "INTEGER",
                categoryProps.GetProperty("members").GetProperty("items").GetProperty("type").GetString());

            // A discovery pass has no "selected" — that belongs to a viewer's pass.
            Assert.False(schema.GetProperty("properties").TryGetProperty("selected", out _));
        }

        /// <summary>
        /// Shared categories now go to every viewer, so a viewer's pass has nothing
        /// to select and asks for the same object the discovery pass does.
        /// </summary>
        [Fact]
        public async Task Google_PersonalShape_AsksForCategoriesOnly()
        {
            var handler = new StubHandler(GoogleResponse);
            var provider = new GoogleProvider(new HttpClient(handler), "gemini-2.5-flash", "AIza-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, ResponseShape.PersonalCategories),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var schema = body.RootElement.GetProperty("generationConfig").GetProperty("responseSchema");

            Assert.False(schema.GetProperty("properties").TryGetProperty("selected", out _));

            var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal(["categories"], required);
        }

        [Fact]
        public async Task Google_ThinkingOn_LeavesTheBudgetToTheModel()
        {
            var handler = new StubHandler(GoogleResponse);
            var provider = new GoogleProvider(new HttpClient(handler), "gemini-2.5-flash", "AIza-test");

            await provider.CompleteAsync(Request, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.False(body.RootElement.GetProperty("generationConfig").TryGetProperty("thinkingConfig", out _));
        }

        [Fact]
        public async Task Google_ThinkingOff_SendsZeroBudget()
        {
            var handler = new StubHandler(GoogleResponse);
            var provider = new GoogleProvider(
                new HttpClient(handler), "gemini-2.5-flash", "AIza-test", baseUrl: null, enableThinking: false);

            await provider.CompleteAsync(Request, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.Equal(
                0,
                body.RootElement.GetProperty("generationConfig")
                    .GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32());
        }

        [Fact]
        public async Task Google_ParsesTextAndUsage()
        {
            var provider = new GoogleProvider(
                new HttpClient(new StubHandler(GoogleResponse)), "gemini-2.5-flash", "AIza-test");

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal("""{"categories":[]}""", result.Text);
            Assert.Equal(1234, result.InputTokens);
            Assert.Equal(56, result.OutputTokens);
            Assert.False(result.Truncated);
        }

        [Fact]
        public async Task Google_SubtractsCachedTokensFromInputAndAddsThinkingToOutput()
        {
            // promptTokenCount is the TOTAL input including the cached span — the
            // opposite of Anthropic — so a cache hit must not read as more input.
            // thoughtsTokenCount is billed as output but reported outside
            // candidatesTokenCount.
            const string cached =
                """
                {"candidates":[{"content":{"parts":[{"text":"{\"categories\":[]}"}],"role":"model"},
                                "finishReason":"STOP"}],
                 "usageMetadata":{"promptTokenCount":5000,"candidatesTokenCount":56,
                                  "thoughtsTokenCount":400,"cachedContentTokenCount":4321,
                                  "totalTokenCount":5456}}
                """;

            var provider = new GoogleProvider(
                new HttpClient(new StubHandler(cached)), "gemini-2.5-flash", "AIza-test");

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal(679, result.InputTokens);
            Assert.Equal(4321, result.CacheReadTokens);
            Assert.Equal(0, result.CacheWriteTokens);
            Assert.Equal(456, result.OutputTokens);
        }

        [Fact]
        public async Task Google_ThoughtParts_AreNotTreatedAsTheAnswer()
        {
            const string withThoughts =
                """
                {"candidates":[{"content":{"parts":[
                    {"text":"Let me think about this library...","thought":true},
                    {"text":"{\"categories\":[]}"}],"role":"model"},
                                "finishReason":"STOP"}],
                 "usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5}}
                """;

            var provider = new GoogleProvider(
                new HttpClient(new StubHandler(withThoughts)), "gemini-2.5-flash", "AIza-test");

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal("""{"categories":[]}""", result.Text);
        }

        [Fact]
        public async Task Google_MaxTokensFinish_IsReportedAsTruncated()
        {
            var response = GoogleResponse.Replace("\"STOP\"", "\"MAX_TOKENS\"", StringComparison.Ordinal);
            var provider = new GoogleProvider(
                new HttpClient(new StubHandler(response)), "gemini-2.5-flash", "AIza-test");

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.True(result.Truncated);
        }

        [Fact]
        public async Task Google_SafetyFinish_Throws()
        {
            var response = GoogleResponse.Replace("\"STOP\"", "\"SAFETY\"", StringComparison.Ordinal);
            var provider = new GoogleProvider(
                new HttpClient(new StubHandler(response)), "gemini-2.5-flash", "AIza-test");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.CompleteAsync(Request, CancellationToken.None));
        }

        [Fact]
        public async Task Google_BlockedPrompt_Throws()
        {
            const string blocked = """{"promptFeedback":{"blockReason":"SAFETY"}}""";
            var provider = new GoogleProvider(
                new HttpClient(new StubHandler(blocked)), "gemini-2.5-flash", "AIza-test");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.CompleteAsync(Request, CancellationToken.None));
        }

        [Fact]
        public async Task Google_HttpError_ThrowsWithStatus()
        {
            var provider = new GoogleProvider(
                new HttpClient(new StubHandler("""{"error":{"message":"API key not valid"}}""", HttpStatusCode.BadRequest)),
                "gemini-2.5-flash",
                "AIza-bad");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(
                () => provider.CompleteAsync(Request, CancellationToken.None));
            Assert.Contains("400", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Google_BaseUrlOverride_IsUsed()
        {
            var handler = new StubHandler(GoogleResponse);
            var provider = new GoogleProvider(
                new HttpClient(handler), "gemini-2.5-flash", "AIza-test", "https://proxy.example.com/v1beta/");

            await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal(
                "https://proxy.example.com/v1beta/models/gemini-2.5-flash:generateContent",
                handler.Request!.RequestUri!.ToString());
        }

        [Fact]
        public async Task Google_TurnsSafetyFilteringOff()
        {
            // Gemini blocks on OUR input — a library of horror and true crime with
            // their synopses. A blocked response loses a paid-for pass, and nothing
            // here is generative in the risky sense.
            var handler = new StubHandler(GoogleResponse);
            var provider = new GoogleProvider(new HttpClient(handler), "gemini-2.5-flash", "AIza-test");

            await provider.CompleteAsync(Request, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var settings = body.RootElement.GetProperty("safetySettings").EnumerateArray()
                .ToDictionary(
                    s => s.GetProperty("category").GetString()!,
                    s => s.GetProperty("threshold").GetString()!);

            Assert.Equal(4, settings.Count);
            Assert.All(settings.Values, threshold => Assert.Equal("OFF", threshold));
            Assert.Contains("HARM_CATEGORY_HARASSMENT", settings.Keys);
            Assert.Contains("HARM_CATEGORY_HATE_SPEECH", settings.Keys);
            Assert.Contains("HARM_CATEGORY_SEXUALLY_EXPLICIT", settings.Keys);
            Assert.Contains("HARM_CATEGORY_DANGEROUS_CONTENT", settings.Keys);
        }

        [Fact]
        public async Task Google_RateLimited_IsRetriedRatherThanEndingTheRun()
        {
            // Quota is per-minute and a run fires a request per user back to back;
            // 429 is ordinary here. Failing the run on one would throw away every
            // pass already paid for.
            var handler = new SequenceHandler(
                new Reply(HttpStatusCode.TooManyRequests, """{"error":{"message":"quota"}}"""),
                new Reply(HttpStatusCode.OK, GoogleResponse));
            var provider = new GoogleProvider(
                new HttpClient(handler), "gemini-2.5-flash", "AIza-test", null, true, NoDelay);

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal(2, handler.SendCount);
            Assert.Equal("""{"categories":[]}""", result.Text);
        }

        [Theory]
        [InlineData(HttpStatusCode.InternalServerError)]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        [InlineData(HttpStatusCode.GatewayTimeout)]
        public async Task Google_TransientServerErrors_AreRetried(HttpStatusCode status)
        {
            var handler = new SequenceHandler(
                new Reply(status),
                new Reply(HttpStatusCode.OK, GoogleResponse));
            var provider = new GoogleProvider(
                new HttpClient(handler), "gemini-2.5-flash", "AIza-test", null, true, NoDelay);

            await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal(2, handler.SendCount);
        }

        [Fact]
        public async Task Google_RetryAfterHeader_IsHonouredOverTheBackoff()
        {
            // The server's own pacing beats any curve we invent. A zero here also
            // proves the header is being read rather than ignored.
            var handler = new SequenceHandler(
                new Reply(HttpStatusCode.TooManyRequests, "{}", RetryAfterSeconds: 0),
                new Reply(HttpStatusCode.OK, GoogleResponse));
            var provider = new GoogleProvider(
                new HttpClient(handler), "gemini-2.5-flash", "AIza-test", null, true, TimeSpan.FromMinutes(5));

            // Would block for five minutes if the header were ignored.
            var result = await provider.CompleteAsync(Request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(2, handler.SendCount);
            Assert.Equal("""{"categories":[]}""", result.Text);
        }

        [Fact]
        public async Task Google_PersistentRateLimit_GivesUpAfterFourAttempts()
        {
            var handler = new SequenceHandler(new Reply(HttpStatusCode.TooManyRequests, """{"error":"quota"}"""));
            var provider = new GoogleProvider(
                new HttpClient(handler), "gemini-2.5-flash", "AIza-test", null, true, NoDelay);

            var ex = await Assert.ThrowsAsync<HttpRequestException>(
                () => provider.CompleteAsync(Request, CancellationToken.None));

            Assert.Equal(4, handler.SendCount);
            Assert.Contains("429", ex.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.BadRequest)]
        [InlineData(HttpStatusCode.NotFound)]
        public async Task Google_PermanentErrors_FailFastWithoutRetrying(HttpStatusCode status)
        {
            // A bad key or a wrong model id must not take a minute to say so.
            var handler = new SequenceHandler(new Reply(status, """{"error":{"message":"nope"}}"""));
            var provider = new GoogleProvider(
                new HttpClient(handler), "gemini-2.5-flash", "AIza-test", null, true, NoDelay);

            await Assert.ThrowsAsync<HttpRequestException>(
                () => provider.CompleteAsync(Request, CancellationToken.None));

            Assert.Equal(1, handler.SendCount);
        }

        [Fact]
        public async Task Google_ReportsThinkingTokensSeparatelyFromTheOutputTotal()
        {
            // Thinking and the answer compete for one output cap, and thinking
            // winning that race is how a schema-constrained response gets truncated
            // mid-JSON. The breakdown is what makes that diagnosable.
            const string thinking =
                """
                {"candidates":[{"content":{"parts":[{"text":"{\"categories\":[]}"}],"role":"model"},
                                "finishReason":"MAX_TOKENS"}],
                 "usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":200,
                                  "thoughtsTokenCount":15800}}
                """;

            var provider = new GoogleProvider(
                new HttpClient(new StubHandler(thinking)), "gemini-2.5-flash", "AIza-test");

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal(15800, result.ThinkingTokens);
            Assert.Equal(16000, result.OutputTokens);
            Assert.True(result.Truncated);
        }

        [Fact]
        public async Task Google_EmptyCacheablePrefix_SendsSinglePart()
        {
            var handler = new StubHandler(GoogleResponse);
            var provider = new GoogleProvider(new HttpClient(handler), "gemini-2.5-flash", "AIza-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", string.Empty, "SUFFIX", 4096), CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var content = Assert.Single(body.RootElement.GetProperty("contents").EnumerateArray());
            var part = Assert.Single(content.GetProperty("parts").EnumerateArray());
            Assert.Equal("SUFFIX", part.GetProperty("text").GetString());
        }
    
        private const string GrokResponse =
            """
            {"id":"chatcmpl-x1","object":"chat.completion","model":"grok-4",
             "choices":[{"index":0,"message":{"role":"assistant","content":"{\"categories\":[]}"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":5000,"completion_tokens":300,"total_tokens":5300,
                      "prompt_tokens_details":{"cached_tokens":4000},
                      "completion_tokens_details":{"reasoning_tokens":120}}}
            """;

        [Fact]
        public async Task Grok_PostsToXaiWithBearerAuth()
        {
            var handler = new StubHandler(GrokResponse);
            var provider = OpenAiChatProvider.CreateGrok(new HttpClient(handler), "grok-4", "xai-test");

            await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal("https://api.x.ai/v1/chat/completions", handler.Request!.RequestUri!.ToString());
            Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
            Assert.Equal("xai-test", handler.Request.Headers.Authorization.Parameter);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.Equal("grok-4", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(4096, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        }

        /// <summary>
        /// The regression this pair exists for. The summary schema declared only
        /// "i" and "s" with additionalProperties:false, while the prompt asked for a
        /// "t" list whenever tag consolidation was on. Strict mode forbids the field,
        /// so the model had no legal place to put the tags and wrote them into "s" —
        /// 17 of 232 stored summaries ended <c>…viciously sharp\u0027,\u0027t\u0027:[</c>,
        /// and every item came back with an empty tag list.
        /// </summary>
        [Fact]
        public async Task Grok_TaggedSummarySchema_AllowsTheTagFieldThePromptAsksFor()
        {
            var handler = new StubHandler(GrokResponse);
            var provider = OpenAiChatProvider.CreateGrok(new HttpClient(handler), "grok-4", "xai-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, ResponseShape.SummariesWithTags),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var item = body.RootElement
                .GetProperty("response_format").GetProperty("json_schema").GetProperty("schema")
                .GetProperty("properties").GetProperty("summaries").GetProperty("items");

            Assert.Equal("array", item.GetProperty("properties").GetProperty("t").GetProperty("type").GetString());
            Assert.Equal(
                "string",
                item.GetProperty("properties").GetProperty("t").GetProperty("items").GetProperty("type").GetString());

            // Strict mode requires every declared property to be required.
            var required = item.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal(["i", "s", "t"], required);
        }

        /// <summary>
        /// Generation order is load-bearing, not cosmetic. The prompt tells the model
        /// to consolidate tags AFTER writing the rewrite so the summary drives the tag
        /// choice; under a constrained decoder that only holds if the schema emits "s"
        /// before "t". Declared order is the only thing enforcing it.
        /// </summary>
        [Fact]
        public async Task Grok_TaggedSummarySchema_GeneratesTheSummaryBeforeTheTags()
        {
            var handler = new StubHandler(GrokResponse);
            var provider = OpenAiChatProvider.CreateGrok(new HttpClient(handler), "grok-4", "xai-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, ResponseShape.SummariesWithTags),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var props = body.RootElement
                .GetProperty("response_format").GetProperty("json_schema").GetProperty("schema")
                .GetProperty("properties").GetProperty("summaries").GetProperty("items")
                .GetProperty("properties");

            Assert.Equal(["i", "s", "t"], props.EnumerateObject().Select(p => p.Name).ToList());
        }

        /// <summary>
        /// Same regression, the context fields. Four shapes over two switches is the
        /// arithmetic that gets done wrong in one of the places it is written, so
        /// every combination is checked in both dialects rather than the two new ones.
        /// </summary>
        [Theory]
        [InlineData(ResponseShape.Summaries, new[] { "i", "s" })]
        [InlineData(ResponseShape.SummariesWithTags, new[] { "i", "s", "t" })]
        [InlineData(ResponseShape.SummariesWithContext, new[] { "i", "s", "w", "d" })]
        [InlineData(ResponseShape.SummariesWithTagsAndContext, new[] { "i", "s", "t", "w", "d" })]
        public async Task Grok_EverySummaryShape_DeclaresExactlyItsOwnFields(
            ResponseShape shape,
            string[] expected)
        {
            var handler = new StubHandler(GrokResponse);
            var provider = OpenAiChatProvider.CreateGrok(new HttpClient(handler), "grok-4", "xai-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, shape),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var item = body.RootElement
                .GetProperty("response_format").GetProperty("json_schema").GetProperty("schema")
                .GetProperty("properties").GetProperty("summaries").GetProperty("items");

            // Declared, required and in that order — strict mode needs all three to
            // agree, and the order is what makes the model write the rewrite first.
            Assert.Equal(expected, item.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList());
            Assert.Equal(expected, item.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList());
        }

        [Theory]
        [InlineData(ResponseShape.Summaries, new[] { "i", "s" })]
        [InlineData(ResponseShape.SummariesWithTags, new[] { "i", "s", "t" })]
        [InlineData(ResponseShape.SummariesWithContext, new[] { "i", "s", "w", "d" })]
        [InlineData(ResponseShape.SummariesWithTagsAndContext, new[] { "i", "s", "t", "w", "d" })]
        public async Task Google_EverySummaryShape_DeclaresExactlyItsOwnFields(
            ResponseShape shape,
            string[] expected)
        {
            var handler = new StubHandler(GoogleResponse);
            var provider = new GoogleProvider(new HttpClient(handler), "gemini-2.5-flash", "AIza-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, shape),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var item = body.RootElement
                .GetProperty("generationConfig").GetProperty("responseSchema")
                .GetProperty("properties").GetProperty("summaries").GetProperty("items");

            Assert.Equal(expected, item.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList());
            Assert.Equal(
                expected,
                item.GetProperty("propertyOrdering").EnumerateArray().Select(e => e.GetString()).ToList());
        }

        /// <summary>
        /// The closed vocabulary has to reach the decoder, not only the prose. A
        /// schema that let the model answer "drizzly" would produce a word the parser
        /// then throws away — a judgement paid for and silently discarded.
        /// </summary>
        [Fact]
        public async Task Grok_ContextSchema_ConstrainsBothListsToTheVocabulary()
        {
            var handler = new StubHandler(GrokResponse);
            var provider = OpenAiChatProvider.CreateGrok(new HttpClient(handler), "grok-4", "xai-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, ResponseShape.SummariesWithContext),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var props = body.RootElement
                .GetProperty("response_format").GetProperty("json_schema").GetProperty("schema")
                .GetProperty("properties").GetProperty("summaries").GetProperty("items")
                .GetProperty("properties");

            Assert.Equal(
                ContextVocabulary.Weather.ToList(),
                props.GetProperty("w").GetProperty("items").GetProperty("enum")
                    .EnumerateArray().Select(e => e.GetString()!).ToList());
            Assert.Equal(
                ContextVocabulary.Dayparts.ToList(),
                props.GetProperty("d").GetProperty("items").GetProperty("enum")
                    .EnumerateArray().Select(e => e.GetString()!).ToList());
        }

        [Fact]
        public async Task Google_ContextSchema_ConstrainsBothListsToTheVocabulary()
        {
            var handler = new StubHandler(GoogleResponse);
            var provider = new GoogleProvider(new HttpClient(handler), "gemini-2.5-flash", "AIza-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, ResponseShape.SummariesWithTagsAndContext),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var props = body.RootElement
                .GetProperty("generationConfig").GetProperty("responseSchema")
                .GetProperty("properties").GetProperty("summaries").GetProperty("items")
                .GetProperty("properties");

            Assert.Equal(
                ContextVocabulary.Weather.ToList(),
                props.GetProperty("w").GetProperty("items").GetProperty("enum")
                    .EnumerateArray().Select(e => e.GetString()!).ToList());
            Assert.Equal("STRING", props.GetProperty("d").GetProperty("items").GetProperty("type").GetString());
        }

        [Fact]
        public async Task Google_TaggedSummarySchema_GeneratesTheSummaryBeforeTheTags()
        {
            var handler = new StubHandler(GoogleResponse);
            var provider = new GoogleProvider(new HttpClient(handler), "gemini-2.5-flash", "AIza-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, ResponseShape.SummariesWithTags),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var item = body.RootElement
                .GetProperty("generationConfig").GetProperty("responseSchema")
                .GetProperty("properties").GetProperty("summaries").GetProperty("items");

            // Google honours an explicit ordering key rather than declaration order.
            Assert.Equal(
                ["i", "s", "t"],
                item.GetProperty("propertyOrdering").EnumerateArray().Select(e => e.GetString()).ToList());
        }

        /// <summary>
        /// The bug this pair exists for. Two passes put their whole prompt in the
        /// cacheable prefix and hand over an empty suffix — the distillation pass and
        /// the recommendation re-rank. The builders guarded an empty *prefix* and not
        /// an empty *suffix*, so Anthropic got a content block with no text and
        /// rejected the request outright: "text content blocks must be non-empty".
        /// Measured on a live server — 195 items, every batch 400ing, 0 distilled,
        /// $0.00, on a model that simply had not been used for that pass before.
        /// </summary>
        /// <summary>
        /// The OpenAI half of the routing problem xAI's header solves. Cache entries
        /// are held per server, and without a hint the calls of one run scatter.
        /// Measured before this: a 139k-token prompt, byte-identical across six calls,
        /// reported ZERO cached tokens on two runs eight minutes apart — while Grok,
        /// through this same class but with its header, served 82k from cache on the
        /// same library.
        /// </summary>
        [Fact]
        public async Task OpenAi_PinsARunsCallsTogetherWithAPromptCacheKey()
        {
            var handler = new StubHandler(OpenAiResponse);
            var provider = OpenAiChatProvider.CreateOpenAi(new HttpClient(handler), "gpt-5", "sk-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, ResponseShape.Categories, "run-abc123"),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.Equal("run-abc123", body.RootElement.GetProperty("prompt_cache_key").GetString());
        }

        [Fact]
        public async Task OpenAi_WithoutAConversationId_SendsNoCacheKey()
        {
            var handler = new StubHandler(OpenAiResponse);
            var provider = OpenAiChatProvider.CreateOpenAi(new HttpClient(handler), "gpt-5", "sk-test");

            await provider.CompleteAsync(Request, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.False(body.RootElement.TryGetProperty("prompt_cache_key", out _));
        }

        [Fact]
        public async Task Grok_UsesItsHeaderRatherThanTheCacheKey()
        {
            // xAI routes on the header and has never been sent this field. Adding it
            // would be an untested body parameter on a vendor that already has a
            // working answer.
            var handler = new StubHandler(GrokResponse);
            var provider = OpenAiChatProvider.CreateGrok(new HttpClient(handler), "grok-4", "xai-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, ResponseShape.Categories, "run-abc123"),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.False(body.RootElement.TryGetProperty("prompt_cache_key", out _));
            Assert.Equal("run-abc123", handler.Request!.Headers.GetValues("x-grok-conv-id").Single());
        }

        [Fact]
        public async Task ACompatibleEndpoint_IsSentNothingItMayNotUnderstand()
        {
            // This class also drives Ollama, LM Studio, vLLM and OpenRouter, which are
            // entitled to reject a body field they have never heard of.
            var handler = new StubHandler(OpenAiResponse);
            var provider = OpenAiChatProvider.CreateCompatible(
                new HttpClient(handler), "llama3.3", "http://localhost:11434/v1");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, ResponseShape.Categories, "run-abc123"),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.False(body.RootElement.TryGetProperty("prompt_cache_key", out _));
            Assert.False(handler.Request!.Headers.Contains("x-grok-conv-id"));

            // And still speaks the older dialect it was built for.
            Assert.True(body.RootElement.TryGetProperty("max_tokens", out _));
            Assert.False(body.RootElement.TryGetProperty("max_completion_tokens", out _));
        }

        [Fact]
        public async Task Anthropic_AnEmptySuffix_SendsOneBlockRatherThanAnEmptyOne()
        {
            var handler = new StubHandler(AnthropicResponse);
            var provider = new AnthropicProvider(new HttpClient(handler), "claude-opus-5", "sk-test", null, false);

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "THE WHOLE PROMPT", string.Empty, 4096),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");

            var block = Assert.Single(content.EnumerateArray());
            Assert.Equal("THE WHOLE PROMPT", block.GetProperty("text").GetString());
            Assert.All(
                content.EnumerateArray(),
                b => Assert.False(string.IsNullOrEmpty(b.GetProperty("text").GetString())));
        }

        [Fact]
        public async Task Anthropic_AnEmptySuffix_DoesNotPayTheCacheWritePremium()
        {
            // The split exists to mark the part that repeats across calls. A caller
            // that puts everything in the prefix is saying there is nothing to reuse,
            // and marking it anyway would pay 2x on the write for every batch of a
            // pass whose prompt is different every time.
            var handler = new StubHandler(AnthropicResponse);
            var provider = new AnthropicProvider(new HttpClient(handler), "claude-opus-5", "sk-test", null, false);

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "THE WHOLE PROMPT", string.Empty, 4096),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var block = Assert.Single(
                body.RootElement.GetProperty("messages")[0].GetProperty("content").EnumerateArray());

            Assert.False(block.TryGetProperty("cache_control", out _));
        }

        [Fact]
        public async Task Anthropic_ARealSplit_StillMarksThePrefixForCaching()
        {
            var handler = new StubHandler(AnthropicResponse);
            var provider = new AnthropicProvider(new HttpClient(handler), "claude-opus-5", "sk-test", null, false);

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var content = body.RootElement.GetProperty("messages")[0].GetProperty("content")
                .EnumerateArray().ToList();

            Assert.Equal(2, content.Count);
            Assert.Equal("1h", content[0].GetProperty("cache_control").GetProperty("ttl").GetString());
            Assert.False(content[1].TryGetProperty("cache_control", out _));
        }

        [Fact]
        public async Task Google_AnEmptySuffix_SendsOnePartRatherThanAnEmptyOne()
        {
            var handler = new StubHandler(GoogleResponse);
            var provider = new GoogleProvider(new HttpClient(handler), "gemini-2.5-flash", "AIza-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "THE WHOLE PROMPT", string.Empty, 4096),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var parts = body.RootElement.GetProperty("contents")[0].GetProperty("parts");

            Assert.Equal("THE WHOLE PROMPT", Assert.Single(parts.EnumerateArray()).GetProperty("text").GetString());
        }

        [Fact]
        public async Task ARequestWithNoUserPromptAtAllIsRefusedBeforeItIsSent()
        {
            // Not a provider quirk to absorb — a request with no user content is a
            // caller bug, and a clear exception beats a 400 from three vendors.
            var anthropic = new AnthropicProvider(
                new HttpClient(new StubHandler(AnthropicResponse)), "claude-opus-5", "sk-test", null, false);
            var google = new GoogleProvider(
                new HttpClient(new StubHandler(GoogleResponse)), "gemini-2.5-flash", "AIza-test");
            var empty = new LlmRequest("SYSTEM", string.Empty, string.Empty, 4096);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => anthropic.CompleteAsync(empty, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => google.CompleteAsync(empty, CancellationToken.None));
        }

        [Fact]
        public async Task Grok_RecommendationOrderSchema_MatchesTheParserContract()
        {
            var handler = new StubHandler(GrokResponse);
            var provider = OpenAiChatProvider.CreateGrok(new HttpClient(handler), "grok-4", "xai-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, ResponseShape.RecommendationOrder),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var schema = body.RootElement
                .GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");

            Assert.Equal("order", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
            Assert.Equal(
                "integer",
                schema.GetProperty("properties").GetProperty("order").GetProperty("items").GetProperty("type").GetString());
        }

        [Fact]
        public async Task Google_RecommendationOrderSchema_MatchesTheParserContract()
        {
            var handler = new StubHandler(GoogleResponse);
            var provider = new GoogleProvider(new HttpClient(handler), "gemini-2.5-flash", "AIza-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, ResponseShape.RecommendationOrder),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var schema = body.RootElement.GetProperty("generationConfig").GetProperty("responseSchema");

            Assert.Equal("order", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
            Assert.Equal(
                "INTEGER",
                schema.GetProperty("properties").GetProperty("order").GetProperty("items").GetProperty("type").GetString());
        }

        [Fact]
        public async Task Grok_PlainSummarySchema_DoesNotAskForTagsThePromptNeverMentions()
        {
            var handler = new StubHandler(GrokResponse);
            var provider = OpenAiChatProvider.CreateGrok(new HttpClient(handler), "grok-4", "xai-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, ResponseShape.Summaries),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var item = body.RootElement
                .GetProperty("response_format").GetProperty("json_schema").GetProperty("schema")
                .GetProperty("properties").GetProperty("summaries").GetProperty("items");

            Assert.False(item.GetProperty("properties").TryGetProperty("t", out _));
            Assert.Equal(["i", "s"], item.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList());
        }

        [Fact]
        public async Task Google_TaggedSummarySchema_AllowsTheTagFieldThePromptAsksFor()
        {
            var handler = new StubHandler(GoogleResponse);
            var provider = new GoogleProvider(new HttpClient(handler), "gemini-2.5-flash", "AIza-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, ResponseShape.SummariesWithTags),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var item = body.RootElement
                .GetProperty("generationConfig").GetProperty("responseSchema")
                .GetProperty("properties").GetProperty("summaries").GetProperty("items");

            Assert.Equal("ARRAY", item.GetProperty("properties").GetProperty("t").GetProperty("type").GetString());
            Assert.Equal(
                ["i", "s", "t"],
                item.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList());
        }

        [Fact]
        public async Task Google_PlainSummarySchema_DoesNotAskForTagsThePromptNeverMentions()
        {
            var handler = new StubHandler(GoogleResponse);
            var provider = new GoogleProvider(new HttpClient(handler), "gemini-2.5-flash", "AIza-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, ResponseShape.Summaries),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var item = body.RootElement
                .GetProperty("generationConfig").GetProperty("responseSchema")
                .GetProperty("properties").GetProperty("summaries").GetProperty("items");

            Assert.False(item.GetProperty("properties").TryGetProperty("t", out _));
        }

        [Fact]
        public async Task Grok_ConstrainsTheAnswerWithAStrictJsonSchema()
        {
            var handler = new StubHandler(GrokResponse);
            var provider = OpenAiChatProvider.CreateGrok(new HttpClient(handler), "grok-4", "xai-test");

            await provider.CompleteAsync(Request, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var format = body.RootElement.GetProperty("response_format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());

            var jsonSchema = format.GetProperty("json_schema");
            Assert.True(jsonSchema.GetProperty("strict").GetBoolean());

            var schema = jsonSchema.GetProperty("schema");
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());

            // The shape ProposalParser reads back, in OpenAI's lowercase dialect.
            var category = schema.GetProperty("properties").GetProperty("categories").GetProperty("items");
            var props = category.GetProperty("properties");
            Assert.Equal("string", props.GetProperty("name").GetProperty("type").GetString());
            Assert.Equal("string", props.GetProperty("description").GetProperty("type").GetString());
            Assert.Equal("integer", props.GetProperty("members").GetProperty("items").GetProperty("type").GetString());

            // Strict mode rejects a schema that leaves any property out of required.
            var required = category.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal(props.EnumerateObject().Count(), required.Count);
        }

        [Fact]
        public async Task Grok_PersonalShape_AsksForCategoriesOnly()
        {
            var handler = new StubHandler(GrokResponse);
            var provider = OpenAiChatProvider.CreateGrok(new HttpClient(handler), "grok-4", "xai-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, ResponseShape.PersonalCategories),
                CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var jsonSchema = body.RootElement.GetProperty("response_format").GetProperty("json_schema");
            Assert.Equal("curator_personal_categories", jsonSchema.GetProperty("name").GetString());

            // Shared categories now go to every viewer, so there is nothing left for
            // a viewer's pass to select; the schema is the discovery one.
            var schema = jsonSchema.GetProperty("schema");
            Assert.False(schema.GetProperty("properties").TryGetProperty("selected", out _));

            var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal(["categories"], required);
        }

        // ---- xAI conversation routing ----

        /// <summary>
        /// xAI stores cache entries per server. Without a routing hint the calls of
        /// one run scatter across the fleet and each lands on a machine that has
        /// never seen the prefix — measured: 16 of 18 calls reported 128 cached
        /// tokens against a ~28k identical prefix.
        /// </summary>
        [Fact]
        public async Task Grok_SendsTheConversationRoutingHeader()
        {
            var handler = new StubHandler(GrokResponse);
            var provider = OpenAiChatProvider.CreateGrok(new HttpClient(handler), "grok-4", "xai-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, ResponseShape.Categories, "run-abc123"),
                CancellationToken.None);

            Assert.Equal("run-abc123", Assert.Single(handler.Request!.Headers.GetValues("x-grok-conv-id")));
        }

        /// <summary>
        /// Every call of a run must carry the same ID, or they route to different
        /// servers and the header buys nothing.
        /// </summary>
        [Fact]
        public async Task Grok_UsesTheSameHeaderForEveryCallOfARun()
        {
            var seen = new List<string>();
            foreach (var shape in new[] { ResponseShape.Categories, ResponseShape.PersonalCategories, ResponseShape.PersonalCategories })
            {
                var handler = new StubHandler(GrokResponse);
                var provider = OpenAiChatProvider.CreateGrok(new HttpClient(handler), "grok-4", "xai-test");
                await provider.CompleteAsync(
                    new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, shape, "one-run"),
                    CancellationToken.None);
                seen.Add(handler.Request!.Headers.GetValues("x-grok-conv-id").Single());
            }

            Assert.Equal(["one-run", "one-run", "one-run"], seen);
        }

        [Fact]
        public async Task Grok_OmitsTheHeaderWhenNoConversationIdIsGiven()
        {
            var handler = new StubHandler(GrokResponse);
            var provider = OpenAiChatProvider.CreateGrok(new HttpClient(handler), "grok-4", "xai-test");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096),
                CancellationToken.None);

            Assert.False(handler.Request!.Headers.Contains("x-grok-conv-id"));
        }

        /// <summary>
        /// The header is xAI's. A local Ollama or LM Studio server has no use for it
        /// and no reason to be sent one.
        /// </summary>
        [Fact]
        public async Task NonGrokEndpointsDoNotGetTheHeader()
        {
            var handler = new StubHandler(GrokResponse);
            var provider = OpenAiChatProvider.CreateCompatible(
                new HttpClient(handler), "llama3.3", "http://localhost:11434/v1");

            await provider.CompleteAsync(
                new LlmRequest("SYSTEM", "ITEMS", "SUFFIX", 4096, ResponseShape.Categories, "run-abc123"),
                CancellationToken.None);

            Assert.False(handler.Request!.Headers.Contains("x-grok-conv-id"));
        }

        [Fact]
        public async Task Grok_SubtractsCachedTokensAndReportsReasoningSeparately()
        {
            var provider = OpenAiChatProvider.CreateGrok(
                new HttpClient(new StubHandler(GrokResponse)), "grok-4", "xai-test");

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            // prompt_tokens includes the cached span, as on Gemini and unlike Anthropic.
            Assert.Equal(1000, result.InputTokens);
            Assert.Equal(4000, result.CacheReadTokens);
            Assert.Equal(300, result.OutputTokens);
            Assert.Equal(120, result.ThinkingTokens);
            Assert.False(result.Truncated);
        }

        [Fact]
        public async Task Grok_RateLimited_IsRetried()
        {
            var handler = new SequenceHandler(
                new Reply(HttpStatusCode.TooManyRequests, """{"error":"rate limited"}"""),
                new Reply(HttpStatusCode.OK, GrokResponse));
            var provider = OpenAiChatProvider.CreateGrok(
                new HttpClient(handler), "grok-4", "xai-test", null, NoDelay);

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal(2, handler.SendCount);
            Assert.Equal("""{"categories":[]}""", result.Text);
        }

        [Fact]
        public async Task Grok_BadKey_FailsFastAndNamesTheProvider()
        {
            var handler = new SequenceHandler(
                new Reply(HttpStatusCode.Unauthorized, """{"error":"bad key"}"""));
            var provider = OpenAiChatProvider.CreateGrok(
                new HttpClient(handler), "grok-4", "xai-bad", null, NoDelay);

            var ex = await Assert.ThrowsAsync<HttpRequestException>(
                () => provider.CompleteAsync(Request, CancellationToken.None));

            Assert.Equal(1, handler.SendCount);
            Assert.Contains("Grok returned 401", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Grok_BaseUrlOverride_IsUsed()
        {
            var handler = new StubHandler(GrokResponse);
            var provider = OpenAiChatProvider.CreateGrok(
                new HttpClient(handler), "grok-4", "xai-test", "https://proxy.example.com/v1");

            await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal("https://proxy.example.com/v1/chat/completions", handler.Request!.RequestUri!.ToString());
        }

        [Fact]
        public async Task CompatibleEndpoint_NeverSendsAResponseFormat()
        {
            // Ollama, LM Studio and friends reject the whole request rather than
            // ignoring a field they do not know.
            var handler = new StubHandler(OpenAiResponse);
            var provider = OpenAiChatProvider.CreateCompatible(
                new HttpClient(handler), "llama3", "http://localhost:11434/v1");

            await provider.CompleteAsync(Request, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        }

        [Fact]
        public async Task OpenAi_StillSendsNoResponseFormat()
        {
            var handler = new StubHandler(OpenAiResponse);
            var provider = OpenAiChatProvider.CreateOpenAi(new HttpClient(handler), "gpt-test", "sk-test");

            await provider.CompleteAsync(Request, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        }
    }
}
