namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Bundle of failure-time signals captured by <c>MergeQueueService</c> when a merge
    /// or post-merge test run fails. Consumed by <c>IMergeFailureClassifier</c> to assign
    /// a structured <c>MergeFailureClassEnum</c>.
    /// </summary>
    public sealed class MergeFailureContext
    {
        /// <summary>
        /// Gets or sets the exit code produced by the git merge process. Null when no
        /// git merge was attempted (e.g. failure occurred before the merge step).
        /// </summary>
        public int? GitExitCode { get; set; }

        /// <summary>
        /// Gets or sets the standard output captured from the git merge process.
        /// </summary>
        public string? GitStandardOutput { get; set; }

        /// <summary>
        /// Gets or sets the standard error captured from the git merge process.
        /// </summary>
        public string? GitStandardError { get; set; }

        /// <summary>
        /// Gets or sets the exit code produced by the post-merge test runner. Null when
        /// no test run was performed.
        /// </summary>
        public int? TestExitCode { get; set; }

        /// <summary>
        /// Gets or sets the captured output from the test runner.
        /// </summary>
        public string? TestOutput { get; set; }

        /// <summary>
        /// Gets or sets the list of file paths reported by git as conflicted. Empty when
        /// no merge conflict was reported.
        /// </summary>
        public IReadOnlyList<string> ConflictedFiles { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets the total number of changed lines in the captain branch's diff
        /// against the target branch, used by the router's triviality heuristic.
        /// </summary>
        public int DiffLineCount { get; set; }
    }
}
