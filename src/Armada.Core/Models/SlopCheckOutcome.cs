namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// The result of one Slop check execution.
    /// </summary>
    public class SlopCheckOutcome
    {
        #region Public-Members

        /// <summary>
        /// True when the reviewed diff was read and classified. False when a step failed first.
        /// </summary>
        public bool Completed { get; set; } = false;

        /// <summary>
        /// Why the check could not classify the reviewed diff, when it could not.
        /// </summary>
        public string? FailureReason { get; set; } = null;

        /// <summary>
        /// The review base: the merge base of the default branch and the reviewed commit.
        /// </summary>
        public string? BaseCommit { get; set; } = null;

        /// <summary>
        /// The reviewed commit.
        /// </summary>
        public string? HeadCommit { get; set; } = null;

        /// <summary>
        /// The classification, when the diff was classified.
        /// </summary>
        public SlopClassificationResult? Classification { get; set; } = null;

        /// <summary>
        /// The full report for the check output.
        /// </summary>
        public string Report { get; set; } = String.Empty;

        /// <summary>
        /// One-line summary for the check record and its events.
        /// </summary>
        public string Summary { get; set; } = String.Empty;

        /// <summary>
        /// True when the diff was classified and no unsuppressed FAIL finding exists.
        /// </summary>
        public bool Passed
        {
            get { return Completed && Classification != null && Classification.Passed; }
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// An outcome for a check that could not classify the reviewed diff.
        /// </summary>
        /// <param name="reason">What failed.</param>
        /// <returns>A failed outcome carrying the reason as report and summary.</returns>
        public static SlopCheckOutcome Failure(string reason)
        {
            return new SlopCheckOutcome
            {
                Completed = false,
                FailureReason = reason,
                Report = reason,
                Summary = "Slop failed: " + reason
            };
        }

        #endregion
    }
}
