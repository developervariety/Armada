namespace Armada.Core.Models
{
    /// <summary>
    /// One captain or session that an emergency stop could not stop.
    /// </summary>
    public class CaptainStopFailure
    {
        #region Public-Members

        /// <summary>
        /// What failed to stop: Captain, PlanningSession or RefinementSession.
        /// </summary>
        public string Kind { get; set; } = "";

        /// <summary>
        /// Identifier of the captain or session.
        /// </summary>
        public string Id { get; set; } = "";

        /// <summary>
        /// Why the stop failed.
        /// </summary>
        public string Message { get; set; } = "";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public CaptainStopFailure()
        {
        }

        /// <summary>
        /// Instantiate with values.
        /// </summary>
        /// <param name="kind">Captain, PlanningSession or RefinementSession.</param>
        /// <param name="id">Captain or session identifier.</param>
        /// <param name="message">Failure reason.</param>
        public CaptainStopFailure(string kind, string id, string message)
        {
            Kind = kind ?? "";
            Id = id ?? "";
            Message = message ?? "";
        }

        #endregion
    }
}
