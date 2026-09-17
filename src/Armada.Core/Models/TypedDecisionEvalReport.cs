namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The result of one typed-decision evaluation run over the synthetic case set.
    /// </summary>
    public sealed class TypedDecisionEvalReport
    {
        #region Public-Members

        /// <summary>
        /// Why the run happened: <c>operator</c>, or <c>model_changed</c>.
        /// </summary>
        public string Reason { get; set; } = String.Empty;

        /// <summary>
        /// The model version the provider reported during the run, when any answer arrived.
        /// </summary>
        public string? Model { get; set; }

        /// <summary>
        /// When the run started.
        /// </summary>
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Cases run.
        /// </summary>
        public int Total { get; set; }

        /// <summary>
        /// Cases whose every check held.
        /// </summary>
        public int Passed { get; set; }

        /// <summary>
        /// Cases with at least one check that did not hold.
        /// </summary>
        public int Failed { get; set; }

        /// <summary>
        /// Cases the provider did not answer.
        /// </summary>
        public int Unavailable { get; set; }

        /// <summary>
        /// Prompt tokens used by the run.
        /// </summary>
        public int InputTokens { get; set; }

        /// <summary>
        /// Completion tokens used by the run.
        /// </summary>
        public int OutputTokens { get; set; }

        /// <summary>
        /// Per-case results.
        /// </summary>
        public List<TypedDecisionEvalCaseResult> Cases { get; set; } = new List<TypedDecisionEvalCaseResult>();

        #endregion
    }
}
