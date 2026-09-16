namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// A decision state after redaction: the value transmitted as the request's <c>state</c> (a string,
    /// or a JSON object or array) and its exact serialized text, which is what events hash and size.
    /// </summary>
    public sealed class RedactedDecisionState
    {
        #region Public-Members

        /// <summary>
        /// The redacted value to transmit.
        /// </summary>
        public object State { get; }

        /// <summary>
        /// The serialized text of <see cref="State"/>: the string itself, or the compact JSON.
        /// </summary>
        public string Text { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a redacted decision state.
        /// </summary>
        /// <param name="state">The redacted value to transmit.</param>
        /// <param name="text">Its serialized text.</param>
        public RedactedDecisionState(object state, string text)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            Text = text ?? throw new ArgumentNullException(nameof(text));
        }

        /// <summary>
        /// Create a redacted state that is plain text.
        /// </summary>
        /// <param name="text">The redacted text.</param>
        /// <returns>The state, transmitted as a string.</returns>
        public static RedactedDecisionState FromText(string text)
        {
            string value = text ?? String.Empty;
            return new RedactedDecisionState(value, value);
        }

        #endregion
    }
}
