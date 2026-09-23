namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Microsoft.Data.Sqlite;
    using Microsoft.Data.SqlClient;
    using MySqlConnector;
    using Npgsql;
    using SyslogLogging;

    internal class SchemaVerificationTests
    {
        private readonly DatabaseSettings _Settings;

        public SchemaVerificationTests(DatabaseSettings settings)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public async Task VerifyAsync(CancellationToken token = default)
        {
            await using DbConnection conn = CreateConnection();
            await conn.OpenAsync(token).ConfigureAwait(false);

            DatabaseAssert.True(await TableExistsAsync(conn, "schema_migrations", token).ConfigureAwait(false), "schema_migrations table missing");
            DatabaseAssert.True(await GetMaxSchemaVersionAsync(conn, token).ConfigureAwait(false) >= GetExpectedMinimumSchemaVersion(), "Expected schema version >= preserved fork baseline");

            DatabaseAssert.True(await TableExistsAsync(conn, "releases", token).ConfigureAwait(false), "releases table missing");
            DatabaseAssert.True(await TableExistsAsync(conn, "environments", token).ConfigureAwait(false), "environments table missing");
            DatabaseAssert.True(await TableExistsAsync(conn, "deployments", token).ConfigureAwait(false), "deployments table missing");
            DatabaseAssert.True(await TableExistsAsync(conn, "workflow_profiles", token).ConfigureAwait(false), "workflow_profiles table missing");
            DatabaseAssert.True(await TableExistsAsync(conn, "check_runs", token).ConfigureAwait(false), "check_runs table missing");
            DatabaseAssert.True(await TableExistsAsync(conn, "objectives", token).ConfigureAwait(false), "objectives table missing");
            DatabaseAssert.True(await TableExistsAsync(conn, "objective_refinement_sessions", token).ConfigureAwait(false), "objective_refinement_sessions table missing");
            DatabaseAssert.True(await TableExistsAsync(conn, "objective_refinement_messages", token).ConfigureAwait(false), "objective_refinement_messages table missing");

            await AssertColumnAsync(conn, "tenants", "is_protected", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "users", "is_protected", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "users", "is_tenant_admin", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "credentials", "is_protected", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "captains", "model", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "missions", "total_runtime_ms", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "vessels", "github_token_override", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "releases", "tenant_id", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "releases", "user_id", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "releases", "vessel_id", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "releases", "workflow_profile_id", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "releases", "status", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "workflow_profiles", "environments_json", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "workflow_profiles", "deployment_verification_command", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "workflow_profiles", "rollback_verification_command", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "check_runs", "deployment_id", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "environments", "tenant_id", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "environments", "user_id", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "environments", "vessel_id", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "environments", "kind", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "environments", "name", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "environments", "verification_definitions_json", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "environments", "rollout_monitoring_window_minutes", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "environments", "rollout_monitoring_interval_seconds", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "environments", "alert_on_regression", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "deployments", "tenant_id", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "deployments", "user_id", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "deployments", "vessel_id", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "deployments", "workflow_profile_id", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "deployments", "environment_id", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "deployments", "environment_name", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "deployments", "status", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "deployments", "verification_status", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "deployments", "monitoring_window_ends_utc", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "deployments", "last_monitored_utc", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "deployments", "last_regression_alert_utc", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "deployments", "latest_monitoring_summary", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "deployments", "monitoring_failure_count", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "objectives", "backlog_state", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "objectives", "rank", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "objectives", "deployment_ids_json", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "objectives", "incident_ids_json", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "objective_refinement_sessions", "captain_id", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "objective_refinement_sessions", "status", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "objective_refinement_messages", "sequence", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "objective_refinement_messages", "is_selected", token).ConfigureAwait(false);

            foreach (string column in new[] { "mission_assignment_state", "stage_order", "mission_mode", "start_from_ref",
                "retry_skip_captain_ids", "recovery_attempts", "landing_retry_count", "last_recovery_action_utc",
                "prestaged_files", "preferred_model", "capabilityhint", "requires_review", "process_id",
                "reconciled_utc", "reconciled_reason", "held_for_operator_review", "held_for_operator_review_reason" })
            {
                await AssertColumnAsync(conn, "missions", column, token).ConfigureAwait(false);
            }
            await AssertColumnAsync(conn, "objectives", "preparation_json", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "objectives", "auto_dispatch_enabled", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "captains", "quarantine_until_utc", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "captains", "quarantine_reason", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "captains", "last_process_alive_utc", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "captains", "preference_rank", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "personas", "specialist", token).ConfigureAwait(false);
            await AssertColumnAsync(conn, "personas", "minimum_tier", token).ConfigureAwait(false);
            DatabaseAssert.True(await TableExistsAsync(conn, "coordination_leases", token).ConfigureAwait(false), "coordination_leases table missing");
            DatabaseAssert.True(await TableExistsAsync(conn, "judge_follow_ups", token).ConfigureAwait(false), "judge_follow_ups table missing");
            DatabaseAssert.True(await TableExistsAsync(conn, "memories", token).ConfigureAwait(false), "memories table missing");
            DatabaseAssert.True(await TableExistsAsync(conn, "memory_tags", token).ConfigureAwait(false), "memory_tags table missing");
            DatabaseAssert.True(!await TableExistsAsync(conn, "jobs", token).ConfigureAwait(false), "jobs table is absent");
            DatabaseAssert.True(!await IndexExistsAsync(conn, "idx_jobs_created", token).ConfigureAwait(false), "idx_jobs_created index is absent");

            foreach (string table in new[] { "fleets", "vessels", "captains", "voyages", "missions", "docks", "signals", "events", "merge_entries" })
            {
                await AssertColumnAsync(conn, table, "tenant_id", token).ConfigureAwait(false);
                await AssertColumnAsync(conn, table, "user_id", token).ConfigureAwait(false);
            }

            foreach (string indexName in new[] {
                "idx_users_tenant_email",
                "idx_credentials_tenant_user",
                "idx_fleets_tenant_user",
                "idx_vessels_tenant_user",
                "idx_missions_tenant_user",
                "idx_signals_tenant_user",
                "idx_events_tenant_user",
                "idx_merge_entries_tenant_user",
                "idx_check_runs_deployment_created",
                "idx_deployments_tenant_created",
                "idx_deployments_status_created",
                "idx_objectives_tenant_backlog_priority_rank",
                "idx_objective_refinement_sessions_tenant_objective_created",
                "idx_objective_refinement_messages_session_sequence",
                "ux_memories_tenant_key",
                "idx_memories_tenant_user",
                "idx_request_history_tenant_created",
                "idx_request_history_user_created",
                "idx_request_history_route_created"
            })
            {
                DatabaseAssert.True(await IndexExistsAsync(conn, indexName, token).ConfigureAwait(false), "Missing index " + indexName);
            }

            if (_Settings.Type == DatabaseTypeEnum.Postgresql)
                DatabaseAssert.True(await IndexExistsAsync(conn, "idx_request_history_created", token).ConfigureAwait(false), "Missing index idx_request_history_created");

            // SQLite and PostgreSQL store planning sessions; MySQL and SQL Server refuse them.
            if (_Settings.Type == DatabaseTypeEnum.Sqlite || _Settings.Type == DatabaseTypeEnum.Postgresql)
            {
                DatabaseAssert.True(await TableExistsAsync(conn, "planning_sessions", token).ConfigureAwait(false), "planning_sessions table missing");
                DatabaseAssert.True(await TableExistsAsync(conn, "planning_session_messages", token).ConfigureAwait(false), "planning_session_messages table missing");
                await AssertColumnAsync(conn, "planning_sessions", "objective_id", token).ConfigureAwait(false);
                await AssertColumnAsync(conn, "planning_session_messages", "is_selected_for_dispatch", token).ConfigureAwait(false);
                foreach (string indexName in new[] { "idx_planning_sessions_tenant_user", "idx_planning_sessions_captain", "idx_planning_sessions_status",
                    "idx_planning_sessions_last_update", "idx_planning_session_messages_session", "idx_planning_session_messages_session_sequence" })
                {
                    DatabaseAssert.True(await IndexExistsAsync(conn, indexName, token).ConfigureAwait(false), "Missing index " + indexName);
                }
            }
        }

        /// <summary>
        /// An upgraded database that still holds the <c>jobs</c> table, with a row in it, loses the table and its index
        /// when the drop migration runs. The table is recreated with its original creation statements, the ledger is
        /// rewound to just below the drop version, and startup stops right after the drop commits, so no later
        /// migration runs twice. The ledger rows above the drop are then restored exactly.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        public async Task VerifyJobsTableDroppedOnUpgradeAsync(CancellationToken token = default)
        {
            int dropVersion = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => 106, DatabaseTypeEnum.Postgresql => 109,
                DatabaseTypeEnum.Mysql => 98, DatabaseTypeEnum.SqlServer => 101,
                _ => throw new NotSupportedException("Unsupported database provider")
            };

            MigrationScenarioRunner history = new MigrationScenarioRunner(_Settings);
            Dictionary<int, string> installed = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            List<LedgerRow> later = new List<LedgerRow>();

            using (DbConnection conn = CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                foreach (string statement in JobsTableCreationStatements())
                    await ExecuteAsync(conn, statement, token).ConfigureAwait(false);
                await ExecuteAsync(conn, "INSERT INTO jobs (id, tenant_id, user_id, name, kind, status, progress, created_utc, last_update_utc) "
                    + "VALUES ('job_upgrade_example', NULL, NULL, 'held row', 'Example', 'Running', 0, '2026-01-02 03:04:05', '2026-01-02 03:04:05');", token).ConfigureAwait(false);
                DatabaseAssert.Equal(1L, await ScalarCountAsync(conn, "SELECT COUNT(*) FROM jobs WHERE id = @id;", new KeyValuePair<string, object>("@id", "job_upgrade_example"), token).ConfigureAwait(false), "The upgraded database holds a jobs row");
                DatabaseAssert.True(await IndexExistsAsync(conn, "idx_jobs_created", token).ConfigureAwait(false), "The upgraded database holds idx_jobs_created");

                using (DbCommand read = conn.CreateCommand())
                {
                    read.CommandText = "SELECT version, description, applied_utc FROM schema_migrations WHERE version > " + dropVersion + " ORDER BY version;";
                    using (DbDataReader reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            later.Add(new LedgerRow { Version = Convert.ToInt32(reader.GetValue(0)), Description = reader.GetValue(1), AppliedUtc = reader.GetValue(2) });
                    }
                }
                await ExecuteAsync(conn, "DELETE FROM schema_migrations WHERE version >= " + dropVersion + ";", token).ConfigureAwait(false);
            }

            try
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                using (DatabaseDriver upgrade = DatabaseDriverFactory.Create(_Settings, logging))
                {
                    upgrade.MigrationCheckpoint = (version, ordinal) =>
                    {
                        if (version == dropVersion && ordinal == -2) throw new StopAfterDropException();
                    };
                    try { await upgrade.InitializeAsync(token).ConfigureAwait(false); }
                    catch (StopAfterDropException) { }
                }
            }
            finally
            {
                await RestoreLedgerRowsAsync(later, token).ConfigureAwait(false);
            }

            using (DbConnection conn = CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                DatabaseAssert.True(!await TableExistsAsync(conn, "jobs", token).ConfigureAwait(false), "jobs table is absent after the upgrade");
                DatabaseAssert.True(!await IndexExistsAsync(conn, "idx_jobs_created", token).ConfigureAwait(false), "idx_jobs_created index is absent after the upgrade");
            }

            Dictionary<int, string> upgraded = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.True(upgraded.ContainsKey(dropVersion), "The upgrade records the jobs table drop version");
            DatabaseAssert.Equal(installed.Count, upgraded.Count, "The upgraded ledger holds every installed version");
            // The drop version is applied again, so only its own row carries a new timestamp.
            MigrationScenarioRunner.AssertHistory(installed.Where(row => row.Key != dropVersion).ToDictionary(row => row.Key, row => row.Value), upgraded);
        }

        private string[] JobsTableCreationStatements()
        {
            switch (_Settings.Type)
            {
                case DatabaseTypeEnum.Sqlite:
                    return Armada.Core.Database.Sqlite.Queries.TableQueries.GetMigrations().Single(migration => migration.Version == 66).Statements.ToArray();
                case DatabaseTypeEnum.Postgresql:
                    return Armada.Core.Database.Postgresql.Queries.TableQueries.GetMigrations().Single(migration => migration.Version == 66).Statements.ToArray();
                case DatabaseTypeEnum.Mysql:
                    return Armada.Core.Database.Mysql.Queries.TableQueries.MigrationV65Statements;
                case DatabaseTypeEnum.SqlServer:
                    return Armada.Core.Database.SqlServer.Queries.TableQueries.GetMigrations().Single(migration => migration.Version == 66).Statements.ToArray();
                default:
                    throw new NotSupportedException("Unsupported database provider");
            }
        }

        private async Task RestoreLedgerRowsAsync(List<LedgerRow> rows, CancellationToken token)
        {
            using (DbConnection conn = CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                foreach (LedgerRow row in rows)
                {
                    long present = await ScalarCountAsync(conn, "SELECT COUNT(*) FROM schema_migrations WHERE version = @v;", new KeyValuePair<string, object>("@v", row.Version), token).ConfigureAwait(false);
                    if (present > 0) continue;
                    using (DbCommand insert = conn.CreateCommand())
                    {
                        insert.CommandText = "INSERT INTO schema_migrations (version, description, applied_utc) VALUES (@v, @d, @t);";
                        foreach (KeyValuePair<string, object> parameter in new[]
                        {
                            new KeyValuePair<string, object>("@v", row.Version),
                            new KeyValuePair<string, object>("@d", row.Description),
                            new KeyValuePair<string, object>("@t", row.AppliedUtc)
                        })
                        {
                            DbParameter dbParameter = insert.CreateParameter();
                            dbParameter.ParameterName = parameter.Key;
                            dbParameter.Value = parameter.Value;
                            insert.Parameters.Add(dbParameter);
                        }
                        await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
            }
        }

        private static async Task ExecuteAsync(DbConnection conn, string sql, CancellationToken token)
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        private long GetExpectedMinimumSchemaVersion()
        {
            switch (_Settings.Type)
            {
                case DatabaseTypeEnum.Sqlite: return 82;
                case DatabaseTypeEnum.Postgresql: return 83;
                case DatabaseTypeEnum.Mysql: return 74;
                case DatabaseTypeEnum.SqlServer: return 77;
                default: throw new NotSupportedException("Unsupported database provider");
            }
        }

        private DbConnection CreateConnection()
        {
            switch (_Settings.Type)
            {
                case DatabaseTypeEnum.Sqlite:
                    return new SqliteConnection(_Settings.GetConnectionString());
                case DatabaseTypeEnum.Postgresql:
                    return new NpgsqlConnection(_Settings.GetConnectionString());
                case DatabaseTypeEnum.Mysql:
                    return new MySqlConnection(_Settings.GetConnectionString());
                case DatabaseTypeEnum.SqlServer:
                    return new SqlConnection(_Settings.GetConnectionString());
                default:
                    throw new NotSupportedException("Unsupported database type: " + _Settings.Type.ToString());
            }
        }

        private async Task AssertColumnAsync(DbConnection conn, string tableName, string columnName, CancellationToken token)
        {
            DatabaseAssert.True(await ColumnExistsAsync(conn, tableName, columnName, token).ConfigureAwait(false), tableName + "." + columnName + " missing");
        }

        private async Task<bool> TableExistsAsync(DbConnection conn, string tableName, CancellationToken token)
        {
            switch (_Settings.Type)
            {
                case DatabaseTypeEnum.Sqlite:
                    return await ScalarCountAsync(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;", new KeyValuePair<string, object>("@name", tableName), token).ConfigureAwait(false) > 0;
                case DatabaseTypeEnum.Postgresql:
                    return await ScalarCountAsync(conn, "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = CURRENT_SCHEMA() AND table_name = @name;", new KeyValuePair<string, object>("@name", tableName), token).ConfigureAwait(false) > 0;
                case DatabaseTypeEnum.Mysql:
                    return await ScalarCountAsync(conn, "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @name;", new KeyValuePair<string, object>("@name", tableName), token).ConfigureAwait(false) > 0;
                case DatabaseTypeEnum.SqlServer:
                    return await ScalarCountAsync(conn, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @name;", new KeyValuePair<string, object>("@name", tableName), token).ConfigureAwait(false) > 0;
                default:
                    return false;
            }
        }

        private async Task<bool> ColumnExistsAsync(DbConnection conn, string tableName, string columnName, CancellationToken token)
        {
            switch (_Settings.Type)
            {
                case DatabaseTypeEnum.Sqlite:
                    using (DbCommand cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA table_info(" + tableName + ");";
                        using (DbDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                        {
                            while (await reader.ReadAsync(token).ConfigureAwait(false))
                            {
                                if (String.Equals(reader["name"].ToString(), columnName, StringComparison.OrdinalIgnoreCase))
                                    return true;
                            }
                        }
                    }
                    return false;
                case DatabaseTypeEnum.Postgresql:
                    return await ScalarCountAsync(conn, "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = CURRENT_SCHEMA() AND table_name = @table AND column_name = @column;", new KeyValuePair<string, object>("@table", tableName), new KeyValuePair<string, object>("@column", columnName), token).ConfigureAwait(false) > 0;
                case DatabaseTypeEnum.Mysql:
                    return await ScalarCountAsync(conn, "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @table AND column_name = @column;", new KeyValuePair<string, object>("@table", tableName), new KeyValuePair<string, object>("@column", columnName), token).ConfigureAwait(false) > 0;
                case DatabaseTypeEnum.SqlServer:
                    return await ScalarCountAsync(conn, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @table AND COLUMN_NAME = @column;", new KeyValuePair<string, object>("@table", tableName), new KeyValuePair<string, object>("@column", columnName), token).ConfigureAwait(false) > 0;
                default:
                    return false;
            }
        }

        private async Task<bool> IndexExistsAsync(DbConnection conn, string indexName, CancellationToken token)
        {
            switch (_Settings.Type)
            {
                case DatabaseTypeEnum.Sqlite:
                    return await ScalarCountAsync(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = @name;", new KeyValuePair<string, object>("@name", indexName), token).ConfigureAwait(false) > 0;
                case DatabaseTypeEnum.Postgresql:
                    return await ScalarCountAsync(conn, "SELECT COUNT(*) FROM pg_indexes WHERE schemaname = CURRENT_SCHEMA() AND indexname = @name;", new KeyValuePair<string, object>("@name", indexName), token).ConfigureAwait(false) > 0;
                case DatabaseTypeEnum.Mysql:
                    return await ScalarCountAsync(conn, "SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND index_name = @name;", new KeyValuePair<string, object>("@name", indexName), token).ConfigureAwait(false) > 0;
                case DatabaseTypeEnum.SqlServer:
                    return await ScalarCountAsync(conn, "SELECT COUNT(*) FROM sys.indexes WHERE name = @name;", new KeyValuePair<string, object>("@name", indexName), token).ConfigureAwait(false) > 0;
                default:
                    return false;
            }
        }

        private async Task<long> GetMaxSchemaVersionAsync(DbConnection conn, CancellationToken token)
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
                object? result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                return Convert.ToInt64(result);
            }
        }

        private async Task<long> ScalarCountAsync(DbConnection conn, string sql, KeyValuePair<string, object> parameter, CancellationToken token)
        {
            return await ScalarCountAsync(conn, sql, new[] { parameter }, token).ConfigureAwait(false);
        }

        private async Task<long> ScalarCountAsync(DbConnection conn, string sql, KeyValuePair<string, object> parameter1, KeyValuePair<string, object> parameter2, CancellationToken token)
        {
            return await ScalarCountAsync(conn, sql, new[] { parameter1, parameter2 }, token).ConfigureAwait(false);
        }

        private async Task<long> ScalarCountAsync(DbConnection conn, string sql, IEnumerable<KeyValuePair<string, object>> parameters, CancellationToken token)
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                foreach (KeyValuePair<string, object> parameter in parameters)
                {
                    DbParameter dbParameter = cmd.CreateParameter();
                    dbParameter.ParameterName = parameter.Key;
                    dbParameter.Value = parameter.Value;
                    cmd.Parameters.Add(dbParameter);
                }

                object? result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                return Convert.ToInt64(result);
            }
        }

        private sealed class StopAfterDropException : Exception { }

        private sealed class LedgerRow
        {
            internal int Version { get; set; }
            internal object Description { get; set; } = "";
            internal object AppliedUtc { get; set; } = "";
        }
    }
}
