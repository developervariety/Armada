namespace Armada.Test.Unit.Suites.Database
{
    using System.Text.RegularExpressions;
    using Armada.Core.Database;
    using Armada.Test.Common;
    using PostgresTableQueries = Armada.Core.Database.Postgresql.Queries.TableQueries;

    /// <summary>
    /// The Npgsql layer binds a .NET bool for every boolean-valued column, so a column declared
    /// INTEGER rejects the insert with "column is of type integer but expression is of type
    /// boolean" and the whole feature is unusable. Two upstream-ported tables carried the SQLite
    /// INTEGER idiom into the PostgreSQL DDL; this scans every CREATE TABLE column across every
    /// migration rather than the two that prompted the test, because one table's guard cannot see
    /// the next port that repeats the mistake.
    /// </summary>
    public class PostgresBooleanColumnTypeTests : TestSuite
    {
        public override string Name => "Postgres Boolean Column Types";

        private static readonly Regex _ColumnPattern = new Regex(
            @"^\s*(?<name>(?:is_|has_|allow_|enable_|require_)\w+|active)\s+(?<type>[A-Za-z]+)",
            RegexOptions.Compiled);

        protected override async Task RunTestsAsync()
        {
            await RunTest("Every boolean-named Postgres column is declared BOOLEAN", () =>
            {
                List<string> offenders = new List<string>();

                foreach (SchemaMigration migration in PostgresTableQueries.GetMigrations())
                {
                    foreach (string statement in migration.Statements)
                    {
                        if (statement.IndexOf("CREATE TABLE", StringComparison.OrdinalIgnoreCase) < 0) continue;

                        string table = "unknown";
                        foreach (string line in statement.Split('\n'))
                        {
                            Match tableMatch = Regex.Match(line, @"CREATE TABLE(?: IF NOT EXISTS)?\s+(?<t>\w+)",
                                RegexOptions.IgnoreCase);
                            if (tableMatch.Success)
                            {
                                table = tableMatch.Groups["t"].Value;
                                continue;
                            }

                            Match column = _ColumnPattern.Match(line);
                            if (!column.Success) continue;
                            if (String.Equals(column.Groups["type"].Value, "BOOLEAN", StringComparison.OrdinalIgnoreCase)) continue;

                            offenders.Add("v" + migration.Version + " " + table + "." + column.Groups["name"].Value
                                + " is " + column.Groups["type"].Value);
                        }
                    }
                }

                AssertEqual(String.Empty, String.Join("; ", offenders));
            });

            await RunTest("Migration 75 converts the ported INTEGER boolean columns", () =>
            {
                SchemaMigration? migration = PostgresTableQueries.GetMigrations()
                    .FirstOrDefault(item => item.Version == 75);
                AssertTrue(migration != null);

                string sql = String.Join("\n", migration!.Statements);
                AssertTrue(sql.Contains("'skills'"));
                AssertTrue(sql.Contains("'project_profiles'"));
                AssertTrue(sql.Contains("data_type <> 'boolean'"));
            });
        }
    }
}
