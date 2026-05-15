namespace Armada.Core.Models
{
    /// <summary>
    /// Structured refinement summary that can be applied back to an objective.
    /// </summary>
    public class ObjectiveRefinementSummaryResponse
    {
        /// <summary>
        /// Gets or sets the refinement session identifier.
        /// </summary>
        public string SessionId { get; set; } = String.Empty;
        /// <summary>
        /// Gets or sets the message identifier.
        /// </summary>
        public string? MessageId { get; set; } = null;
        /// <summary>
        /// Gets or sets the summary.
        /// </summary>
        public string Summary { get; set; } = String.Empty;
        /// <summary>
        /// Gets or sets the acceptance criteria.
        /// </summary>
        public List<string> AcceptanceCriteria { get; set; } = new List<string>();
        /// <summary>
        /// Gets or sets the non-goals.
        /// </summary>
        public List<string> NonGoals { get; set; } = new List<string>();
        /// <summary>
        /// Gets or sets the rollout constraints.
        /// </summary>
        public List<string> RolloutConstraints { get; set; } = new List<string>();
        /// <summary>
        /// Gets or sets the suggested pipeline identifier.
        /// </summary>
        public string? SuggestedPipelineId { get; set; } = null;
        /// <summary>
        /// Gets or sets the summary generation method.
        /// </summary>
        public string Method { get; set; } = "assistant-fallback";
    }
}
