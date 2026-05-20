namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Lightweight mission history point used for server-side chart aggregation.
    /// </summary>
    public class MissionHistoryPoint
    {
        /// <summary>
        /// Mission creation timestamp in UTC.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Mission status.
        /// </summary>
        public MissionStatusEnum Status { get; set; } = MissionStatusEnum.Pending;

        /// <summary>
        /// Associated vessel identifier.
        /// </summary>
        public string? VesselId { get; set; } = null;
    }
}
