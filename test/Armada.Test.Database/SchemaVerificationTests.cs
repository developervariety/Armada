namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Microsoft.Data.Sqlite;
    using Microsoft.Data.SqlClient;
    using MySqlConnector;
    using Npgsql;

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
            DatabaseAssert.True(await GetMaxSchemaVersionAsync(conn, token).ConfigureAwait(false) >= GetExpectedMinimumSchemaVersion(), "Expected schema version >= backlog baseline");

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
                "idx_objective_refinement_messages_session_sequence"
            })
            {
                DatabaseAssert.True(await IndexExistsAsync(conn, indexName, token).ConfigureAwait(false), "Missing index " + indexName);
            }
        }

        private long GetExpectedMinimumSchemaVersion()
        {
            return _Settings.Type == DatabaseTypeEnum.Sqlite ? 43 : 42;
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
    }
}
