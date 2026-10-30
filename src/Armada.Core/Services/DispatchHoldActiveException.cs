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
        /// The reason code every dispatch surface reports when the hold refuses a dispatch.
        /// </summary>
        public const string ReasonCode = "dispatch_hold_active";

        /// <summary>
        /// Machine-readable code for a refusal by a vessel hold.
        /// </summary>
        public const string VesselReasonCode = "vessel_hold_active";

        /// <summary>
        /// The refusal code: <see cref="VesselReasonCode"/> for a vessel hold, otherwise <see cref="ReasonCode"/>.
        /// </summary>
        public string Code => CodeFor(Hold);

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

        #region Public-Methods

        /// <summary>
        /// The refusal code for a hold.
        /// </summary>
        /// <param name="hold">The hold.</param>
        /// <returns><see cref="VesselReasonCode"/> for a vessel hold, otherwise <see cref="ReasonCode"/>.</returns>
        public static string CodeFor(DispatchHoldSnapshot? hold)
        {
            return String.IsNullOrWhiteSpace(hold?.VesselId) ? ReasonCode : VesselReasonCode;
        }

        #endregion
    }
}
