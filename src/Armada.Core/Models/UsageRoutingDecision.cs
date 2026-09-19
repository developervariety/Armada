namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>A Smart Routing result: the Legacy Routing order, the usage filter, the persona model groups, and the chosen captain.</summary>
    public sealed class UsageRoutingDecision
    {
        #region Public-Members

        /// <summary>The Legacy Routing order of the route-restricted pool, before the usage filter.</summary>
        public List<Captain> LegacyOrder { get; set; } = new List<Captain>();

        /// <summary>Per-captain usage verdicts, in Legacy Routing order, followed by captains a route restriction excluded.</summary>
        public List<SmartRoutingCaptainVerdict> Verdicts { get; set; } = new List<SmartRoutingCaptainVerdict>();

        /// <summary>The persona model groups in the order they were tried; empty when the persona has no model preference.</summary>
        public List<SmartRoutingModelGroup> Groups { get; set; } = new List<SmartRoutingModelGroup>();

        /// <summary>Eligible captains in final Smart Routing order; the first is chosen.</summary>
        public List<Captain> Candidates { get; set; } = new List<Captain>();

        /// <summary>True when a persona route restriction applies.</summary>
        public bool HasPersonaRoutes { get; set; } = false;

        /// <summary>True when the persona has a model preference entry.</summary>
        public bool HasPersonaModels { get; set; } = false;

        /// <summary>The persona model list tried first.</summary>
        public CapacityChoiceEnum Capacity { get; set; } = CapacityChoiceEnum.Default;

        /// <summary>Where the capacity reading came from; see <c>CapacityEscalationResolver</c> source constants.</summary>
        public string CapacitySource { get; set; } = String.Empty;

        /// <summary>Explains the selection or the wait.</summary>
        public string Reason { get; set; } = String.Empty;

        /// <summary>
        /// True when the capacity-chosen list named models and no eligible captain ran any of them.
        /// Routing still falls through; it does not refuse.
        /// </summary>
        public bool MatchedNothing { get; set; }

        /// <summary>Dead entries on the capacity-chosen list, when <see cref="MatchedNothing"/> is true.</summary>
        public List<DeadPersonaModelEntry> DeadListEntries { get; set; } = new List<DeadPersonaModelEntry>();

        #endregion
    }
}
