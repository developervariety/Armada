namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// The result of grading one battery task.
    /// </summary>
    public class BatteryGrade
    {
        #region Public-Members

        /// <summary>
        /// The grade.
        /// </summary>
        public BatteryTaskGradeEnum Grade { get; set; } = BatteryTaskGradeEnum.Failed;

        /// <summary>
        /// Fraction of mutants killed (Mutation only), or null.
        /// </summary>
        public double? Score { get; set; } = null;

        /// <summary>
        /// One-line summary of the grade.
        /// </summary>
        public string? Summary { get; set; } = null;

        /// <summary>
        /// Why the task could not be graded or failed before grading, or null.
        /// </summary>
        public string? Reason { get; set; } = null;

        /// <summary>
        /// The evidence.
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
