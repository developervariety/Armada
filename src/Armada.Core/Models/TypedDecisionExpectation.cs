namespace Armada.Core.Models
{
    /// <summary>
    /// The reference answer for one question in a typed-decision evaluation case. Set only the
    /// fields that apply to the question's type; every set field must hold.
    /// </summary>
    public sealed class TypedDecisionExpectation
    {
        #region Public-Members

        /// <summary>
        /// For a choice question, the option the model must select.
        /// </summary>
        public string? Choice { get; init; }

        /// <summary>
        /// For a noul question, the lowest acceptable probability.
        /// </summary>
        public double? NoulAtLeast { get; init; }

        /// <summary>
        /// For a noul question, the highest acceptable probability.
        /// </summary>
        public double? NoulAtMost { get; init; }

        /// <summary>
        /// For a score question, the lowest acceptable score.
        /// </summary>
        public double? ScoreAtLeast { get; init; }

        /// <summary>
        /// For a score question, the highest acceptable score.
        /// </summary>
        public double? ScoreAtMost { get; init; }

        #endregion
    }
}
