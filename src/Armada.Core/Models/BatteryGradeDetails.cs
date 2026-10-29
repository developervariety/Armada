namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// The evidence behind a grade. Which lists are filled depends on the grader type.
    /// </summary>
    public class BatteryGradeDetails
    {
        #region Public-Members

        /// <summary>
        /// Command and Mutation: the outcome of each command that ran.
        /// </summary>
        public List<BatteryCommandOutcome> Commands
        {
            get => _Commands;
            set => _Commands = value ?? new List<BatteryCommandOutcome>();
        }

        /// <summary>
        /// ByteComparison: the comparison of each file.
        /// </summary>
        public List<BatteryFileComparison> Files
        {
            get => _Files;
            set => _Files = value ?? new List<BatteryFileComparison>();
        }

        /// <summary>
        /// Mutation: the outcome of each mutant.
        /// </summary>
        public List<BatteryMutantOutcome> Mutants
        {
            get => _Mutants;
            set => _Mutants = value ?? new List<BatteryMutantOutcome>();
        }

        #endregion

        #region Private-Members

        private List<BatteryCommandOutcome> _Commands = new List<BatteryCommandOutcome>();
        private List<BatteryFileComparison> _Files = new List<BatteryFileComparison>();
        private List<BatteryMutantOutcome> _Mutants = new List<BatteryMutantOutcome>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatteryGradeDetails()
        {
        }

        #endregion
    }
}
