namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Lightweight captain-backed refinement session attached to a backlog entry.
    /// </summary>
    public class ObjectiveRefinementSession
    {
        public string Id { get; set; } = Constants.IdGenerator.GenerateKSortable("ors_", 24);
        public string ObjectiveId { get; set; } = String.Empty;
        public string? TenantId { get; set; } = null;
        public string? UserId { get; set; } = null;
        public string CaptainId { get; set; } = String.Empty;
        public string? FleetId { get; set; } = null;
        public string? VesselId { get; set; } = null;
        public string Title { get; set; } = "Objective Refinement";
        public ObjectiveRefinementSessionStatusEnum Status { get; set; } = ObjectiveRefinementSessionStatusEnum.Created;
        public int? ProcessId { get; set; } = null;
        public string? FailureReason { get; set; } = null;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? StartedUtc { get; set; } = null;
        public DateTime? CompletedUtc { get; set; } = null;
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;
    }
}
