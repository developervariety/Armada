namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// A request-independent background job with a time-ordered id and a pollable status. Enqueued by a
    /// caller, claimed and advanced by the job coordinator, and read back over REST/MCP for status polling.
    /// Every field the API sorts, filters, or reports on is a first-class column, not a blob.
    /// </summary>
    public class Job
    {
        #region Public-Members

        /// <summary>
        /// Unique, time-sortable identifier (job_ prefix).
        /// </summary>
        public string Id
        {
            get => _Id;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(Id));
                _Id = value;
            }
        }

        /// <summary>
        /// Tenant identifier the job belongs to.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// User identifier that owns the job.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Human-readable job name/title.
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// The kind of work this job performs.
        /// </summary>
        public JobKindEnum Kind { get; set; } = JobKindEnum.Generic;

        /// <summary>
        /// Lifecycle status.
        /// </summary>
        public JobStatusEnum Status { get; set; } = JobStatusEnum.Queued;

        /// <summary>
        /// Completion percentage, clamped to [0, 100].
        /// </summary>
        public int Progress
        {
            get => _Progress;
            set => _Progress = value < 0 ? 0 : (value > 100 ? 100 : value);
        }

        /// <summary>
        /// Optional job-specific result payload as JSON (an explicit, documented raw-JSON exception used
        /// only for the opaque per-kind result), or null.
        /// </summary>
        public string? ResultJson { get; set; } = null;

        /// <summary>
        /// Failure reason when <see cref="Status"/> is Failed; null otherwise.
        /// </summary>
        public string? ErrorReason { get; set; } = null;

        /// <summary>
        /// UTC creation time.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// UTC time the job started running, or null.
        /// </summary>
        public DateTime? StartedUtc { get; set; } = null;

        /// <summary>
        /// UTC time the job reached a terminal status, or null.
        /// </summary>
        public DateTime? CompletedUtc { get; set; } = null;

        /// <summary>
        /// UTC time of the last update.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Private-Members

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.JobIdPrefix, 24);
        private int _Progress = 0;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public Job()
        {
        }

        /// <summary>
        /// Instantiate with a name and kind.
        /// </summary>
        /// <param name="name">Job name.</param>
        /// <param name="kind">Job kind.</param>
        public Job(string name, JobKindEnum kind = JobKindEnum.Generic)
        {
            Name = name ?? "";
            Kind = kind;
        }

        #endregion
    }
}
