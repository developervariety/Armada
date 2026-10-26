namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>Mutable run progress stored independently from its input snapshot.</summary>
    public sealed class FleetActionRunState
    {
        /// <summary>Aggregate run status.</summary>
        public FleetActionRunStatusEnum Status { get; set; } = FleetActionRunStatusEnum.Pending;
        /// <summary>First start time in UTC.</summary>
        public DateTime? StartedUtc { get; set; }
        /// <summary>Terminal time in UTC.</summary>
        public DateTime? CompletedUtc { get; set; }
        /// <summary>Current state for every snapshotted target.</summary>
        public List<FleetActionRunTargetState> TargetStates { get; set; } = new List<FleetActionRunTargetState>();
    }
}
