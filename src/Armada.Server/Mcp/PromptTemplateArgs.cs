namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP tool arguments for prompt template operations.
    /// </summary>
    public class PromptTemplateArgs
    {
        /// <summary>
        /// Template name.
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Template content with {Placeholder} parameters.
        /// </summary>
        public string? Content { get; set; }

        /// <summary>
        /// Template description.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Template category.
        /// </summary>
        public string? Category { get; set; }

        /// <summary>
        /// Optional active flag.
        /// </summary>
        public bool? Active { get; set; }

        /// <summary>
        /// Optional 1-based page number for list operations.
        /// </summary>
        public int? PageNumber { get; set; }

        /// <summary>
        /// Optional page size for list operations.
        /// </summary>
        public int? PageSize { get; set; }

        /// <summary>
        /// Optional ownership scope for create (UserSpecific or TenantWide), resolved against what the caller may create.
        /// </summary>
        public Armada.Core.Enums.ScopeEnum? Scope { get; set; }
    }
}
