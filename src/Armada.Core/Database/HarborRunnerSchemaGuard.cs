namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;

    /// <summary>Checks a partially created Harbor enrollment table before migration replay.</summary>
    internal static class HarborRunnerSchemaGuard
    {
        private const string TableName = "harbor_runner_enrollments";

        internal static async Task EnsureAsync(DbConnection connection, DbTransaction? transaction,
            DatabaseTypeEnum provider, CancellationToken token)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            List<Dictionary<string, string?>> columns;
            if (provider == DatabaseTypeEnum.Sqlite)
                columns = await QueryAsync(connection, transaction, "SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info(@table);", token).ConfigureAwait(false);
            else if (provider == DatabaseTypeEnum.Postgresql)
                columns = await QueryAsync(connection, transaction, "SELECT column_name, data_type, is_nullable, column_default FROM information_schema.columns WHERE table_schema=current_schema() AND table_name=@table;", token).ConfigureAwait(false);
            else if (provider == DatabaseTypeEnum.Mysql)
                columns = await QueryAsync(connection, transaction, "SELECT column_name, column_type, is_nullable, column_default, collation_name FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table;", token).ConfigureAwait(false);
            else if (provider == DatabaseTypeEnum.SqlServer)
                columns = await QueryAsync(connection, transaction, "SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE, COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=SCHEMA_NAME() AND TABLE_NAME=@table;", token).ConfigureAwait(false);
            else throw new NotSupportedException("Unsupported Harbor runner schema provider.");

            if (columns.Count == 0)
            {
                if (provider != DatabaseTypeEnum.Sqlite)
                {
                    string tableQuery = provider == DatabaseTypeEnum.Postgresql
                        ? "SELECT table_name FROM information_schema.tables WHERE table_schema=current_schema() AND lower(table_name)=lower(@table);"
                        : provider == DatabaseTypeEnum.Mysql
                            ? "SELECT table_name FROM information_schema.tables WHERE table_schema=DATABASE() AND lower(table_name)=lower(@table);"
                            : "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA=SCHEMA_NAME() AND LOWER(TABLE_NAME)=LOWER(@table);";
                    List<Dictionary<string, string?>> similarlyNamedTables = await QueryAsync(connection, transaction, tableQuery, token).ConfigureAwait(false);
                    if (similarlyNamedTables.Count > 0) throw Incompatible(provider, "table name");
                }
                return;
            }
            string[] expected = new[] { "runner_id", "tenant_id", "user_id", "auth_method", "credential_id", "generation", "active", "created_utc", "last_update_utc", "revoked_utc", "revoked_by_user_id" };
            foreach (string name in expected)
            {
                Dictionary<string, string?>? column = columns.Find(item => String.Equals(Value(item, provider == DatabaseTypeEnum.Sqlite ? "name" : "column_name"), name, StringComparison.Ordinal));
                if (column == null) throw Incompatible(provider, name);
                bool nullable = String.Equals(Value(column, provider == DatabaseTypeEnum.Sqlite ? "notnull" : "is_nullable"), provider == DatabaseTypeEnum.Sqlite ? "0" : "YES", StringComparison.OrdinalIgnoreCase);
                bool expectedNullable = name is "credential_id" or "revoked_utc" or "revoked_by_user_id";
                if (nullable != expectedNullable) throw Incompatible(provider, name + " nullability");
                string type = Value(column, provider == DatabaseTypeEnum.Sqlite ? "type" : provider == DatabaseTypeEnum.SqlServer ? "DATA_TYPE" : provider == DatabaseTypeEnum.Mysql ? "column_type" : "data_type").ToLowerInvariant();
                if (!TypeMatches(provider, name, type, column)) throw Incompatible(provider, name + " type");
                if (provider == DatabaseTypeEnum.Mysql && (name is "runner_id" or "tenant_id" or "user_id" or "auth_method" or "credential_id" or "revoked_by_user_id")
                    && !String.Equals(Value(column, "collation_name"), "utf8mb4_bin", StringComparison.OrdinalIgnoreCase))
                    throw Incompatible(provider, name + " collation");
                if (!DefaultMatches(provider, name, Value(column, provider == DatabaseTypeEnum.Sqlite ? "dflt_value" : "column_default")))
                    throw Incompatible(provider, name + " default");
            }
            foreach (Dictionary<string, string?> column in columns)
            {
                string name = Value(column, provider == DatabaseTypeEnum.Sqlite ? "name" : "column_name");
                bool known = false;
                foreach (string expectedName in expected)
                    if (String.Equals(expectedName, name, StringComparison.Ordinal)) known = true;
                bool nullable = String.Equals(Value(column, provider == DatabaseTypeEnum.Sqlite ? "notnull" : "is_nullable"), provider == DatabaseTypeEnum.Sqlite ? "0" : "YES", StringComparison.OrdinalIgnoreCase);
                if (!known && !nullable && String.IsNullOrWhiteSpace(Value(column, provider == DatabaseTypeEnum.Sqlite ? "dflt_value" : "column_default")))
                    throw Incompatible(provider, "unexpected required column");
            }
            if (provider == DatabaseTypeEnum.Sqlite)
            {
                List<Dictionary<string, string?>> keys = columns.FindAll(item => Value(item, "pk") != "0");
                if (keys.Count != 1 || !String.Equals(Value(keys[0], "name"), "runner_id", StringComparison.Ordinal))
                    throw Incompatible(provider, "primary key");
            }
            else
            {
                List<Dictionary<string, string?>> keys = provider == DatabaseTypeEnum.Postgresql
                    ? await QueryAsync(connection, transaction, "SELECT k.column_name FROM information_schema.table_constraints t JOIN information_schema.key_column_usage k ON t.constraint_name=k.constraint_name AND t.constraint_schema=k.constraint_schema WHERE t.table_schema=current_schema() AND t.table_name=@table AND t.constraint_type='PRIMARY KEY' ORDER BY k.ordinal_position;", token).ConfigureAwait(false)
                    : provider == DatabaseTypeEnum.Mysql
                        ? await QueryAsync(connection, transaction, "SELECT column_name FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name=@table AND index_name='PRIMARY' ORDER BY seq_in_index;", token).ConfigureAwait(false)
                        : await QueryAsync(connection, transaction, "SELECT k.COLUMN_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS t JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE k ON t.CONSTRAINT_SCHEMA=k.CONSTRAINT_SCHEMA AND t.CONSTRAINT_NAME=k.CONSTRAINT_NAME AND t.TABLE_SCHEMA=k.TABLE_SCHEMA AND t.TABLE_NAME=k.TABLE_NAME WHERE t.TABLE_SCHEMA=SCHEMA_NAME() AND t.TABLE_NAME=@table AND t.CONSTRAINT_TYPE='PRIMARY KEY' ORDER BY k.ORDINAL_POSITION;", token).ConfigureAwait(false);
                if (keys.Count != 1 || !String.Equals(Value(keys[0], "column_name"), "runner_id", StringComparison.Ordinal))
                    throw Incompatible(provider, "primary key");
            }
        }

        private static bool TypeMatches(DatabaseTypeEnum provider, string name, string type, Dictionary<string, string?> column)
        {
            if (provider == DatabaseTypeEnum.Sqlite)
                return name == "generation" || name == "active" ? type == "integer" : type == "text";
            if (provider == DatabaseTypeEnum.Postgresql)
                return name == "generation" ? type == "bigint" : name == "active" ? type == "boolean" : name is "created_utc" or "last_update_utc" or "revoked_utc" ? type == "timestamp with time zone" : type == "text";
            if (provider == DatabaseTypeEnum.Mysql)
                return name == "generation" ? type == "bigint" : name == "active" ? type == "tinyint(1)" : name is "created_utc" or "last_update_utc" or "revoked_utc" ? type == "datetime(6)" : name == "auth_method" ? type == "varchar(64)" : type == "varchar(450)";
            if (name == "generation") return type == "bigint";
            if (name == "active") return type == "bit";
            if (name is "auth_method") return type == "nvarchar" && Value(column, "CHARACTER_MAXIMUM_LENGTH") == "64";
            if (name is "created_utc" or "last_update_utc" or "revoked_utc") return type == "datetime2";
            return type == "nvarchar" && Value(column, "CHARACTER_MAXIMUM_LENGTH") == "450";
        }

        private static bool DefaultMatches(DatabaseTypeEnum provider, string name, string actual)
        {
            string normalized = actual.Trim().ToLowerInvariant();
            while (normalized.Length >= 2 && normalized[0] == '(' && normalized[normalized.Length - 1] == ')')
                normalized = normalized.Substring(1, normalized.Length - 2).Trim();
            normalized = normalized.Trim('\'', '"');
            if (name != "active") return normalized.Length == 0;
            return provider == DatabaseTypeEnum.Postgresql ? normalized == "true" : normalized == "1";
        }

        private static async Task<List<Dictionary<string, string?>>> QueryAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken token)
        {
            List<Dictionary<string, string?>> rows = new List<Dictionary<string, string?>>();
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = "@table";
                parameter.Value = TableName;
                command.Parameters.Add(parameter);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        Dictionary<string, string?> row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                        for (int index = 0; index < reader.FieldCount; index++)
                            row[reader.GetName(index)] = reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture);
                        rows.Add(row);
                    }
                }
            }
            return rows;
        }

        private static string Value(Dictionary<string, string?> row, string name)
        {
            return row.TryGetValue(name, out string? value) ? value ?? String.Empty : String.Empty;
        }

        private static InvalidOperationException Incompatible(DatabaseTypeEnum provider, string item)
        {
            return new InvalidOperationException(provider + " Harbor runner enrollment schema is incompatible: " + item);
        }
    }
}
