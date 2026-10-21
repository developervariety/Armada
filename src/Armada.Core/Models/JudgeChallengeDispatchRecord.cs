namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Payload of the <c>judge_challenge.dispatched</c> event: the durable link from a failed Judge
    /// mission to the Challenger that rules on its findings.
    /// </summary>
    public class JudgeChallengeDispatchRecord
    {
        #region Public-Members

        /// <summary>
        /// Challenger mission identifier.
        /// </summary>
        public string ChallengerMissionId { get; set; } = String.Empty;

        /// <summary>
        /// Completion time of the Judge failure the challenge belongs to, so a later failure of the same
        /// mission starts a new challenge.
        /// </summary>
        public DateTime? JudgeCompletedUtc { get; set; } = null;

        /// <summary>
        /// Number of findings the Challenger was asked to rule on.
        /// </summary>
        public int FindingCount { get; set; } = 0;

        #endregion
    }
}
