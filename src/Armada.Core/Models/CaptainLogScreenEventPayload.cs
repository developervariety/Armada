namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Payload of the event written once per screened captain log. An operator reads the per-class
    /// counts over a date range by enumerating the screen event type and summing these payloads, so
    /// the counts live here rather than in prose in the message.
    /// </summary>
    public class CaptainLogScreenEventPayload
    {
        #region Public-Members

        /// <summary>
        /// What the screen concluded: findings were produced, or the tail was clean. A screen that
        /// did not run writes no event at all, so an absent event never means "clean".
        /// </summary>
        public string Outcome { get; set; } = String.Empty;

        /// <summary>
        /// Findings by rule class. Empty on a clean screen.
        /// </summary>
        public Dictionary<string, int> Counts
        {
            get => _Counts;
            set => _Counts = value ?? new Dictionary<string, int>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Names of the passes that ran over the tail, so a count is read against the passes that
        /// produced it.
        /// </summary>
        public List<string> Passes
        {
            get => _Passes;
            set => _Passes = value ?? new List<string>();
        }

        /// <summary>
        /// Lowercase hexadecimal SHA-256 of the tail that was screened. The tail itself is never
        /// stored.
        /// </summary>
        public string TailSha256 { get; set; } = String.Empty;

        /// <summary>
        /// Size of the screened tail in UTF-8 bytes.
        /// </summary>
        public int TailBytes { get; set; } = 0;

        #endregion

        #region Private-Members

        private Dictionary<string, int> _Counts = new Dictionary<string, int>(StringComparer.Ordinal);
        private List<string> _Passes = new List<string>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CaptainLogScreenEventPayload()
        {
        }

        #endregion
    }
}
