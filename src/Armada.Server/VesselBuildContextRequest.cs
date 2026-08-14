namespace Armada.Server
{
    /// <summary>
    /// Request body for building or refining a vessel's Model Context.
    /// </summary>
    public class VesselBuildContextRequest
    {
        /// <summary>
        /// Captain identifier (cpt_ prefix) whose runtime analyzes the repository.
        /// </summary>
        public string CaptainId { get; set; } = "";

        /// <summary>
        /// Optional operator guidance for the captain to focus on while building the context.
        /// </summary>
        public string? Notes { get; set; } = null;
    }
}
