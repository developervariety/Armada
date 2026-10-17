namespace Armada.Core.Database.Postgresql.Queries
{
    /// <summary>Read-only storage catalog queries for Postgresql.</summary>
    public static class StorageQueries
    {
        /// <summary>Table statistics restricted to the configured database.</summary>
        public const string Tables = @"SELECT schemaname AS schema_name, relname AS table_name, n_live_tup AS row_count, pg_table_size(relid) AS data_bytes, pg_indexes_size(relid) AS index_bytes FROM pg_stat_user_tables ORDER BY schemaname, relname";
        /// <summary>Database allocation query.</summary>
        public const string Size = "SELECT pg_database_size(current_database());";
    }
}
