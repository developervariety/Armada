namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP tool arguments for resolve_workflow_profile and preview_workflow_profile.
    /// </summary>
    public class WorkflowProfileVesselArgs
    {
        /// <summary>
        /// Vessel identifier (vsl_ prefix).
        /// </summary>
        public string? VesselId { get; set; }

        /// <summary>
        /// Optional explicit workflow profile override (wfp_ prefix).
        /// </summary>
        public string? WorkflowProfileId { get; set; }
    }
}
