namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP arguments for list_failure_cause_rules.
    /// </summary>
    public class FailureCauseRuleListArgs
    {
        /// <summary>
        /// Page number (default 1).
        /// </summary>
        public int? PageNumber { get; set; } = null;

        /// <summary>
        /// Page size (1-1000, default 100 for MCP).
        /// </summary>
        public int? PageSize { get; set; } = null;
    }
}
