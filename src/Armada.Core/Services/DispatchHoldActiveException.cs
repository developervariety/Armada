namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Raised by <see cref="DispatchHold.ThrowIfActive"/> when a dispatch is refused because the
    /// fleet-wide dispatch hold is engaged. It carries the hold that refused the dispatch, so a
    /// caller that defers work instead of failing it can name the hold in its own record.
    /// </summary>
    public class DispatchHoldActiveException : InvalidOperationException
    {
        #region Public-Members

        /// <summary>
        /// The hold that refused the dispatch.
        /// </summary>
        public DispatchHoldSnapshot Hold { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="hold">The engaged hold.</param>
        /// <param name="message">Operator-facing refusal message.</param>
        public DispatchHoldActiveException(DispatchHoldSnapshot hold, string message)
            : base(message)
        {
            Hold = hold ?? throw new ArgumentNullException(nameof(hold));
        }

        #endregion
    }
}
