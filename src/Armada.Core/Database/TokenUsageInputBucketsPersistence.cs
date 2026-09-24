namespace Armada.Core.Database
{
    /// <summary>
    /// Provider-specific schema changes that store a token-usage record's input as three buckets (uncached, cache-read
    /// and cache-write input) and the counting rule that produced its counts, and make every token count on the table
    /// a 64-bit integer. Every new column is nullable and nothing is backfilled: a row written before the columns
    /// existed keeps its stored counts, has no buckets and a null rule, and reads under the legacy rule. SQLite needs
    /// no widening: an INTEGER column already stores 64-bit values.
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
            @"ALTER TABLE token_usage ADD COLUMN IF NOT EXISTS usage_rule VARCHAR(64) NULL;",
            @"ALTER TABLE token_usage ALTER COLUMN input_tokens TYPE BIGINT, ALTER COLUMN output_tokens TYPE BIGINT, " +
                "ALTER COLUMN cached_tokens TYPE BIGINT, ALTER COLUMN total_tokens TYPE BIGINT;"
        };

        /// <summary>MySQL migration statements.</summary>
        internal static readonly string[] MysqlStatements =
        {
            @"ALTER TABLE token_usage ADD COLUMN uncached_input_tokens BIGINT NULL;",
            @"ALTER TABLE token_usage ADD COLUMN cache_read_input_tokens BIGINT NULL;",
            @"ALTER TABLE token_usage ADD COLUMN cache_write_input_tokens BIGINT NULL;",
            @"ALTER TABLE token_usage ADD COLUMN usage_rule VARCHAR(64) NULL;",
            @"ALTER TABLE token_usage MODIFY COLUMN input_tokens BIGINT NOT NULL DEFAULT 0, MODIFY COLUMN output_tokens BIGINT NOT NULL DEFAULT 0, " +
                "MODIFY COLUMN cached_tokens BIGINT NOT NULL DEFAULT 0, MODIFY COLUMN total_tokens BIGINT NOT NULL DEFAULT 0;"
        };

        /// <summary>SQL Server migration statements.</summary>
        internal static readonly string[] SqlServerStatements =
        {
            @"IF COL_LENGTH('token_usage', 'uncached_input_tokens') IS NULL ALTER TABLE token_usage ADD uncached_input_tokens BIGINT NULL;",
            @"IF COL_LENGTH('token_usage', 'cache_read_input_tokens') IS NULL ALTER TABLE token_usage ADD cache_read_input_tokens BIGINT NULL;",
            @"IF COL_LENGTH('token_usage', 'cache_write_input_tokens') IS NULL ALTER TABLE token_usage ADD cache_write_input_tokens BIGINT NULL;",
            @"IF COL_LENGTH('token_usage', 'usage_rule') IS NULL ALTER TABLE token_usage ADD usage_rule NVARCHAR(64) NULL;",
            WidenSqlServerCount("input_tokens"),
            WidenSqlServerCount("output_tokens"),
            WidenSqlServerCount("cached_tokens"),
            WidenSqlServerCount("total_tokens")
        };

        /// <summary>
        /// SQL Server refuses to change the type of a column that a default constraint depends on, so the default is
        /// dropped, the column widened to BIGINT, and the zero default restored under a fixed name.
        /// </summary>
        private static string WidenSqlServerCount(string column)
        {
            return "DECLARE @constraint_name NVARCHAR(128); " +
                "SELECT @constraint_name = dc.name FROM sys.default_constraints dc " +
                "JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id " +
                "WHERE dc.parent_object_id = OBJECT_ID('token_usage') AND c.name = '" + column + "'; " +
                "IF @constraint_name IS NOT NULL EXEC('ALTER TABLE token_usage DROP CONSTRAINT [' + @constraint_name + ']'); " +
                "ALTER TABLE token_usage ALTER COLUMN " + column + " BIGINT NOT NULL; " +
                "ALTER TABLE token_usage ADD CONSTRAINT DF_token_usage_" + column + " DEFAULT 0 FOR " + column + ";";
        }
    }
}
