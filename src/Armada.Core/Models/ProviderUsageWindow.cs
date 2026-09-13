namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>One account or model allowance window.</summary>
    public sealed class ProviderUsageWindow
    {
        #region Public-Members

        /// <summary>Unique window identifier.</summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>Measured percentage remaining from 0 through 100; null means unknown.</summary>
        public double? RemainingPercent { get; set; }

        /// <summary>Provider-published UTC reset. Passed resets require a new measurement.</summary>
        public DateTime? ResetsUtc { get; set; }

        /// <summary>Models subject to this window; empty applies to every account model.</summary>
        public List<string> Models { get; set; } = new List<string>();

        #endregion
    }
}
