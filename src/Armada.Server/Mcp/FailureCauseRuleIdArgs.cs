namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP arguments naming a failure-cause rule.
    /// </summary>
    public class FailureCauseRuleIdArgs
    {
        /// <summary>
        /// Rule ID (fcr_ prefix).
        /// </summary>
        public string RuleId { get; set; } = "";
    }
}
