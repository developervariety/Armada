namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// What a landing gate needs before it may pass a change: the vessel's rules, every changed
    /// path, and the unified diff. <see cref="Available"/> is false when any part could not be read;
    /// an empty <see cref="ChangedFiles"/> list with <see cref="Available"/> true is a verified empty
    /// change, never a read failure.
    /// </summary>
    public sealed class LandingEvidence
    {
        #region Public-Members

        /// <summary>
        /// Prefix of every refusal a landing records when its evidence is unavailable.
        /// </summary>
        public const string RefusalPrefix = "landing_evidence_unavailable";

        /// <summary>
        /// True when the vessel, the changed paths and the diff were all read.
        /// </summary>
        public bool Available { get; set; } = false;

        /// <summary>
        /// Which part could not be read and why, when <see cref="Available"/> is false.
        /// </summary>
        public string? UnavailableReason { get; set; } = null;

        /// <summary>
        /// The vessel whose rules apply, or null when the landing names no vessel.
        /// </summary>
        public Vessel? Vessel { get; set; } = null;

        /// <summary>
        /// Every repository-relative path the change touches.
        /// </summary>
        public List<string> ChangedFiles { get; set; } = new List<string>();

        /// <summary>
        /// Unified diff of the change.
        /// </summary>
        public string UnifiedDiff { get; set; } = String.Empty;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Evidence that could not be read.
        /// </summary>
        /// <param name="reason">Which part failed and why.</param>
        /// <returns>Unavailable evidence.</returns>
        public static LandingEvidence Unavailable(string reason)
        {
            return new LandingEvidence
            {
                Available = false,
                UnavailableReason = String.IsNullOrWhiteSpace(reason) ? "unknown" : reason
            };
        }

        /// <summary>
        /// The named refusal a landing records when this evidence is unavailable.
        /// </summary>
        /// <returns>Refusal text.</returns>
        public string FormatRefusal()
        {
            return RefusalPrefix + ": " + (UnavailableReason ?? "unknown") +
                "; the change was not landed or pushed because the landing gate could not read what it would land";
        }

        #endregion
    }
}
