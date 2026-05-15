namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Lightweight captain-backed refinement session attached to a backlog entry.
    /// </summary>
    public class ObjectiveRefinementSession
    {
        /// <summary>
        /// Gets or sets the identifier.
        /// </summary>
        public string Id { get; set; } = Constants.IdGenerator.GenerateKSortable("ors_", 24);
        /// <summary>
        /// Gets or sets the objective identifier.
        /// </summary>
        public string ObjectiveId { get; set; } = String.Empty;
        /// <summary>
        /// Gets or sets the tenant identifier.
        /// </summary>
        public string? TenantId { get; set; } = null;
        /// <summary>
        /// Gets or sets the user identifier.
        /// </summary>
        public string? UserId { get; set; } = null;
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
        public string Title { get; set; } = "Objective Refinement";
        /// <summary>
        /// Gets or sets the status.
        /// </summary>
        public ObjectiveRefinementSessionStatusEnum Status { get; set; } = ObjectiveRefinementSessionStatusEnum.Created;
        /// <summary>
        /// Gets or sets the process identifier.
        /// </summary>
        public int? ProcessId { get; set; } = null;
        /// <summary>
        /// Gets or sets the failure reason.
        /// </summary>
        public string? FailureReason { get; set; } = null;
        /// <summary>
        /// Gets or sets the creation UTC timestamp.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        /// <summary>
        /// Gets or sets the start UTC timestamp.
        /// </summary>
        public DateTime? StartedUtc { get; set; } = null;
        /// <summary>
        /// Gets or sets the completion UTC timestamp.
        /// </summary>
        public DateTime? CompletedUtc { get; set; } = null;
        /// <summary>
        /// Gets or sets the last update UTC timestamp.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;
    }
}
