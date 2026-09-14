namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// The server-owned captain fields as they appear in a create or update request body.
    /// Every property is nullable so an absent field reads as null and can be told apart
    /// from a field the caller actually sent.
    /// </summary>
    public class CaptainServerOwnedFields
    {
        #region Public-Members

        /// <summary>
        /// Captain identifier.
        /// </summary>
        public string? Id { get; set; } = null;

        /// <summary>
        /// Tenant identifier.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Owning user identifier.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Captain state.
        /// </summary>
        public CaptainStateEnum? State { get; set; } = null;

        /// <summary>
        /// Currently assigned mission identifier.
        /// </summary>
        public string? CurrentMissionId { get; set; } = null;

        /// <summary>
        /// Currently assigned dock identifier.
        /// </summary>
        public string? CurrentDockId { get; set; } = null;

        /// <summary>
        /// Operating system process identifier.
        /// </summary>
        public int? ProcessId { get; set; } = null;

        /// <summary>
        /// Auto-recovery attempts for the current mission.
        /// </summary>
        public int? RecoveryAttempts { get; set; } = null;

        /// <summary>
        /// Last output heartbeat in UTC.
        /// </summary>
        public DateTime? LastHeartbeatUtc { get; set; } = null;

        /// <summary>
        /// Last observed process liveness in UTC.
        /// </summary>
        public DateTime? LastProcessAliveUtc { get; set; } = null;

        /// <summary>
        /// Quarantine expiry in UTC.
        /// </summary>
        public DateTime? QuarantineUntilUtc { get; set; } = null;

        /// <summary>
        /// Quarantine reason.
        /// </summary>
        public string? QuarantineReason { get; set; } = null;

        /// <summary>
        /// Creation timestamp in UTC.
        /// </summary>
        public DateTime? CreatedUtc { get; set; } = null;

        /// <summary>
        /// Last update timestamp in UTC.
        /// </summary>
        public DateTime? LastUpdateUtc { get; set; } = null;

        #endregion
    }
}
