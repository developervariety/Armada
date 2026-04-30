namespace Armada.Core.Recovery
{
    /// <summary>
    /// Snapshot of the post-merge test command's outcome at fail-time. Null
    /// when the merge step itself failed before tests ran, or when the entry
    /// had no test command configured.
    /// </summary>
    /// <param name="ExitCode">Process exit code from the test runner.</param>
    /// <param name="Output">
    /// Combined stdout/stderr of the test invocation, truncated to a
    /// reasonable size. Used by the classifier to distinguish
    /// TestFailureAfterMerge from TestFailureBeforeMerge.
    /// </param>
    /// <param name="RanAfterCleanMerge">
    /// True when the merge fold completed cleanly and the tests ran against
    /// the integrated worktree. False indicates the captain branch was
    /// already broken before any merge attempt.
    /// </param>
    public sealed record TestRunOutcome(
        int ExitCode,
        string Output,
        bool RanAfterCleanMerge);
}
