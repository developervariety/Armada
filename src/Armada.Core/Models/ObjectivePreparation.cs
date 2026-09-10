namespace Armada.Core.Models
{
    /// <summary>
    /// Bounded repository preparation that can be delivered to objective dispatch.
    /// </summary>
    public class ObjectivePreparation
    {
        /// <summary>
        /// Source repository revision inspected during preparation.
        /// </summary>
        public ObjectivePreparationAnchor? Source { get; set; } = null;

        /// <summary>
        /// Target repository revision inspected during preparation.
        /// </summary>
        public ObjectivePreparationAnchor? Target { get; set; } = null;

        /// <summary>
        /// Evidence-backed preparation claims.
        /// </summary>
        public List<ObjectivePreparationClaim> Claims { get; set; } = new List<ObjectivePreparationClaim>();
    }
}
