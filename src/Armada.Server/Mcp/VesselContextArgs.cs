namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP tool arguments for updating a vessel's project context and style guide.
    /// </summary>
    public class VesselContextArgs
    {
        /// <summary>
        /// Vessel ID (vsl_ prefix).
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Project context describing architecture, key files, and dependencies.
        /// </summary>
        public string? ProjectContext { get; set; }

        /// <summary>
        /// Style guide describing naming conventions, patterns, and library preferences.
        /// </summary>
        public string? StyleGuide { get; set; }

        /// <summary>
        /// Agent-accumulated context about this repository.
        /// </summary>
        public string? ModelContext { get; set; }

        /// <summary>
        /// Orchestrator/operator opt-in permitting a direct <see cref="ModelContext"/> write. Captains must
        /// NOT set this -- they emit [CLAUDE.MD-PROPOSAL] / [LEARNED-FACT-PROPOSAL] instead and the
        /// orchestrator applies approved edits with this flag. When true, a null/empty modelContext is a
        /// deliberate clear.
        /// </summary>
        public bool OperatorOverride { get; set; }
    }
}
