namespace Armada.Server.Mcp
{
    /// <summary>
    /// Arguments for approve_mission_review and deny_mission_review.
    /// </summary>
    public class MissionReviewArgs
    {
        #region Public-Members

        /// <summary>Mission ID (msn_ prefix).</summary>
        public string MissionId { get; set; } = "";

        /// <summary>Reviewer comment; on a deny it is added to the retried stage's instructions.</summary>
        public string? Comment { get; set; } = null;

        /// <summary>Approve with conditions: the comment becomes guidance for the following stages.</summary>
        public bool Conditional { get; set; } = false;

        /// <summary>Deny action override: RetryStage or FailPipeline; null uses the stage's configured action.</summary>
        public string? Action { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public MissionReviewArgs()
        {
        }

        #endregion
    }
}
