namespace Armada.Core.Models
{
    /// <summary>
    /// Request payload for applying a refinement summary back to an objective.
    /// </summary>
    public class ObjectiveRefinementApplyRequest
    {
        public string? MessageId { get; set; } = null;
        public bool MarkMessageSelected { get; set; } = true;
        public bool PromoteBacklogState { get; set; } = true;
    }
}
