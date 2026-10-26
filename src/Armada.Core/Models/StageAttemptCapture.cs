namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// What the exit handler knew about a stage attempt when it claimed the agent's exit: the mission as it was before
    /// Armada acted on the exit, the captain, the exit, and the stage check. The stage outcome is recorded from it once
    /// the handler has finished.
    /// </summary>
    public class StageAttemptCapture
    {
        #region Public-Members

        /// <summary>
        /// Whether this handler claimed the exit (only a claimed exit is recorded).
        /// </summary>
        public bool Claimed { get; set; } = false;

        /// <summary>
        /// Mission identifier.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Captain that ran the attempt, as read at exit.
        /// </summary>
        public Captain? Captain { get; set; } = null;

        /// <summary>
        /// Mission status before the exit was handled.
        /// </summary>
        public MissionStatusEnum StatusBefore { get; set; } = MissionStatusEnum.InProgress;

        /// <summary>
        /// Output repair attempts before the exit was handled.
        /// </summary>
        public int OutputRepairAttemptsBefore { get; set; } = 0;

        /// <summary>
        /// Redispatch attempts before the exit was handled.
        /// </summary>
        public int RedispatchAttemptsBefore { get; set; } = 0;

        /// <summary>
        /// When the mission (attempt) started, or null.
        /// </summary>
        public DateTime? StartedUtc { get; set; } = null;

        /// <summary>
        /// Dock the attempt ran in, or null.
        /// </summary>
        public string? DockId { get; set; } = null;

        /// <summary>
        /// Agent exit code, or null.
        /// </summary>
        public int? ExitCode { get; set; } = null;

        /// <summary>
        /// Runtime classification of the exit, or null.
        /// </summary>
        public RuntimeFailureKindEnum? RuntimeFailureKind { get; set; } = null;

        /// <summary>
        /// Stage check result, or null when no check ran.
        /// </summary>
        public StageCheckResult? StageCheck { get; set; } = null;

        /// <summary>
        /// When the agent exited (UTC).
        /// </summary>
        public DateTime EndedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When the exit escalated the mission to another captain: that captain, or null.
        /// </summary>
        public string? EscalatedToCaptainId { get; set; } = null;

        /// <summary>
        /// When the exit escalated the mission: the commit the attempt left on the branch, or null when unknown.
        /// </summary>
        public string? EscalatedCommit { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public StageAttemptCapture()
        {
        }

        #endregion
    }
}
