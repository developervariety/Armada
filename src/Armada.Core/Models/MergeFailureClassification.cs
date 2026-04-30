namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Output of <c>IMergeFailureClassifier.Classify</c>: the structured failure class,
    /// a one-line human-readable summary, and the conflicted file list (if any) for
    /// triviality scoring and router input.
    /// </summary>
    /// <param name="FailureClass">Classified failure shape.</param>
    /// <param name="Summary">One-line human-readable description of the failure.</param>
    /// <param name="ConflictedFiles">File paths reported as conflicted, empty when none.</param>
    public sealed record MergeFailureClassification(
        MergeFailureClassEnum FailureClass,
        string Summary,
        IReadOnlyList<string> ConflictedFiles);
}
