namespace Armada.Core.Database.Mysql
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;

    /// <summary>
    /// Catalog-checked execution of pending DDL, including MySQL's nontransactional restarts.
    /// Original Unicode columns and full-value uniqueness remain intact.
    /// </summary>
    internal sealed class MysqlSchemaCompatibility
    {
        private readonly MySqlConnection _Connection;
        private const string _TenantKey = "armada_internal_tenant_key";
        private static readonly Dictionary<string, string> _TenantUniqueKeys = new Dictionary<string, string>
        {
            { "users", "email" }, { "prompt_templates", "name" }, { "personas", "name" },
            { "pipelines", "name" }, { "playbooks", "file_name" }
        };

        internal MysqlSchemaCompatibility(MySqlConnection connection)
        {
            _Connection = connection;
        }

        internal async Task ExecuteAsync(string statement, int version, CancellationToken token)
        {
            string sql = statement.Trim();
            Match table = Regex.Match(sql, @"^CREATE TABLE (?:IF NOT EXISTS )?`?(\w+)`? \(", RegexOptions.IgnoreCase);
            Match column = Regex.Match(sql, @"^ALTER TABLE `?(\w+)`? ADD (?:COLUMN )?(?:IF NOT EXISTS )?`?(\w+)`? (.+);$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            Match index = Regex.Match(sql, @"^CREATE (UNIQUE )?INDEX (?:IF NOT EXISTS )?`?(\w+)`?\s+ON `?(\w+)`?\s*\((.+)\);$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            Match foreignKey = Regex.Match(sql, @"^ALTER TABLE (\w+) ADD CONSTRAINT (\w+) FOREIGN KEY \((\w+)\) REFERENCES (\w+)\((\w+)\)(?: ON DELETE (CASCADE|SET NULL|NO ACTION))?;$", RegexOptions.IgnoreCase);
            if (table.Success)
            {
                await EnsureTableAsync(table.Groups[1].Value, sql, version, token).ConfigureAwait(false);
            }
            else if (foreignKey.Success)
            {
                await EnsureForeignKeyAsync(foreignKey.Groups[1].Value, foreignKey.Groups[3].Value,
                    foreignKey.Groups[4].Value, foreignKey.Groups[5].Value,
                    foreignKey.Groups[6].Success ? foreignKey.Groups[6].Value : "RESTRICT", sql, token).ConfigureAwait(false);
            }
            else if (column.Success)
            {
                await EnsureColumnAsync(column.Groups[1].Value, column.Groups[2].Value, column.Groups[3].Value, version, token).ConfigureAwait(false);
            }
            else if (index.Success)
            {
                await EnsureIndexAsync(index.Groups[3].Value, index.Groups[2].Value, index.Groups[4].Value,
                    index.Groups[1].Success, token).ConfigureAwait(false);
            }
            else
            {
                await ExecuteSqlAsync(sql, token).ConfigureAwait(false);
            }
        }

        private async Task EnsureTableAsync(string table, string sql, int version, CancellationToken token)
        {
            List<Dictionary<string, string?>> columns = await ColumnsAsync(table, token).ConfigureAwait(false);
            if (columns.Count == 0)
                await ExecuteSqlAsync(sql, token).ConfigureAwait(false);

            List<string> primaryColumns = new List<string>();
            foreach (string raw in sql.Split('\n'))
            {
                string line = raw.Trim().TrimEnd(',');
                Match column = Regex.Match(line, @"^`?(\w+)`? ((?:VARCHAR\(\d+\)|LONGTEXT|TEXT|INT|INTEGER|BIGINT(?: UNSIGNED)?|TINYINT\(1\)|DOUBLE|REAL|DATETIME\(6\)|BOOLEAN)(?=\s|$).*)$", RegexOptions.IgnoreCase);
                if (!column.Success) continue;
                string name = column.Groups[1].Value;
                string declaration = column.Groups[2].Value;
                await EnsureColumnAsync(table, name, declaration, version, token).ConfigureAwait(false);
                if (declaration.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase)) primaryColumns.Add(name);
                if (declaration.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
                    await VerifyFullUniqueColumnAsync(table, name, token).ConfigureAwait(false);
            }
            Match compositePrimary = Regex.Match(sql, @"PRIMARY KEY \(([^)]+)\)");
            if (compositePrimary.Success) primaryColumns = compositePrimary.Groups[1].Value.Split(',').Select(item => item.Trim(' ', '`')).ToList();
            List<Dictionary<string, string?>> primary = await IndexAsync(table, "PRIMARY", token).ConfigureAwait(false);
            if (!primary.Select(row => row["column_name"]).SequenceEqual(primaryColumns)
                || primary.Any(row => row["sub_part"] != null || row["non_unique"] != "0"))
                throw Incompatible(table, "primary key");

            foreach (Match key in Regex.Matches(sql, @"(?:CONSTRAINT (\w+) )?FOREIGN KEY \((\w+)\) REFERENCES (\w+)\((\w+)\)(?: ON DELETE (CASCADE|SET NULL|NO ACTION))?"))
            {
                string source = key.Groups[2].Value;
                string target = key.Groups[3].Value;
                string targetColumn = key.Groups[4].Value;
                string action = key.Groups[5].Success ? key.Groups[5].Value : "RESTRICT";
                await EnsureForeignKeyAsync(table, source, target, targetColumn, action,
                    "ALTER TABLE " + Quote(table) + " ADD FOREIGN KEY (" + Quote(source) + ") REFERENCES " + Quote(target) + "(" + Quote(targetColumn) + ") ON DELETE " + action + ";", token).ConfigureAwait(false);
            }
            foreach (Match key in Regex.Matches(sql, @"UNIQUE KEY (\w+) \(([^)]+)\)"))
                await EnsureIndexAsync(table, key.Groups[1].Value, key.Groups[2].Value, true, token).ConfigureAwait(false);
        }

        private async Task EnsureColumnAsync(string table, string name, string declaration, int version, CancellationToken token)
        {
            List<Dictionary<string, string?>> columns = await ColumnsAsync(table, token).ConfigureAwait(false);
            Dictionary<string, string?>? column = columns.Find(item => item["column_name"] == name);
            if (column == null)
            {
                await ExecuteSqlAsync("ALTER TABLE " + Quote(table) + " ADD COLUMN " + Quote(name) + " " + declaration + ";", token).ConfigureAwait(false);
                column = (await ColumnsAsync(table, token).ConfigureAwait(false)).Find(item => item["column_name"] == name)
                    ?? throw Incompatible(table, name);
            }
            string type = Regex.Match(declaration, @"^\w+(?:\(\d+\))?(?: UNSIGNED)?", RegexOptions.IgnoreCase).Value.ToLowerInvariant();
            type = type switch { "integer" => "int", "boolean" => "tinyint(1)", "real" => "double", _ => type };
            bool nullable = !declaration.Contains("NOT NULL", StringComparison.OrdinalIgnoreCase) && !declaration.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase);
            Match defaultMatch = Regex.Match(declaration, @"\bDEFAULT (.+?)(?:\s+UNIQUE)?$", RegexOptions.IgnoreCase);
            string? expectedDefault = defaultMatch.Success ? defaultMatch.Groups[1].Value : null;
            string actualType = column["column_type"] ?? String.Empty;
            if (table == "voyages" && version == 76
                && name is "source_planning_session_id" or "source_planning_message_id"
                && !SupportsFullUnicode(column["character_set_name"]))
                throw Incompatible(table, name + " requires a full Unicode character set");
            if (table == "vessels" && version == 75
                && name is "protected_branch_patterns" or "release_branch_prefix" or "hotfix_branch_prefix"
                && !SupportsFullUnicode(column["character_set_name"]))
                throw Incompatible(table, name + " requires a full Unicode character set");
            // Historical v70 repeats the same nullable reasoning preference with a smaller bound.
            bool widerReasoning = table == "captains" && name == "reasoning_effort" && version == 70
                && type == "varchar(64)" && actualType == "varchar(450)";
            // The original table stored the mission identifier in VARCHAR(450); v40 widens it.
            if (table == "vessels" && name == "last_reflection_mission_id" && version == 40 && type == "longtext" && actualType == "varchar(450)")
            {
                await ExecuteSqlAsync("ALTER TABLE vessels MODIFY COLUMN last_reflection_mission_id LONGTEXT NULL;", token).ConfigureAwait(false);
                actualType = "longtext";
            }
            if (column["extra"]!.StartsWith("VIRTUAL GENERATED", StringComparison.OrdinalIgnoreCase)
                || column["extra"]!.StartsWith("STORED GENERATED", StringComparison.OrdinalIgnoreCase)
                || actualType != type && !widerReasoning || (column["is_nullable"] == "YES") != nullable
                || (column["extra"]!.Contains("DEFAULT_GENERATED", StringComparison.OrdinalIgnoreCase)
                    ? NormalizeDefaultExpression(column["column_default"]) : column["column_default"]) != NormalizeDefaultExpression(expectedDefault))
                throw Incompatible(table, name + " type/nullability/default (found " + actualType + ", " + column["is_nullable"] + ", " + column["column_default"] + ")");
        }

        internal async Task EnsureIndexAsync(string table, string name, string fields, bool unique, CancellationToken token)
        {
            string[] names = fields.Split(',').Select(field => Regex.Match(field.Trim(), @"^`?(\w+)`?").Groups[1].Value).ToArray();
            bool[] descending = fields.Split(',').Select(field => field.Trim().EndsWith(" DESC", StringComparison.OrdinalIgnoreCase)).ToArray();
            List<Dictionary<string, string?>> existing = await IndexAsync(table, name, token).ConfigureAwait(false);
            if (unique && _TenantUniqueKeys.TryGetValue(table, out string? fullValue) && names.SequenceEqual(new[] { "tenant_id", fullValue }))
            {
                List<Dictionary<string, string?>> parentColumns = await ColumnsAsync("tenants", token).ConfigureAwait(false);
                List<Dictionary<string, string?>> childColumns = await ColumnsAsync(table, token).ConfigureAwait(false);
                Dictionary<string, string?> parentId = parentColumns.Find(column => column["column_name"] == "id") ?? throw Incompatible("tenants", "id");
                Dictionary<string, string?> childTenant = childColumns.Find(column => column["column_name"] == "tenant_id") ?? throw Incompatible(table, "tenant_id");
                Dictionary<string, string?> childValue = childColumns.Find(column => column["column_name"] == fullValue) ?? throw Incompatible(table, fullValue);
                if (parentId["column_type"] != "varchar(450)" || parentId["is_nullable"] != "NO"
                    || childTenant["column_type"] != "varchar(450)" || childTenant["is_nullable"] != (table == "users" ? "NO" : "YES")
                    || childValue["column_type"] != "varchar(450)" || childValue["is_nullable"] != "NO"
                    || !SupportsFullUnicode(parentId["character_set_name"]) || !SupportsFullUnicode(childTenant["character_set_name"])
                    || !SupportsFullUnicode(childValue["character_set_name"])
                    || childTenant["character_set_name"] != parentId["character_set_name"] || childTenant["collation_name"] != parentId["collation_name"])
                    throw Incompatible(table, "original Unicode identifier/value domain");
                string deleteAction = table == "users" || table == "playbooks" ? "CASCADE" : "RESTRICT";
                await EnsureForeignKeyAsync(table, "tenant_id", "tenants", "id", deleteAction,
                    "ALTER TABLE " + Quote(table) + " ADD FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE " + deleteAction + ";", token).ConfigureAwait(false);
                using (MySqlCommand command = _Connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM " + Quote(table) + " c LEFT JOIN tenants t ON c.tenant_id=t.id WHERE c.tenant_id IS NOT NULL AND t.id IS NULL;";
                    if (Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
                        throw Incompatible(table, "orphan tenant identifier");
                }
                if (IndexMatches(existing, names, descending, true, new string?[names.Length])) return;
                await EnsureTenantUniquenessAsync(table, name, fullValue, token).ConfigureAwait(false);
                return;
            }
            List<Dictionary<string, string?>> columns = await ColumnsAsync(table, token).ConfigureAwait(false);
            List<Dictionary<string, string?>> selected = names.Select(columnName => columns.Find(column => column["column_name"] == columnName)
                ?? throw Incompatible(table, "index column " + columnName)).ToList();
            string?[] prefixes = new string?[names.Length];
            long total = selected.Sum(ColumnBytes);
            if (total > 3000)
            {
                if (unique) throw Incompatible(table, "unreviewed oversized unique index " + name);
                long remaining = 3000 - selected.Where(column => column["character_octet_length"] == null).Sum(ColumnBytes);
                for (int index = 0; index < names.Length; index++)
                {
                    Dictionary<string, string?> column = selected[index];
                    if (column["character_octet_length"] == null) continue;
                    long characters = Int64.Parse(column["character_maximum_length"]!, CultureInfo.InvariantCulture);
                    long bytesPerCharacter = Math.Max(1, ColumnBytes(column) / characters);
                    int laterStrings = selected.Skip(index + 1).Count(item => item["character_octet_length"] != null);
                    // Keep the first full key for existing foreign keys when it fits. Prefix only
                    // nonunique lookup columns; complete row predicates still decide equality.
                    long allowed = index == 0 && ColumnBytes(column) < remaining
                        ? characters : Math.Min(characters, remaining / (laterStrings + 1) / bytesPerCharacter);
                    if (allowed < 1) throw Incompatible(table, "index budget " + name);
                    if (allowed < characters) prefixes[index] = allowed.ToString(CultureInfo.InvariantCulture);
                    remaining -= allowed * bytesPerCharacter;
                }
            }
            if (existing.Count > 0)
            {
                if (!IndexMatches(existing, names, descending, unique, prefixes)) throw Incompatible(table, "index " + name);
                return;
            }
            string[] keys = names.Select((column, index) => Quote(column) + (prefixes[index] == null ? "" : "(" + prefixes[index] + ")") + (descending[index] ? " DESC" : "")).ToArray();
            await ExecuteSqlAsync("CREATE " + (unique ? "UNIQUE " : "") + "INDEX " + Quote(name) + " ON " + Quote(table) + "(" + String.Join(",", keys) + ");", token).ConfigureAwait(false);
        }

        internal async Task EnsureUniqueContractsAsync(CancellationToken token)
        {
            foreach (KeyValuePair<string, string> key in _TenantUniqueKeys)
                await EnsureIndexAsync(key.Key, "idx_" + key.Key + "_tenant_" + key.Value,
                    "tenant_id, " + key.Value, true, token).ConfigureAwait(false);
        }

        internal async Task EnsureParallelStageIndexAsync(CancellationToken token)
        {
            List<Dictionary<string, string?>> existing = await IndexAsync("pipeline_stages", "idx_pipeline_stages_order", token).ConfigureAwait(false);
            string[] fields = { "pipeline_id", "stage_order" };
            if (existing.Count > 0 && !IndexMatches(existing, fields, new bool[2], false, new string?[2]))
            {
                if (!IndexMatches(existing, fields, new bool[2], true, new string?[2]))
                    throw Incompatible("pipeline_stages", "parallel stage index");
                await ExecuteSqlAsync("DROP INDEX idx_pipeline_stages_order ON pipeline_stages;", token).ConfigureAwait(false);
            }
            await EnsureIndexAsync("pipeline_stages", "idx_pipeline_stages_order", "pipeline_id, stage_order", false, token).ConfigureAwait(false);
        }

        private async Task EnsureTenantUniquenessAsync(string table, string indexName, string valueColumn, CancellationToken token)
        {
            List<Dictionary<string, string?>> tenantColumns = await ColumnsAsync("tenants", token).ConfigureAwait(false);
            Dictionary<string, string?>? tenantKey = tenantColumns.Find(column => column["column_name"] == _TenantKey);
            if (tenantKey == null)
            {
                await ExecuteSqlAsync("ALTER TABLE tenants ADD COLUMN " + Quote(_TenantKey) + " BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, ADD UNIQUE INDEX ux_armada_internal_tenant_key (" + Quote(_TenantKey) + ");", token).ConfigureAwait(false);
                tenantKey = (await ColumnsAsync("tenants", token).ConfigureAwait(false)).Find(column => column["column_name"] == _TenantKey)!;
            }
            if (tenantKey["column_type"] != "bigint unsigned" || tenantKey["is_nullable"] != "NO" || tenantKey["extra"] != "auto_increment")
                throw Incompatible("tenants", _TenantKey);
            await EnsureIndexAsync("tenants", "ux_armada_internal_tenant_key", _TenantKey, true, token).ConfigureAwait(false);
            await EnsureTriggerAsync("tenants", "tr_armada_tenant_key_immutable", "UPDATE",
                "BEGIN IF NOT (NEW." + Quote(_TenantKey) + " <=> OLD." + Quote(_TenantKey) + ") THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Internal tenant key is immutable'; END IF; END", token).ConfigureAwait(false);
            await EnsureColumnAsync(table, _TenantKey, "BIGINT UNSIGNED NULL", 0, token).ConfigureAwait(false);
            string body = "BEGIN DECLARE tenant_key BIGINT UNSIGNED DEFAULT NULL; DECLARE CONTINUE HANDLER FOR NOT FOUND SET tenant_key = NULL; "
                + "IF NEW.tenant_id IS NOT NULL THEN SELECT " + Quote(_TenantKey) + " INTO tenant_key FROM tenants WHERE id = NEW.tenant_id FOR SHARE; "
                + "IF tenant_key IS NULL THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Tenant for unique key does not exist'; END IF; END IF; "
                + "SET NEW." + Quote(_TenantKey) + " = tenant_key; END";
            await EnsureTriggerAsync(table, "tr_armada_" + table + "_tenant_insert", "INSERT", body, token).ConfigureAwait(false);
            await EnsureTriggerAsync(table, "tr_armada_" + table + "_tenant_update", "UPDATE", body, token).ConfigureAwait(false);
            // Only an unfinished installation needs a backfill. Completed keys must validate
            // without rewriting application rows or hiding a damaged mapping on restart.
            List<Dictionary<string, string?>> installed = await IndexAsync(table, indexName, token).ConfigureAwait(false);
            string mismatch = "SELECT COUNT(*) FROM " + Quote(table) + " c LEFT JOIN tenants t ON t.id=c.tenant_id WHERE NOT (c."
                + Quote(_TenantKey) + " <=> t." + Quote(_TenantKey) + ");";
            if (installed.Count == 0)
                await ExecuteSqlAsync("UPDATE " + Quote(table) + " c LEFT JOIN tenants t ON t.id=c.tenant_id SET c."
                    + Quote(_TenantKey) + " = t." + Quote(_TenantKey) + " WHERE NOT (c." + Quote(_TenantKey)
                    + " <=> t." + Quote(_TenantKey) + ");", token).ConfigureAwait(false);
            using (MySqlCommand command = _Connection.CreateCommand())
            {
                command.CommandText = mismatch;
                if (Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
                    throw Incompatible(table, "tenant key mapping");
            }
            await EnsureIndexAsync(table, "idx_armada_" + table + "_tenant_fk", "tenant_id", false, token).ConfigureAwait(false);
            List<Dictionary<string, string?>> existing = await IndexAsync(table, indexName, token).ConfigureAwait(false);
            string[] keys = { _TenantKey, valueColumn };
            if (existing.Count > 0)
            {
                if (!IndexMatches(existing, keys, new bool[2], true, new string?[2])) throw Incompatible(table, "full unique key " + indexName);
                return;
            }
            await ExecuteSqlAsync("CREATE UNIQUE INDEX " + Quote(indexName) + " ON " + Quote(table) + "(" + Quote(_TenantKey) + "," + Quote(valueColumn) + ");", token).ConfigureAwait(false);
        }

        private async Task EnsureTriggerAsync(string table, string name, string operation, string body, CancellationToken token)
        {
            List<Dictionary<string, string?>> rows = await QueryAsync("SELECT event_object_table, event_manipulation, action_timing, action_statement FROM information_schema.triggers WHERE trigger_schema=DATABASE() AND trigger_name=@name;", name, null, token).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                await ExecuteSqlAsync("CREATE TRIGGER " + Quote(name) + " BEFORE " + operation + " ON " + Quote(table) + " FOR EACH ROW " + body, token).ConfigureAwait(false);
                return;
            }
            if (rows.Count != 1 || rows[0]["event_object_table"] != table || rows[0]["event_manipulation"] != operation || rows[0]["action_timing"] != "BEFORE"
                || Regex.Replace(rows[0]["action_statement"]!, @"\s+", " ").Trim() != Regex.Replace(body, @"\s+", " ").Trim())
                throw Incompatible(table, "trigger " + name);
        }

        private async Task EnsureForeignKeyAsync(string table, string column, string target, string targetColumn,
            string action, string ddl, CancellationToken token)
        {
            List<Dictionary<string, string?>> found = await QueryAsync(@"SELECT k.referenced_table_name, k.referenced_column_name, (k.referenced_table_schema=DATABASE()) AS same_schema, r.delete_rule, r.update_rule, (SELECT COUNT(*) FROM information_schema.key_column_usage members WHERE members.constraint_schema=k.constraint_schema AND members.constraint_name=k.constraint_name AND members.table_name=k.table_name) AS key_columns FROM information_schema.key_column_usage k JOIN information_schema.referential_constraints r ON r.constraint_schema=k.constraint_schema AND r.constraint_name=k.constraint_name AND r.table_name=k.table_name WHERE k.table_schema=DATABASE() AND k.table_name=@name AND k.column_name=@column AND k.referenced_table_name IS NOT NULL;", table, column, token).ConfigureAwait(false);
            if (found.Count == 0) await ExecuteSqlAsync(ddl, token).ConfigureAwait(false);
            else if (found.Count != 1 || found[0]["referenced_table_name"] != target || found[0]["referenced_column_name"] != targetColumn || found[0]["same_schema"] != "1" || found[0]["key_columns"] != "1"
                || NormalizeAction(found[0]["delete_rule"]!) != NormalizeAction(action) || NormalizeAction(found[0]["update_rule"]!) != "RESTRICT")
                throw Incompatible(table, "foreign key " + column);
        }

        private async Task VerifyFullUniqueColumnAsync(string table, string column, CancellationToken token)
        {
            List<Dictionary<string, string?>> found = await QueryAsync(@"SELECT index_name FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name=@name AND non_unique=0 GROUP BY index_name HAVING COUNT(*)=1 AND MAX(column_name)=@column AND MAX(sub_part) IS NULL;", table, column, token).ConfigureAwait(false);
            if (found.Count == 0) throw Incompatible(table, "full unique column " + column);
        }

        private Task<List<Dictionary<string, string?>>> ColumnsAsync(string table, CancellationToken token) =>
            QueryAsync("SELECT column_name, column_type, character_maximum_length, character_octet_length, is_nullable, column_default, extra, character_set_name, collation_name FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@name;", table, null, token);

        private Task<List<Dictionary<string, string?>>> IndexAsync(string table, string index, CancellationToken token) =>
            QueryAsync("SELECT column_name, sub_part, non_unique, collation FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name=@name AND index_name=@column ORDER BY seq_in_index;", table, index, token);

        private static bool IndexMatches(List<Dictionary<string, string?>> actual, string[] columns, bool[] descending, bool unique, string?[] prefixes)
        {
            return actual.Count == columns.Length && actual.Select((row, index) => row["column_name"] == columns[index]
                && row["sub_part"] == prefixes[index] && row["non_unique"] == (unique ? "0" : "1")
                && row["collation"] == (descending[index] ? "D" : "A")).All(matches => matches);
        }

        private static bool SupportsFullUnicode(string? charset) => charset is "utf8mb4" or "utf16" or "utf16le" or "utf32";

        private static long ColumnBytes(Dictionary<string, string?> column)
        {
            if (column["character_octet_length"] != null) return Int64.Parse(column["character_octet_length"]!, CultureInfo.InvariantCulture);
            string type = column["column_type"]!;
            return type.StartsWith("tinyint") ? 1 : type.StartsWith("int") ? 4 : 8;
        }

        private static string? NormalizeDefaultExpression(string? value)
        {
            if (value == null) return null;
            string expression = value.Trim();
            while (expression.StartsWith("(", StringComparison.Ordinal) && expression.EndsWith(")", StringComparison.Ordinal))
                expression = expression.Substring(1, expression.Length - 2).Trim();
            if (expression.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return null;
            expression = Regex.Replace(expression.Replace("\\'", "'"), @"^_[a-z0-9]+(?=')", "", RegexOptions.IgnoreCase);
            if (expression.Length >= 2 && expression[0] == '\'' && expression[expression.Length - 1] == '\'')
                return expression.Substring(1, expression.Length - 2).Replace("''", "'");
            return expression;
        }

        private static string NormalizeAction(string value) => value == "NO ACTION" ? "RESTRICT" : value;
        private static string Quote(string value) => "`" + value.Replace("`", "``") + "`";
        private static InvalidOperationException Incompatible(string table, string detail) => new InvalidOperationException("Incompatible MySQL schema " + table + "." + detail + "; no data or applied migration record was replaced");

        internal async Task ExecuteSqlAsync(string sql, CancellationToken token)
        {
            using (MySqlCommand command = _Connection.CreateCommand())
            {
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        private async Task<List<Dictionary<string, string?>>> QueryAsync(string sql, string name, string? column, CancellationToken token)
        {
            using (MySqlCommand command = _Connection.CreateCommand())
            {
                command.CommandText = sql;
                command.Parameters.AddWithValue("@name", name);
                if (column != null) command.Parameters.AddWithValue("@column", column);
                List<Dictionary<string, string?>> rows = new List<Dictionary<string, string?>>();
                using (MySqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
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
