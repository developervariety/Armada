namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Result of an attempt to start an unattended lead cycle.
    /// </summary>
    public class LeadCycleStartResult
    {
        #region Public-Members

        /// <summary>
        /// True when the caller acquired the shared lead lease.
        /// </summary>
        public bool Acquired { get; set; } = false;

        /// <summary>
        /// Cycle identifier when acquisition succeeded.
        /// </summary>
        public string? CycleId { get; set; } = null;

        /// <summary>
        /// Effective lead operating mode.
        /// </summary>
        public LeadOperatingModeEnum Mode { get; set; } = LeadOperatingModeEnum.LegacyPrimary;

        /// <summary>
        /// Lease deadline when acquisition succeeded.
        /// </summary>
        public DateTime? DeadlineUtc { get; set; } = null;

        /// <summary>
        /// Reason acquisition was refused.
        /// </summary>
        public string? RefusalReason { get; set; } = null;

        #endregion
    }
}
