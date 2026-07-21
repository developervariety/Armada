namespace Armada.Server.Mcp.Tools
{
    /// <summary>
    /// MCP tool arguments for the unlanded-branch report.
    /// </summary>
    public class UnlandedBranchArgs
    {
        /// <summary>
        /// Optional vessel ID (vsl_ prefix). Null reports every vessel.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// When true, include individual branch entries rather than counts only.
        /// </summary>
        public bool IncludeBranches { get; set; } = false;
    }
}
