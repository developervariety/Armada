namespace Armada.Core.Models
{
    using System;

    /// <summary>How one layer of routing treated one captain of the pool: the eligibility layer (persona lock and tier floor), the persona route restriction, or the usage filter.</summary>
    public sealed class SmartRoutingCaptainVerdict
    {
        #region Public-Members

        /// <summary>Captain identifier.</summary>
        public string CaptainId { get; set; } = String.Empty;

        /// <summary>Captain model, when set.</summary>
        public string? Model { get; set; }

        /// <summary>The captain's usage account, or null when it belongs to none.</summary>
        public string? AccountId { get; set; }

        /// <summary>The account usage state for the captain's model (Normal, Low, Reserve, Exhausted, Unknown), or null without an account.</summary>
        public string? State { get; set; }

        /// <summary>The layer that decided the outcome: <c>eligibility</c>, <c>routes</c>, or <c>usage</c>.</summary>
        public string Layer { get; set; } = String.Empty;

        /// <summary><c>kept</c>, <c>demoted</c>, <c>removed</c>, <c>outside_routes</c>, or <c>excluded</c>.</summary>
        public string Outcome { get; set; } = String.Empty;

        /// <summary>The safe reason code for the outcome.</summary>
        public string Reason { get; set; } = String.Empty;

        #endregion
    }
}
