namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// First-class cross-repository objective or intake record.
    /// </summary>
    public class Objective
    {
        /// <summary>
        /// Unique identifier.
        /// </summary>
        public string Id
        {
            get => _Id;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(Id));
                _Id = value.Trim();
            }
        }

        /// <summary>
        /// Tenant identifier.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Creating or owning user identifier.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Human-facing title.
        /// </summary>
        public string Title
        {
            get => _Title;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(Title));
                _Title = value.Trim();
            }
        }

        /// <summary>
        /// Optional long-form description.
        /// </summary>
        public string? Description { get; set; } = null;

        /// <summary>
        /// Objective state.
        /// </summary>
        public ObjectiveStatusEnum Status { get; set; } = ObjectiveStatusEnum.Draft;

        /// <summary>
        /// Optional owner display label or principal reference.
        /// </summary>
        public string? Owner { get; set; } = null;

        /// <summary>
        /// Freeform tags.
        /// </summary>
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>
        /// Acceptance criteria captured for the objective.
        /// </summary>
        public List<string> AcceptanceCriteria { get; set; } = new List<string>();

        /// <summary>
        /// Explicit non-goals.
        /// </summary>
        public List<string> NonGoals { get; set; } = new List<string>();

        /// <summary>
        /// Rollout or execution constraints.
        /// </summary>
        public List<string> RolloutConstraints { get; set; } = new List<string>();

        /// <summary>
        /// Evidence or source links.
        /// </summary>
        public List<string> EvidenceLinks { get; set; } = new List<string>();

        /// <summary>
        /// Linked fleet identifiers.
        /// </summary>
        public List<string> FleetIds { get; set; } = new List<string>();

        /// <summary>
        /// Linked vessel identifiers.
        /// </summary>
        public List<string> VesselIds { get; set; } = new List<string>();

        /// <summary>
        /// Linked planning-session identifiers.
        /// </summary>
        public List<string> PlanningSessionIds { get; set; } = new List<string>();

        /// <summary>
        /// Linked voyage identifiers.
        /// </summary>
        public List<string> VoyageIds { get; set; } = new List<string>();

        /// <summary>
        /// Linked mission identifiers.
        /// </summary>
        public List<string> MissionIds { get; set; } = new List<string>();

        /// <summary>
        /// Linked check-run identifiers.
        /// </summary>
        public List<string> CheckRunIds { get; set; } = new List<string>();

        /// <summary>
        /// Linked release identifiers.
        /// </summary>
        public List<string> ReleaseIds { get; set; } = new List<string>();

        /// <summary>
        /// Linked deployment identifiers.
        /// </summary>
        public List<string> DeploymentIds { get; set; } = new List<string>();

        /// <summary>
        /// Linked incident identifiers.
        /// </summary>
        public List<string> IncidentIds { get; set; } = new List<string>();

        /// <summary>
        /// Creation timestamp in UTC.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Last update timestamp in UTC.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Completion timestamp in UTC.
        /// </summary>
        public DateTime? CompletedUtc { get; set; } = null;

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.ObjectiveIdPrefix, 24);
        private string _Title = "Objective";
    }
}
