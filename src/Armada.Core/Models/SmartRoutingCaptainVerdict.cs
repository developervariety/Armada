namespace Armada.Core.Models
{
    using System;

    /// <summary>How the Smart Routing usage filter treated one captain of the Legacy Routing order.</summary>
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

        /// <summary><c>kept</c>, <c>demoted</c>, <c>removed</c>, or <c>outside_routes</c>.</summary>
        public string Outcome { get; set; } = String.Empty;

        /// <summary>The safe reason code for the outcome.</summary>
        public string Reason { get; set; } = String.Empty;

        #endregion
    }
}
