namespace Armada.Core.Models
{
    /// <summary>
    /// Request body for approving or denying a mission review.
    /// </summary>
    public class MissionReviewDecisionRequest
    {
        /// <summary>
        /// Optional reviewer comment.
        /// </summary>
        public string? Comment { get; set; } = null;
    }
}
