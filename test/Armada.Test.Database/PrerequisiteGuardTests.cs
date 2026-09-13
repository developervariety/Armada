namespace Armada.Test.Database
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Settings;

    /// <summary>Destructive catalog fixtures, called only after the empty-database scenario guard.</summary>
    internal sealed class PrerequisiteGuardTests
    {
        private readonly DatabaseSettings _Settings;
        internal PrerequisiteGuardTests(DatabaseSettings settings) { _Settings = settings; }

        internal async Task VerifyAsync(CancellationToken token)
        {
            if (_Settings.Type == DatabaseTypeEnum.Sqlite)
                throw new InvalidOperationException("Server prerequisite fixtures require a server provider");
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                bool pg = _Settings.Type == DatabaseTypeEnum.Postgresql;
                bool mysql = _Settings.Type == DatabaseTypeEnum.Mysql;
                string wrongType = pg ? "ALTER TABLE workflow_profiles ALTER COLUMN lint_command TYPE INTEGER USING NULL::integer;"
                    : mysql ? "ALTER TABLE workflow_profiles MODIFY COLUMN lint_command INT NULL;"
                    : "ALTER TABLE workflow_profiles ALTER COLUMN lint_command INT NULL;";
                string restoreType = pg ? "ALTER TABLE workflow_profiles ALTER COLUMN lint_command TYPE TEXT;"
                    : mysql ? "ALTER TABLE workflow_profiles MODIFY COLUMN lint_command LONGTEXT NULL;"
                    : "ALTER TABLE workflow_profiles ALTER COLUMN lint_command NVARCHAR(MAX) NULL;";
                await RejectAsync(connection, wrongType, restoreType, "column type", token).ConfigureAwait(false);
                string wrongNull = pg ? "ALTER TABLE workflow_profiles ALTER COLUMN name DROP NOT NULL;"
                    : mysql ? "ALTER TABLE workflow_profiles MODIFY COLUMN name LONGTEXT NULL;"
                    : "ALTER TABLE workflow_profiles ALTER COLUMN name NVARCHAR(MAX) NULL;";
                string restoreNull = pg ? "ALTER TABLE workflow_profiles ALTER COLUMN name SET NOT NULL;"
                    : mysql ? "ALTER TABLE workflow_profiles MODIFY COLUMN name LONGTEXT NOT NULL;"
                    : "ALTER TABLE workflow_profiles ALTER COLUMN name NVARCHAR(MAX) NOT NULL;";
                await RejectAsync(connection, wrongNull, restoreNull, "column nullability", token).ConfigureAwait(false);
                string wrongDefault = pg ? "ALTER TABLE workflow_profiles ALTER COLUMN active SET DEFAULT FALSE;"
                    : mysql ? "ALTER TABLE workflow_profiles ALTER COLUMN active SET DEFAULT 0;"
                    : SqlServerDefault("0");
                string restoreDefault = pg ? "ALTER TABLE workflow_profiles ALTER COLUMN active SET DEFAULT TRUE;"
                    : mysql ? "ALTER TABLE workflow_profiles ALTER COLUMN active SET DEFAULT 1;"
                    : SqlServerDefault("1");
                await RejectAsync(connection, wrongDefault, restoreDefault, "column default", token).ConfigureAwait(false);
                string dropIndex = pg ? "DROP INDEX idx_workflow_profiles_fleet;" : "DROP INDEX idx_workflow_profiles_fleet ON workflow_profiles;";
                string badIndex = "CREATE INDEX idx_workflow_profiles_fleet ON workflow_profiles(" + (mysql ? "vessel_id" : "fleet_id") + ")"
                    + (pg ? " WHERE FALSE;" : mysql ? ";" : " WHERE fleet_id IS NOT NULL;");
                string goodIndex = "CREATE INDEX idx_workflow_profiles_fleet ON workflow_profiles(fleet_id);";
                await RejectAsync(connection, dropIndex + badIndex, dropIndex + goodIndex, "index semantics", token).ConfigureAwait(false);
                if (_Settings.Type == DatabaseTypeEnum.SqlServer)
                {
                    string primaryPrefix = "DECLARE @index sysname; SELECT @index=name FROM sys.indexes WHERE object_id=OBJECT_ID('workflow_profiles') AND is_primary_key=1; DECLARE @ddl nvarchar(max)='ALTER INDEX '+QUOTENAME(@index)+' ON workflow_profiles ";
                    await RejectAsync(connection, primaryPrefix + "DISABLE'; EXEC(@ddl);", "ALTER INDEX ALL ON workflow_profiles REBUILD;", "disabled primary key", token).ConfigureAwait(false);
                    await RejectAsync(connection, "ALTER TABLE objectives NOCHECK CONSTRAINT FK_objectives_tenant;",
                        "ALTER TABLE objectives WITH CHECK CHECK CONSTRAINT FK_objectives_tenant;", "untrusted foreign key", token).ConfigureAwait(false);
                }
                if (pg)
                    await RejectAsync(connection, "ALTER TABLE objectives DISABLE TRIGGER ALL;", "ALTER TABLE objectives ENABLE TRIGGER ALL;", "disabled foreign key enforcement", token).ConfigureAwait(false);
                if (pg)
                {
                    // An older manually provisioned schema stored UTC values without a zone.
                    // Conversion must not reinterpret those values in the server's local zone.
                    await ExecuteAsync(connection, "INSERT INTO workflow_profiles(id,name,created_utc,last_update_utc) VALUES ('historical-profile','historical UTC','2024-02-03T04:05:06.123456Z','2024-02-03T04:05:06.123456Z'); ALTER TABLE workflow_profiles ALTER COLUMN created_utc TYPE TIMESTAMP USING created_utc AT TIME ZONE 'UTC'; ALTER TABLE workflow_profiles ALTER COLUMN last_update_utc TYPE TIMESTAMP USING last_update_utc AT TIME ZONE 'UTC';", token).ConfigureAwait(false);
                    await ExecuteAsync(connection, "SET TIME ZONE 'Pacific/Auckland';", token).ConfigureAwait(false);
                    await ServerSchemaPrerequisites.EnsureAsync(connection, DatabaseTypeEnum.Postgresql, token).ConfigureAwait(false);
                    using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false)) { }
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT created_utc FROM workflow_profiles WHERE id='historical-profile';";
                        DateTime actual = (DateTime)await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                        DatabaseAssert.Equal(new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc).AddTicks(1234560), actual, "Historical UTC microseconds");
                    }
                }
            }
        }

        private async Task RejectAsync(DbConnection connection, string damage, string restore, string label, CancellationToken token)
        {
            await ExecuteAsync(connection, damage, token).ConfigureAwait(false);
            try
            {
                bool rejected = false;
                try
                {
                    using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false)) { }
                }
                catch (InvalidOperationException exception) when (exception.Message.StartsWith("Incompatible", StringComparison.Ordinal)) { rejected = true; }
                DatabaseAssert.True(rejected, "Incompatible " + label + " rejected");
            }
            finally { await ExecuteAsync(connection, restore, token).ConfigureAwait(false); }
            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false)) { }
            Console.WriteLine("PASS prerequisite rejection and corrected restart: " + label);
        }

        private static string SqlServerDefault(string value)
        {
            return "DECLARE @constraint sysname; SELECT @constraint=d.name FROM sys.default_constraints d JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE d.parent_object_id=OBJECT_ID('workflow_profiles') AND c.name='active'; IF @constraint IS NOT NULL BEGIN DECLARE @ddl nvarchar(max)='ALTER TABLE workflow_profiles DROP CONSTRAINT '+QUOTENAME(@constraint); EXEC(@ddl); END; ALTER TABLE workflow_profiles ADD DEFAULT " + value + " FOR active;";
        }

        private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
    }
}
