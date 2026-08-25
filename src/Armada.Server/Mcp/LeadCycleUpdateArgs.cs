namespace Armada.Server.Mcp
{
    /// <summary>
    /// Arguments for updating or ending one unattended lead cycle.
    /// </summary>
    public class LeadCycleUpdateArgs
    {
        /// <summary>
        /// Cycle identifier returned by armada_lead_cycle_begin.
        /// </summary>
        public string CycleId { get; set; } = String.Empty;

        /// <summary>
        /// Required handoff for normal completion.
        /// </summary>
        public string? Handoff { get; set; } = null;

        /// <summary>
        /// Failure or stop reason.
        /// </summary>
        public string? Reason { get; set; } = null;
    }
}
