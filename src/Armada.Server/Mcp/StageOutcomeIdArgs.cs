namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP arguments naming a stage outcome (get_stage_outcome, draft_failure_cause_rule).
    /// </summary>
    public class StageOutcomeIdArgs
    {
        /// <summary>
        /// Outcome ID (sto_ prefix).
        /// </summary>
        public string OutcomeId { get; set; } = "";
    }
}
