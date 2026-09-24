namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The token_usage row-to-model contract, shared by every provider. Every count is a 64-bit integer; an input
    /// bucket that was never recorded reads as null, and a usage rule outside the model reads as the legacy rule.
    /// </summary>
    internal static class TokenUsageColumns
    {
        /// <summary>
        /// Read a token_usage row.
        /// </summary>
        /// <param name="record">Reader positioned on a token_usage row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The token-usage record.</returns>
        internal static TokenUsageRecord Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "TokenUsageRecord");
            TokenUsageRecord usage = new TokenUsageRecord();
            usage.Id = row.Text("id");
            usage.TenantId = row.NullableText("tenant_id");
            usage.UserId = row.NullableText("user_id");
            usage.Model = row.Text("model");
            usage.Runtime = row.NullableText("runtime");
            usage.Source = row.Text("source");
            usage.SourceId = row.NullableText("source_id");
            usage.VesselId = row.NullableText("vessel_id");
            usage.CaptainId = row.NullableText("captain_id");
            usage.InputTokens = row.Long("input_tokens");
            usage.OutputTokens = row.Long("output_tokens");
            usage.CachedTokens = row.Long("cached_tokens");
            usage.UncachedInputTokens = row.NullableLong("uncached_input_tokens");
            usage.CacheReadInputTokens = row.NullableLong("cache_read_input_tokens");
            usage.CacheWriteInputTokens = row.NullableLong("cache_write_input_tokens");
            usage.UsageRule = row.EnumOrFallback("usage_rule", TokenUsageRuleEnum.Legacy);
            usage.TotalTokens = row.Long("total_tokens");
            usage.Estimated = row.Bool("estimated");
            usage.CreatedUtc = row.Utc("created_utc");
            return usage;
        }
    }
}
