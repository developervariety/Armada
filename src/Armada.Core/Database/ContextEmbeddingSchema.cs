namespace Armada.Core.Database
{
    /// <summary>
    /// Provider-specific DDL for derived context search vectors. The key is a SHA-256 of tenant, source
    /// and model, so it stays inside every provider's index-key limit. Vectors are stored as base64
    /// little-endian float32 text, which every provider stores the same way.
    /// </summary>
    internal static class ContextEmbeddingSchema
    {
        internal static readonly string[] SqliteStatements =
        {
            "CREATE TABLE IF NOT EXISTS context_embeddings (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL, source_key TEXT NOT NULL, model TEXT NOT NULL, content_sha256 TEXT NOT NULL, dimensions INTEGER NOT NULL, vector TEXT NOT NULL);",
            "CREATE INDEX IF NOT EXISTS idx_context_embeddings_source ON context_embeddings(tenant_id, source_key);"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            "CREATE TABLE IF NOT EXISTS context_embeddings (id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, source_key TEXT NOT NULL, model TEXT NOT NULL, content_sha256 TEXT NOT NULL, dimensions INTEGER NOT NULL, vector TEXT NOT NULL);",
            "CREATE INDEX IF NOT EXISTS idx_context_embeddings_source ON context_embeddings(tenant_id, source_key);"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"CREATE TABLE IF NOT EXISTS context_embeddings (
                id CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                tenant_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                source_key VARCHAR(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                model VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                content_sha256 CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                dimensions INT NOT NULL,
                vector LONGTEXT CHARACTER SET ascii NOT NULL,
                PRIMARY KEY (id)
            );",
            "CREATE INDEX idx_context_embeddings_source ON context_embeddings(tenant_id, source_key(191));"
        };

        internal static readonly string[] SqlServerStatements =
        {
            "IF OBJECT_ID('context_embeddings','U') IS NULL CREATE TABLE context_embeddings (id NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL PRIMARY KEY, tenant_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, source_key NVARCHAR(512) COLLATE Latin1_General_BIN2 NOT NULL, model NVARCHAR(256) COLLATE Latin1_General_BIN2 NOT NULL, content_sha256 NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL, dimensions INT NOT NULL, vector NVARCHAR(MAX) NOT NULL);",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_context_embeddings_source' AND object_id=OBJECT_ID('context_embeddings')) CREATE INDEX idx_context_embeddings_source ON context_embeddings(tenant_id, source_key);"
        };
    }
}
