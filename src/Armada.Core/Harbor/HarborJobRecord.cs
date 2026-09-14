namespace Armada.Core.Harbor
{
    using System;

    /// <summary>
    /// Durable record of a job the Admiral launched on a Harbor runner. The record outlives the Admiral process:
    /// after a restart every job that was not terminal is failed with a named reason instead of being forgotten.
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

        /// <summary>Whether a state is terminal: the job can no longer run or report.</summary>
        /// <param name="state">Job state.</param>
        /// <returns>True for Exited, Failed and Lost.</returns>
        public static bool IsTerminal(HarborJobStateEnum state)
        {
            return state == HarborJobStateEnum.Exited || state == HarborJobStateEnum.Failed || state == HarborJobStateEnum.Lost;
        }

        #endregion
    }
}
