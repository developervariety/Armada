namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// One independent typed-decision item to be answered in a shared request: its already-redacted
    /// state and the questions asked about it alone.
    /// </summary>
    public sealed class TypedDecisionBatchItem
    {
        #region Public-Members

        /// <summary>
        /// The redacted state for this item.
        /// </summary>
        public RedactedDecisionState State { get; }

        /// <summary>
        /// The questions about this item, keyed by question id.
        /// </summary>
        public IReadOnlyDictionary<string, TypedQuestion> Questions { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a batch item.
        /// </summary>
        /// <param name="state">The redacted state for this item.</param>
        /// <param name="questions">The questions about this item.</param>
        public TypedDecisionBatchItem(RedactedDecisionState state, IReadOnlyDictionary<string, TypedQuestion> questions)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            Questions = questions ?? throw new ArgumentNullException(nameof(questions));
        }

        #endregion
    }
}
