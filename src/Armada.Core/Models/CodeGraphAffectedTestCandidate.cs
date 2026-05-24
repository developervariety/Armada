namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Ranked affected-test candidate derived from graph traversal.
    /// </summary>
    public class CodeGraphAffectedTestCandidate
    {
        #region Public-Members

        /// <summary>
        /// Candidate test file path.
        /// </summary>
        public string TestPath { get; set; } = "";

        /// <summary>
        /// Candidate test symbol name.
        /// </summary>
        public string Symbol { get; set; } = "";

        /// <summary>
        /// Score. Higher is better.
        /// </summary>
        public double Score { get; set; } = 0;

        /// <summary>
        /// True when a direct test symbol or test file signal was observed.
        /// </summary>
        public bool IsExplicitSignal { get; set; } = false;

        /// <summary>
        /// Minimum traversal depth used as evidence for this candidate.
        /// </summary>
        public int EvidenceDepth { get; set; } = 0;

        /// <summary>
        /// Brief evidence reasons.
        /// </summary>
        public List<string> Reasons { get; set; } = new List<string>();

        #endregion
    }
}
