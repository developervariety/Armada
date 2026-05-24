namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One mission-history chart bucket.
    /// </summary>
    public class MissionHistoryBucket
    {
        /// <summary>
        /// Inclusive bucket start in UTC.
        /// </summary>
        public DateTime StartUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Count of completed missions in this bucket.
        /// </summary>
        public int CompleteCount { get; set; } = 0;

        /// <summary>
        /// Count of failed missions in this bucket.
        /// </summary>
        public int FailedCount { get; set; } = 0;

        /// <summary>
        /// Count of all other mission statuses in this bucket.
        /// </summary>
        public int OtherCount { get; set; } = 0;

        /// <summary>
        /// Total mission count in this bucket.
        /// </summary>
        public int TotalCount => CompleteCount + FailedCount + OtherCount;
    }
}
