namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// One mission examined by a terminal-voyage mission reconciliation pass.
    /// </summary>
    public sealed class TerminalVoyageMissionReconciliationItem
    {
        #region Public-Members

        /// <summary>
        /// Mission identifier.
        /// </summary>
        public string MissionId { get; set; } = string.Empty;

        /// <summary>
        /// Voyage identifier.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Vessel identifier.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Mission persona.
        /// </summary>
        public string? Persona { get; set; } = null;

        /// <summary>
        /// Mission commit.
        /// </summary>
        public string? CommitHash { get; set; } = null;

        /// <summary>
        /// Voyage status at the time of the pass.
        /// </summary>
        public VoyageStatusEnum? VoyageStatus { get; set; } = null;

        /// <summary>
        /// Landing probe result, when a probe ran.
        /// </summary>
        public TerminalVoyageLandingProbeEnum? Landing { get; set; } = null;

        /// <summary>
        /// Mission status before the pass.
        /// </summary>
        public MissionStatusEnum FromStatus { get; set; } = MissionStatusEnum.WorkProduced;

        /// <summary>
        /// Status the mission moved to (or would move to in a dry run); null when left unchanged.
        /// </summary>
        public MissionStatusEnum? ToStatus { get; set; } = null;

        /// <summary>
        /// Reason code for the decision.
        /// </summary>
        public string Reason { get; set; } = string.Empty;

        #endregion
    }
}
