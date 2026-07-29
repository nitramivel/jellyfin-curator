using System;
using System.Net;
using System.Net.Http;
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

        private static readonly LlmRequest Request = new("SYSTEM", "USER", 4096);

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
            var message = Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray());
            Assert.Equal("user", message.GetProperty("role").GetString());
            Assert.Equal("USER", message.GetProperty("content").GetString());
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
