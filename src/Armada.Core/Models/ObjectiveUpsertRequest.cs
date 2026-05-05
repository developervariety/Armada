namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Request payload for creating or updating an objective.
    /// </summary>
    public class ObjectiveUpsertRequest
    {
        /// <summary>
        /// Title override.
        /// </summary>
        public string? Title { get; set; } = null;

        /// <summary>
        /// Description override.
        /// </summary>
        public string? Description { get; set; } = null;

        /// <summary>
        /// Status override.
        /// </summary>
        public ObjectiveStatusEnum? Status { get; set; } = null;

        /// <summary>
        /// Owner override.
        /// </summary>
        public string? Owner { get; set; } = null;

        /// <summary>
        /// Tags.
        /// </summary>
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>
        /// Acceptance criteria.
        /// </summary>
        public List<string> AcceptanceCriteria { get; set; } = new List<string>();

        /// <summary>
        /// Non-goals.
        /// </summary>
        public List<string> NonGoals { get; set; } = new List<string>();

        /// <summary>
        /// Rollout constraints.
        /// </summary>
        public List<string> RolloutConstraints { get; set; } = new List<string>();

        /// <summary>
        /// Evidence links.
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
    }
}
