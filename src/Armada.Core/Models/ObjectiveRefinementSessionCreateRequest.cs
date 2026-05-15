namespace Armada.Core.Models
{
    /// <summary>
    /// Request payload for creating an objective refinement session.
    /// </summary>
    public class ObjectiveRefinementSessionCreateRequest
    {
        public string CaptainId { get; set; } = String.Empty;
        public string? FleetId { get; set; } = null;
        public string? VesselId { get; set; } = null;
        public string? Title { get; set; } = null;
        public string? InitialMessage { get; set; } = null;
    }
}
