namespace Armada.Test.Database
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Provider-backed persistence of a token-usage record's three input buckets and its counting rule, and the reading
    /// of a row stored before those columns existed.
    /// </summary>
    internal sealed class TokenUsageInputBucketsDatabaseTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly bool _NoCleanup;

        internal TokenUsageInputBucketsDatabaseTests(DatabaseDriver driver, DatabaseSettings settings, bool noCleanup)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _NoCleanup = noCleanup;
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            TokenUsageRecord bucketed = new TokenUsageRecord
            {
                Model = "bucket-model-" + suffix,
                Runtime = "ClaudeCode",
                Source = "mission",
                SourceId = "msn_bucket_" + suffix,
                UsageRule = TokenUsageRuleEnum.SeparateInputBuckets,
                UncachedInputTokens = 12,
                CacheReadInputTokens = 480000,
                CacheWriteInputTokens = 20000,
                InputTokens = 500012,
                OutputTokens = 3000,
                CachedTokens = 480000,
                TotalTokens = 503012
            };
            TokenUsageRecord legacy = new TokenUsageRecord
            {
                Model = "legacy-model-" + suffix,
                Runtime = "ClaudeCode",
                Source = "mission",
                SourceId = "msn_legacy_" + suffix,
                UsageRule = TokenUsageRuleEnum.SeparateInputBuckets,
                UncachedInputTokens = 1,
                CacheReadInputTokens = 1,
                CacheWriteInputTokens = 1,
                InputTokens = 12,
                OutputTokens = 3000,
                CachedTokens = 480000,
                TotalTokens = 3012
            };
            await _Driver.TokenUsage.CreateAsync(bucketed, token).ConfigureAwait(false);
            await _Driver.TokenUsage.CreateAsync(legacy, token).ConfigureAwait(false);

            try
            {
                TokenUsageRecord read = await ReadAsync(bucketed.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(TokenUsageRuleEnum.SeparateInputBuckets.ToString(), read.UsageRule.ToString(), "The counting rule round-trips");
                DatabaseAssert.Equal(12L, read.UncachedInputTokens ?? -1, "The uncached input bucket round-trips");
                DatabaseAssert.Equal(480000L, read.CacheReadInputTokens ?? -1, "The cache-read input bucket round-trips");
                DatabaseAssert.Equal(20000L, read.CacheWriteInputTokens ?? -1, "The cache-write input bucket round-trips");
                DatabaseAssert.Equal(500012L, read.InputTokens, "Input is stored as the sum of the buckets");
                DatabaseAssert.Equal(503012L, read.TotalTokens, "Total is stored as input plus output");

                // A row written before the columns existed carries nulls in all four; make one by clearing them.
                using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
                {
                    await connection.OpenAsync(token).ConfigureAwait(false);
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "UPDATE token_usage SET uncached_input_tokens = NULL, cache_read_input_tokens = NULL, " +
                            "cache_write_input_tokens = NULL, usage_rule = NULL WHERE id = '" + legacy.Id + "';";
                        DatabaseAssert.Equal(1, await command.ExecuteNonQueryAsync(token).ConfigureAwait(false), "The legacy row is cleared");
                    }
                }

                TokenUsageRecord legacyRead = await ReadAsync(legacy.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(TokenUsageRuleEnum.Legacy.ToString(), legacyRead.UsageRule.ToString(), "A row with no rule reads under the legacy rule");
                DatabaseAssert.True(legacyRead.UncachedInputTokens == null, "A legacy row has no uncached bucket");
                DatabaseAssert.True(legacyRead.CacheReadInputTokens == null, "A legacy row has no cache-read bucket");
                DatabaseAssert.True(legacyRead.CacheWriteInputTokens == null, "A legacy row has no cache-write bucket");
                DatabaseAssert.Equal(12L, legacyRead.InputTokens, "A legacy row keeps its stored input");
                DatabaseAssert.Equal(480000L, legacyRead.CachedTokens, "A legacy row keeps its stored cached count");
                DatabaseAssert.Equal(3012L, legacyRead.TotalTokens, "A legacy row keeps its stored total");
            }
            finally
            {
                if (!_NoCleanup)
                {
                    await _Driver.TokenUsage.DeleteByFilterAsync(new TokenUsageQuery { SourceId = bucketed.SourceId }, token).ConfigureAwait(false);
                    await _Driver.TokenUsage.DeleteByFilterAsync(new TokenUsageQuery { SourceId = legacy.SourceId }, token).ConfigureAwait(false);
                }
            }
        }

        internal async Task VerifyWideCountsAsync(CancellationToken token)
        {
            // Each count is above the 32-bit limit and distinct, so a truncated or swapped column cannot pass.
            const long Above32Bit = 2147483648L;
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            TokenUsageRecord wide = new TokenUsageRecord
            {
                Model = "wide-model-" + suffix,
                Runtime = "Codex",
                Source = "mission",
                SourceId = "msn_wide_" + suffix,
                UsageRule = TokenUsageRuleEnum.SeparateInputBuckets,
                UncachedInputTokens = Above32Bit + 1,
                CacheReadInputTokens = Above32Bit + 2,
                CacheWriteInputTokens = Above32Bit + 3,
                InputTokens = Above32Bit * 3 + 6,
                OutputTokens = Above32Bit + 5,
                CachedTokens = Above32Bit + 7,
                TotalTokens = Above32Bit * 4 + 11
            };
            await _Driver.TokenUsage.CreateAsync(wide, token).ConfigureAwait(false);

            try
            {
                TokenUsageRecord read = await ReadAsync(wide.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(Above32Bit + 1, read.UncachedInputTokens ?? -1, "uncached_input_tokens holds a 64-bit count");
                DatabaseAssert.Equal(Above32Bit + 2, read.CacheReadInputTokens ?? -1, "cache_read_input_tokens holds a 64-bit count");
                DatabaseAssert.Equal(Above32Bit + 3, read.CacheWriteInputTokens ?? -1, "cache_write_input_tokens holds a 64-bit count");
                DatabaseAssert.Equal(Above32Bit * 3 + 6, read.InputTokens, "input_tokens holds a 64-bit count");
                DatabaseAssert.Equal(Above32Bit + 5, read.OutputTokens, "output_tokens holds a 64-bit count");
                DatabaseAssert.Equal(Above32Bit + 7, read.CachedTokens, "cached_tokens holds a 64-bit count");
                DatabaseAssert.Equal(Above32Bit * 4 + 11, read.TotalTokens, "total_tokens holds a 64-bit count");
            }
            finally
            {
                if (!_NoCleanup)
                    await _Driver.TokenUsage.DeleteByFilterAsync(new TokenUsageQuery { SourceId = wide.SourceId }, token).ConfigureAwait(false);
            }
        }

        private async Task<TokenUsageRecord> ReadAsync(string id, CancellationToken token)
        {
            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                TokenUsageRecord? record = await reopened.TokenUsage.ReadAsync(id, null, token).ConfigureAwait(false);
                DatabaseAssert.True(record != null, "The token-usage record reads back after reopening the database");
                return record!;
            }
        }
    }
}
