namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// One runbook execution linked to a mission's incident.
    /// </summary>
    public class MissionRecoveryRunbookExecution
    {
        #region Public-Members

        /// <summary>
        /// Execution identifier.
        /// </summary>
        public string ExecutionId { get; set; } = "";

        /// <summary>
        /// Runbook identifier.
        /// </summary>
        public string RunbookId { get; set; } = "";

        /// <summary>
        /// Execution title.
        /// </summary>
        public string Title { get; set; } = "";

        /// <summary>
        /// Execution status.
        /// </summary>
        public RunbookExecutionStatusEnum Status { get; set; } = RunbookExecutionStatusEnum.Running;

        /// <summary>
        /// UTC start time.
        /// </summary>
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// UTC completion time, or null.
        /// </summary>
        public DateTime? CompletedUtc { get; set; } = null;

        #endregion
    }
}
