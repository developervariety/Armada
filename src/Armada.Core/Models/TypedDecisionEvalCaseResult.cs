namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The outcome of one typed-decision evaluation case.
    /// </summary>
    public sealed class TypedDecisionEvalCaseResult
    {
        #region Public-Members

        /// <summary>
        /// The case identifier.
        /// </summary>
        public string CaseId { get; set; } = String.Empty;

        /// <summary>
        /// The decision point.
        /// </summary>
        public string DecisionPoint { get; set; } = String.Empty;

        /// <summary>
        /// The case kind, as text.
        /// </summary>
        public string Kind { get; set; } = String.Empty;

        /// <summary>
        /// <c>passed</c>, <c>failed</c>, or <c>unavailable</c> (the provider did not answer, so the case
        /// neither passed nor failed).
        /// </summary>
        public string Outcome { get; set; } = String.Empty;

        /// <summary>
        /// The unavailable reason, when the outcome is unavailable.
        /// </summary>
        public string? UnavailableReason { get; set; }

        /// <summary>
        /// One line per check that did not hold, naming the question, the expectation, and the answer.
        /// </summary>
        public List<string> Failures { get; set; } = new List<string>();

        /// <summary>
        /// The answers received, one compact line per variant and question.
        /// </summary>
        public List<string> Answers { get; set; } = new List<string>();

        #endregion
    }
}
