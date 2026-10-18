namespace Armada.Core.Database
{
    /// <summary>
    /// Provider-specific schema changes for memory proposal promotion: who promoted the proposal, the
    /// AI-Memory commit that holds the promoted rule, and when the promotion was recorded.
    /// </summary>
    internal static class MemoryProposalPromotionPersistence
    {
        internal static readonly string[] SqliteStatements =
        {
            @"ALTER TABLE memory_proposals ADD COLUMN promoted_by TEXT NULL;",
            @"ALTER TABLE memory_proposals ADD COLUMN promoted_commit_sha TEXT NULL;",
            @"ALTER TABLE memory_proposals ADD COLUMN promoted_utc TEXT NULL;"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            @"ALTER TABLE memory_proposals ADD COLUMN IF NOT EXISTS promoted_by TEXT NULL;",
            @"ALTER TABLE memory_proposals ADD COLUMN IF NOT EXISTS promoted_commit_sha TEXT NULL;",
            @"ALTER TABLE memory_proposals ADD COLUMN IF NOT EXISTS promoted_utc TIMESTAMPTZ NULL;"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"ALTER TABLE memory_proposals ADD COLUMN promoted_by VARCHAR(2000) CHARACTER SET utf8mb4 NULL;",
            @"ALTER TABLE memory_proposals ADD COLUMN promoted_commit_sha VARCHAR(40) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL;",
            @"ALTER TABLE memory_proposals ADD COLUMN promoted_utc DATETIME(6) NULL;"
        };

        internal static readonly string[] SqlServerStatements =
        {
            @"IF COL_LENGTH('memory_proposals', 'promoted_by') IS NULL ALTER TABLE memory_proposals ADD promoted_by NVARCHAR(2000) NULL;",
            @"IF COL_LENGTH('memory_proposals', 'promoted_commit_sha') IS NULL ALTER TABLE memory_proposals ADD promoted_commit_sha NVARCHAR(40) NULL;",
            @"IF COL_LENGTH('memory_proposals', 'promoted_utc') IS NULL ALTER TABLE memory_proposals ADD promoted_utc DATETIME2 NULL;"
        };
    }
}
