namespace Armada.Core.Models
{
    /// <summary>
    /// Detail response for a refinement session.
    /// </summary>
    public class ObjectiveRefinementSessionDetail
    {
        public ObjectiveRefinementSession Session { get; set; } = new ObjectiveRefinementSession();
        public List<ObjectiveRefinementMessage> Messages { get; set; } = new List<ObjectiveRefinementMessage>();
        public Captain? Captain { get; set; } = null;
        public Vessel? Vessel { get; set; } = null;
        public Objective? Objective { get; set; } = null;
    }
}
