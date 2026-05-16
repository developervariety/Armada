namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Models;

    /// <summary>
    /// Builds request-history summaries from matching request-history entries.
    /// </summary>
    public static class RequestHistorySummaryBuilder
    {
        /// <summary>
        /// Build a request-history summary for the supplied entries and query window.
        /// </summary>
        public static RequestHistorySummaryResult Build(List<RequestHistoryEntry> entries, RequestHistoryQuery query)
        {
            List<RequestHistoryEntry> safeEntries = entries ?? new List<RequestHistoryEntry>();
            RequestHistoryQuery safeQuery = query ?? new RequestHistoryQuery();

            RequestHistorySummaryResult result = new RequestHistorySummaryResult
            {
                TotalCount = safeEntries.Count,
                SuccessCount = safeEntries.Count(entry => entry.IsSuccess),
                FailureCount = safeEntries.Count(entry => !entry.IsSuccess),
                AverageDurationMs = safeEntries.Count == 0 ? 0 : Math.Round(safeEntries.Average(entry => entry.DurationMs), 2),
                FromUtc = safeQuery.FromUtc,
                ToUtc = safeQuery.ToUtc,
                BucketMinutes = safeQuery.BucketMinutes <= 0 ? 15 : safeQuery.BucketMinutes
            };

            result.SuccessRate = result.TotalCount == 0
                ? 0
                : Math.Round((double)result.SuccessCount / result.TotalCount * 100.0, 2);

            Dictionary<DateTime, RequestHistorySummaryBucket> buckets = new Dictionary<DateTime, RequestHistorySummaryBucket>();
            foreach (RequestHistoryEntry entry in safeEntries)
            {
                DateTime bucketStart = FloorToBucket(entry.CreatedUtc, result.BucketMinutes);
                if (!buckets.TryGetValue(bucketStart, out RequestHistorySummaryBucket? bucket))
                {
                    bucket = new RequestHistorySummaryBucket
                    {
                        BucketStartUtc = bucketStart,
                        BucketEndUtc = bucketStart.AddMinutes(result.BucketMinutes)
                    };
                    buckets[bucketStart] = bucket;
                }

                bucket.TotalCount++;
                if (entry.IsSuccess) bucket.SuccessCount++;
                else bucket.FailureCount++;
                bucket.AverageDurationMs = bucket.TotalCount == 1
                    ? entry.DurationMs
                    : Math.Round(((bucket.AverageDurationMs * (bucket.TotalCount - 1)) + entry.DurationMs) / bucket.TotalCount, 2);
            }

            if (safeQuery.FromUtc.HasValue && safeQuery.ToUtc.HasValue)
            {
                DateTime cursor = FloorToBucket(safeQuery.FromUtc.Value.ToUniversalTime(), result.BucketMinutes);
                DateTime end = safeQuery.ToUtc.Value.ToUniversalTime();
                while (cursor <= end)
                {
                    if (!buckets.ContainsKey(cursor))
                    {
                        buckets[cursor] = new RequestHistorySummaryBucket
                        {
                            BucketStartUtc = cursor,
                            BucketEndUtc = cursor.AddMinutes(result.BucketMinutes)
                        };
                    }

                    cursor = cursor.AddMinutes(result.BucketMinutes);
                }
            }

            result.Buckets = buckets.Values
                .OrderBy(bucket => bucket.BucketStartUtc)
                .ToList();
            return result;
        }

        private static DateTime FloorToBucket(DateTime value, int bucketMinutes)
        {
            DateTime utc = value.ToUniversalTime();
            int minute = utc.Minute - (utc.Minute % bucketMinutes);
            return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, minute, 0, DateTimeKind.Utc);
        }
    }
}
