namespace Armada.Core.Harbor
{
    using System;

    /// <summary>
    /// Durable record of a job the Admiral launched on a Harbor runner. The record outlives the Admiral process:
    /// after a restart every job remains fenced until its owner confirms that its process is stopped or running.
    /// </summary>
    public sealed class HarborJobRecord
    {
        #region Public-Members

        /// <summary>Server-issued job identifier.</summary>
        public string JobId { get; set; } = String.Empty;

        /// <summary>Runner the job is bound to.</summary>
        public string RunnerId { get; set; } = String.Empty;

        /// <summary>Launch key used to reject duplicate launches.</summary>
        public string LaunchKey { get; set; } = String.Empty;

        /// <summary>Tenant the job runs for. It is always the runner's enrolled tenant.</summary>
        public string TenantId { get; set; } = String.Empty;

        /// <summary>User the job runs for. It is always the runner's enrolled user.</summary>
        public string UserId { get; set; } = String.Empty;

        /// <summary>Durable enrollment generation that authorized the job.</summary>
        public long EnrollmentGeneration { get; set; } = 0;

        /// <summary>Connection generation last allowed to report for the job.</summary>
        public long SessionGeneration { get; set; } = 0;

        /// <summary>Lifecycle state.</summary>
        public HarborJobStateEnum State { get; set; } = HarborJobStateEnum.Pending;

        /// <summary>Host process identifier reported by the runner.</summary>
        public int? ProcessId { get; set; } = null;

        /// <summary>UTC start time reported by the runner for its operating-system process.</summary>
        public DateTime? RunnerProcessStartedUtc { get; set; } = null;

        /// <summary>Exit code reported by the runner.</summary>
        public int? ExitCode { get; set; } = null;

        /// <summary>Stable reason for a failed or lost job.</summary>
        public string? FailureReason { get; set; } = null;

        /// <summary>Next output sequence the Admiral expects; the last accepted sequence is one less.</summary>
        public long NextOutputSequence { get; set; } = 0;

        /// <summary>Mission the job runs, when it was launched for a mission.</summary>
        public string? MissionId { get; set; } = null;

        /// <summary>Captain the job runs for, when it was launched for a mission.</summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>Dock that owns the mission attempt, when mission-bound.</summary>
        public string? DockId { get; set; } = null;

        /// <summary>Admiral synthetic process identifier bound to this attempt.</summary>
        public int? AdmiralProcessId { get; set; } = null;

        /// <summary>Start time of the Admiral process attempt, in UTC.</summary>
        public DateTime? AdmiralProcessStartedUtc { get; set; } = null;

        /// <summary>Whether the runner confirmed that the remote process has stopped.</summary>
        public bool StopConfirmed { get; set; } = false;

        /// <summary>Whether an operator or owner requested that this runner job stop.</summary>
        public bool StopRequested { get; set; } = false;

        /// <summary>Whether the exact lifecycle completion callback acknowledged this terminal result.</summary>
        public bool CompletionDelivered { get; set; } = false;

        /// <summary>Monotonic revision. A stored record is replaced only by a record with a higher revision.</summary>
        public long Revision { get; set; } = 0;

        /// <summary>Creation time in UTC.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Last change time in UTC.</summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Time the job reached a terminal state, in UTC.</summary>
        public DateTime? CompletedUtc { get; set; } = null;

        #endregion

        #region Public-Methods

        /// <summary>Whether the control state is terminal. StopConfirmed separately proves that the remote writer stopped.</summary>
        /// <param name="state">Job state.</param>
        /// <returns>True for Exited, Failed and Lost.</returns>
        public static bool IsTerminal(HarborJobStateEnum state)
        {
            return state == HarborJobStateEnum.Exited || state == HarborJobStateEnum.Failed || state == HarborJobStateEnum.Lost;
        }

        #endregion
    }
}
