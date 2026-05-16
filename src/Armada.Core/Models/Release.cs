namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// First-class release record aggregating linked work, checks, notes, and artifacts.
    /// </summary>
    public class Release
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
                _Id = value;
            }
        }

        /// <summary>
        /// Tenant identifier.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Owning user identifier.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Vessel identifier this release belongs to.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Workflow profile used when drafting this release.
        /// </summary>
        public string? WorkflowProfileId { get; set; } = null;

        /// <summary>
        /// Human-facing release title.
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
        /// Optional semantic or custom version label.
        /// </summary>
        public string? Version { get; set; } = null;

        /// <summary>
        /// Optional tag name associated with the release.
        /// </summary>
        public string? TagName { get; set; } = null;

        /// <summary>
        /// Short release summary.
        /// </summary>
        public string? Summary { get; set; } = null;

        /// <summary>
        /// Longer-form release notes or changelog body.
        /// </summary>
        public string? Notes { get; set; } = null;

        /// <summary>
        /// Release lifecycle state.
        /// </summary>
        public ReleaseStatusEnum Status { get; set; } = ReleaseStatusEnum.Draft;

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
        /// Linked release artifacts.
        /// </summary>
        public List<ReleaseArtifact> Artifacts { get; set; } = new List<ReleaseArtifact>();

        /// <summary>
        /// Creation timestamp in UTC.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Last update timestamp in UTC.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Ship timestamp in UTC once the release is marked shipped.
        /// </summary>
        public DateTime? PublishedUtc { get; set; } = null;

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.ReleaseIdPrefix, 24);
        private string _Title = "Draft Release";
    }
}
