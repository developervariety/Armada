namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// How a battery task is graded. Which fields apply depends on Type; see battery/README.md.
    /// </summary>
    public class BatteryGraderDefinition
    {
        #region Public-Members

        /// <summary>
        /// Grader type.
        /// </summary>
        public BatteryGraderTypeEnum? Type { get; set; } = null;

        /// <summary>
        /// Command and Mutation: directory, relative to the pack, copied into the worktree before the commands run
        /// (hidden acceptance checks).
        /// </summary>
        public string? Overlay { get; set; } = null;

        /// <summary>
        /// Command and Mutation: commands run in the worktree, in order.
        /// </summary>
        public List<BatteryCommand> Commands
        {
            get => _Commands;
            set => _Commands = value ?? new List<BatteryCommand>();
        }

        /// <summary>
        /// ByteComparison: files that must match their expected bytes.
        /// </summary>
        public List<BatteryExpectedFile> Files
        {
            get => _Files;
            set => _Files = value ?? new List<BatteryExpectedFile>();
        }

        /// <summary>
        /// Mutation: source changes the tests must detect.
        /// </summary>
        public List<BatteryMutant> Mutants
        {
            get => _Mutants;
            set => _Mutants = value ?? new List<BatteryMutant>();
        }

        #endregion

        #region Private-Members

        private List<BatteryCommand> _Commands = new List<BatteryCommand>();
        private List<BatteryExpectedFile> _Files = new List<BatteryExpectedFile>();
        private List<BatteryMutant> _Mutants = new List<BatteryMutant>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatteryGraderDefinition()
        {
        }

        #endregion
    }
}
