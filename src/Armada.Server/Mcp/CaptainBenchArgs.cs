namespace Armada.Server.Mcp
{
    using System;

    /// <summary>
    /// MCP tool arguments for benching (quarantining) a captain with a reason and expiry.
    /// </summary>
    public class CaptainBenchArgs
    {
        /// <summary>
        /// Captain ID (cpt_ prefix).
        /// </summary>
        public string CaptainId { get; set; } = "";

        /// <summary>
        /// Operator-visible reason the captain is being benched.
        /// </summary>
        public string Reason { get; set; } = "";

        /// <summary>
        /// Optional UTC instant at which the bench expires. Takes precedence over
        /// <see cref="DurationMinutes"/> when both are supplied.
        /// </summary>
        public DateTime? UntilUtc { get; set; } = null;

        /// <summary>
        /// Optional bench duration in minutes, applied from now when
        /// <see cref="UntilUtc"/> is not supplied.
        /// </summary>
        public int? DurationMinutes { get; set; } = null;
    }
}
