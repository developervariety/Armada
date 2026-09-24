namespace Armada.Core.Database
{
    /// <summary>
    /// Provider-specific schema changes that store a token-usage record's input as three buckets (uncached, cache-read
    /// and cache-write input) and the counting rule that produced its counts. Every column is nullable and nothing is
    /// backfilled: a row written before the columns existed keeps its stored counts, has no buckets and a null rule,
    /// and reads under the legacy rule.
    /// </summary>
    internal static class TokenUsageInputBucketsPersistence
    {
        /// <summary>SQLite migration statements.</summary>
        internal static readonly string[] SqliteStatements =
        {
            @"ALTER TABLE token_usage ADD COLUMN uncached_input_tokens INTEGER NULL;",
            @"ALTER TABLE token_usage ADD COLUMN cache_read_input_tokens INTEGER NULL;",
            @"ALTER TABLE token_usage ADD COLUMN cache_write_input_tokens INTEGER NULL;",
            @"ALTER TABLE token_usage ADD COLUMN usage_rule TEXT NULL;"
        };

        /// <summary>PostgreSQL migration statements.</summary>
        internal static readonly string[] PostgresqlStatements =
        {
            @"ALTER TABLE token_usage ADD COLUMN IF NOT EXISTS uncached_input_tokens BIGINT NULL;",
            @"ALTER TABLE token_usage ADD COLUMN IF NOT EXISTS cache_read_input_tokens BIGINT NULL;",
            @"ALTER TABLE token_usage ADD COLUMN IF NOT EXISTS cache_write_input_tokens BIGINT NULL;",
            @"ALTER TABLE token_usage ADD COLUMN IF NOT EXISTS usage_rule VARCHAR(64) NULL;"
        };

        /// <summary>MySQL migration statements.</summary>
        internal static readonly string[] MysqlStatements =
        {
            @"ALTER TABLE token_usage ADD COLUMN uncached_input_tokens BIGINT NULL;",
            @"ALTER TABLE token_usage ADD COLUMN cache_read_input_tokens BIGINT NULL;",
            @"ALTER TABLE token_usage ADD COLUMN cache_write_input_tokens BIGINT NULL;",
            @"ALTER TABLE token_usage ADD COLUMN usage_rule VARCHAR(64) NULL;"
        };

        /// <summary>SQL Server migration statements.</summary>
        internal static readonly string[] SqlServerStatements =
        {
            @"IF COL_LENGTH('token_usage', 'uncached_input_tokens') IS NULL ALTER TABLE token_usage ADD uncached_input_tokens BIGINT NULL;",
            @"IF COL_LENGTH('token_usage', 'cache_read_input_tokens') IS NULL ALTER TABLE token_usage ADD cache_read_input_tokens BIGINT NULL;",
            @"IF COL_LENGTH('token_usage', 'cache_write_input_tokens') IS NULL ALTER TABLE token_usage ADD cache_write_input_tokens BIGINT NULL;",
            @"IF COL_LENGTH('token_usage', 'usage_rule') IS NULL ALTER TABLE token_usage ADD usage_rule NVARCHAR(64) NULL;"
        };
    }
}
