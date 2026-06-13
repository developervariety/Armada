namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Represents an auto-rescue mission landing that is suspected of being a false positive --
    /// a rescue that completed without performing real corrective work.
    /// </summary>
    public class SuspectRescueLanding
    {
        #region Public-Members

        /// <summary>
        /// Mission identifier of the rescue mission.
        /// </summary>
        public string MissionId { get; set; } = string.Empty;

        /// <summary>
        /// Parent mission identifier -- the original mission the rescue was spawned to recover.
        /// </summary>
        public string? ParentMissionId { get; set; } = null;

        /// <summary>
        /// Vessel identifier.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Persona assigned to the rescue mission (e.g. "Worker", "Judge").
        /// </summary>
        public string? Persona { get; set; } = null;

        /// <summary>
        /// Git commit hash captured when the rescue mission completed.
        /// Null or empty when the mission completed without producing a commit.
        /// </summary>
        public string? CommitHash { get; set; } = null;

        /// <summary>
        /// UTC timestamp when the rescue mission completed.
        /// </summary>
        public DateTime? CompletedUtc { get; set; } = null;

        /// <summary>
        /// One or more reason codes explaining why this rescue landing is suspect.
        /// Values: reviewer_persona_rescue_completed, empty_commit_hash, noop_merge_entry, commit_hash_equals_parent.
        /// </summary>
        public List<string> Reasons { get; set; } = new List<string>();

        #endregion
    }
}
