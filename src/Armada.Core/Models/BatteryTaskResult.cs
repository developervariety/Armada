namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// The grade of one task of a battery run. The attempts that produced it are the mission's attempt provenance
    /// records; this record does not repeat them.
    /// </summary>
    public class BatteryTaskResult
    {
        #region Public-Members

        /// <summary>
        /// Task identifier in the pack.
        /// </summary>
        public string TaskId
        {
            get => _TaskId;
            set => _TaskId = value ?? String.Empty;
        }

        /// <summary>
        /// Task title.
        /// </summary>
        public string? Title { get; set; } = null;

        /// <summary>
        /// The grade.
        /// </summary>
        public BatteryTaskGradeEnum Grade { get; set; } = BatteryTaskGradeEnum.CouldNotRun;

        /// <summary>
        /// Fraction of mutants killed (Mutation graders), or null.
        /// </summary>
        public double? Score { get; set; } = null;

        /// <summary>
        /// One-line summary of the grade.
        /// </summary>
        public string? Summary { get; set; } = null;

        /// <summary>
        /// Why the task failed before grading or could not be graded, or null.
        /// </summary>
        public string? Reason { get; set; } = null;

        /// <summary>
        /// Voyage that ran the task, or null when none was dispatched.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Mission that ran the task, or null when none was dispatched.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// UTC time the fixture reset began.
        /// </summary>
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// UTC time grading finished.
        /// </summary>
        public DateTime CompletedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Milliseconds from reset to the end of grading.
        /// </summary>
        public long DurationMs { get; set; } = 0;

        /// <summary>
        /// Milliseconds the mission ran, from dispatch to the end of the wait, or null when none was dispatched.
        /// </summary>
        public long? MissionDurationMs { get; set; } = null;

        /// <summary>
        /// The grading evidence.
        /// </summary>
        public BatteryGradeDetails Details { get; set; } = new BatteryGradeDetails();

        #endregion

        #region Private-Members

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
