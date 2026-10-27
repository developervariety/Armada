namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP arguments for classify_stage_outcome.
    /// </summary>
    public class StageOutcomeClassifyArgs
    {
        /// <summary>
        /// Outcome ID (sto_ prefix).
        /// </summary>
        public string OutcomeId { get; set; } = "";

        /// <summary>
        /// Cause to set by hand (a FailureCauseEnum name other than None), or null to hand the outcome back to the rules.
        /// </summary>
        public string? Cause { get; set; } = null;

        /// <summary>
        /// Optional note (evidence).
        /// </summary>
        public string? Note { get; set; } = null;
    }
}
