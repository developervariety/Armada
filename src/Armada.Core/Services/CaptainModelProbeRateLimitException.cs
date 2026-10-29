namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// The same captain was probed again before the minimum interval elapsed.
    /// </summary>
    public sealed class CaptainModelProbeRateLimitException : InvalidOperationException
    {
        /// <summary>
        /// UTC time when another probe of this captain is allowed.
        /// </summary>
        public DateTime RetryAfterUtc { get; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="retryAfterUtc">UTC time when another probe is allowed.</param>
        public CaptainModelProbeRateLimitException(DateTime retryAfterUtc)
            : base("captain_model_probe_rate_limited")
        {
            RetryAfterUtc = retryAfterUtc;
        }
    }
}
