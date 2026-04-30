namespace Armada.Core.Recovery
{
    using System.Collections.Generic;

    /// <summary>
    /// Snapshot of the git auto-fold step's outcome at fail-time. Captured
    /// inside <c>MergeQueueService</c> when a merge produces conflicts or
    /// otherwise fails before tests run, then passed to the
    /// <c>IMergeFailureClassifier</c> for shape detection.
    /// </summary>
    /// <param name="MergeAttempted">
    /// True when <c>git merge</c> was actually invoked (i.e. the fetch +
    /// worktree-create steps both succeeded). False indicates an
    /// infrastructure failure earlier in the pipeline.
    /// </param>
    /// <param name="MergeSucceeded">
    /// True when the 3-way fold completed without conflicts. When false,
    /// inspect <see cref="ConflictedFiles"/> for the conflicting paths.
    /// </param>
    /// <param name="ConflictedFiles">
    /// Paths reported by <c>git status --porcelain</c> as conflicted (UU /
    /// AA / DD entries). Empty when the merge succeeded or was never
    /// attempted.
    /// </param>
    /// <param name="MergeBaseSha">
    /// Output of <c>git merge-base</c> between the captain branch and target,
    /// captured before the merge attempt. Null if it could not be resolved.
    /// </param>
    /// <param name="TargetTipSha">
    /// SHA of the target branch tip the merge was attempted against. Null
    /// if it could not be resolved.
    /// </param>
    /// <param name="MergeOutput">
    /// Combined stdout/stderr of the <c>git merge</c> invocation, truncated
    /// to a reasonable size. Used by the classifier for heuristic matching
    /// when porcelain status alone is insufficient.
    /// </param>
    public sealed record GitMergeOutcome(
        bool MergeAttempted,
        bool MergeSucceeded,
        IReadOnlyList<string> ConflictedFiles,
        string? MergeBaseSha,
        string? TargetTipSha,
        string? MergeOutput);
}
