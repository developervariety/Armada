namespace Armada.Core.Database.SqlServer.Queries
{
    /// <summary>Read-only storage catalog queries for SqlServer.</summary>
    public static class StorageQueries
    {
        /// <summary>Table statistics restricted to the configured database.</summary>
        public const string Tables = @"SELECT s.name AS schema_name, t.name AS table_name, SUM(CASE WHEN p.index_id IN (0,1) THEN p.rows ELSE 0 END) AS row_count, SUM(CASE WHEN p.index_id IN (0,1) THEN a.pages ELSE 0 END)*8192 AS data_bytes, SUM(CASE WHEN p.index_id>1 THEN a.pages ELSE 0 END)*8192 AS index_bytes FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.partitions p ON p.object_id=t.object_id JOIN (SELECT container_id, SUM(total_pages) AS pages FROM sys.allocation_units GROUP BY container_id) a ON a.container_id=p.hobt_id WHERE t.is_ms_shipped=0 GROUP BY s.name,t.name ORDER BY s.name,t.name";
        /// <summary>Database allocation query.</summary>
        public const string Size = "SELECT SUM(CONVERT(bigint,size))*8192 FROM sys.database_files;";
    }
}
