namespace Armada.Core.Models
{
    /// <summary>
    /// What one probe exchange produced.
    /// </summary>
    public class ModelProbeExchange
    {
        #region Public-Members

        /// <summary>
        /// Why the exchange could not be started (runtime not installed, no Harbor connected), or null when it ran.
        /// A probe whose exchange could not start is recorded as could-not-run, never as failed.
        /// </summary>
        public string? CouldNotRunReason { get; set; } = null;

        /// <summary>
        /// The model's answer (the runtime's final message, or its standard output).
        /// </summary>
        public string Output { get; set; } = "";

        /// <summary>
        /// Process exit code, or null.
        /// </summary>
        public int? ExitCode { get; set; } = null;

        /// <summary>
        /// Whether the exchange hit its timeout and was stopped.
        /// </summary>
        public bool TimedOut { get; set; } = false;

        /// <summary>
        /// Milliseconds from start to the first output line, or null when nothing was printed.
        /// </summary>
        public long? FirstOutputMs { get; set; } = null;

        /// <summary>
        /// Milliseconds from start to exit.
        /// </summary>
        public long DurationMs { get; set; } = 0;

        /// <summary>
        /// Harbor that ran the exchange, or null for the Admiral's host.
        /// </summary>
        public string? HarborId { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public ModelProbeExchange()
        {
        }

        /// <summary>
        /// An exchange that could not start.
        /// </summary>
        /// <param name="reason">Why.</param>
        /// <returns>The exchange.</returns>
        public static ModelProbeExchange NotStarted(string reason)
        {
            return new ModelProbeExchange { CouldNotRunReason = reason };
        }

        #endregion
    }
}
