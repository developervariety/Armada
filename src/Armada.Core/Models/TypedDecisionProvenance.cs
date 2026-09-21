namespace Armada.Core.Models
{
    /// <summary>
    /// Request evidence for host-local evaluation. Questions are redacted before this object is
    /// created. The wire hashes identify the original request without retaining its text.
    /// </summary>
    public sealed record TypedDecisionProvenance
    {
        /// <summary>Version of this evidence format; older samples have no provenance.</summary>
        public int Version { get; init; } = 1;

        /// <summary>Stable reason when question evidence could not be captured; no exception text.</summary>
        public string? UnavailableReason { get; init; }

        /// <summary>Redacted provider-format question definitions for the complete request.</summary>
        public required string QuestionsJson { get; init; }

        /// <summary>SHA-256 of QuestionsJson, including its exact UTF-8 representation.</summary>
        public required string QuestionsSha256 { get; init; }

        /// <summary>SHA-256 of the question JSON before retention redaction.</summary>
        public required string WireQuestionsSha256 { get; init; }

        /// <summary>SHA-256 of the complete provider request body, without headers or credentials.</summary>
        public required string RequestSha256 { get; init; }

        /// <summary>Zero-based item in a packed request; null for an unwrapped request.</summary>
        public int? BatchItemIndex { get; init; }
    }
}
