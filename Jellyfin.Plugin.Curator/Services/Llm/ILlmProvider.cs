using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Curator.Services.Llm
{
    /// <summary>
    /// Which JSON object the model is being asked for. The prompts describe these
    /// shapes in prose and <see cref="Core.Llm.ProposalParser"/> enforces them on the
    /// way back; a provider that supports structured outputs can additionally hand
    /// the shape to the API so valid JSON becomes a guarantee rather than a hope.
    /// <para>
    /// Kept provider-neutral on purpose: the enum names the contract, and each
    /// provider translates it into whatever its own schema dialect is. Providers
    /// without structured outputs ignore it and rely on the prompt, which is why
    /// the prompts must keep describing the shape in full.
    /// </para>
    /// </summary>
    public enum ResponseShape
    {
        /// <summary>{"categories":[{"name","description","members":[int]}]}.</summary>
        Categories = 0,

        /// <summary>The above plus {"selected":[string]} — one viewer's pass.</summary>
        PersonalCategories = 1,

        /// <summary>{"summaries":[{"i":int,"s":string}]} — the condensed-summary pass.</summary>
        Summaries = 2,

        /// <summary>
        /// {"summaries":[{"i":int,"s":string,"t":[string]}]} — the same pass with tag
        /// consolidation on.
        /// </summary>
        /// <remarks>
        /// A separate shape rather than an optional field because strict structured
        /// output requires every declared property, and a schema that always demands
        /// "t" would ask for tags on a pass whose prompt never mentioned them. The two
        /// must match: a prompt asking for a field the schema forbids has no legal
        /// way to answer, and the model writes the field into the previous string
        /// instead — measured, that corrupted 17 of 232 summaries and returned an
        /// empty tag list for every single item.
        /// </remarks>
        SummariesWithTags = 3,

        /// <summary>
        /// {"order":[int]} — one viewer's shortlist put in a recommended order.
        /// </summary>
        RecommendationOrder = 4,

        /// <summary>
        /// {"summaries":[{"i":int,"s":string,"w":[string],"d":[string]}]} — the
        /// condensing pass also judging when an item suits watching.
        /// </summary>
        SummariesWithContext = 5,

        /// <summary>
        /// {"summaries":[{"i":int,"s":string,"t":[string],"w":[string],"d":[string]}]}
        /// — the condensing pass doing all three jobs in one call.
        /// </summary>
        /// <remarks>
        /// Four shapes for two independent switches, and there is no way around it:
        /// strict structured output requires every declared property, so a schema
        /// cannot make a field optional. The alternative — one schema always
        /// demanding every field — asks a pass whose prompt never mentioned tags to
        /// produce them, which is the same mismatch in the other direction.
        /// </remarks>
        SummariesWithTagsAndContext = 6,

        /// <summary>
        /// {"titles":[string]} — names for one context row, for one set of conditions.
        /// </summary>
        ContextTitles = 7,
    }

    /// <summary>
    /// The one place that knows which condensing shape carries which fields.
    ///
    /// <para>
    /// Four shapes over two independent switches is exactly the arithmetic that gets
    /// done wrong in one of the places it is written out. Both structured-output
    /// providers and the pass that picks a shape all read it from here, so a fifth
    /// combination is added once rather than three times.
    /// </para>
    /// </summary>
    public static class SummaryShapes
    {
        /// <summary>
        /// The shape for a condensing call with these two switches.
        /// </summary>
        /// <param name="includeTags">Whether tags are being consolidated.</param>
        /// <param name="includeContext">Whether context is being classified.</param>
        /// <returns>The shape to ask for.</returns>
        public static ResponseShape For(bool includeTags, bool includeContext)
            => (includeTags, includeContext) switch
            {
                (false, false) => ResponseShape.Summaries,
                (true, false) => ResponseShape.SummariesWithTags,
                (false, true) => ResponseShape.SummariesWithContext,
                (true, true) => ResponseShape.SummariesWithTagsAndContext,
            };

        /// <summary>Whether a shape is one of the condensing pass's.</summary>
        /// <param name="shape">The shape.</param>
        /// <returns>Whether it describes a summaries response.</returns>
        public static bool Describes(ResponseShape shape) => shape
            is ResponseShape.Summaries
            or ResponseShape.SummariesWithTags
            or ResponseShape.SummariesWithContext
            or ResponseShape.SummariesWithTagsAndContext;

        /// <summary>Whether a shape declares the consolidated tag list.</summary>
        /// <param name="shape">The shape.</param>
        /// <returns>Whether "t" is part of it.</returns>
        public static bool IncludesTags(ResponseShape shape) => shape
            is ResponseShape.SummariesWithTags
            or ResponseShape.SummariesWithTagsAndContext;

        /// <summary>Whether a shape declares the context affinities.</summary>
        /// <param name="shape">The shape.</param>
        /// <returns>Whether "w" and "d" are part of it.</returns>
        public static bool IncludesContext(ResponseShape shape) => shape
            is ResponseShape.SummariesWithContext
            or ResponseShape.SummariesWithTagsAndContext;
    }

    /// <summary>
    /// One request to the LLM. The user prompt is split at the point where it stops
    /// being reusable: <paramref name="CacheablePrefix"/> is byte-identical across the
    /// per-user passes for a given batch, <paramref name="VariableSuffix"/> is not.
    /// Providers that support prompt caching mark the boundary; the rest concatenate.
    /// </summary>
    /// <param name="SystemPrompt">The system prompt.</param>
    /// <param name="CacheablePrefix">The reusable leading portion of the user prompt.</param>
    /// <param name="VariableSuffix">The per-request trailing portion of the user prompt.</param>
    /// <param name="MaxOutputTokens">Maximum output tokens for this call.</param>
    /// <param name="Shape">The JSON object being asked for; used by providers with structured outputs.</param>
    /// <param name="ConversationId">
    /// Groups the calls of one run so a provider can route them to the same cache.
    /// Null omits the routing hint entirely.
    /// </param>
    public sealed record LlmRequest(
        string SystemPrompt,
        string CacheablePrefix,
        string VariableSuffix,
        int MaxOutputTokens,
        ResponseShape Shape = ResponseShape.Categories,
        string? ConversationId = null)
    {
        /// <summary>
        /// Gets the whole user prompt, for providers with no caching support.
        /// </summary>
        public string UserPrompt => CacheablePrefix + VariableSuffix;
    }

    /// <summary>
    /// The result of one LLM call, with authoritative token usage from the provider.
    /// </summary>
    /// <param name="Text">The model's text output.</param>
    /// <param name="InputTokens">Uncached input tokens billed, as reported by the provider.</param>
    /// <param name="OutputTokens">Output tokens billed, as reported by the provider.</param>
    /// <param name="Truncated">Whether the output was cut off by the output-token cap.</param>
    /// <param name="CacheWriteTokens">Input tokens written to the prompt cache; 0 when unsupported.</param>
    /// <param name="CacheReadTokens">Input tokens served from the prompt cache; 0 when unsupported.</param>
    /// <param name="ThinkingTokens">
    /// The share of <paramref name="OutputTokens"/> spent reasoning rather than
    /// answering, where the provider reports it separately; 0 when unknown.
    /// Included in the output total because it is billed as output — this is the
    /// breakdown, not an addition. Worth surfacing because thinking and the visible
    /// answer compete for one output cap, and thinking winning that race is how a
    /// response gets truncated mid-JSON.
    /// </param>
    public sealed record LlmResult(
        string Text,
        long InputTokens,
        long OutputTokens,
        bool Truncated,
        long CacheWriteTokens = 0,
        long CacheReadTokens = 0,
        long ThinkingTokens = 0);

    /// <summary>
    /// One request in a batch submission, paired with the caller's key for it.
    /// </summary>
    /// <param name="CustomId">Caller-assigned key; results are matched back by this.</param>
    /// <param name="Request">The request.</param>
    public sealed record BatchLlmRequest(string CustomId, LlmRequest Request);

    /// <summary>
    /// One result from a batch submission. <paramref name="Result"/> is null when
    /// that request errored or expired, in which case <paramref name="Error"/> says why.
    /// </summary>
    /// <param name="CustomId">The key supplied on the corresponding request.</param>
    /// <param name="Result">The completion, or null if this request did not succeed.</param>
    /// <param name="Error">A short description of the failure, or null on success.</param>
    public sealed record BatchLlmResult(string CustomId, LlmResult? Result, string? Error);

    /// <summary>
    /// A provider that can submit many requests as one asynchronous job. Implemented
    /// only where the backend actually offers it; callers must fall back to
    /// <see cref="ILlmProvider.CompleteAsync"/> when a provider does not.
    /// </summary>
    public interface IBatchLlmProvider
    {
        /// <summary>
        /// Submits every request as one job, waits for it to finish, and returns the
        /// results. Ordering is not preserved — match on <see cref="BatchLlmResult.CustomId"/>,
        /// never on position.
        /// </summary>
        /// <param name="requests">The requests, each with a unique custom id.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>One result per submitted request.</returns>
        Task<IReadOnlyList<BatchLlmResult>> CompleteBatchAsync(
            IReadOnlyList<BatchLlmRequest> requests,
            CancellationToken cancellationToken);
    }

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
