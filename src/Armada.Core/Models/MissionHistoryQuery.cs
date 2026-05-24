namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Query parameters for aggregated mission history.
    /// </summary>
    public class MissionHistoryQuery
    {
        /// <summary>
        /// Inclusive start time in UTC.
        /// </summary>
        public DateTime FromUtc { get; set; } = DateTime.UtcNow.AddDays(-7);

        /// <summary>
        /// Exclusive end time in UTC.
        /// </summary>
        public DateTime ToUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Bucket size in minutes.
        /// </summary>
        public int BucketMinutes { get; set; } = 60;

        /// <summary>
        /// Optional fleet filter.
        /// </summary>
        public string? FleetId { get; set; } = null;

        /// <summary>
        /// Optional vessel filter.
        /// </summary>
        public string? VesselId { get; set; } = null;
    }
}
