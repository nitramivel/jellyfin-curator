using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Curator.Services.Llm
{
    /// <summary>
    /// One request to the LLM: a system prompt, a user prompt, and an output cap.
    /// </summary>
    /// <param name="SystemPrompt">The system prompt.</param>
    /// <param name="UserPrompt">The user prompt.</param>
    /// <param name="MaxOutputTokens">Maximum output tokens for this call.</param>
    public sealed record LlmRequest(string SystemPrompt, string UserPrompt, int MaxOutputTokens);

    /// <summary>
    /// The result of one LLM call, with authoritative token usage from the provider.
    /// </summary>
    /// <param name="Text">The model's text output.</param>
    /// <param name="InputTokens">Input tokens billed, as reported by the provider.</param>
    /// <param name="OutputTokens">Output tokens billed, as reported by the provider.</param>
    /// <param name="Truncated">Whether the output was cut off by the output-token cap.</param>
    public sealed record LlmResult(string Text, long InputTokens, long OutputTokens, bool Truncated);

    /// <summary>
    /// A chat-completion-shaped LLM backend. Implementations are stateless and
    /// carry their own endpoint, credentials, and wire format.
    /// </summary>
    public interface ILlmProvider
    {
        /// <summary>
        /// Gets the model identifier requests are sent to, for logging and tagging.
        /// </summary>
        string ModelId { get; }

        /// <summary>
        /// Sends one prompt and returns the model's text plus token usage.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The completion result.</returns>
        Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
    }
}
