namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Durable event payload for one unattended lead cycle.
    /// </summary>
    public class LeadCycleEventPayload
    {
        #region Public-Members

        /// <summary>
        /// Cycle identifier.
        /// </summary>
        public string CycleId { get; set; } = String.Empty;

        /// <summary>
        /// Lead implementation that owns the cycle.
        /// </summary>
        public LeadRunnerTypeEnum Runner { get; set; } = LeadRunnerTypeEnum.Legacy;

        /// <summary>
        /// Stable Armada participant key.
        /// </summary>
        public string ParticipantKey { get; set; } = String.Empty;

        /// <summary>
        /// True when the legacy lead started as a standby fallback while Grok was primary.
        /// </summary>
        public bool StandbyFallback { get; set; } = false;

        /// <summary>
        /// UTC time when the cycle acquired its lease.
        /// </summary>
        public DateTime? StartedUtc { get; set; } = null;

        /// <summary>
        /// Lease deadline for start and heartbeat events.
        /// </summary>
        public DateTime? DeadlineUtc { get; set; } = null;

        /// <summary>
        /// Handoff posted before successful completion.
        /// </summary>
        public string? Handoff { get; set; } = null;

        /// <summary>
        /// Failure or stop reason.
        /// </summary>
        public string? Reason { get; set; } = null;

        #endregion
    }
}
