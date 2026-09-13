namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Result of a manual captain quarantine or release request, shared by REST, MCP and the dashboard.
    /// </summary>
    public class CaptainQuarantineResult
    {
        #region Public-Members

        /// <summary>
        /// Request outcome.
        /// </summary>
        public CaptainQuarantineOutcomeEnum Outcome { get; set; } = CaptainQuarantineOutcomeEnum.NotFound;

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
        public CaptainQuarantineResult()
        {
        }

        /// <summary>
        /// Instantiate with values.
        /// </summary>
        /// <param name="outcome">Outcome.</param>
        /// <param name="captain">Captain after the request.</param>
        /// <param name="message">Explanation.</param>
        public CaptainQuarantineResult(CaptainQuarantineOutcomeEnum outcome, Captain? captain, string message)
        {
            Outcome = outcome;
            Captain = captain;
            Message = message ?? "";
        }

        #endregion
    }
}
