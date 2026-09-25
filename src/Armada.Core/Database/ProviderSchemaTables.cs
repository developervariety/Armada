namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;

    /// <summary>
    /// The tables a provider's own schema statements create: its migrations, its fork operational prerequisites and
    /// its migration and repair ledgers. A table a database holds that none of these statements creates, such as an
    /// operator's backup copy, is not part of the provider's schema, whatever its name.
    /// </summary>
    internal static class ProviderSchemaTables
    {
        #region Private-Members

        private static readonly Regex _CreateTable = new Regex(@"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:\[?dbo\]?\.)?[`\[""]?(\w+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        #endregion

        #region Internal-Methods

        /// <summary>
        /// The tables the provider's schema statements create.
        /// </summary>
        /// <param name="provider">Provider.</param>
        /// <returns>Table names, compared without regard to case.</returns>
        internal static HashSet<string> For(DatabaseTypeEnum provider)
        {
            HashSet<string> tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string statement in Statements(provider))
            {
                foreach (Match match in _CreateTable.Matches(statement)) tables.Add(match.Groups[1].Value);
            }

            return tables;
        }

        #endregion

        #region Private-Methods

        private static IEnumerable<string> Statements(DatabaseTypeEnum provider)
        {
            List<SchemaMigration> migrations;
            List<string> statements = new List<string>();
            switch (provider)
            {
                case DatabaseTypeEnum.Sqlite:
                    migrations = Sqlite.Queries.TableQueries.GetMigrations();
                    statements.Add(Sqlite.SqliteDatabaseDriver.SchemaMigrationsTable);
                    break;
                case DatabaseTypeEnum.Postgresql:
                    migrations = Postgresql.Queries.TableQueries.GetMigrations();
                    statements.Add(Postgresql.PostgresqlDatabaseDriver.SchemaMigrationsTable);
                    statements.Add(ServerSchemaPrerequisites.PostgresqlSchemaRepairsTable);
                    statements.AddRange(Postgresql.Queries.ForkOperationalSchema.Statements);
                    break;
                case DatabaseTypeEnum.Mysql:
                    migrations = Mysql.MysqlDatabaseDriver.GetMigrations();
                    statements.Add(Mysql.Queries.TableQueries.SchemaMigrations);
                    statements.Add(Mysql.MysqlMigrationRunner.SchemaMigrationStatementsTable);
                    statements.Add(Mysql.MysqlMigrationRunner.SchemaRepairsTable);
                    statements.AddRange(Mysql.Queries.ForkOperationalSchema.Statements);
                    break;
                case DatabaseTypeEnum.SqlServer:
                    migrations = SqlServer.Queries.TableQueries.GetMigrations();
                    statements.Add(SqlServer.Queries.TableQueries.SchemaMigrations);
                    statements.Add(ServerSchemaPrerequisites.SqlServerSchemaRepairsTable);
                    statements.AddRange(SqlServer.Queries.ForkOperationalSchema.Statements);
                    break;
                default:
                    throw new NotSupportedException("No schema statements for " + provider + ".");
            }

            foreach (SchemaMigration migration in migrations) statements.AddRange(migration.Statements);
            return statements;
        }

        #endregion
    }
}
