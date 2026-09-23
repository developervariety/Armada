namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Result of stopping one captain.
    /// </summary>
    public class CaptainStopResult
    {
        #region Public-Members

        /// <summary>
        /// Status reported when the captain was stopped.
        /// </summary>
        public const string StoppedStatus = "stopped";

        /// <summary>
        /// Request outcome.
        /// </summary>
        public CaptainAdministrationOutcomeEnum Outcome { get; set; } = CaptainAdministrationOutcomeEnum.NotFound;

        /// <summary>
        /// Status: <see cref="StoppedStatus"/> when the captain was stopped, otherwise null.
        /// </summary>
        public string? Status { get; set; } = null;

        /// <summary>
        /// Captain identifier the request named.
        /// </summary>
        public string CaptainId { get; set; } = "";

        /// <summary>
        /// Planning session stopped in place of the captain, when the captain was Planning.
        /// </summary>
        public string? PlanningSessionId { get; set; } = null;

        /// <summary>
        /// Objective refinement session stopped in place of the captain, when the captain was Refining.
        /// </summary>
        public string? ObjectiveRefinementSessionId { get; set; } = null;

        /// <summary>
        /// Human-readable explanation of the outcome.
        /// </summary>
        public string Message { get; set; } = "";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public CaptainStopResult()
        {
        }

        /// <summary>
        /// Instantiate with values.
        /// </summary>
        /// <param name="outcome">Outcome.</param>
        /// <param name="captainId">Captain identifier.</param>
        /// <param name="message">Explanation.</param>
        public CaptainStopResult(CaptainAdministrationOutcomeEnum outcome, string captainId, string message)
        {
            Outcome = outcome;
            CaptainId = captainId ?? "";
            Message = message ?? "";
            if (outcome == CaptainAdministrationOutcomeEnum.Completed) Status = StoppedStatus;
        }

        #endregion
    }
}
