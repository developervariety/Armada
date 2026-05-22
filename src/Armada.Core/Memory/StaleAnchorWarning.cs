namespace Armada.Core.Memory
{
    /// <summary>A staleness warning for an anchored reflection memory note.</summary>
    public sealed class StaleAnchorWarning
    {
        #region Public-Members

        /// <summary>Event ID of the reflection.accepted event that produced this anchor.</summary>
        public string EventId { get; set; } = "";

        /// <summary>Playbook ID where the accepted memory note lives.</summary>
        public string? PlaybookId { get; set; }

        /// <summary>Mission ID that produced the accepted memory note.</summary>
        public string? SourceMissionId { get; set; }

        /// <summary>Warning kind: missing_file or missing_mission.</summary>
        public string WarnKind { get; set; } = "";

        /// <summary>File path that could not be resolved on disk (set when WarnKind is missing_file).</summary>
        public string? AffectedPath { get; set; }

        /// <summary>Mission ID that no longer resolves in the database (set when WarnKind is missing_mission).</summary>
        public string? AffectedMissionId { get; set; }

        /// <summary>Human-readable description of the staleness.</summary>
        public string Detail { get; set; } = "";

        #endregion
    }
}
