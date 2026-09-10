namespace Armada.Server.Mcp
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Arguments for a read-only objective dispatch preview.
    /// </summary>
    public class ObjectiveDispatchPreviewArgs
    {
        /// <summary>
        /// Objective identifier.
        /// </summary>
        public string ObjectiveId { get; set; } = String.Empty;

        /// <summary>
        /// Optional target vessel override.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Optional pipeline identifier or name override.
        /// </summary>
        public string? PipelineId { get; set; } = null;

        /// <summary>
        /// Optional per-persona captain routing overrides.
        /// </summary>
        public List<CaptainAssignmentOverride>? CaptainAssignments { get; set; } = null;
    }
}
