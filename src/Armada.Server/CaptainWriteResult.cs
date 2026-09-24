namespace Armada.Server
{
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Outcome of creating or updating a captain through <see cref="CaptainAdministrationService"/>.
    /// </summary>
    public sealed class CaptainWriteResult
    {
        #region Public-Members

        /// <summary>
        /// Refusal code for a name another captain already has.
        /// </summary>
        public const string NameConflictCode = "captain_name_conflict";

        /// <summary>
        /// Refusal code for a runtime and model that model validation rejected.
        /// </summary>
        public const string InvalidModelCode = "captain_model_invalid";

        /// <summary>
        /// Completed when the captain was written; Busy for a name conflict; Failed for a rejected model.
        /// </summary>
        public CaptainAdministrationOutcomeEnum Outcome { get; private set; }

        /// <summary>
        /// Refusal code when refused; null on success.
        /// </summary>
        public string? Code { get; private set; }

        /// <summary>
        /// Refusal reason when refused; null on success.
        /// </summary>
        public string? Message { get; private set; }

        /// <summary>
        /// The stored captain when written; null when refused.
        /// </summary>
        public Captain? Captain { get; private set; }

        /// <summary>
        /// Set when the write was kept although model validation could not be verified (a provider credit,
        /// authentication or quota failure); the reason the model could not be verified.
        /// </summary>
        public string? CannotVerifyReason { get; private set; }

        /// <summary>
        /// True when the captain was written.
        /// </summary>
        public bool Succeeded => Outcome == CaptainAdministrationOutcomeEnum.Completed;

        #endregion

        #region Constructors-and-Factories

        private CaptainWriteResult()
        {
        }

        /// <summary>
        /// A completed write.
        /// </summary>
        /// <param name="captain">Stored captain.</param>
        /// <param name="cannotVerifyReason">Why validation could not be verified, or null.</param>
        /// <returns>The result.</returns>
        public static CaptainWriteResult Written(Captain captain, string? cannotVerifyReason)
        {
            return new CaptainWriteResult { Outcome = CaptainAdministrationOutcomeEnum.Completed, Captain = captain, CannotVerifyReason = cannotVerifyReason };
        }

        /// <summary>
        /// A refused write. Nothing was stored.
        /// </summary>
        /// <param name="outcome">Busy for a conflict, Failed for a rejected model.</param>
        /// <param name="code">Refusal code.</param>
        /// <param name="message">Refusal reason.</param>
        /// <returns>The result.</returns>
        public static CaptainWriteResult Refused(CaptainAdministrationOutcomeEnum outcome, string code, string message)
        {
            return new CaptainWriteResult { Outcome = outcome, Code = code, Message = message };
        }

        #endregion
    }
}
