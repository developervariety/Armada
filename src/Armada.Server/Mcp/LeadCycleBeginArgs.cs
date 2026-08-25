namespace Armada.Server.Mcp
{
    /// <summary>
    /// Arguments for starting one unattended lead cycle.
    /// </summary>
    public class LeadCycleBeginArgs
    {
        /// <summary>
        /// True when the legacy lead requests standby fallback while Grok is primary.
        /// </summary>
        public bool StandbyFallback { get; set; } = false;
    }
}
