namespace Armada.Core.Models
{
    using System;

    /// <summary>A durable run with immutable input snapshots and mutable state.</summary>
    public sealed class FleetActionRun
    {
        /// <summary>Run identifier.</summary>
        public string Id { get; set; } = Constants.IdGenerator.GenerateKSortable("far_", 24);
        /// <summary>Initiating actor tenant identifier.</summary>
        public string TenantId { get; set; } = string.Empty;
        /// <summary>Initiating actor user identifier.</summary>
        public string UserId { get; set; } = string.Empty;
        /// <summary>Source action identifier.</summary>
        public string ActionId { get; set; } = string.Empty;
        /// <summary>Caller request identifier used for replay.</summary>
        public string RequestId { get; set; } = string.Empty;
        /// <summary>SHA-256 fingerprint of canonical caller input.</summary>
        public string RequestFingerprint { get; set; } = string.Empty;
        /// <summary>Revision used for whole-state compare-and-set.</summary>
        public long Revision { get; set; }
        /// <summary>Immutable action and target snapshots.</summary>
        public FleetActionRunSnapshot Snapshot { get; set; } = new FleetActionRunSnapshot();
        /// <summary>Mutable run and target state.</summary>
        public FleetActionRunState State { get; set; } = new FleetActionRunState();
        /// <summary>Creation time in UTC.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        /// <summary>Last update time in UTC.</summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;
    }
}
