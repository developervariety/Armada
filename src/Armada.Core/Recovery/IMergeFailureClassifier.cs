namespace Armada.Core.Recovery
{
    using System.Collections.Generic;

    /// <summary>
    /// Pure classifier mapping a captured (git, tests, conflicted-files)
    /// snapshot to a structured failure signal. No I/O; no database access;
    /// implementations must be deterministic so the same inputs always
    /// produce the same shape (covered by unit tests with captured fixtures).
    /// M1 ships the interface only; the implementation lands in M2.
    /// </summary>
    public interface IMergeFailureClassifier
    {
        /// <summary>
        /// Classify a merge-queue failure into a <see cref="MergeFailureSignal"/>.
        /// </summary>
        /// <param name="git">
        /// Snapshot of the auto-fold step's outcome. Always required (even
        /// when the merge was never attempted, the snapshot carries that
        /// state via <see cref="GitMergeOutcome.MergeAttempted"/>).
        /// </param>
        /// <param name="tests">
        /// Snapshot of the post-merge test step's outcome, or null when no
        /// tests were configured / executed.
        /// </param>
        /// <param name="conflictedFiles">
        /// Paths the merge step reported as conflicted; empty when there was
        /// no conflict (e.g. test-after-merge failure).
        /// </param>
        /// <returns>
        /// A signal carrying the failure class, a one-line summary, and the
        /// canonical list of conflicted files.
        /// </returns>
        MergeFailureSignal Classify(
            GitMergeOutcome git,
            TestRunOutcome? tests,
            IReadOnlyList<string> conflictedFiles);
    }
}
