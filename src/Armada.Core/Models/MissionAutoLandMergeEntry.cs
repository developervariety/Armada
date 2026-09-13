namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Latest merge entry for a mission with its recorded auto-land audit fields.
    /// </summary>
    public class MissionAutoLandMergeEntry
    {
        #region Public-Members

        /// <summary>
        /// Merge entry identifier.
        /// </summary>
        public string EntryId { get; set; } = "";

        /// <summary>
        /// Current merge entry status.
        /// </summary>
        public MergeStatusEnum Status { get; set; } = MergeStatusEnum.Queued;

        /// <summary>
        /// Audit lane (Fast or Deferred), or null when no auto-land audit ran.
        /// </summary>
        public string? AuditLane { get; set; } = null;

        /// <summary>
        /// Whether the convention check passed, or null when it did not run.
        /// </summary>
        public bool? AuditConventionPassed { get; set; } = null;

        /// <summary>
        /// Critical triggers that fired, comma-separated, or null.
        /// </summary>
        public string? AuditCriticalTrigger { get; set; } = null;

        /// <summary>
        /// Whether a deep review was picked, or null when no audit ran.
        /// </summary>
        public bool? AuditDeepPicked { get; set; } = null;

        /// <summary>
        /// Deep review verdict, or null.
        /// </summary>
        public string? AuditDeepVerdict { get; set; } = null;

        /// <summary>
        /// UTC time the deep review completed, or null.
        /// </summary>
        public DateTime? AuditDeepCompletedUtc { get; set; } = null;

        /// <summary>
        /// UTC creation time.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// UTC last update time.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        #endregion
    }
}
