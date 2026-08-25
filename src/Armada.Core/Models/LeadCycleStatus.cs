namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Current shared unattended lead state.
    /// </summary>
    public class LeadCycleStatus
    {
        #region Public-Members

        /// <summary>
        /// Effective lead operating mode.
        /// </summary>
        public LeadOperatingModeEnum Mode { get; set; } = LeadOperatingModeEnum.LegacyPrimary;

        /// <summary>
        /// True when a non-expired cycle lease exists.
        /// </summary>
        public bool Active { get; set; } = false;

        /// <summary>
        /// Active cycle identifier.
        /// </summary>
        public string? CycleId { get; set; } = null;

        /// <summary>
        /// UTC time when the active cycle started.
        /// </summary>
        public DateTime? StartedUtc { get; set; } = null;

        /// <summary>
        /// Active lease deadline.
        /// </summary>
        public DateTime? DeadlineUtc { get; set; } = null;

        /// <summary>
        /// Lead implementation from the matching start event, when available.
        /// </summary>
        public LeadRunnerTypeEnum? Runner { get; set; } = null;

        /// <summary>
        /// Stable participant key from the matching start event, when available.
        /// </summary>
        public string? ParticipantKey { get; set; } = null;

        #endregion
    }
}
