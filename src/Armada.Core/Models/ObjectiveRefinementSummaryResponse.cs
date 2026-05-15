namespace Armada.Core.Models
{
    /// <summary>
    /// Structured refinement summary that can be applied back to an objective.
    /// </summary>
    public class ObjectiveRefinementSummaryResponse
    {
        public string SessionId { get; set; } = String.Empty;
        public string? MessageId { get; set; } = null;
        public string Summary { get; set; } = String.Empty;
        public List<string> AcceptanceCriteria { get; set; } = new List<string>();
        public List<string> NonGoals { get; set; } = new List<string>();
        public List<string> RolloutConstraints { get; set; } = new List<string>();
        public string? SuggestedPipelineId { get; set; } = null;
        public string Method { get; set; } = "assistant-fallback";
    }
}
