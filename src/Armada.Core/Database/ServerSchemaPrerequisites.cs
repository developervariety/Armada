namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;

    /// <summary>
    /// Restores omitted prerequisites before the first historical consumer.
    /// This uses its own repair ledger, never historical migration version rows.
    /// </summary>
    internal static class ServerSchemaPrerequisites
    {
        private const string _RepairId = "fork-operational-prerequisites-v1";

        /// <summary>
        /// Creates the PostgreSQL repair ledger.
        /// </summary>
        internal const string PostgresqlSchemaRepairsTable = "CREATE TABLE IF NOT EXISTS schema_repairs (id TEXT PRIMARY KEY, checksum TEXT NOT NULL, applied_utc TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP);";

        /// <summary>
        /// Creates the SQL Server repair ledger.
        /// </summary>
        internal const string SqlServerSchemaRepairsTable = "IF OBJECT_ID(N'dbo.schema_repairs', N'U') IS NULL CREATE TABLE dbo.schema_repairs (id NVARCHAR(128) NOT NULL PRIMARY KEY, checksum VARCHAR(64) NOT NULL, applied_utc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());";

        internal static async Task EnsureAsync(DbConnection connection, DatabaseTypeEnum provider, CancellationToken token)
        {
            string[] statements = provider switch
            {
                DatabaseTypeEnum.Postgresql => Postgresql.Queries.ForkOperationalSchema.Statements,
                DatabaseTypeEnum.SqlServer => SqlServer.Queries.ForkOperationalSchema.Statements,
                _ => throw new NotSupportedException("Unsupported prerequisite provider")
            };
            using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
            {
                await ExecuteAsync(connection, transaction, provider == DatabaseTypeEnum.Postgresql
                    ? PostgresqlSchemaRepairsTable
                    : SqlServerSchemaRepairsTable, token).ConfigureAwait(false);
                string checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(String.Join("\n", statements))));
                List<Dictionary<string, string?>> ledger = await QueryAsync(connection, transaction,
                    "SELECT checksum FROM schema_repairs WHERE id = @name;", _RepairId, null, token).ConfigureAwait(false);
                if (ledger.Count > 0 && ledger[0]["checksum"] != checksum)
                    throw new InvalidOperationException("Schema repair definition changed: " + _RepairId);

                string? legacyChecksum = null;
                List<Dictionary<string, string?>> legacyLedger = new List<Dictionary<string, string?>>();
                if (provider == DatabaseTypeEnum.Postgresql)
                {
                    legacyChecksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PostgresqlLegacyOperationalRepair.Sql)));
                    legacyLedger = await QueryAsync(connection, transaction,
                        "SELECT checksum FROM schema_repairs WHERE id = @name;", PostgresqlLegacyOperationalRepair.RepairId,
                        null, token).ConfigureAwait(false);
                    if (legacyLedger.Count > 0 && legacyLedger[0]["checksum"] != legacyChecksum)
                        throw new InvalidOperationException("Schema repair definition changed: " + PostgresqlLegacyOperationalRepair.RepairId);
                    await PostgresqlLegacyOperationalRepair.ApplyAsync(connection, transaction, token).ConfigureAwait(false);
                }

                foreach (string statement in statements)
                {
                    Match table = Regex.Match(statement, @"^CREATE TABLE (\w+) \(");
                    Match column = Regex.Match(statement, @"^ALTER TABLE (\w+) ADD (?:COLUMN )?(\w+) (.+);$", RegexOptions.Singleline);
                    Match index = Regex.Match(statement, @"^CREATE INDEX (\w+)\s+ON (\w+)\(([^)]+)\);$", RegexOptions.Singleline);
                    if (table.Success)
                        await EnsureTableAsync(connection, transaction, provider, table.Groups[1].Value, statement, token).ConfigureAwait(false);
                    else if (column.Success)
                        await EnsureColumnAsync(connection, transaction, provider, column.Groups[1].Value,
                            column.Groups[2].Value, column.Groups[3].Value, token).ConfigureAwait(false);
                    else if (index.Success)
                        await EnsureIndexAsync(connection, transaction, provider, index.Groups[2].Value,
                            index.Groups[1].Value, index.Groups[3].Value, statement, token).ConfigureAwait(false);
                    else
                        throw new InvalidOperationException("Unrecognized prerequisite declaration");
                }
                if (ledger.Count == 0)
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "INSERT INTO schema_repairs (id, checksum) VALUES (@id, @checksum);";
                        AddParameter(command, "@id", _RepairId);
                        AddParameter(command, "@checksum", checksum);
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
                if (legacyChecksum != null && legacyLedger.Count == 0)
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "INSERT INTO schema_repairs (id, checksum) VALUES (@id, @checksum);";
                        AddParameter(command, "@id", PostgresqlLegacyOperationalRepair.RepairId);
                        AddParameter(command, "@checksum", legacyChecksum);
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
                await transaction.CommitAsync(token).ConfigureAwait(false);
            }
        }

        private static async Task EnsureTableAsync(DbConnection connection, DbTransaction transaction,
            DatabaseTypeEnum provider, string table, string ddl, CancellationToken token)
        {
            List<Dictionary<string, string?>> columns = await ColumnsAsync(connection, transaction, provider, table, token).ConfigureAwait(false);
            if (columns.Count == 0)
                await ExecuteAsync(connection, transaction, ddl, token).ConfigureAwait(false);

            foreach (string line in ddl.Split('\n'))
            {
                Match column = Regex.Match(line.Trim().TrimEnd(','), @"^(\w+) ((?:TEXT|INTEGER|INT|BIGINT|BOOLEAN|BIT|FLOAT|DOUBLE PRECISION|TIMESTAMP(?:TZ)?|NVARCHAR\((?:MAX|\d+)\))(?=\s|$).*)$", RegexOptions.IgnoreCase);
                if (!column.Success) continue;
                await EnsureColumnAsync(connection, transaction, provider, table, column.Groups[1].Value,
                    column.Groups[2].Value, token).ConfigureAwait(false);
            }

            List<Dictionary<string, string?>> primary = await QueryAsync(connection, transaction,
                "SELECT k.column_name FROM information_schema.table_constraints t JOIN information_schema.key_column_usage k ON t.constraint_name = k.constraint_name AND t.constraint_schema = k.constraint_schema AND t.table_name = k.table_name WHERE t.table_name = @name AND t.table_schema = " + Schema(provider) + " AND t.constraint_type = 'PRIMARY KEY' ORDER BY k.ordinal_position;",
                table, null, token).ConfigureAwait(false);
            string expectedPrimary = table == "request_history_detail" ? "request_history_id" : "id";
            if (primary.Count != 1 || primary[0]["column_name"] != expectedPrimary)
                throw Incompatible(table, "primary key");

            string primaryQuery = provider == DatabaseTypeEnum.Postgresql
                ? "SELECT CASE WHEN i.indisvalid AND i.indisready AND i.indisunique AND i.indpred IS NULL THEN '1' ELSE '0' END AS enforced FROM pg_index i JOIN pg_class t ON t.oid=i.indrelid JOIN pg_namespace n ON n.oid=t.relnamespace WHERE n.nspname=current_schema() AND t.relname=@name AND i.indisprimary;"
                : "SELECT CASE WHEN i.is_disabled=0 AND i.is_hypothetical=0 AND i.is_unique=1 AND i.has_filter=0 THEN '1' ELSE '0' END AS enforced FROM sys.indexes i JOIN sys.tables t ON t.object_id=i.object_id WHERE t.schema_id=SCHEMA_ID() AND t.name=@name AND i.is_primary_key=1;";
            List<Dictionary<string, string?>> primaryState = await QueryAsync(connection, transaction, primaryQuery, table, null, token).ConfigureAwait(false);
            if (primaryState.Count != 1 || primaryState[0]["enforced"] != "1")
                throw Incompatible(table, "primary key enforcement");

            foreach (Match foreignKey in Regex.Matches(ddl,
                @"(?:CONSTRAINT (\w+) )?FOREIGN KEY \((\w+)\) REFERENCES (\w+)\((\w+)\)(?: ON DELETE (CASCADE|SET NULL|NO ACTION))?"))
            {
                string source = foreignKey.Groups[2].Value;
                string targetTable = foreignKey.Groups[3].Value;
                string targetColumn = foreignKey.Groups[4].Value;
                string deleteAction = foreignKey.Groups[5].Success ? foreignKey.Groups[5].Value : "NO ACTION";
                string query = provider == DatabaseTypeEnum.Postgresql
                    ? @"SELECT k.column_name, c.table_name AS target_table, c.column_name AS target_column, r.delete_rule, r.update_rule, c.table_schema AS target_schema, CASE WHEN p.convalidated AND NOT p.condeferrable AND NOT EXISTS (SELECT 1 FROM pg_trigger trigger_state WHERE trigger_state.tgconstraint=p.oid AND trigger_state.tgenabled<>'O') THEN '1' ELSE '0' END AS enforced FROM information_schema.key_column_usage k JOIN information_schema.referential_constraints r ON r.constraint_name=k.constraint_name AND r.constraint_schema=k.constraint_schema JOIN information_schema.constraint_column_usage c ON c.constraint_name=r.unique_constraint_name AND c.constraint_schema=r.unique_constraint_schema JOIN pg_namespace n ON n.nspname=k.constraint_schema JOIN pg_class t ON t.relnamespace=n.oid AND t.relname=k.table_name JOIN pg_constraint p ON p.connamespace=n.oid AND p.conrelid=t.oid AND p.conname=k.constraint_name WHERE k.table_schema=current_schema() AND k.table_name=@name AND k.column_name=@column;"
                    : @"SELECT sc.name AS column_name, tt.name AS target_table, tc.name AS target_column, REPLACE(f.delete_referential_action_desc,'_',' ') AS delete_rule, REPLACE(f.update_referential_action_desc,'_',' ') AS update_rule, SCHEMA_NAME(tt.schema_id) AS target_schema, CASE WHEN f.is_disabled=0 AND f.is_not_trusted=0 AND f.is_not_for_replication=0 AND (SELECT COUNT(*) FROM sys.foreign_key_columns members WHERE members.constraint_object_id=f.object_id)=1 THEN '1' ELSE '0' END AS enforced FROM sys.foreign_keys f JOIN sys.foreign_key_columns k ON k.constraint_object_id=f.object_id JOIN sys.tables st ON st.object_id=k.parent_object_id JOIN sys.columns sc ON sc.object_id=st.object_id AND sc.column_id=k.parent_column_id JOIN sys.tables tt ON tt.object_id=k.referenced_object_id JOIN sys.columns tc ON tc.object_id=tt.object_id AND tc.column_id=k.referenced_column_id WHERE st.schema_id=SCHEMA_ID() AND st.name=@name AND sc.name=@column;";
                List<Dictionary<string, string?>> found = await QueryAsync(connection, transaction, query, table, source, token).ConfigureAwait(false);
                if (found.Count == 0)
                {
                    await ExecuteAsync(connection, transaction, "ALTER TABLE " + table + " ADD CONSTRAINT fk_repair_" + table + "_" + source
                        + " FOREIGN KEY (" + source + ") REFERENCES " + targetTable + "(" + targetColumn + ") ON DELETE " + deleteAction + ";", token).ConfigureAwait(false);
                }
                else if (found.Count != 1 || found[0]["target_table"] != targetTable || found[0]["target_column"] != targetColumn || found[0]["delete_rule"] != deleteAction || found[0]["update_rule"] != "NO ACTION" || found[0]["enforced"] != "1"
                    || found[0]["target_schema"] != (await QueryAsync(connection, transaction, "SELECT " + Schema(provider) + " AS name;", table, null, token).ConfigureAwait(false))[0]["name"])
                    throw Incompatible(table, "foreign key " + source);
            }
        }

        internal static async Task EnsureColumnAsync(DbConnection connection, DbTransaction transaction,
            DatabaseTypeEnum provider, string table, string name, string declaration, CancellationToken token)
        {
            List<Dictionary<string, string?>> columns = await ColumnsAsync(connection, transaction, provider, table, token).ConfigureAwait(false);
            Dictionary<string, string?>? column = columns.Find(item => item["column_name"] == name);
            if (column == null)
            {
                await ExecuteAsync(connection, transaction, "ALTER TABLE " + table + " ADD " + name + " " + declaration + ";", token).ConfigureAwait(false);
                columns = await ColumnsAsync(connection, transaction, provider, table, token).ConfigureAwait(false);
                column = columns.Find(item => item["column_name"] == name) ?? throw Incompatible(table, name);
            }
            string type = Regex.Match(declaration, @"^(DOUBLE PRECISION|\w+(?:\((?:MAX|\d+)\))?)", RegexOptions.IgnoreCase).Value.ToLowerInvariant();
            string? length = null;
            if (type.StartsWith("nvarchar("))
            {
                length = type.Contains("max") ? "-1" : Regex.Match(type, @"\d+").Value;
                type = "nvarchar";
            }
            type = type switch { "int" => "int", "timestamp" => "timestamp without time zone", "timestamptz" => "timestamp with time zone", _ => type };
            bool nullable = !declaration.Contains("NOT NULL", StringComparison.OrdinalIgnoreCase) && !declaration.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase);
            string? expectedDefault = Regex.Match(declaration, @"\bDEFAULT (.+?)(?:,)?$", RegexOptions.IgnoreCase).Groups[1].Value;
            if (provider == DatabaseTypeEnum.Postgresql && type == "timestamp with time zone" && column["data_type"] == "timestamp without time zone")
            {
                await ExecuteAsync(connection, transaction, "ALTER TABLE " + table + " ALTER COLUMN " + name + " TYPE TIMESTAMPTZ USING " + name + " AT TIME ZONE 'UTC';", token).ConfigureAwait(false);
                column = (await ColumnsAsync(connection, transaction, provider, table, token).ConfigureAwait(false)).Find(item => item["column_name"] == name)!;
            }
            if (column["is_generated"] != "0" || column["data_type"] != type || length != null && column["character_maximum_length"] != length
                || (column["is_nullable"] == "YES") != nullable
                || NormalizeDefault(column["column_default"]) != NormalizeDefault(expectedDefault))
                throw Incompatible(table, name + " type/nullability/default (found " + column["data_type"] + ", " + column["is_nullable"] + ", " + column["column_default"] + ")");
        }

        private static async Task EnsureIndexAsync(DbConnection connection, DbTransaction transaction,
            DatabaseTypeEnum provider, string table, string name, string fields, string ddl, CancellationToken token)
        {
            string query = provider == DatabaseTypeEnum.Postgresql
                ? @"SELECT a.attname AS column_name, CASE WHEN (x.indoption[k.ordinality-1] & 1)=1 THEN 'DESC' ELSE 'ASC' END AS direction, CASE WHEN x.indisunique THEN '1' ELSE '0' END AS is_unique, CASE WHEN x.indisvalid AND x.indisready AND x.indpred IS NULL THEN '1' ELSE '0' END AS usable FROM pg_index x JOIN pg_class t ON t.oid=x.indrelid JOIN pg_class i ON i.oid=x.indexrelid JOIN pg_namespace n ON n.oid=t.relnamespace CROSS JOIN LATERAL unnest(x.indkey) WITH ORDINALITY k(attnum,ordinality) JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum=k.attnum WHERE n.nspname=current_schema() AND t.relname=@name AND i.relname=@column ORDER BY k.ordinality;"
                : @"SELECT c.name AS column_name, CASE WHEN k.is_descending_key=1 THEN 'DESC' ELSE 'ASC' END AS direction, CAST(i.is_unique AS VARCHAR(1)) AS is_unique, CASE WHEN i.is_disabled=0 AND i.has_filter=0 AND i.is_hypothetical=0 THEN '1' ELSE '0' END AS usable FROM sys.indexes i JOIN sys.tables t ON t.object_id=i.object_id JOIN sys.index_columns k ON k.object_id=t.object_id AND k.index_id=i.index_id JOIN sys.columns c ON c.object_id=t.object_id AND c.column_id=k.column_id WHERE t.schema_id=SCHEMA_ID() AND t.name=@name AND i.name=@column ORDER BY k.key_ordinal;";
            List<Dictionary<string, string?>> actual = await QueryAsync(connection, transaction, query, table, name, token).ConfigureAwait(false);
            if (actual.Count == 0)
            {
                await ExecuteAsync(connection, transaction, ddl, token).ConfigureAwait(false);
                return;
            }
            string[] expected = fields.Split(',').Select(field => Regex.Replace(field.Trim(), @"\s+", " ").ToUpperInvariant()).ToArray();
            string[] found = actual.Select(row => (row["column_name"] + (row["direction"] == "DESC" ? " DESC" : "")).ToUpperInvariant()).ToArray();
            if (!expected.SequenceEqual(found) || actual.Any(row => row["is_unique"] != "0" || row["usable"] != "1"))
                throw Incompatible(table, "index " + name);
        }

        private static Task<List<Dictionary<string, string?>>> ColumnsAsync(DbConnection connection,
            DbTransaction transaction, DatabaseTypeEnum provider, string table, CancellationToken token)
        {
            string generated = provider == DatabaseTypeEnum.Postgresql
                ? "CASE WHEN is_generated='NEVER' AND is_identity='NO' THEN '0' ELSE '1' END"
                : "CASE WHEN COLUMNPROPERTY(OBJECT_ID(QUOTENAME(table_schema)+'.'+QUOTENAME(table_name)),column_name,'IsComputed')=0 THEN '0' ELSE '1' END";
            return QueryAsync(connection, transaction, "SELECT column_name, data_type, character_maximum_length, is_nullable, column_default, " + generated + " AS is_generated FROM information_schema.columns WHERE table_schema = " + Schema(provider) + " AND table_name = @name;", table, null, token);
        }

        private static string Schema(DatabaseTypeEnum provider) => provider == DatabaseTypeEnum.Postgresql ? "current_schema()" : "SCHEMA_NAME()";

        private static string NormalizeDefault(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return String.Empty;
            string normalized = Regex.Replace(value.Trim(), @"::[a-z ]+$", "", RegexOptions.IgnoreCase).Trim('(', ')');
            if (normalized.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return String.Empty;
            if (normalized.StartsWith("N'", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(1);
            if (normalized.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || normalized.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
                return normalized.ToLowerInvariant();
            return normalized;
        }

        private static InvalidOperationException Incompatible(string table, string detail) => new InvalidOperationException("Incompatible schema prerequisite " + table + "." + detail + "; existing data and migration history were not replaced");

        private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        private static void AddParameter(DbCommand command, string name, object value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        private static async Task<List<Dictionary<string, string?>>> QueryAsync(DbConnection connection,
            DbTransaction transaction, string sql, string name, string? column, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                AddParameter(command, "@name", name);
                if (column != null) AddParameter(command, "@column", column);
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
    }
}
