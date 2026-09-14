namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Settings;

    /// <summary>Historical PostgreSQL operational types, rejection and restart proof.</summary>
    internal sealed class PostgresqlLegacySchemaTests
    {
        private readonly DatabaseSettings _Settings;
        internal PostgresqlLegacySchemaTests(DatabaseSettings settings) { _Settings = settings; }

        internal async Task VerifyAsync(CancellationToken token)
        {
            if (_Settings.Type != DatabaseTypeEnum.Postgresql) throw new InvalidOperationException("PostgreSQL fixture only");
            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token)) { }
            MigrationScenarioRunner history = new MigrationScenarioRunner(_Settings);
            Dictionary<int, string> before = await history.ReadHistoryAsync(token);
            // The historical columns are the ones the repair contract names. Later migrations add timestamp
            // columns that were never stored as text, so the proof measures both sets instead of a count.
            SortedSet<string> historicalColumns = ReadRepairColumns();
            SortedSet<string> installedTimestamps;
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token);
                installedTimestamps = await ReadColumnsAsync(connection, "timestamp with time zone", token);
            }
            List<string> missingHistorical = new List<string>();
            foreach (string column in historicalColumns) if (!installedTimestamps.Contains(column)) missingHistorical.Add(column);
            DatabaseAssert.True(historicalColumns.Contains("workflow_profiles.created_utc") && historicalColumns.Contains("workflow_profiles.last_update_utc"),
                "Repair contract names the workflow profile timestamp columns");
            DatabaseAssert.True(missingHistorical.Count == 0, "Installed schema has every repair-contract timestamp column; missing: " + String.Join(", ", missingHistorical));
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token);
                await ExecuteAsync(connection, @"DELETE FROM schema_repairs WHERE id='postgres-operational-types-v1';
                    INSERT INTO workflow_profiles(id,name,created_utc,last_update_utc)
                    VALUES('legacy_profile','日本語','2020-01-02T03:04:05+05:30','2020-01-02T03:04:05+05:30');
                    ALTER TABLE workflow_profiles ALTER COLUMN created_utc TYPE TEXT USING created_utc::text;
                    ALTER TABLE workflow_profiles ALTER COLUMN last_update_utc TYPE TEXT USING last_update_utc::text;
                    ALTER TABLE check_runs ALTER COLUMN duration_ms TYPE INTEGER;
                    ALTER TABLE deployments ALTER COLUMN approval_required DROP DEFAULT;
                    ALTER TABLE deployments ALTER COLUMN approval_required TYPE INTEGER USING CASE WHEN approval_required THEN 1 ELSE 0 END;
                    ALTER TABLE deployments ALTER COLUMN approval_required SET DEFAULT 0;
                    ALTER TABLE objective_refinement_sessions DROP CONSTRAINT objective_refinement_sessions_captain_id_fkey;
                    ALTER TABLE objective_refinement_sessions ADD CONSTRAINT objective_refinement_sessions_captain_id_fkey
                        FOREIGN KEY(captain_id) REFERENCES captains(id) ON DELETE SET NULL;", token);
            }
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token);
                await ExecuteAsync(connection, @"INSERT INTO deployments(id,title,status,verification_status,approval_required,created_utc,last_update_utc)
                    VALUES('legacy_deployment','日本語','Pending','Pending',1,CURRENT_TIMESTAMP,CURRENT_TIMESTAMP);", token);
                foreach (string invalid in new[] { "2020-01-02T03:04:05", "2020-02-31T03:04:05Z", "2020-01-02T24:00:00Z", "2020-01-02T03:04:60Z", "2020-01-02T03:04:05.1234567Z", "infinity" })
                {
                    await ExecuteAsync(connection, "UPDATE workflow_profiles SET last_update_utc='" + invalid + "' WHERE id='legacy_profile';", token);
                    await AssertRejectedAsync(connection, token);
                }
                await ExecuteAsync(connection, "UPDATE workflow_profiles SET last_update_utc='2020-01-01T21:34:05.0000000Z' WHERE id='legacy_profile';", token);
                await ExecuteAsync(connection, "UPDATE deployments SET approval_required=2 WHERE id='legacy_deployment';", token);
                await AssertRejectedAsync(connection, token);
                await ExecuteAsync(connection, "UPDATE deployments SET approval_required=1 WHERE id='legacy_deployment'; ALTER TABLE deployments ALTER COLUMN approval_required SET DEFAULT 1;", token);
                await AssertRejectedAsync(connection, token);
                await ExecuteAsync(connection, "ALTER TABLE deployments ALTER COLUMN approval_required SET DEFAULT 0;", token);
                await ExecuteAsync(connection, @"ALTER TABLE objective_refinement_sessions DROP CONSTRAINT objective_refinement_sessions_captain_id_fkey;
                    ALTER TABLE objective_refinement_sessions ADD CONSTRAINT objective_refinement_sessions_captain_id_fkey
                    FOREIGN KEY(captain_id) REFERENCES captains(id) ON DELETE RESTRICT;", token);
                await AssertRejectedAsync(connection, token);
                await ExecuteAsync(connection, @"ALTER TABLE objective_refinement_sessions DROP CONSTRAINT objective_refinement_sessions_captain_id_fkey;
                    ALTER TABLE objective_refinement_sessions ADD CONSTRAINT objective_refinement_sessions_captain_id_fkey
                    FOREIGN KEY(captain_id) REFERENCES captains(id) ON DELETE SET NULL;", token);

            }
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token);
                foreach (string column in historicalColumns)
                {
                    string[] parts = column.Split('.');
                    await ExecuteAsync(connection, "DO $fixture$ BEGIN IF (SELECT data_type FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='"
                        + parts[0] + "' AND column_name='" + parts[1] + "')='timestamp with time zone' THEN ALTER TABLE " + parts[0] + " ALTER COLUMN " + parts[1]
                        + " TYPE TEXT USING " + parts[1] + "::text; END IF; END $fixture$;", token);
                }
                SortedSet<string> textColumns = await ReadColumnsAsync(connection, "text", token);
                List<string> unconverted = new List<string>();
                foreach (string column in historicalColumns) if (!textColumns.Contains(column)) unconverted.Add(column);
                DatabaseAssert.True(unconverted.Count == 0, "Every repair-contract timestamp column is historical TEXT before repair; not converted: " + String.Join(", ", unconverted));
                SortedSet<string> untouched = new SortedSet<string>(installedTimestamps, StringComparer.Ordinal);
                untouched.ExceptWith(historicalColumns);
                DatabaseAssert.True(untouched.SetEquals(await ReadColumnsAsync(connection, "timestamp with time zone", token)), "Timestamp columns outside the repair contract stay timestamptz");
            }
            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token)) { }
            MigrationScenarioRunner.AssertHistory(before, await history.ReadHistoryAsync(token));
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token);
                SortedSet<string> repaired = await ReadColumnsAsync(connection, "timestamp with time zone", token);
                DatabaseAssert.True(repaired.SetEquals(installedTimestamps), "Startup restores every repair-contract column to timestamptz; expected "
                    + String.Join(", ", installedTimestamps) + " got " + String.Join(", ", repaired));
            }
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM workflow_profiles WHERE id='legacy_profile' AND name='日本語' AND created_utc='2020-01-01T21:34:05Z'::timestamptz;";
                    DatabaseAssert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync(token)), "Historical instant and Unicode content preserved");
                }
            }
            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token)) { }
            Console.WriteLine("PASS historical operational types and repeated startup");
        }

        private async Task AssertRejectedAsync(DbConnection connection, CancellationToken token)
        {
            bool rejected = false;
            try
            {
                using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token)) { }
            }
            catch (DbException) { rejected = true; }
            catch (InvalidOperationException) { rejected = true; }
            DatabaseAssert.True(rejected, "Unsafe historical value or default must be rejected");
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT data_type FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='workflow_profiles' AND column_name='created_utc';";
                DatabaseAssert.Equal("text", (string)(await command.ExecuteScalarAsync(token))!, "Failed repair rolls back earlier conversions");
                command.CommandText = "SELECT data_type FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='check_runs' AND column_name='duration_ms';";
                DatabaseAssert.Equal("integer", (string)(await command.ExecuteScalarAsync(token))!, "Failed repair rolls back widening");
                command.CommandText = "SELECT COUNT(*) FROM schema_repairs WHERE id='postgres-operational-types-v1';";
                DatabaseAssert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(token)), "Failed repair has no accepted ledger entry");
            }
        }

        private static SortedSet<string> ReadRepairColumns()
        {
            string sql = PostgresqlLegacyOperationalRepair.Sql;
            int start = sql.IndexOf("FROM (VALUES", StringComparison.Ordinal);
            int end = sql.IndexOf(") AS known(", StringComparison.Ordinal);
            if (start < 0 || end < start) throw new InvalidOperationException("Repair contract column list not found");
            SortedSet<string> columns = new SortedSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(sql.Substring(start, end - start), @"\('([a-z_]+)','([a-z_]+)'\)"))
                columns.Add(match.Groups[1].Value + "." + match.Groups[2].Value);
            if (columns.Count == 0) throw new InvalidOperationException("Repair contract names no timestamp columns");
            return columns;
        }

        private static async Task<SortedSet<string>> ReadColumnsAsync(DbConnection connection, string dataType, CancellationToken token)
        {
            SortedSet<string> columns = new SortedSet<string>(StringComparer.Ordinal);
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT table_name,column_name FROM information_schema.columns WHERE table_schema=current_schema() AND table_name IN ('workflow_profiles','check_runs','environments','releases','deployments') AND data_type=@type;";
                DbParameter type = command.CreateParameter(); type.ParameterName = "@type"; type.Value = dataType; command.Parameters.Add(type);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token))
                {
                    while (await reader.ReadAsync(token)) columns.Add(reader.GetString(0) + "." + reader.GetString(1));
                }
            }
            return columns;
        }

        private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(token);
            }
        }
    }
}
