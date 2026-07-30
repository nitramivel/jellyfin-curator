using System;
using Jellyfin.Plugin.Curator.Core;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Pins the line between "the server went away underneath this run" and "this
    /// run is broken". Getting it wrong in the reassuring direction would hide real
    /// faults, so only disposal is treated as teardown.
    /// </summary>
    public class RunFailureTests
    {
        /// <summary>
        /// The exact shape seen on 30 Jul 2026: a run orphaned by the in-process host
        /// restart that a plugin install triggers, dying at GetUserById when it
        /// reached a pooled DbContext behind a disposed provider.
        /// </summary>
        [Fact]
        public void DisposedServiceProvider_IsTeardown()
        {
            Assert.True(RunFailure.IsHostTeardown(new ObjectDisposedException("IServiceProvider")));
        }

        [Fact]
        public void DisposedProviderWrappedInAnotherException_IsStillTeardown()
        {
            var wrapped = new InvalidOperationException(
                "Failed to resolve a service.",
                new ObjectDisposedException("IServiceProvider"));

            Assert.True(RunFailure.IsHostTeardown(wrapped));
        }

        [Fact]
        public void DisposedProviderNestedTwoDeep_IsStillTeardown()
        {
            var wrapped = new InvalidOperationException(
                "outer",
                new AggregateException(new ObjectDisposedException("IServiceProvider")));

            Assert.True(RunFailure.IsHostTeardown(wrapped));
        }

        /// <summary>
        /// EF reports a disposed pooled context as InvalidOperationException, and the
        /// concrete type lives in a package Curator does not reference.
        /// </summary>
        [Fact]
        public void DisposedDbContext_IsTeardown()
        {
            Assert.True(RunFailure.IsHostTeardown(new InvalidOperationException(
                "Cannot access a disposed context instance.")));
        }

        [Fact]
        public void OrdinaryFailures_AreNotTeardown()
        {
            Assert.False(RunFailure.IsHostTeardown(new InvalidOperationException(
                "Curator: plugin configuration unavailable.")));
            Assert.False(RunFailure.IsHostTeardown(new FormatException("Model response is not valid JSON.")));
            Assert.False(RunFailure.IsHostTeardown(new HttpRequestExceptionStub()));
            Assert.False(RunFailure.IsHostTeardown(null));
        }

        /// <summary>
        /// "Disposed" alone must not be enough — an LLM provider reporting a disposed
        /// stream is a real fault and has to keep surfacing as one.
        /// </summary>
        [Fact]
        public void AnUnrelatedMentionOfDisposedIsNotTeardown()
        {
            Assert.False(RunFailure.IsHostTeardown(new InvalidOperationException(
                "The response stream was disposed before it could be read.")));
        }

        [Fact]
        public void TheTeardownMessageExplainsItselfWithoutAStackTrace()
        {
            Assert.Contains("shut down or reloaded its plugins", RunFailure.HostTeardownMessage, StringComparison.Ordinal);
            Assert.Contains("Nothing was left half-built", RunFailure.HostTeardownMessage, StringComparison.Ordinal);
        }

        private sealed class HttpRequestExceptionStub : Exception
        {
            public HttpRequestExceptionStub()
                : base("Connection refused.")
            {
            }
        }
    }
}
