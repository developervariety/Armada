namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP tool arguments for listing captain-reported papercuts.
    /// </summary>
    public class PapercutListArgs
    {
        /// <summary>
        /// Filter to one vessel.
        /// </summary>
        public string? VesselId { get; set; }

        /// <summary>
        /// Filter to one category.
        /// </summary>
        public string? Category { get; set; }

        /// <summary>
        /// Minimum severity to include: Low, Medium, or High.
        /// </summary>
        public string? MinSeverity { get; set; }

        /// <summary>
        /// Only include reports newer than this many hours.
        /// </summary>
        public int? SinceHours { get; set; }

        /// <summary>
        /// Return every report instead of collapsing repeats into groups.
        /// </summary>
        public bool? Ungrouped { get; set; }

        /// <summary>
        /// Maximum rows returned (default 25).
        /// </summary>
        public int? Limit { get; set; }

        /// <summary>
        /// Number of stored reports to scan before filtering (default 500, maximum 5000).
        /// </summary>
        public int? ScanLimit { get; set; }
    }
}
