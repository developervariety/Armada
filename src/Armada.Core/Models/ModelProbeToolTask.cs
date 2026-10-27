namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// The per-run values of the tool-calling probe: random strings the model can only hash correctly by calling a tool,
    /// and the names of the files it works with.
    /// </summary>
    public class ModelProbeToolTask
    {
        #region Public-Members

        /// <summary>
        /// Text hashed with one tool call (single call).
        /// </summary>
        public string SingleText { get; set; } = "";

        /// <summary>
        /// Text written to a file and then hashed from that file (sequential calls).
        /// </summary>
        public string SequentialText { get; set; } = "";

        /// <summary>
        /// First text hashed in parallel.
        /// </summary>
        public string ParallelTextA { get; set; } = "";

        /// <summary>
        /// Second text hashed in parallel.
        /// </summary>
        public string ParallelTextB { get; set; } = "";

        /// <summary>
        /// File that does not exist (the tool returns an error).
        /// </summary>
        public string MissingFileName { get; set; } = "";

        /// <summary>
        /// Marker used in every line of the large file.
        /// </summary>
        public string LargeMarker { get; set; } = "";

        /// <summary>
        /// Lines in the large file.
        /// </summary>
        public int LargeLineCount { get; set; } = 2000;

        /// <summary>
        /// Line of the large file the model reports (1-based).
        /// </summary>
        public int LargeLineNumber { get; set; } = 1234;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with empty values.
        /// </summary>
        public ModelProbeToolTask()
        {
        }

        /// <summary>
        /// Create a task with fresh random values.
        /// </summary>
        /// <returns>The task.</returns>
        public static ModelProbeToolTask CreateRandom()
        {
            return new ModelProbeToolTask
            {
                SingleText = "armada-" + Nonce(),
                SequentialText = "armada-" + Nonce(),
                ParallelTextA = "armada-" + Nonce(),
                ParallelTextB = "armada-" + Nonce(),
                MissingFileName = "armada-probe-missing-" + Nonce() + ".txt",
                LargeMarker = Nonce()
            };
        }

        #endregion

        #region Private-Methods

        private static string Nonce()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        #endregion
    }
}
