namespace Armada.Helm.Infrastructure
{
    /// <summary>
    /// The result of asking the Admiral to stop.
    /// </summary>
    public sealed class AdmiralStopResult
    {
        #region Public-Members

        /// <summary>
        /// How the stop ended.
        /// </summary>
        public AdmiralStopOutcomeEnum Outcome { get; set; } = AdmiralStopOutcomeEnum.StillRunning;

        /// <summary>
        /// HTTP status the Admiral returned for the stop request, when it returned one.
        /// </summary>
        public int? StatusCode { get; set; } = null;

        /// <summary>
        /// Human-readable description of the outcome.
        /// </summary>
        public string Message { get; set; } = "";

        /// <summary>
        /// True only when nothing answers at the Admiral address, so its data can be touched safely.
        /// </summary>
        public bool IsDown
        {
            get
            {
                return Outcome == AdmiralStopOutcomeEnum.NotRunning || Outcome == AdmiralStopOutcomeEnum.Stopped;
            }
        }

        #endregion
    }
}
