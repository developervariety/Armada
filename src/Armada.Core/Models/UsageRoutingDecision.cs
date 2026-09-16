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

        /// <summary>
        /// How the D16 <c>routing_hint</c> shape hint (when one was supplied) affected route order:
        /// <c>applied_shape:&lt;shape&gt;</c>, <c>policy_tolerant</c>, <c>no_tolerant_route</c>,
        /// <c>no_shape_match</c>, <c>reserved_persona_unchanged</c>, or null when no hint applied. It
        /// never changes which routes are eligible; it only reorders already-eligible routes.
        /// </summary>
        public string? RoutingHintOutcome { get; set; } = null;

        #endregion
    }
}
