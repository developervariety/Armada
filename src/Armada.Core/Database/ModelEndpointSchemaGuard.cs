namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;

    /// <summary>
    /// Checks the model endpoint table before its first migration can be marked applied.
    /// </summary>
    internal static class ModelEndpointSchemaGuard
    {
        private static readonly string[] _ColumnNames = new[]
        {
            "id", "tenant_id", "user_id", "name", "kind", "provider", "base_url", "api_key", "model",
            "dimensionality", "timeout_ms", "enabled", "health_status", "last_health_check_utc", "last_health_error",
            "last_latency_ms", "health_history_json", "scope", "created_utc", "last_update_utc"
        };

        internal static Task EnsureAsync(DbConnection connection, DbTransaction? transaction,
            DatabaseTypeEnum provider, CancellationToken token)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            return provider switch
            {
                DatabaseTypeEnum.Sqlite => EnsureSqliteAsync(connection, transaction, token),
                DatabaseTypeEnum.Postgresql => EnsurePostgresqlAsync(connection, transaction, token),
                DatabaseTypeEnum.Mysql => EnsureMysqlAsync(connection, transaction, token),
                DatabaseTypeEnum.SqlServer => EnsureSqlServerAsync(connection, transaction, token),
                _ => throw new NotSupportedException("Unsupported model endpoint schema provider")
            };
        }

        private static async Task EnsureSqliteAsync(DbConnection connection, DbTransaction? transaction, CancellationToken token)
        {
            List<Dictionary<string, string?>> columns = await QueryAsync(connection, transaction,
                "SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info(@table);", "model_endpoints", null, token).ConfigureAwait(false);
            if (columns.Count == 0) return;

            Dictionary<string, string> types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "id", "TEXT" }, { "tenant_id", "TEXT" }, { "user_id", "TEXT" }, { "name", "TEXT" },
                { "kind", "TEXT" }, { "provider", "TEXT" }, { "base_url", "TEXT" }, { "api_key", "TEXT" },
                { "model", "TEXT" }, { "dimensionality", "INTEGER" }, { "timeout_ms", "INTEGER" },
                { "enabled", "INTEGER" }, { "health_status", "TEXT" }, { "last_health_check_utc", "TEXT" },
                { "last_health_error", "TEXT" }, { "last_latency_ms", "INTEGER" }, { "health_history_json", "TEXT" },
                { "scope", "TEXT" }, { "created_utc", "TEXT" }, { "last_update_utc", "TEXT" }
            };
            foreach (string name in _ColumnNames)
            {
                Dictionary<string, string?>? column = columns.Find(row => String.Equals(row["name"], name, StringComparison.OrdinalIgnoreCase));
                if (column == null || !String.Equals(column["type"], types[name], StringComparison.OrdinalIgnoreCase))
                    throw Incompatible("SQLite", name);
                bool expectedNotNull = name is "name" or "kind" or "provider" or "base_url" or "dimensionality" or "timeout_ms"
                    or "enabled" or "health_status" or "scope" or "created_utc" or "last_update_utc";
                if (Convert.ToInt32(column["notnull"], CultureInfo.InvariantCulture) != (expectedNotNull ? 1 : 0)
                    || !String.Equals(NormalizeDefault(column["dflt_value"]), NormalizeDefault(SqliteDefault(name)), StringComparison.Ordinal))
                    throw Incompatible("SQLite", name);
            }
            if (columns.Any(row => !_ColumnNames.Contains(row["name"]!, StringComparer.OrdinalIgnoreCase)
                && row["notnull"] == "1" && row["dflt_value"] == null))
                throw Incompatible("SQLite", "unexpected required column");
            List<Dictionary<string, string?>> keys = columns.Where(row => Convert.ToInt32(row["pk"], CultureInfo.InvariantCulture) > 0).ToList();
            if (keys.Count != 1 || !String.Equals(keys[0]["name"], "id", StringComparison.OrdinalIgnoreCase))
                throw Incompatible("SQLite", "primary key");

            List<Dictionary<string, string?>> indexes = await QueryAsync(connection, transaction,
                "SELECT name, \"unique\", partial FROM pragma_index_list(@table) WHERE name=@index;", "model_endpoints", "idx_model_endpoints_tenant", token).ConfigureAwait(false);
            if (indexes.Count > 0)
            {
                if (indexes.Count != 1 || indexes[0]["unique"] != "0" || indexes[0]["partial"] != "0") throw Incompatible("SQLite", "tenant index");
                List<Dictionary<string, string?>> members = await QueryAsync(connection, transaction,
                    "SELECT name FROM pragma_index_info(@index) ORDER BY seqno;", "model_endpoints", "idx_model_endpoints_tenant", token).ConfigureAwait(false);
                if (members.Count != 1 || !String.Equals(members[0]["name"], "tenant_id", StringComparison.OrdinalIgnoreCase))
                    throw Incompatible("SQLite", "tenant index");
            }
        }

        private static async Task EnsurePostgresqlAsync(DbConnection connection, DbTransaction? transaction, CancellationToken token)
        {
            List<Dictionary<string, string?>> tables = await QueryAsync(connection, transaction,
                "SELECT table_name FROM information_schema.tables WHERE table_schema=current_schema() AND table_name=@table;", "model_endpoints", null, token).ConfigureAwait(false);
            if (tables.Count == 0) return;
            List<Dictionary<string, string?>> columns = await QueryAsync(connection, transaction,
                "SELECT column_name, data_type, is_nullable, column_default FROM information_schema.columns WHERE table_schema=current_schema() AND table_name=@table;", "model_endpoints", null, token).ConfigureAwait(false);
            Dictionary<string, string> types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "id", "text" }, { "tenant_id", "text" }, { "user_id", "text" }, { "name", "text" }, { "kind", "text" },
                { "provider", "text" }, { "base_url", "text" }, { "api_key", "text" }, { "model", "text" },
                { "dimensionality", "integer" }, { "timeout_ms", "integer" }, { "enabled", "boolean" }, { "health_status", "text" },
                { "last_health_check_utc", "timestamp with time zone" }, { "last_health_error", "text" }, { "last_latency_ms", "integer" },
                { "health_history_json", "text" }, { "scope", "text" }, { "created_utc", "timestamp with time zone" },
                { "last_update_utc", "timestamp with time zone" }
            };
            foreach (string name in _ColumnNames)
            {
                Dictionary<string, string?>? column = columns.Find(row => String.Equals(row["column_name"], name, StringComparison.OrdinalIgnoreCase));
                if (column == null || !String.Equals(column["data_type"], types[name], StringComparison.OrdinalIgnoreCase))
                    throw Incompatible("PostgreSQL", name);
                bool expectedNullable = name is "tenant_id" or "user_id" or "api_key" or "model" or "last_health_check_utc"
                    or "last_health_error" or "last_latency_ms" or "health_history_json";
                if (String.Equals(column["is_nullable"], expectedNullable ? "YES" : "NO", StringComparison.OrdinalIgnoreCase) == false
                    || !String.Equals(NormalizeDefault(column["column_default"]), NormalizeDefault(PostgresqlDefault(name)), StringComparison.Ordinal))
                    throw Incompatible("PostgreSQL", name);
            }
            if (columns.Any(row => !_ColumnNames.Contains(row["column_name"]!, StringComparer.OrdinalIgnoreCase)
                && row["is_nullable"] == "NO" && row["column_default"] == null))
                throw Incompatible("PostgreSQL", "unexpected required column");
            List<Dictionary<string, string?>> primary = await QueryAsync(connection, transaction,
                "SELECT k.column_name FROM information_schema.table_constraints t JOIN information_schema.key_column_usage k ON t.constraint_name=k.constraint_name AND t.constraint_schema=k.constraint_schema AND t.table_name=k.table_name WHERE t.table_schema=current_schema() AND t.table_name=@table AND t.constraint_type='PRIMARY KEY' ORDER BY k.ordinal_position;",
                "model_endpoints", null, token).ConfigureAwait(false);
            if (primary.Count != 1 || !String.Equals(primary[0]["column_name"], "id", StringComparison.OrdinalIgnoreCase))
                throw Incompatible("PostgreSQL", "primary key");

            List<Dictionary<string, string?>> indexes = await QueryAsync(connection, transaction,
                "SELECT i.indisunique, i.indisvalid, i.indisready, i.indpred IS NULL AS no_predicate, COALESCE(string_agg(a.attname, ',' ORDER BY keys.ordinality),'') AS columns FROM pg_index i JOIN pg_class t ON t.oid=i.indrelid JOIN pg_class x ON x.oid=i.indexrelid JOIN pg_namespace n ON n.oid=t.relnamespace CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY keys(attnum,ordinality) JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum=keys.attnum WHERE n.nspname=current_schema() AND t.relname=@table AND x.relname=@index GROUP BY i.indisunique,i.indisvalid,i.indisready,i.indpred;",
                "model_endpoints", "idx_model_endpoints_tenant", token).ConfigureAwait(false);
            if (indexes.Count > 0 && (indexes.Count != 1 || indexes[0]["indisunique"] != "False" || indexes[0]["indisvalid"] != "True"
                || indexes[0]["indisready"] != "True" || indexes[0]["no_predicate"] != "True" || indexes[0]["columns"] != "tenant_id"))
                throw Incompatible("PostgreSQL", "tenant index");
        }

        private static async Task EnsureMysqlAsync(DbConnection connection, DbTransaction? transaction, CancellationToken token)
        {
            List<Dictionary<string, string?>> tables = await QueryAsync(connection, transaction,
                "SELECT table_name FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name=@table;", "model_endpoints", null, token).ConfigureAwait(false);
            if (tables.Count == 0) return;
            List<Dictionary<string, string?>> columns = await QueryAsync(connection, transaction,
                "SELECT column_name, column_type, is_nullable, column_default, character_set_name, collation_name, extra FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table;", "model_endpoints", null, token).ConfigureAwait(false);
            Dictionary<string, string> types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "id", "varchar(450)" }, { "tenant_id", "varchar(450)" }, { "user_id", "varchar(450)" }, { "name", "text" },
                { "kind", "varchar(64)" }, { "provider", "varchar(64)" }, { "base_url", "text" }, { "api_key", "text" },
                { "model", "text" }, { "dimensionality", "int" }, { "timeout_ms", "int" }, { "enabled", "tinyint(1)" },
                { "health_status", "varchar(64)" }, { "last_health_check_utc", "datetime(6)" }, { "last_health_error", "text" },
                { "last_latency_ms", "int" }, { "health_history_json", "longtext" }, { "scope", "varchar(32)" },
                { "created_utc", "datetime(6)" }, { "last_update_utc", "datetime(6)" }
            };
            string? idCollation = null;
            foreach (string name in _ColumnNames)
            {
                Dictionary<string, string?>? column = columns.Find(row => String.Equals(row["column_name"], name, StringComparison.OrdinalIgnoreCase));
                if (column == null || !String.Equals(column["column_type"], types[name], StringComparison.OrdinalIgnoreCase))
                    throw Incompatible("MySQL", name);
                bool expectedNullable = name is "tenant_id" or "user_id" or "api_key" or "model" or "last_health_check_utc"
                    or "last_health_error" or "last_latency_ms" or "health_history_json";
                if (!String.Equals(column["is_nullable"], expectedNullable ? "YES" : "NO", StringComparison.OrdinalIgnoreCase)
                    || !String.Equals(NormalizeDefault(column["column_default"]), NormalizeDefault(MysqlDefault(name)), StringComparison.Ordinal))
                    throw Incompatible("MySQL", name);
                if (name is "id" or "tenant_id" or "user_id" && column["character_set_name"] != "utf8mb4")
                    throw Incompatible("MySQL", name + " Unicode character set");
                if (name == "id") idCollation = column["collation_name"];
                else if (name is "tenant_id" or "user_id" && !String.Equals(column["collation_name"], idCollation, StringComparison.OrdinalIgnoreCase))
                    throw Incompatible("MySQL", name + " ID collation");
            }
            if (columns.Any(row => !_ColumnNames.Contains(row["column_name"]!, StringComparer.OrdinalIgnoreCase)
                && row["is_nullable"] == "NO" && row["column_default"] == null
                && !String.Equals(row["extra"], "auto_increment", StringComparison.OrdinalIgnoreCase)))
                throw Incompatible("MySQL", "unexpected required column");
            List<Dictionary<string, string?>> primary = await QueryAsync(connection, transaction,
                "SELECT column_name, sub_part, non_unique FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name=@table AND index_name='PRIMARY' ORDER BY seq_in_index;",
                "model_endpoints", null, token).ConfigureAwait(false);
            if (primary.Count != 1 || primary[0]["column_name"] != "id" || primary[0]["sub_part"] != null || primary[0]["non_unique"] != "0")
                throw Incompatible("MySQL", "primary key");
            List<Dictionary<string, string?>> indexes = await QueryAsync(connection, transaction,
                "SELECT column_name, sub_part, non_unique FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name=@table AND index_name=@index ORDER BY seq_in_index;",
                "model_endpoints", "idx_model_endpoints_tenant", token).ConfigureAwait(false);
            // tenant_id is VARCHAR(450) in the established full-value Unicode ID domain. With utf8mb4 this
            // consumes 1,800 bytes, within InnoDB's current 3,072-byte key budget, so the owned migration
            // creates a full-column lookup index. A prefix index does not match that established lookup-index
            // contract and can reduce selectivity, so it is incompatible.
            if (indexes.Count > 0 && (indexes.Count != 1 || indexes[0]["column_name"] != "tenant_id" || indexes[0]["sub_part"] != null || indexes[0]["non_unique"] != "1"))
                throw Incompatible("MySQL", "tenant index");
        }

        private static async Task EnsureSqlServerAsync(DbConnection connection, DbTransaction? transaction, CancellationToken token)
        {
            List<Dictionary<string, string?>> tables = await QueryAsync(connection, transaction,
                "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA=SCHEMA_NAME() AND TABLE_NAME=@table;", "model_endpoints", null, token).ConfigureAwait(false);
            if (tables.Count == 0) return;
            List<Dictionary<string, string?>> columns = await QueryAsync(connection, transaction,
                "SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE, COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=SCHEMA_NAME() AND TABLE_NAME=@table;", "model_endpoints", null, token).ConfigureAwait(false);
            Dictionary<string, string> types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "id", "nvarchar" }, { "tenant_id", "nvarchar" }, { "user_id", "nvarchar" }, { "name", "nvarchar" }, { "kind", "nvarchar" },
                { "provider", "nvarchar" }, { "base_url", "nvarchar" }, { "api_key", "nvarchar" }, { "model", "nvarchar" }, { "dimensionality", "int" },
                { "timeout_ms", "int" }, { "enabled", "bit" }, { "health_status", "nvarchar" }, { "last_health_check_utc", "datetime2" },
                { "last_health_error", "nvarchar" }, { "last_latency_ms", "int" }, { "health_history_json", "nvarchar" }, { "scope", "nvarchar" },
                { "created_utc", "datetime2" }, { "last_update_utc", "datetime2" }
            };
            Dictionary<string, string> lengths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "id", "450" }, { "tenant_id", "450" }, { "user_id", "450" }, { "name", "450" }, { "kind", "64" }, { "provider", "64" },
                { "base_url", "2000" }, { "api_key", "4000" }, { "model", "450" }, { "health_status", "64" }, { "last_health_error", "4000" },
                { "health_history_json", "-1" }, { "scope", "32" }
            };
            foreach (string name in _ColumnNames)
            {
                Dictionary<string, string?>? column = columns.Find(row => String.Equals(row["COLUMN_NAME"], name, StringComparison.OrdinalIgnoreCase));
                if (column == null || !String.Equals(column["DATA_TYPE"], types[name], StringComparison.OrdinalIgnoreCase)
                    || lengths.TryGetValue(name, out string? length) && column["CHARACTER_MAXIMUM_LENGTH"] != length)
                    throw Incompatible("SQL Server", name);
                bool expectedNullable = name is "tenant_id" or "user_id" or "api_key" or "model" or "last_health_check_utc"
                    or "last_health_error" or "last_latency_ms" or "health_history_json";
                if (!String.Equals(column["IS_NULLABLE"], expectedNullable ? "YES" : "NO", StringComparison.OrdinalIgnoreCase)
                    || !String.Equals(NormalizeDefault(column["COLUMN_DEFAULT"]), NormalizeDefault(SqlServerDefault(name)), StringComparison.Ordinal))
                    throw Incompatible("SQL Server", name);
            }
            if (columns.Any(row => !_ColumnNames.Contains(row["COLUMN_NAME"]!, StringComparer.OrdinalIgnoreCase)
                && row["IS_NULLABLE"] == "NO" && row["COLUMN_DEFAULT"] == null))
                throw Incompatible("SQL Server", "unexpected required column");
            List<Dictionary<string, string?>> primary = await QueryAsync(connection, transaction,
                "SELECT c.name AS column_name FROM sys.indexes i JOIN sys.index_columns k ON k.object_id=i.object_id AND k.index_id=i.index_id JOIN sys.columns c ON c.object_id=k.object_id AND c.column_id=k.column_id JOIN sys.tables t ON t.object_id=i.object_id WHERE t.schema_id=SCHEMA_ID() AND t.name=@table AND i.is_primary_key=1 ORDER BY k.key_ordinal;",
                "model_endpoints", null, token).ConfigureAwait(false);
            if (primary.Count != 1 || primary[0]["column_name"] != "id") throw Incompatible("SQL Server", "primary key");
            List<Dictionary<string, string?>> indexes = await QueryAsync(connection, transaction,
                "SELECT c.name AS column_name, i.is_unique, i.is_disabled, i.has_filter, i.is_hypothetical FROM sys.indexes i JOIN sys.index_columns k ON k.object_id=i.object_id AND k.index_id=i.index_id JOIN sys.columns c ON c.object_id=k.object_id AND c.column_id=k.column_id JOIN sys.tables t ON t.object_id=i.object_id WHERE t.schema_id=SCHEMA_ID() AND t.name=@table AND i.name=@index ORDER BY k.key_ordinal;",
                "model_endpoints", "idx_model_endpoints_tenant", token).ConfigureAwait(false);
            if (indexes.Count > 0 && (indexes.Count != 1 || indexes[0]["column_name"] != "tenant_id" || indexes[0]["is_unique"] != "0"
                || indexes[0]["is_disabled"] != "0" || indexes[0]["has_filter"] != "0" || indexes[0]["is_hypothetical"] != "0"))
                throw Incompatible("SQL Server", "tenant index");
        }

        private static string? SqliteDefault(string name) => name switch
        {
            "name" or "base_url" => "''", "dimensionality" => "0", "timeout_ms" => "120000", "enabled" => "0",
            "health_status" => "'Unknown'", "scope" => "'TenantWide'", _ => null
        };

        private static string? PostgresqlDefault(string name) => name switch
        {
            "name" or "base_url" => "''", "dimensionality" => "0", "timeout_ms" => "120000", "enabled" => "false",
            "health_status" => "'Unknown'", "scope" => "'TenantWide'", _ => null
        };

        private static string? MysqlDefault(string name) => name switch
        {
            "dimensionality" => "0", "timeout_ms" => "120000", "enabled" => "0", "health_status" => "Unknown", "scope" => "TenantWide", _ => null
        };

        private static string? SqlServerDefault(string name) => name switch
        {
            "name" or "base_url" => "''", "dimensionality" => "0", "timeout_ms" => "120000", "enabled" => "0",
            "health_status" => "'Unknown'", "scope" => "'TenantWide'", _ => null
        };

        private static string NormalizeDefault(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return String.Empty;
            string normalized = value.Trim();
            while (normalized.StartsWith("(", StringComparison.Ordinal) && normalized.EndsWith(")", StringComparison.Ordinal))
                normalized = normalized.Substring(1, normalized.Length - 2).Trim();
            normalized = Regex.Replace(normalized, @"::[a-z ]+$", String.Empty, RegexOptions.IgnoreCase).Trim();
            if (normalized.StartsWith("N'", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(1);
            if (normalized.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return String.Empty;
            if (normalized.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) return "true";
            if (normalized.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return "false";
            return normalized;
        }

        private static async Task<List<Dictionary<string, string?>>> QueryAsync(DbConnection connection, DbTransaction? transaction,
            string sql, string table, string? index, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                AddParameter(command, "@table", table);
                if (index != null) AddParameter(command, "@index", index);
                List<Dictionary<string, string?>> rows = new List<Dictionary<string, string?>>();
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        Dictionary<string, string?> row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < reader.FieldCount; i++)
                            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                        rows.Add(row);
                    }
                }
                return rows;
            }
        }

        private static void AddParameter(DbCommand command, string name, string value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        private static InvalidOperationException Incompatible(string provider, string detail) =>
            new InvalidOperationException("Incompatible " + provider + " model_endpoints schema: " + detail + "; migration history was not advanced");
    }
}
