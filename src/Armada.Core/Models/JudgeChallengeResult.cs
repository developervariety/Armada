namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// The settled outcome of one challenge: which findings stand and which were refuted.
    /// </summary>
    public class JudgeChallengeResult
    {
        #region Public-Members

        /// <summary>
        /// Why the Challenger output could not be used. Null when the result is valid.
        /// </summary>
        public string? Error { get; set; } = null;

        /// <summary>
        /// One-based numbers of the findings that stand.
        /// </summary>
        public List<int> Upheld { get; set; } = new List<int>();

        /// <summary>
        /// The rulings that refuted a finding at or above the confidence floor.
        /// </summary>
        public List<JudgeChallengeRuling> Refuted { get; set; } = new List<JudgeChallengeRuling>();

        /// <summary>
        /// One-based numbers of findings upheld only because a REFUTE fell below the confidence floor or
        /// carried no evidence.
        /// </summary>
        public List<int> UpheldBelowFloor { get; set; } = new List<int>();

        /// <summary>
        /// One-based numbers of findings upheld because they are unchallengeable, whatever the ruling.
        /// </summary>
        public List<int> UpheldUnchallengeable { get; set; } = new List<int>();

        /// <summary>
        /// Whether the result can be acted on.
        /// </summary>
        public bool IsValid => Error == null;

        #endregion
    }
}
