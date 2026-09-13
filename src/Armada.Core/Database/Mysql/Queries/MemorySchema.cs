namespace Armada.Core.Database.Mysql.Queries
{
    /// <summary>
    /// MySQL DDL for native captain memory. Kept outside the historical TableQueries file so that
    /// file stays byte-identical to its verified baseline.
    /// </summary>
    internal static class MemorySchema
    {
        /// <summary>
        /// Migration v78: memories and memory_tags. The tenant/key unique index fits the utf8mb4
        /// index budget without prefixes, so key uniqueness covers the full value.
        /// </summary>
        internal static readonly string[] MigrationV78Statements = new string[]
        {
            @"CREATE TABLE IF NOT EXISTS memories (
                id VARCHAR(255) NOT NULL PRIMARY KEY,
                tenant_id VARCHAR(255) NULL,
                user_id VARCHAR(255) NULL,
                scope VARCHAR(32) NOT NULL DEFAULT 'TenantWide',
                type VARCHAR(32) NOT NULL DEFAULT 'Semantic',
                topic VARCHAR(255) NULL,
                memory_key VARCHAR(255) NULL,
                summary LONGTEXT NULL,
                content LONGTEXT NOT NULL,
                salience DOUBLE NOT NULL DEFAULT 0.5,
                version INT NOT NULL DEFAULT 1,
                source_kind VARCHAR(32) NOT NULL DEFAULT 'Manual',
                source_voyage_id VARCHAR(255) NULL,
                source_mission_id VARCHAR(255) NULL,
                source_vessel_id VARCHAR(255) NULL,
                source_detail LONGTEXT NULL,
                vessel_id VARCHAR(255) NULL,
                created_utc DATETIME(6) NOT NULL,
                last_update_utc DATETIME(6) NOT NULL,
                UNIQUE KEY ux_memories_tenant_key (tenant_id, memory_key),
                KEY idx_memories_tenant_user (tenant_id, user_id),
                KEY idx_memories_tenant_created (tenant_id, created_utc),
                KEY idx_memories_vessel (vessel_id)
            );",
            @"CREATE TABLE IF NOT EXISTS memory_tags (
                memory_id VARCHAR(255) NOT NULL,
                tag VARCHAR(128) NOT NULL,
                PRIMARY KEY (memory_id, tag),
                KEY idx_memory_tags_tag (tag),
                FOREIGN KEY (memory_id) REFERENCES memories(id) ON DELETE CASCADE
            );"
        };
    }
}
