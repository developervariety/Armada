namespace Armada.Core.Recovery
{
    using System.Collections.Generic;

    /// <summary>
    /// Structured output of <see cref="IMergeFailureClassifier"/>. Persisted
    /// onto the <c>merge_entries</c> row at fail-time so the recovery router
    /// (and any subsequent operator review) sees the exact classification
    /// without reparsing raw git output.
    /// </summary>
    /// <param name="Class">
    /// Failure shape -- determines whether routing is even possible (Unknown
    /// always surfaces).
    /// </param>
    /// <param name="Summary">
    /// One-line human-readable summary, capped at 512 characters to fit the
    /// schema column. Stored in <c>merge_entries.merge_failure_summary</c>.
    /// </param>
    /// <param name="ConflictedFiles">
    /// Paths reported as conflicted; empty for non-conflict shapes. Stored
    /// JSON-serialized in <c>merge_entries.conflicted_files</c>.
    /// </param>
    public sealed record MergeFailureSignal(
        MergeFailureClass Class,
        string Summary,
        IReadOnlyList<string> ConflictedFiles);
}
