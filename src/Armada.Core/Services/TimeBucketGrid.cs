namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// The one bucket-alignment rule for time-series summaries. Buckets sit on the absolute epoch grid,
    /// so every bucket width (including widths longer than an hour or that do not divide an hour) matches
    /// the dashboard's floor(timeMs / bucketMs) * bucketMs alignment.
    /// </summary>
    public static class TimeBucketGrid
    {
        #region Public-Methods

        /// <summary>
        /// Floor a timestamp to the start of its bucket on the epoch grid.
        /// </summary>
        /// <param name="value">Timestamp to floor; converted to UTC.</param>
        /// <param name="bucketMinutes">Bucket width in minutes. Fractional widths are supported; a non-positive width is treated as one minute.</param>
        /// <returns>The UTC start of the bucket that contains <paramref name="value"/>.</returns>
        public static DateTime FloorUtc(DateTime value, double bucketMinutes)
        {
            DateTime utc = value.ToUniversalTime();
            double minutes = bucketMinutes > 0 ? bucketMinutes : 1;
            long bucketTicks = (long)(minutes * TimeSpan.TicksPerMinute);
            if (bucketTicks < 1) bucketTicks = 1;
            long flooredTicks = (utc.Ticks / bucketTicks) * bucketTicks;
            return new DateTime(flooredTicks, DateTimeKind.Utc);
        }

        #endregion
    }
}
