namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// The one response shape every dispatch surface returns when the dispatch hold refuses a
    /// request at submission. Nothing was accepted, queued or created.
    /// </summary>
    public class DispatchHoldRefusal
    {
        #region Public-Members

        /// <summary>
        /// Operator-facing refusal message.
        /// </summary>
        public string Error { get; set; } = String.Empty;

        /// <summary>
        /// Always <see cref="DispatchHoldActiveException.ReasonCode"/>.
        /// </summary>
        public string Code { get; set; } = DispatchHoldActiveException.ReasonCode;

        /// <summary>
        /// Session or operator that engaged the hold, when known.
        /// </summary>
        public string? SetBy { get; set; } = null;

        /// <summary>
        /// When the hold was engaged, in UTC.
        /// </summary>
        public DateTime SetByUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Why the hold was engaged.
        /// </summary>
        public string Reason { get; set; } = String.Empty;

        /// <summary>
        /// What the caller should do next.
        /// </summary>
        public string Action { get; set; } = "Nothing was accepted or created. Dispatch again after the hold is cleared; ask its owner on the coordination board.";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Build the refusal for an engaged hold.
        /// </summary>
        /// <param name="hold">The engaged hold.</param>
        /// <returns>The refusal.</returns>
        public static DispatchHoldRefusal From(DispatchHoldSnapshot hold)
        {
            if (hold == null) throw new ArgumentNullException(nameof(hold));
            return new DispatchHoldRefusal
            {
                Error = DispatchHold.RefusalMessage(hold),
                SetBy = hold.SetBy,
                SetByUtc = hold.SetByUtc,
                Reason = hold.Reason
            };
        }

        #endregion
    }
}
