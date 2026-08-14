namespace Armada.Core.Models
{
    /// <summary>
    /// Request body for approving or denying a mission review.
    /// </summary>
    public class MissionReviewDecisionRequest
    {
        /// <summary>
        /// Optional reviewer comment. Required in practice for a conditional approval or a "more work"
        /// decision, since that feedback is injected into the downstream or re-run mission prompt.
        /// </summary>
        public string? Comment { get; set; } = null;

        /// <summary>
        /// Approve only: when true, the reviewer comment is attached to the next pipeline stage as guidance
        /// the next captain must take into account ("Conditionally Approve"). Ignored when there is no
        /// downstream stage.
        /// </summary>
        public bool Conditional { get; set; } = false;

        /// <summary>
        /// Deny only: overrides the mission's configured deny action. Accepts "RetryStage" (revisit the same
        /// stage with the reviewer feedback -- "More Work Required") or "FailPipeline" (reject the stage and
        /// cancel dependents -- "Deny"). When null the mission's configured deny action is used.
        /// </summary>
        public string? Action { get; set; } = null;
    }
}
