namespace Armada.Core.Database.Mysql.Queries
{
    /// <summary>Read-only storage catalog queries for Mysql.</summary>
    public static class StorageQueries
    {
        /// <summary>Table statistics restricted to the configured database.</summary>
        public const string Tables = @"SELECT TABLE_SCHEMA AS schema_name, TABLE_NAME AS table_name, TABLE_ROWS AS row_count, DATA_LENGTH AS data_bytes, INDEX_LENGTH AS index_bytes FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME";
        /// <summary>Database allocation query.</summary>
        public const string Size = "SELECT SUM(DATA_LENGTH+INDEX_LENGTH) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE();";
    }
}
