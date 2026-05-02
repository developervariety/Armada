using System;

namespace Armada.Core.Models
{
    /// <summary>
    /// One time bucket in request-history summary charts.
    /// </summary>
    public class RequestHistorySummaryBucket
    {
        #region Public-Members

        /// <summary>
        /// Inclusive bucket start.
        /// </summary>
        public DateTime BucketStartUtc { get; set; }

        /// <summary>
        /// Exclusive bucket end.
        /// </summary>
        public DateTime BucketEndUtc { get; set; }

        /// <summary>
        /// Total requests in the bucket.
        /// </summary>
        public int TotalCount { get; set; }

        /// <summary>
        /// Successful requests in the bucket.
        /// </summary>
        public int SuccessCount { get; set; }

        /// <summary>
        /// Failed requests in the bucket.
        /// </summary>
        public int FailureCount { get; set; }

        /// <summary>
        /// Average duration in milliseconds.
        /// </summary>
        public double AverageDurationMs { get; set; }

        #endregion
    }
}
