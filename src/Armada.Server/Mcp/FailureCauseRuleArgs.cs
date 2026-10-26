namespace Armada.Server.Mcp
{
    using Armada.Core.Models;

    /// <summary>
    /// MCP arguments for create_failure_cause_rule and update_failure_cause_rule. On update, an omitted field keeps its
    /// stored value.
    /// </summary>
    public class FailureCauseRuleArgs
    {
        /// <summary>
        /// Rule ID (fcr_ prefix); required for update.
        /// </summary>
        public string? RuleId { get; set; } = null;

        /// <summary>
        /// Tenant for a rule created by a global admin; omit for every tenant (create only; ignored for other callers).
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Short name, unique per tenant, at most 200 characters.
        /// </summary>
        public string? Name { get; set; } = null;

        /// <summary>
        /// What the rule recognizes and why.
        /// </summary>
        public string? Description { get; set; } = null;

        /// <summary>
        /// Evaluation order, lowest first (default 100 on create).
        /// </summary>
        public int? Priority { get; set; } = null;

        /// <summary>
        /// Whether the rule is evaluated (default true on create).
        /// </summary>
        public bool? Enabled { get; set; } = null;

        /// <summary>
        /// Cause assigned on a match (a FailureCauseEnum name other than None).
        /// </summary>
        public string? Cause { get; set; } = null;

        /// <summary>
        /// What the rule matches (at least one criterion).
        /// </summary>
        public FailureCauseRuleMatch? Match { get; set; } = null;
    }
}
