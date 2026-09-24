namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>Current allowance evaluation and collection health for an account.</summary>
    public sealed class ProviderUsageStatus
    {
        #region Public-Members

        /// <summary>Account identifier.</summary>
        public string AccountId { get; set; } = String.Empty;

        /// <summary>Normal, Low, Reserve, Exhausted, Partial, or Unknown. Partial means one Cursor pool is exhausted while another remains usable.</summary>
        public string State { get; set; } = "Unknown";

        /// <summary>Safe explanation of the evaluated state.</summary>
        public string Reason { get; set; } = String.Empty;

        /// <summary>Last accepted measurement time.</summary>
        public DateTime? ObservedUtc { get; set; }

        /// <summary>Snapshot source label.</summary>
        public string Source { get; set; } = "none";

        /// <summary>Safe collection error code; never raw provider output.</summary>
        public string? CollectionError { get; set; }

        /// <summary>Captain runtime whose login the account owns, or null for a usage-only account.</summary>
        public string? Runtime { get; set; }

        /// <summary>When the runtime's login status command last reported on this account's home, or null before any probe.</summary>
        public DateTime? LoginCheckedUtc { get; set; }

        /// <summary>When a quota, billing, or authentication failure on one captain holds the whole account Exhausted.</summary>
        public DateTime? ExhaustedUntilUtc { get; set; }

        /// <summary>Last known usage windows; inspect measurement time before use.</summary>
        public List<ProviderUsageWindow> Windows { get; set; } = new List<ProviderUsageWindow>();

        #endregion
    }
}
