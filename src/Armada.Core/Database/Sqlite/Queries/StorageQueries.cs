namespace Armada.Core.Database.Sqlite.Queries
{
    /// <summary>Read-only storage catalog queries for Sqlite.</summary>
    public static class StorageQueries
    {
        /// <summary>Table statistics restricted to the configured database.</summary>
        public const string Tables = @"SELECT 'main' AS schema_name, name AS table_name, NULL AS row_count, NULL AS data_bytes, NULL AS index_bytes FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        /// <summary>Database allocation query.</summary>
        public const string Size = "PRAGMA page_count;";
    }
}
