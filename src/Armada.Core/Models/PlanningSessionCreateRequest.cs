namespace Armada.Core.Models
{
    /// <summary>
    /// Request payload for creating a planning session.
    /// </summary>
    public class PlanningSessionCreateRequest
    {
        /// <summary>
        /// Optional session title.
        /// </summary>
        public string? Title { get; set; } = null;

        /// <summary>
        /// Captain identifier to reserve.
        /// </summary>
        public string CaptainId { get; set; } = String.Empty;

        /// <summary>
        /// Vessel identifier to plan against.
        /// </summary>
        public string VesselId { get; set; } = String.Empty;

        /// <summary>
        /// Optional fleet identifier used for UX filtering.
        /// </summary>
        public string? FleetId { get; set; } = null;

        /// <summary>
        /// Optional pipeline identifier inherited by dispatch.
        /// </summary>
        public string? PipelineId { get; set; } = null;

        /// <summary>
        /// Optional ordered playbook selections.
        /// </summary>
        public List<SelectedPlaybook> SelectedPlaybooks { get; set; } = new List<SelectedPlaybook>();
    }
}
