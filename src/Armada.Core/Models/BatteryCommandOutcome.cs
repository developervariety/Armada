namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// What one grader command did.
    /// </summary>
    public class BatteryCommandOutcome
    {
        #region Public-Members

        /// <summary>
        /// Command line.
        /// </summary>
        public string CommandLine
        {
            get => _CommandLine;
            set => _CommandLine = value ?? String.Empty;
        }

        /// <summary>
        /// Exit code.
        /// </summary>
        public int ExitCode { get; set; } = 0;

        /// <summary>
        /// Whether it timed out.
        /// </summary>
        public bool TimedOut { get; set; } = false;

        /// <summary>
        /// Whether it exited with the expected code in time.
        /// </summary>
        public bool Passed { get; set; } = false;

        /// <summary>
        /// End of its output.
        /// </summary>
        public string? OutputExcerpt { get; set; } = null;

        #endregion

        #region Private-Members

        private string _CommandLine = String.Empty;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatteryCommandOutcome()
        {
        }

        #endregion
    }
}
