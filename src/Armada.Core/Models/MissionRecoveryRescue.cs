namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// One autonomous rescue mission dispatched for a failed mission.
    /// </summary>
    public class MissionRecoveryRescue
    {
        #region Public-Members

        /// <summary>
        /// Rescue mission identifier.
        /// </summary>
        public string MissionId { get; set; } = "";

        /// <summary>
        /// Rescue mission title.
        /// </summary>
        public string Title { get; set; } = "";

        /// <summary>
        /// Current rescue mission status. A Complete status is not evidence that its work landed.
        /// </summary>
        public MissionStatusEnum Status { get; set; } = MissionStatusEnum.Pending;

        /// <summary>
        /// Voyage the rescue runs in, or null.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Captain assigned to the rescue, or null.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// Recorded rescue commit hash, or null.
        /// </summary>
        public string? CommitHash { get; set; } = null;

        /// <summary>
        /// Redacted and bounded rescue failure reason, or null.
        /// </summary>
        public string? FailureReason { get; set; } = null;

        /// <summary>
        /// UTC creation time.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// UTC completion time, or null.
        /// </summary>
        public DateTime? CompletedUtc { get; set; } = null;

        #endregion
    }
}
