namespace Armada.Core.Models
{
    /// <summary>
    /// Detail response for a refinement session.
    /// </summary>
    public class ObjectiveRefinementSessionDetail
    {
        /// <summary>
        /// Gets or sets the refinement session.
        /// </summary>
        public ObjectiveRefinementSession Session { get; set; } = new ObjectiveRefinementSession();
        /// <summary>
        /// Gets or sets the refinement messages.
        /// </summary>
        public List<ObjectiveRefinementMessage> Messages { get; set; } = new List<ObjectiveRefinementMessage>();
        /// <summary>
        /// Gets or sets the captain.
        /// </summary>
        public Captain? Captain { get; set; } = null;
        /// <summary>
        /// Gets or sets the vessel.
        /// </summary>
        public Vessel? Vessel { get; set; } = null;
        /// <summary>
        /// Gets or sets the objective.
        /// </summary>
        public Objective? Objective { get; set; } = null;
    }
}
