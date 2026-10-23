namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// The earlier run that a check took its result from instead of running its own command. A check that carries
    /// one did no work of its own: its pass is the named run's pass, and it has no run duration.
    /// </summary>
    public class CheckRunSatisfiedBy
    {
        #region Public-Members

        /// <summary>
        /// Kind of run the result came from. Always <c>definition_of_done_gate</c>: a passing definition-of-done gate
        /// that ran the same command in full on the same commit.
        /// </summary>
        public string Kind { get; set; } = DefinitionOfDoneGate;

        /// <summary>
        /// Mission whose definition-of-done gate ran the command.
        /// </summary>
        public string MissionId { get; set; } = String.Empty;

        /// <summary>
        /// Gate label of the command, such as <c>build</c> or <c>unit-test</c>.
        /// </summary>
        public string Label { get; set; } = String.Empty;

        /// <summary>
        /// Full commit the gate ran the command on.
        /// </summary>
        public string CommitHash { get; set; } = String.Empty;

        /// <summary>
        /// When the command passed in the gate.
        /// </summary>
        public DateTime PassedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The <see cref="Kind"/> of a definition-of-done gate pass.
        /// </summary>
        public const string DefinitionOfDoneGate = "definition_of_done_gate";

        #endregion
    }
}
