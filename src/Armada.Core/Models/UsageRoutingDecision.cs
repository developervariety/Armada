namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>Preference-first routing result with an operator explanation.</summary>
    public sealed class UsageRoutingDecision
    {
        #region Public-Members

        /// <summary>Eligible candidates after usage filtering.</summary>
        public List<Captain> Candidates { get; set; } = new List<Captain>();

        /// <summary>True when an explicit persona route order applies.</summary>
        public bool HasPersonaRoutes { get; set; } = false;

        /// <summary>Explains preferred selection, conservation fallback, or waiting.</summary>
        public string Reason { get; set; } = String.Empty;

        #endregion
    }
}
