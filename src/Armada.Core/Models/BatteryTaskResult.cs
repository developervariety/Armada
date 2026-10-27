namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// One graded attempt of one battery task within a run.
    /// </summary>
    public class BatteryTaskResult
    {
        #region Public-Members

        /// <summary>
        /// Unique identifier (btr_ prefix).
        /// </summary>
        public string Id
        {
            get => _Id;
            set => _Id = String.IsNullOrEmpty(value) ? throw new ArgumentNullException(nameof(Id)) : value;
        }

        /// <summary>
        /// Run (bat_ prefix).
        /// </summary>
        public string RunId
        {
            get => _RunId;
            set => _RunId = value ?? String.Empty;
        }

        /// <summary>
        /// Tenant.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Pack.
        /// </summary>
        public string PackId
        {
            get => _PackId;
            set => _PackId = value ?? String.Empty;
        }

        /// <summary>
        /// Pack battery version.
        /// </summary>
        public int PackVersion { get; set; } = 0;

        /// <summary>
        /// Task.
        /// </summary>
        public string TaskId
        {
            get => _TaskId;
            set => _TaskId = value ?? String.Empty;
        }

        /// <summary>
        /// Task title.
        /// </summary>
        public string? TaskTitle { get; set; } = null;

        /// <summary>
        /// Persona the task tests.
        /// </summary>
        public string? Persona { get; set; } = null;

        /// <summary>
        /// Grader type.
        /// </summary>
        public BatteryGraderTypeEnum Grader { get; set; } = BatteryGraderTypeEnum.Command;

        /// <summary>
        /// Which run of the task (1 to the run count).
        /// </summary>
        public int Attempt { get; set; } = 1;

        /// <summary>
        /// Outcome.
        /// </summary>
        public BatteryTaskOutcomeEnum Outcome { get; set; } = BatteryTaskOutcomeEnum.Failed;

        /// <summary>
        /// Fraction from 0 to 1 where the grader has one, or null.
        /// </summary>
        public double? Score { get; set; } = null;

        /// <summary>
        /// One-line summary.
        /// </summary>
        public string? Summary { get; set; } = null;

        /// <summary>
        /// Why it failed or could not run, or null.
        /// </summary>
        public string? Reason { get; set; } = null;

        /// <summary>
        /// Grader evidence.
        /// </summary>
        public BatteryGradeDetails Details { get; set; } = new BatteryGradeDetails();

        /// <summary>
        /// The captain's raw output (kept for the evaluator; capped at 64 KB).
        /// </summary>
        public string? Output { get; set; } = null;

        /// <summary>
        /// Fixture vessel.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Voyage dispatched for the attempt.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Mission dispatched for the attempt.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Mission status when grading started.
        /// </summary>
        public string? MissionStatus { get; set; } = null;

        /// <summary>
        /// Fixture commit the task started from (after the reset).
        /// </summary>
        public string? FixtureCommit { get; set; } = null;

        /// <summary>
        /// Duration of dispatch, execution and grading in milliseconds.
        /// </summary>
        public long DurationMs { get; set; } = 0;

        /// <summary>
        /// Created (UTC).
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Completed (UTC).
        /// </summary>
        public DateTime? CompletedUtc { get; set; } = null;

        #endregion

        #region Private-Members

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.BatteryTaskResultIdPrefix, 24);
        private string _RunId = String.Empty;
        private string _PackId = String.Empty;
        private string _TaskId = String.Empty;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatteryTaskResult()
        {
        }

        #endregion
    }
}
