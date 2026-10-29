namespace Armada.Core.Database
{
    /// <summary>
    /// Adds the delivery proof on a merge entry: the serialized evidence that the entry's mission work is already on
    /// the target branch. The column is nullable. A null value reads as no proof.
    /// </summary>
    internal static class MergeDeliveryPersistence
    {
        internal static readonly string[] SqliteStatements =
        {
            @"ALTER TABLE merge_entries ADD COLUMN delivery_evidence TEXT NULL;"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            @"ALTER TABLE merge_entries ADD COLUMN IF NOT EXISTS delivery_evidence TEXT NULL;"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"ALTER TABLE merge_entries ADD COLUMN delivery_evidence LONGTEXT NULL;"
        };

        internal static readonly string[] SqlServerStatements =
        {
            @"IF COL_LENGTH('merge_entries', 'delivery_evidence') IS NULL ALTER TABLE merge_entries ADD delivery_evidence NVARCHAR(MAX) NULL;"
        };
    }
}
