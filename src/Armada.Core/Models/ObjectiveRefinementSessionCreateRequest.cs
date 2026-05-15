namespace Armada.Core.Models
{
    /// <summary>
    /// Request payload for creating an objective refinement session.
    /// </summary>
    public class ObjectiveRefinementSessionCreateRequest
    {
        /// <summary>
        /// Gets or sets the captain identifier.
        /// </summary>
        public string CaptainId { get; set; } = String.Empty;
        /// <summary>
        /// Gets or sets the fleet identifier.
        /// </summary>
        public string? FleetId { get; set; } = null;
        /// <summary>
        /// Gets or sets the vessel identifier.
        /// </summary>
        public string? VesselId { get; set; } = null;
        /// <summary>
        /// Gets or sets the title.
        /// </summary>
        public string? Title { get; set; } = null;
        /// <summary>
        /// Gets or sets the initial message.
        /// </summary>
        public string? InitialMessage { get; set; } = null;
    }
}
