namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// A grader's decision on one battery task attempt.
    /// </summary>
    public class BatteryGrade
    {
        #region Public-Members

        /// <summary>
        /// Outcome.
        /// </summary>
        public BatteryTaskOutcomeEnum Outcome { get; set; } = BatteryTaskOutcomeEnum.Failed;

        /// <summary>
        /// Fraction from 0 to 1 where the grader has one (seeded items found, mutants killed), or null.
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
        /// Evidence.
        /// </summary>
        public BatteryGradeDetails Details { get; set; } = new BatteryGradeDetails();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatteryGrade()
        {
        }

        #endregion
    }
}
