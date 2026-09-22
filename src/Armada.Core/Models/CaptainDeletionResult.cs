namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Result of deleting one captain through the shared deletion rule.
    /// </summary>
    public class CaptainDeletionResult
    {
        #region Public-Members

        /// <summary>
        /// Request outcome.
        /// </summary>
        public CaptainAdministrationOutcomeEnum Outcome { get; set; } = CaptainAdministrationOutcomeEnum.NotFound;

        /// <summary>
        /// Identifier of the captain the request named.
        /// </summary>
        public string CaptainId { get; set; } = "";

        /// <summary>
        /// Human-readable explanation of the outcome.
        /// </summary>
        public string Message { get; set; } = "";

        /// <summary>
        /// Number of dependent rows (events, planning sessions, refinement sessions) removed with the captain.
        /// </summary>
        public int DependentsRemoved { get; set; } = 0;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public CaptainDeletionResult()
        {
        }

        /// <summary>
        /// Instantiate with values.
        /// </summary>
        /// <param name="outcome">Outcome.</param>
        /// <param name="captainId">Captain identifier.</param>
        /// <param name="message">Explanation.</param>
        public CaptainDeletionResult(CaptainAdministrationOutcomeEnum outcome, string captainId, string message)
        {
            Outcome = outcome;
            CaptainId = captainId ?? "";
            Message = message ?? "";
        }

        #endregion
    }
}
