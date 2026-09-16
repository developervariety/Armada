namespace Armada.Core.Models
{
    using System;

    /// <summary>Outcome of a hard usage refresh for one subscription account.</summary>
    public sealed class UsageAccountRefreshResult
    {
        #region Public-Members

        /// <summary>Account identifier.</summary>
        public string AccountId { get; set; } = String.Empty;

        /// <summary>True when the provider or snapshot file was read by this refresh.</summary>
        public bool Collected { get; set; } = false;

        /// <summary>
        /// Named outcome: <c>usage_refreshed</c>, <c>usage_refresh_rate_limited</c> (the provider asked to wait, so it
        /// was not called), <c>usage_refresh_manual_snapshot</c> (nothing to collect), or the collection error code.
        /// </summary>
        public string Reason { get; set; } = String.Empty;

        /// <summary>When the provider allows the next usage read, while a provider rate limit is active.</summary>
        public DateTime? RetryAfterUtc { get; set; }

        /// <summary>True when the account's runtime login check ran again during this refresh.</summary>
        public bool LoginProbeRerun { get; set; } = false;

        /// <summary>The account's usage status after the refresh.</summary>
        public ProviderUsageStatus Status { get; set; } = new ProviderUsageStatus();

        #endregion
    }
}
