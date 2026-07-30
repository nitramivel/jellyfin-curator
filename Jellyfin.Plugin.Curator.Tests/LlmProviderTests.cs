using System;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

        private static readonly LlmRequest Request = new("SYSTEM", "ITEMS", "SUFFIX", 4096);

        private const string AnthropicResponse =
            """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5",
             "content":[{"type":"text","text":"{\"categories\":[]}"}],
             "stop_reason":"end_turn",
             "usage":{"input_tokens":1234,"output_tokens":56}}
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

            // Sonnet 5 and later think by default, and max_tokens caps thinking plus
            // visible text together — which truncates the JSON before it closes.
            Assert.Equal(
                "disabled",
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
    }
}
