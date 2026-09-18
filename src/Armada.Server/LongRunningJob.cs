namespace Armada.Server
{
    using System;

    /// <summary>
    /// Describes a long-running operation and its current state. The record is journalled to disk when
    /// the owning service has a journal directory, so it outlives the process that accepted it.
    /// </summary>
    public class LongRunningJob
    {
        #region Public-Members

        /// <summary>
        /// Generic job identifier.
        /// </summary>
        public string JobId { get; set; } = String.Empty;

        /// <summary>
        /// Name of the operation performed by the job.
        /// </summary>
        public string Operation { get; set; } = String.Empty;

        /// <summary>
        /// Current lifecycle state.
        /// </summary>
        public LongRunningJobStatusEnum Status { get; set; } = LongRunningJobStatusEnum.Accepted;

        /// <summary>
        /// UTC timestamp when the job was submitted.
        /// </summary>
        public DateTime SubmittedAtUtc { get; set; }

        /// <summary>
        /// UTC timestamp when background execution began, when available.
        /// </summary>
        public DateTime? StartedAtUtc { get; set; }

        /// <summary>
        /// UTC timestamp when background execution completed, when available.
        /// </summary>
        public DateTime? CompletedAtUtc { get; set; }

        /// <summary>
        /// Successful operation result, when available.
        /// </summary>
        public object? Result { get; set; }

        /// <summary>
        /// Bounded failure message, when available.
        /// </summary>
        public string? FailureMessage { get; set; }

        /// <summary>
        /// Objective the job acts for, when known. A failed or lost job writes an event on it.
        /// </summary>
        public string? ObjectiveId { get; set; }

        /// <summary>
        /// Vessel the job acts on, when known.
        /// </summary>
        public string? VesselId { get; set; }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// True for a status that no later transition leaves.
        /// </summary>
        internal static bool IsTerminal(LongRunningJobStatusEnum status)
        {
            return status == LongRunningJobStatusEnum.Succeeded
                || status == LongRunningJobStatusEnum.Failed
                || status == LongRunningJobStatusEnum.Lost;
        }

        /// <summary>
        /// Create a snapshot that can be returned without exposing tracked state.
        /// </summary>
        internal LongRunningJob CreateSnapshot()
        {
            return new LongRunningJob
            {
                JobId = JobId,
                Operation = Operation,
                Status = Status,
                SubmittedAtUtc = SubmittedAtUtc,
                StartedAtUtc = StartedAtUtc,
                CompletedAtUtc = CompletedAtUtc,
                Result = Result,
                FailureMessage = FailureMessage,
                ObjectiveId = ObjectiveId,
                VesselId = VesselId
            };
        }

        #endregion
    }
}
