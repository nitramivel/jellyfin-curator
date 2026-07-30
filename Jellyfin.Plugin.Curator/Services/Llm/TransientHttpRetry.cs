using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Curator.Services.Llm
{
    /// <summary>
    /// Re-sends a request through the failures that are ordinary rather than
    /// wrong.
    ///
    /// <para>
    /// A run fires one library-sized request per user in quick succession, and
    /// every hosted provider meters per minute, so 429 is an expected part of
    /// normal operation here — not an error condition. Without a retry a single
    /// one ends the run and discards every pass already paid for. 500, 502, 503
    /// and 504 are retried on the same grounds.
    /// </para>
    /// <para>
    /// Everything else is raised immediately, so a bad key, a wrong model id or a
    /// malformed request still fails fast instead of taking a minute to do it.
    /// </para>
    /// <para>
    /// This is transport-level and distinct from the single retry
    /// <see cref="CategoryProposalService"/> does on an unparseable body: that one
    /// re-samples the model, this one re-sends the same request unchanged.
    /// </para>
    /// </summary>
    public static class TransientHttpRetry
    {
        /// <summary>How many times one request is sent before giving up.</summary>
        public const int MaxAttempts = 4;

        /// <summary>The first backoff step, doubling from there.</summary>
        public static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(5);

        /// <summary>The longest a single backoff will wait.</summary>
        public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Sends until it succeeds, hits a permanent failure, or runs out of
        /// attempts, and returns the successful body.
        /// </summary>
        /// <param name="httpClient">The HTTP client.</param>
        /// <param name="buildRequest">
        /// Builds a fresh request. Called once per attempt — an
        /// <see cref="HttpRequestMessage"/> cannot be sent twice.
        /// </param>
        /// <param name="describeFailure">Turns a status and body into the exception message.</param>
        /// <param name="initialDelay">First backoff step; defaults to <see cref="DefaultInitialDelay"/>.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The response body of the first successful attempt.</returns>
        public static async Task<string> SendAsync(
            HttpClient httpClient,
            Func<HttpRequestMessage> buildRequest,
            Func<HttpStatusCode, string, string> describeFailure,
            TimeSpan? initialDelay,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(httpClient);
            ArgumentNullException.ThrowIfNull(buildRequest);
            ArgumentNullException.ThrowIfNull(describeFailure);

            var delay = initialDelay ?? DefaultInitialDelay;

            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var message = buildRequest();
                using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return body;
                }

                if (!IsTransient(response.StatusCode) || attempt >= MaxAttempts)
                {
                    throw new HttpRequestException(describeFailure(response.StatusCode, body));
                }

                // The server's own pacing beats any curve we invent: a quota error
                // knows when the window resets and a backoff does not.
                var wait = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } date
                        ? (TimeSpan?)(date - DateTimeOffset.UtcNow)
                        : null)
                    ?? delay;

                if (wait < TimeSpan.Zero)
                {
                    wait = delay;
                }

                await Task.Delay(wait > MaxDelay ? MaxDelay : wait, cancellationToken).ConfigureAwait(false);
                delay += delay;
            }
        }

        private static bool IsTransient(HttpStatusCode status)
        {
            return status is HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;
        }
    }
}
