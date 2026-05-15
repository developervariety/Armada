namespace Armada.Core.Models
{
    /// <summary>
    /// Response returned after applying a refinement summary back to an objective.
    /// </summary>
    public class ObjectiveRefinementApplyResponse
    {
        public ObjectiveRefinementSummaryResponse Summary { get; set; } = new ObjectiveRefinementSummaryResponse();
        public Objective Objective { get; set; } = new Objective();
    }
}
