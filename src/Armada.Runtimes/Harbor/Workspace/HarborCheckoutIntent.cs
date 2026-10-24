namespace Armada.Runtimes.Harbor
{
    using System;
    using Armada.Core.Models;

    /// <summary>Private local proof for one runner-owned checkout mutation.</summary>
    internal sealed class HarborCheckoutIntent
    {
        /// <summary>Creates an empty intent for strict JSON deserialization.</summary>
        public HarborCheckoutIntent() { }

        /// <summary>Immutable runner binding recorded for this checkout.</summary>
        public HarborCheckoutBinding Binding { get; set; } = new HarborCheckoutBinding();
        /// <summary>Durable Armada dock that owns the checkout.</summary>
        public string DockId { get; set; } = String.Empty;
        /// <summary>Vessel whose configured repository grant created the checkout.</summary>
        public string VesselId { get; set; } = String.Empty;
        /// <summary>Request identity that created the durable intent.</summary>
        public string RequestId { get; set; } = String.Empty;
        /// <summary>Persisted lifecycle state, such as Pending, Completed, or Failed.</summary>
        public string State { get; set; } = "Pending";
        /// <summary>UTC time of the latest durable intent update.</summary>
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        /// <summary>Named failure recorded when checkout creation could not complete safely.</summary>
        public string? FailureReason { get; set; }
    }
}
