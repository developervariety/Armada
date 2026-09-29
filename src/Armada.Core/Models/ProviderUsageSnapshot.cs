namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>Measured allowance across all relevant provider windows.</summary>
    public sealed class ProviderUsageSnapshot
    {
        #region Public-Members

        /// <summary>UTC time the provider was queried, not the time the file was read.</summary>
        public DateTime ObservedUtc { get; set; }

        /// <summary>Non-secret source label.</summary>
        public string Source { get; set; } = "manual";

        /// <summary>Required allowance windows. An empty list means unknown usage.</summary>
        public List<ProviderUsageWindow> Windows { get; set; } = new List<ProviderUsageWindow>();

        /// <summary>
        /// The provider's own verdict on whether it still serves ordinary requests: true when it says usage is allowed
        /// and no rate limit is reached, false when it says usage is blocked, null when it does not report one. A spent
        /// window meter alone does not mean the provider refuses requests.
        /// </summary>
        public bool? ProviderAllowsUsage { get; set; } = null;

        #endregion
    }
}
