namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Builds token-usage summaries (time buckets with a per-model breakdown, a whole-window per-model
    /// aggregate, and grand totals) from matching token-usage records. At every level the three input
    /// buckets sum only records under the separate-input-buckets rule, and legacy-rule records are summed
    /// and counted on their own, so a total never mixes the two rules without saying so.
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
                if (record.UsageRule == TokenUsageRuleEnum.SeparateInputBuckets)
                {
                    result.UncachedInputTokens += record.UncachedInputTokens ?? 0;
                    result.CacheReadInputTokens += record.CacheReadInputTokens ?? 0;
                    result.CacheWriteInputTokens += record.CacheWriteInputTokens ?? 0;
                }
                else
                {
                    result.LegacyInputTokens += record.InputTokens;
                    result.LegacyRecordCount++;
                }

                Accumulate(byModel, model, record);

                DateTime bucketStart = TimeBucketGrid.FloorUtc(record.CreatedUtc, result.BucketMinutes);
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
                if (record.UsageRule == TokenUsageRuleEnum.SeparateInputBuckets)
                {
                    bucket.UncachedInputTokens += record.UncachedInputTokens ?? 0;
                    bucket.CacheReadInputTokens += record.CacheReadInputTokens ?? 0;
                    bucket.CacheWriteInputTokens += record.CacheWriteInputTokens ?? 0;
                }
                else
                {
                    bucket.LegacyInputTokens += record.InputTokens;
                    bucket.LegacyRecordCount++;
                }
                Accumulate(bucketModels[bucketStart], model, record);
            }

            // Gap-fill the requested window so the time axis is continuous.
            if (safeQuery.FromUtc.HasValue && safeQuery.ToUtc.HasValue)
            {
                DateTime cursor = TimeBucketGrid.FloorUtc(safeQuery.FromUtc.Value.ToUniversalTime(), result.BucketMinutes);
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
            breakdown.CacheWriteTokens += record.CacheWriteInputTokens ?? 0;
            breakdown.TotalTokens += record.TotalTokens;
            if (record.UsageRule == TokenUsageRuleEnum.SeparateInputBuckets)
            {
                breakdown.UncachedInputTokens += record.UncachedInputTokens ?? 0;
                breakdown.CacheReadInputTokens += record.CacheReadInputTokens ?? 0;
                breakdown.CacheWriteInputTokens += record.CacheWriteInputTokens ?? 0;
            }
            else
            {
                breakdown.LegacyInputTokens += record.InputTokens;
                breakdown.LegacyRecordCount++;
            }
        }

        #endregion
    }
}
