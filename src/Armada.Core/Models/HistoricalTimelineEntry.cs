namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One cross-entity historical timeline entry.
    /// </summary>
    public class HistoricalTimelineEntry
    {
        /// <summary>
        /// Timeline entry identifier.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Source category such as Mission, CheckRun, MergeEntry, Event, or Request.
        /// </summary>
        public string SourceType { get; set; } = string.Empty;

        /// <summary>
        /// Source record identifier.
        /// </summary>
        public string SourceId { get; set; } = string.Empty;

        /// <summary>
        /// Optional entity type.
        /// </summary>
        public string? EntityType { get; set; } = null;

        /// <summary>
        /// Optional entity identifier.
        /// </summary>
        public string? EntityId { get; set; } = null;

        /// <summary>
        /// Optional objective identifier.
        /// </summary>
        public string? ObjectiveId { get; set; } = null;

        /// <summary>
        /// Optional vessel identifier.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Optional environment identifier.
        /// </summary>
        public string? EnvironmentId { get; set; } = null;

        /// <summary>
        /// Optional deployment identifier.
        /// </summary>
        public string? DeploymentId { get; set; } = null;

        /// <summary>
        /// Optional incident identifier.
        /// </summary>
        public string? IncidentId { get; set; } = null;

        /// <summary>
        /// Optional mission identifier.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Optional voyage identifier.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Optional user or actor identifier.
        /// </summary>
        public string? ActorId { get; set; } = null;

        /// <summary>
        /// Optional user-facing actor label.
        /// </summary>
        public string? ActorDisplay { get; set; } = null;

        /// <summary>
        /// Main timeline title.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Secondary timeline description.
        /// </summary>
        public string? Description { get; set; } = null;

        /// <summary>
        /// Optional status label.
        /// </summary>
        public string? Status { get; set; } = null;

        /// <summary>
        /// Optional severity or tone.
        /// </summary>
        public string? Severity { get; set; } = null;

        /// <summary>
        /// Optional related route, URL, or command hint.
        /// </summary>
        public string? Route { get; set; } = null;

        /// <summary>
        /// Timestamp of the entry in UTC.
        /// </summary>
        public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Optional serialized metadata for inspection.
        /// </summary>
        public string? MetadataJson { get; set; } = null;
    }
}
