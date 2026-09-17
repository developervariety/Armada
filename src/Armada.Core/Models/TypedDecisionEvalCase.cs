namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// One synthetic evaluation case for a typed decision: the exact request a decision would send for
    /// variant A (and optionally B), with reference answers or the answers that must agree. Cases carry
    /// synthetic state only, never operational records.
    /// </summary>
    public sealed class TypedDecisionEvalCase
    {
        #region Public-Members

        /// <summary>
        /// Stable case identifier.
        /// </summary>
        public required string Id { get; init; }

        /// <summary>
        /// The decision point the case exercises.
        /// </summary>
        public required string DecisionPoint { get; init; }

        /// <summary>
        /// Whether the case checks reference answers or consistency between the variants.
        /// </summary>
        public TypedDecisionEvalCaseKindEnum Kind { get; init; } = TypedDecisionEvalCaseKindEnum.Reference;

        /// <summary>
        /// What the case tests and the assumptions behind its reference answers.
        /// </summary>
        public string Note { get; init; } = String.Empty;

        /// <summary>
        /// The request for variant A.
        /// </summary>
        public required TypedDecisionBatchItem VariantA { get; init; }

        /// <summary>
        /// The request for variant B, when the case is a pair.
        /// </summary>
        public TypedDecisionBatchItem? VariantB { get; init; }

        /// <summary>
        /// Reference answers for variant A, keyed by question id.
        /// </summary>
        public IReadOnlyDictionary<string, TypedDecisionExpectation> ExpectedA { get; init; } = new Dictionary<string, TypedDecisionExpectation>();

        /// <summary>
        /// Reference answers for variant B, keyed by question id.
        /// </summary>
        public IReadOnlyDictionary<string, TypedDecisionExpectation> ExpectedB { get; init; } = new Dictionary<string, TypedDecisionExpectation>();

        /// <summary>
        /// For a consistency case, the question ids whose answers must agree between the variants.
        /// </summary>
        public IReadOnlyList<string> ConsistentQuestionIds { get; init; } = new List<string>();

        #endregion
    }
}
