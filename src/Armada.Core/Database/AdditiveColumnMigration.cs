namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;

    /// <summary>Validate equivalent columns before accepting an additive migration.</summary>
    internal static class AdditiveColumnMigration
    {
        internal static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction,
            DatabaseTypeEnum provider, string sql, CancellationToken token)
        {
            Match statement = Regex.Match(sql, @"^ALTER TABLE (\w+) ADD (?:COLUMN )?(\w+) (.+);$", RegexOptions.CultureInvariant);
            if (!statement.Success) throw new InvalidOperationException("Unrecognized additive column migration statement.");
            string table = statement.Groups[1].Value;
            string name = statement.Groups[2].Value;
            string declaration = statement.Groups[3].Value;
            if (provider != DatabaseTypeEnum.Sqlite)
            {
                await ServerSchemaPrerequisites.EnsureColumnAsync(connection, transaction, provider,
                    table, name, declaration, token).ConfigureAwait(false);
                return;
            }

            bool exists = false;
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT name, type, \"notnull\", dflt_value, hidden, pk FROM pragma_table_xinfo(@table);";
                DbParameter tableParameter = command.CreateParameter();
                tableParameter.ParameterName = "@table"; tableParameter.Value = table;
                command.Parameters.Add(tableParameter);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (!String.Equals((string)reader["name"], name, StringComparison.OrdinalIgnoreCase)) continue;
                        exists = true;
                        string expectedType = declaration.Split(' ')[0];
                        bool notNull = declaration.Contains("NOT NULL", StringComparison.OrdinalIgnoreCase);
                        int defaultIndex = declaration.IndexOf(" DEFAULT ", StringComparison.Ordinal);
                        string expectedDefault = defaultIndex < 0 ? "" : declaration.Substring(defaultIndex + 9);
                        string actualDefault = reader["dflt_value"] == DBNull.Value ? "" : Convert.ToString(reader["dflt_value"])!;
                        while (actualDefault.StartsWith("(") && actualDefault.EndsWith(")"))
                            actualDefault = actualDefault.Substring(1, actualDefault.Length - 2).Trim();
                        if (actualDefault.Equals("NULL", StringComparison.OrdinalIgnoreCase)) actualDefault = "";
                        if (expectedDefault.Equals("NULL", StringComparison.OrdinalIgnoreCase)) expectedDefault = "";
                        if (!String.Equals((string)reader["type"], expectedType, StringComparison.OrdinalIgnoreCase)
                            || Convert.ToInt32(reader["notnull"]) != (notNull ? 1 : 0) || Convert.ToInt32(reader["hidden"]) != 0
                            || Convert.ToInt32(reader["pk"]) != 0 || actualDefault != expectedDefault)
                            throw new InvalidOperationException("Incompatible existing column " + table + "." + name);
                    }
                }
            }
            if (exists) return;
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
    }
}
