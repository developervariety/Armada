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

        #endregion
    }
}
