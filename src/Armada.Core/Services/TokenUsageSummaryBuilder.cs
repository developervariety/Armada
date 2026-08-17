namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Models;

    /// <summary>
    /// Builds token-usage summaries (time buckets with a per-model breakdown, a whole-window per-model
    /// aggregate, and grand totals) from matching token-usage records.
    /// </summary>
    public static class TokenUsageSummaryBuilder
    {
        #region Public-Methods

        /// <summary>
        /// Build a token-usage summary for the supplied records and query window.
        /// </summary>
        /// <param name="records">Token-usage records that matched the query.</param>
        /// <param name="query">The query window (from/to/bucket width).</param>
        /// <returns>The aggregated summary.</returns>
        public static TokenUsageSummaryResult Build(List<TokenUsageRecord> records, TokenUsageQuery query)
        {
            List<TokenUsageRecord> safeRecords = records ?? new List<TokenUsageRecord>();
            TokenUsageQuery safeQuery = query ?? new TokenUsageQuery();

            TokenUsageSummaryResult result = new TokenUsageSummaryResult
            {
                FromUtc = safeQuery.FromUtc,
                ToUtc = safeQuery.ToUtc,
                BucketMinutes = safeQuery.BucketMinutes <= 0 ? 15 : safeQuery.BucketMinutes,
                RecordCount = safeRecords.Count,
                EstimatedCount = safeRecords.Count(record => record.Estimated)
            };

            // Grand totals and whole-window per-model aggregate.
            Dictionary<string, TokenUsageModelBreakdown> byModel = new Dictionary<string, TokenUsageModelBreakdown>(StringComparer.OrdinalIgnoreCase);

            // Time buckets keyed by floored start; each bucket carries its own per-model breakdown.
            Dictionary<DateTime, TokenUsageBucket> buckets = new Dictionary<DateTime, TokenUsageBucket>();
            Dictionary<DateTime, Dictionary<string, TokenUsageModelBreakdown>> bucketModels =
                new Dictionary<DateTime, Dictionary<string, TokenUsageModelBreakdown>>();

            foreach (TokenUsageRecord record in safeRecords)
            {
                string model = string.IsNullOrWhiteSpace(record.Model) ? "unknown" : record.Model;

                result.InputTokens += record.InputTokens;
                result.OutputTokens += record.OutputTokens;
                result.CachedTokens += record.CachedTokens;
                result.TotalTokens += record.TotalTokens;

                Accumulate(byModel, model, record);

                DateTime bucketStart = FloorToBucket(record.CreatedUtc, result.BucketMinutes);
                if (!buckets.TryGetValue(bucketStart, out TokenUsageBucket? bucket))
                {
                    bucket = new TokenUsageBucket
                    {
                        BucketStartUtc = bucketStart,
                        BucketEndUtc = bucketStart.AddMinutes(result.BucketMinutes)
                    };
                    buckets[bucketStart] = bucket;
                    bucketModels[bucketStart] = new Dictionary<string, TokenUsageModelBreakdown>(StringComparer.OrdinalIgnoreCase);
                }

                bucket.InputTokens += record.InputTokens;
                bucket.OutputTokens += record.OutputTokens;
                bucket.CachedTokens += record.CachedTokens;
                bucket.TotalTokens += record.TotalTokens;
                Accumulate(bucketModels[bucketStart], model, record);
            }

            // Gap-fill the requested window so the time axis is continuous.
            if (safeQuery.FromUtc.HasValue && safeQuery.ToUtc.HasValue)
            {
                DateTime cursor = FloorToBucket(safeQuery.FromUtc.Value.ToUniversalTime(), result.BucketMinutes);
                DateTime end = safeQuery.ToUtc.Value.ToUniversalTime();
                while (cursor <= end)
                {
                    if (!buckets.ContainsKey(cursor))
                    {
                        buckets[cursor] = new TokenUsageBucket
                        {
                            BucketStartUtc = cursor,
                            BucketEndUtc = cursor.AddMinutes(result.BucketMinutes)
                        };
                        bucketModels[cursor] = new Dictionary<string, TokenUsageModelBreakdown>(StringComparer.OrdinalIgnoreCase);
                    }

                    cursor = cursor.AddMinutes(result.BucketMinutes);
                }
            }

            foreach (KeyValuePair<DateTime, TokenUsageBucket> pair in buckets)
            {
                pair.Value.Models = bucketModels[pair.Key].Values
                    .OrderByDescending(entry => entry.TotalTokens)
                    .ToList();
            }

            result.Buckets = buckets.Values
                .OrderBy(bucket => bucket.BucketStartUtc)
                .ToList();

            result.ByModel = byModel.Values
                .OrderByDescending(entry => entry.TotalTokens)
                .ToList();

            return result;
        }

        #endregion

        #region Private-Methods

        private static void Accumulate(Dictionary<string, TokenUsageModelBreakdown> map, string model, TokenUsageRecord record)
        {
            if (!map.TryGetValue(model, out TokenUsageModelBreakdown? breakdown))
            {
                breakdown = new TokenUsageModelBreakdown { Model = model };
                map[model] = breakdown;
            }

            breakdown.InputTokens += record.InputTokens;
            breakdown.OutputTokens += record.OutputTokens;
            breakdown.CacheReadTokens += record.CachedTokens;
            breakdown.TotalTokens += record.TotalTokens;
        }

        private static DateTime FloorToBucket(DateTime value, double bucketMinutes)
        {
            // Floor on the absolute epoch grid so bucket boundaries match the dashboard's
            // Math.floor(timeMs / bucketMs) * bucketMs alignment for every bucket width. Supports
            // fractional bucket widths (for example 0.5 minutes = 30-second buckets).
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
