namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP tool arguments for playbook CRUD operations.
    /// </summary>
    public class PlaybookArgs
    {
        /// <summary>
        /// Playbook identifier.
        /// </summary>
        public string? Id { get; set; }

        /// <summary>
        /// Playbook file name.
        /// </summary>
        public string? FileName { get; set; }

        /// <summary>
        /// Optional human-readable description.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Markdown body.
        /// </summary>
        public string? Content { get; set; }

        /// <summary>
        /// Whether the playbook is active.
        /// </summary>
        public bool? Active { get; set; }

        /// <summary>
        /// Optional ownership scope for create (UserSpecific or TenantWide), resolved against what the caller may create.
        /// </summary>
        public Armada.Core.Enums.ScopeEnum? Scope { get; set; }
    }
}
