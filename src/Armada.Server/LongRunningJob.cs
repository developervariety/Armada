namespace Armada.Server
{
    using System;

    /// <summary>
    /// Describes a process-local long-running operation and its current state.
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

        #endregion

        #region Internal-Methods

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
                FailureMessage = FailureMessage
            };
        }

        #endregion
    }
}
