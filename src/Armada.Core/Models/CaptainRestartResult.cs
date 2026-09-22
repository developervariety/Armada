namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Result of restarting one captain in place.
    /// </summary>
    public class CaptainRestartResult
    {
        #region Public-Members

        /// <summary>
        /// Request outcome.
        /// </summary>
        public CaptainAdministrationOutcomeEnum Outcome { get; set; } = CaptainAdministrationOutcomeEnum.NotFound;

        /// <summary>
        /// Captain as read after the request, or null when not found.
        /// </summary>
        public Captain? Captain { get; set; } = null;

        /// <summary>
        /// Human-readable explanation of the outcome.
        /// </summary>
        public string Message { get; set; } = "";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public CaptainRestartResult()
        {
        }

        /// <summary>
        /// Instantiate with values.
        /// </summary>
        /// <param name="outcome">Outcome.</param>
        /// <param name="captain">Captain after the request.</param>
        /// <param name="message">Explanation.</param>
        public CaptainRestartResult(CaptainAdministrationOutcomeEnum outcome, Captain? captain, string message)
        {
            Outcome = outcome;
            Captain = captain;
            Message = message ?? "";
        }

        #endregion
    }
}
