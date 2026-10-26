namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>Mutable progress for one immutable target snapshot.</summary>
    public sealed class FleetActionRunTargetState
    {
        /// <summary>Target identity matching the immutable snapshot.</summary>
        public string TargetId { get; set; } = string.Empty;
        /// <summary>Current target status.</summary>
        public FleetActionTargetStatusEnum Status { get; set; } = FleetActionTargetStatusEnum.Pending;
        /// <summary>Exact voyage identifier returned by dispatch.</summary>
        public string? VoyageId { get; set; }
        /// <summary>Whether cancellation was requested while dispatch was in flight.</summary>
        public bool CancelRequested { get; set; }
        /// <summary>Bounded explanatory reason.</summary>
        public string? Reason { get; set; }
        /// <summary>Bounded error summary.</summary>
        public string? Error { get; set; }
        /// <summary>Time when dispatch was claimed.</summary>
        public DateTime? DispatchStartedUtc { get; set; }
        /// <summary>Work start time in UTC.</summary>
        public DateTime? StartedUtc { get; set; }
        /// <summary>Terminal time in UTC.</summary>
        public DateTime? CompletedUtc { get; set; }
        /// <summary>Last update time in UTC.</summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;
    }
}
