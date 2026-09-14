namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;

    /// <summary>Checks a pre-existing captain model endpoint link before its migration is accepted.</summary>
    internal static class CaptainModelEndpointSchemaGuard
    {
        internal static Task EnsureAsync(DbConnection connection, DbTransaction? transaction,
            DatabaseTypeEnum provider, bool requireForeignKey, CancellationToken token)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            return provider switch
            {
                DatabaseTypeEnum.Sqlite => EnsureSqliteAsync(connection, transaction, requireForeignKey, token),
                DatabaseTypeEnum.Postgresql => EnsureServerAsync(connection, transaction, DatabaseTypeEnum.Postgresql, requireForeignKey, token),
                DatabaseTypeEnum.Mysql => EnsureServerAsync(connection, transaction, DatabaseTypeEnum.Mysql, requireForeignKey, token),
                DatabaseTypeEnum.SqlServer => EnsureServerAsync(connection, transaction, DatabaseTypeEnum.SqlServer, requireForeignKey, token),
                _ => throw new NotSupportedException("Unsupported captain model endpoint schema provider")
            };
        }

        private static async Task EnsureSqliteAsync(DbConnection connection, DbTransaction? transaction,
            bool requireForeignKey, CancellationToken token)
        {
            List<Dictionary<string, string?>> columns = await QueryAsync(connection, transaction,
                "SELECT name FROM pragma_table_info('captains') WHERE name='model_endpoint_id';", token).ConfigureAwait(false);
            if (columns.Count == 0) return;

            List<Dictionary<string, string?>> allKeys = await QueryAsync(connection, transaction,
                "SELECT id, \"table\", \"from\", \"to\", \"on_delete\", \"on_update\", \"seq\" FROM pragma_foreign_key_list('captains');", token).ConfigureAwait(false);
            List<Dictionary<string, string?>> keys = allKeys.FindAll(row => String.Equals(row.GetValueOrDefault("from"), "model_endpoint_id", StringComparison.OrdinalIgnoreCase));
            if (keys.Count == 0)
            {
                if (requireForeignKey) throw Incompatible("SQLite", "captains.model_endpoint_id foreign key");
                return;
            }
            string keyId = keys[0].GetValueOrDefault("id") ?? String.Empty;
            if (keys.Count != 1 || allKeys.FindAll(row => String.Equals(row.GetValueOrDefault("id"), keyId, StringComparison.Ordinal)).Count != 1
                || !Matches(keys[0], "model_endpoints", "model_endpoint_id", "id", "RESTRICT", "NO ACTION", null))
                throw Incompatible("SQLite", "captains.model_endpoint_id foreign key");
        }

        private static async Task EnsureServerAsync(DbConnection connection, DbTransaction? transaction,
            DatabaseTypeEnum provider, bool requireForeignKey, CancellationToken token)
        {
            string query = provider switch
            {
                DatabaseTypeEnum.Postgresql => @"SELECT child_att.attname AS column_name, target.relname AS target_table,
                    target_att.attname AS target_column, target_ns.nspname AS target_schema,
                    CASE fk.confdeltype WHEN 'r' THEN 'RESTRICT' WHEN 'a' THEN 'NO ACTION' ELSE fk.confdeltype::text END AS delete_rule,
                    CASE fk.confupdtype WHEN 'r' THEN 'RESTRICT' WHEN 'a' THEN 'NO ACTION' ELSE fk.confupdtype::text END AS update_rule,
                    CASE WHEN fk.condeferrable THEN 'YES' ELSE 'NO' END AS is_deferrable,
                    CASE WHEN fk.condeferred THEN 'YES' ELSE 'NO' END AS initially_deferred,
                    CASE WHEN fk.convalidated THEN 'YES' ELSE 'NO' END AS validated,
                    array_length(fk.conkey, 1)::text AS key_count
                    FROM pg_constraint fk
                    JOIN pg_class child ON child.oid=fk.conrelid
                    JOIN pg_namespace child_ns ON child_ns.oid=child.relnamespace
                    JOIN pg_class target ON target.oid=fk.confrelid
                    JOIN pg_namespace target_ns ON target_ns.oid=target.relnamespace
                    JOIN pg_attribute child_att ON child_att.attrelid=child.oid AND child_att.attnum=ANY(fk.conkey)
                    JOIN pg_attribute target_att ON target_att.attrelid=target.oid AND target_att.attnum=ANY(fk.confkey)
                    WHERE fk.contype='f' AND child_ns.nspname=current_schema() AND child.relname='captains' AND child_att.attname='model_endpoint_id';",
                DatabaseTypeEnum.Mysql => @"SELECT kcu.column_name, kcu.referenced_table_name AS target_table,
                    kcu.referenced_column_name AS target_column, kcu.referenced_table_schema AS target_schema,
                    rc.delete_rule, rc.update_rule,
                    (SELECT COUNT(*) FROM information_schema.key_column_usage allk WHERE allk.constraint_schema=kcu.constraint_schema AND allk.constraint_name=kcu.constraint_name) AS key_count
                    FROM information_schema.key_column_usage kcu
                    JOIN information_schema.referential_constraints rc ON rc.constraint_schema=kcu.constraint_schema AND rc.constraint_name=kcu.constraint_name
                    WHERE kcu.constraint_schema=DATABASE() AND kcu.table_name='captains' AND kcu.column_name='model_endpoint_id';",
                DatabaseTypeEnum.SqlServer => @"SELECT c.name AS column_name, tt.name AS target_table, rc.name AS target_column,
                    SCHEMA_NAME(tt.schema_id) AS target_schema,
                    REPLACE(f.delete_referential_action_desc,'_',' ') AS delete_rule,
                    REPLACE(f.update_referential_action_desc,'_',' ') AS update_rule,
                    CASE WHEN f.is_disabled=0 AND f.is_not_trusted=0 AND f.is_not_for_replication=0
                        AND (SELECT COUNT(*) FROM sys.foreign_key_columns members WHERE members.constraint_object_id=f.object_id)=1 THEN '1' ELSE '0' END AS enforced
                    FROM sys.foreign_keys f
                    JOIN sys.foreign_key_columns k ON k.constraint_object_id=f.object_id
                    JOIN sys.tables st ON st.object_id=k.parent_object_id
                    JOIN sys.columns c ON c.object_id=st.object_id AND c.column_id=k.parent_column_id
                    JOIN sys.tables tt ON tt.object_id=k.referenced_object_id
                    JOIN sys.columns rc ON rc.object_id=tt.object_id AND rc.column_id=k.referenced_column_id
                    WHERE st.schema_id=SCHEMA_ID() AND st.name='captains' AND c.name='model_endpoint_id';",
                _ => throw new NotSupportedException()
            };
            List<Dictionary<string, string?>> keys = await QueryAsync(connection, transaction, query, token).ConfigureAwait(false);
            if (keys.Count == 0)
            {
                if (requireForeignKey) throw Incompatible(provider.ToString(), "captains.model_endpoint_id foreign key");
                return;
            }
            string expectedTargetSchema = await CurrentSchemaAsync(connection, transaction, provider, token).ConfigureAwait(false);
            if (keys.Count != 1 || !Matches(keys[0], "model_endpoints", "model_endpoint_id", "id",
                provider == DatabaseTypeEnum.SqlServer ? "NO ACTION" : "RESTRICT",
                provider == DatabaseTypeEnum.Mysql ? "RESTRICT" : "NO ACTION",
                provider == DatabaseTypeEnum.SqlServer ? "1" : null, expectedTargetSchema))
                throw Incompatible(provider.ToString(), "captains.model_endpoint_id foreign key");
        }

        private static bool Matches(Dictionary<string, string?> row, string targetTable, string sourceColumn,
            string targetColumn, string deleteRule, string updateRule, string? enforced, string? targetSchema = null)
        {
            // Identifier equality is provider metadata equality. Case-folding here can accept a
            // quoted PostgreSQL object or a case-sensitive MySQL database with a different target.
            // Rule values are normalized by each provider query, so those are exact too.
            return String.Equals(row.GetValueOrDefault("table") ?? row.GetValueOrDefault("target_table"), targetTable, StringComparison.Ordinal)
                && String.Equals(row.GetValueOrDefault("from") ?? row.GetValueOrDefault("column_name"), sourceColumn, StringComparison.Ordinal)
                && String.Equals(row.GetValueOrDefault("to") ?? row.GetValueOrDefault("target_column"), targetColumn, StringComparison.Ordinal)
                && String.Equals(row.GetValueOrDefault("on_delete") ?? row.GetValueOrDefault("delete_rule"), deleteRule, StringComparison.Ordinal)
                && String.Equals(row.GetValueOrDefault("on_update") ?? row.GetValueOrDefault("update_rule"), updateRule, StringComparison.Ordinal)
                && (targetSchema == null || targetSchema == String.Empty || String.Equals(row.GetValueOrDefault("target_schema"), targetSchema, StringComparison.Ordinal))
                && (!row.ContainsKey("is_deferrable") || String.Equals(row.GetValueOrDefault("is_deferrable"), "NO", StringComparison.Ordinal))
                && (!row.ContainsKey("initially_deferred") || String.Equals(row.GetValueOrDefault("initially_deferred"), "NO", StringComparison.Ordinal))
                && (!row.ContainsKey("validated") || String.Equals(row.GetValueOrDefault("validated"), "YES", StringComparison.Ordinal))
                && (!row.ContainsKey("key_count") || String.Equals(row.GetValueOrDefault("key_count"), "1", StringComparison.Ordinal))
                && (enforced == null || String.Equals(row.GetValueOrDefault("enforced"), enforced, StringComparison.Ordinal));
        }

        private static async Task<string> CurrentSchemaAsync(DbConnection connection, DbTransaction? transaction,
            DatabaseTypeEnum provider, CancellationToken token)
        {
            List<Dictionary<string, string?>> rows = await QueryAsync(connection, transaction,
                provider == DatabaseTypeEnum.Postgresql ? "SELECT current_schema() AS schema_name;"
                    : provider == DatabaseTypeEnum.Mysql ? "SELECT DATABASE() AS schema_name;"
                    : "SELECT SCHEMA_NAME() AS schema_name;", token).ConfigureAwait(false);
            return rows.Count == 1 ? rows[0].GetValueOrDefault("schema_name") ?? String.Empty : String.Empty;
        }

        private static async Task<List<Dictionary<string, string?>>> QueryAsync(DbConnection connection,
            DbTransaction? transaction, string sql, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                List<Dictionary<string, string?>> rows = new List<Dictionary<string, string?>>();
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        Dictionary<string, string?> row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                        for (int index = 0; index < reader.FieldCount; index++)
                            row[reader.GetName(index)] = reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
                        rows.Add(row);
                    }
                }
                return rows;
            }
        }

        private static InvalidOperationException Incompatible(string provider, string detail) =>
            new InvalidOperationException("Incompatible " + provider + " captain model endpoint link schema: " + detail);
    }
}
