namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP arguments for list_stage_outcomes and reclassify_stage_outcomes (the stage outcome filters; reclassify
    /// ignores paging).
    /// </summary>
    public class StageOutcomeListArgs
    {
        /// <summary>
        /// Tenant filter (global admins only), or null.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Mission filter, or null.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Voyage filter, or null.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Vessel filter, or null.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Captain filter, or null.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// Persona name (exact, case-insensitive), or null.
        /// </summary>
        public string? Persona { get; set; } = null;

        /// <summary>
        /// Model (exact, case-insensitive), or null.
        /// </summary>
        public string? Model { get; set; } = null;

        /// <summary>
        /// Suspected cause (FailureCauseEnum name), or null.
        /// </summary>
        public string? SuspectedCause { get; set; } = null;

        /// <summary>
        /// Cause source (None, Rule, Judge, Manual), or null.
        /// </summary>
        public string? CauseSource { get; set; } = null;

        /// <summary>
        /// Disposition (StageDispositionEnum name), or null.
        /// </summary>
        public string? Disposition { get; set; } = null;

        /// <summary>
        /// Evaluation attempts only (true), production only (false), or both (null).
        /// </summary>
        public bool? IsEvaluation { get; set; } = null;

        /// <summary>
        /// Ended at or after (ISO 8601 UTC), or null.
        /// </summary>
        public string? FromUtc { get; set; } = null;

        /// <summary>
        /// Ended before (ISO 8601 UTC), or null.
        /// </summary>
        public string? ToUtc { get; set; } = null;

        /// <summary>
        /// Page number (default 1).
        /// </summary>
        public int? PageNumber { get; set; } = null;

        /// <summary>
        /// Page size (1-1000, default 25 for MCP).
        /// </summary>
        public int? PageSize { get; set; } = null;
    }
}
