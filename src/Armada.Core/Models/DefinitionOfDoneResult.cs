namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Result produced by the definition-of-done gate after evaluating a Worker mission's
    /// in-dock build and unit-test commands.
    /// </summary>
    public class DefinitionOfDoneResult
    {
        #region Public-Members

        /// <summary>
        /// True when the gate either passed all required commands or was intentionally skipped.
        /// </summary>
        public bool Passed { get; set; }

        /// <summary>
        /// Human-readable label of the failing command (e.g., "build", "unit-test"), or null
        /// when no command failed.
        /// </summary>
        public string? CommandLabel { get; set; }

        /// <summary>
        /// Process exit code of the failing command, or 0 when no command failed.
        /// </summary>
        public int ExitCode { get; set; }

        /// <summary>
        /// Bounded tail of the command output, or null when no command failed.
        /// </summary>
        public string? OutputTail { get; set; }

        /// <summary>
        /// Structured classification of the failure, or null for passed and skipped results.
        /// </summary>
        public DefinitionOfDoneFailureClassEnum? FailureClass { get; set; }

        /// <summary>
        /// Non-null when the gate was skipped rather than run (e.g., persona not applicable,
        /// doc-only marker present). A skipped gate is treated as passed.
        /// </summary>
        public string? SkippedReason { get; set; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public DefinitionOfDoneResult()
        {
        }

        /// <summary>
        /// Convenience factory for a skipped result.
        /// </summary>
        /// <param name="reason">Human-readable reason why the gate was skipped.</param>
        /// <returns>A passed, skipped result.</returns>
        public static DefinitionOfDoneResult Skipped(string reason)
        {
            return new DefinitionOfDoneResult
            {
                Passed = true,
                SkippedReason = reason ?? throw new ArgumentNullException(nameof(reason))
            };
        }

        /// <summary>
        /// Convenience factory for a passing result.
        /// </summary>
        /// <returns>A clean pass result with no failure details.</returns>
        public static DefinitionOfDoneResult Pass()
        {
            return new DefinitionOfDoneResult { Passed = true };
        }

        /// <summary>
        /// Convenience factory for a failing result.
        /// </summary>
        /// <param name="commandLabel">Label of the failing command.</param>
        /// <param name="exitCode">Process exit code of the failing command.</param>
        /// <param name="outputTail">Bounded output tail from the failing command.</param>
        /// <param name="failureClass">Structured classification of the failure.</param>
        /// <returns>A failing result with the specified details.</returns>
        public static DefinitionOfDoneResult Fail(
            string commandLabel,
            int exitCode,
            string? outputTail,
            DefinitionOfDoneFailureClassEnum failureClass)
        {
            return new DefinitionOfDoneResult
            {
                Passed = false,
                CommandLabel = commandLabel ?? throw new ArgumentNullException(nameof(commandLabel)),
                ExitCode = exitCode,
                OutputTail = outputTail,
                FailureClass = failureClass
            };
        }

        #endregion
    }
}
