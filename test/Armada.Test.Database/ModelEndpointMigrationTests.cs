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

    /// <summary>Fresh, interrupted, and restarted model endpoint migration proof.</summary>
    internal sealed class ModelEndpointMigrationTests
    {
        private readonly DatabaseSettings _Settings;
        private sealed class StopException : Exception { }

        internal ModelEndpointMigrationTests(DatabaseSettings settings) { _Settings = settings; }

        internal async Task VerifyAsync(CancellationToken token)
        {
            int version = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => 88, DatabaseTypeEnum.Postgresql => 89,
                DatabaseTypeEnum.Mysql => 80, DatabaseTypeEnum.SqlServer => 83,
                _ => throw new NotSupportedException()
            };
            MigrationScenarioRunner scenarioRunner = new MigrationScenarioRunner(_Settings);
            using (DatabaseDriver driver = scenarioRunner.CreateDriver())
            {
                driver.MigrationCheckpoint = (seen, ordinal) =>
                {
                    if (seen == version && ordinal == 0) throw new StopException();
                };
                bool stopped = false;
                try { await driver.InitializeAsync(token).ConfigureAwait(false); }
                catch (StopException) { stopped = true; }
                DatabaseAssert.True(stopped, "Model endpoint migration fault point was reached");
            }
            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                DatabaseAssert.True(await TableExistsAsync(token).ConfigureAwait(false), "Restart creates model endpoint table");
            }
        }

        internal async Task VerifyGuardAsync(CancellationToken token)
        {
            int version = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => 88, DatabaseTypeEnum.Postgresql => 89,
                DatabaseTypeEnum.Mysql => 80, DatabaseTypeEnum.SqlServer => 83,
                _ => throw new NotSupportedException()
            };
            MigrationScenarioRunner scenarioRunner = new MigrationScenarioRunner(_Settings);
            using (DatabaseDriver driver = scenarioRunner.CreateDriver())
            {
                driver.MigrationCheckpoint = (seen, ordinal) =>
                {
                    if (seen == version && ordinal == -1) throw new StopException();
                };
                bool stopped = false;
                try { await driver.InitializeAsync(token).ConfigureAwait(false); }
                catch (StopException) { stopped = true; }
                DatabaseAssert.True(stopped, "Model endpoint pre-apply checkpoint was reached");
            }
            Dictionary<int, string> before = await scenarioRunner.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(version - 1, before.Count == 0 ? 0 : System.Linq.Enumerable.Max(before.Keys), "Model endpoint migration is pending");

            await ExecuteAsync(IncompatibleTableSql(), token).ConfigureAwait(false);
            bool rejected = false;
            try
            {
                using (DatabaseDriver driver = scenarioRunner.CreateDriver())
                    await driver.InitializeAsync(token).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("model_endpoints schema", StringComparison.Ordinal))
            {
                rejected = true;
            }
            DatabaseAssert.True(rejected, "Incompatible model endpoint table is rejected");
            MigrationScenarioRunner.AssertHistory(before, await scenarioRunner.ReadHistoryAsync(token).ConfigureAwait(false));
            await ExecuteAsync("DROP TABLE model_endpoints;", token).ConfigureAwait(false);

            await ExecuteAsync(EnabledDefaultMismatchSql(), token).ConfigureAwait(false);
            await AssertRejectedAsync(scenarioRunner, before, "Enabled model endpoint default mismatch", token).ConfigureAwait(false);
            await ExecuteAsync("DROP TABLE model_endpoints;", token).ConfigureAwait(false);

            await ExecuteAsync(CompatibleTableSql(), token).ConfigureAwait(false);
            await ExecuteAsync(RequiredExtraColumnSql(), token).ConfigureAwait(false);
            await AssertRejectedAsync(scenarioRunner, before, "Unexpected required model endpoint column", token).ConfigureAwait(false);
            await ExecuteAsync("DROP TABLE model_endpoints;", token).ConfigureAwait(false);

            await ExecuteAsync(CompatibleTableSql(), token).ConfigureAwait(false);
            await ExecuteAsync(BadTenantIndexSql(), token).ConfigureAwait(false);
            await AssertRejectedAsync(scenarioRunner, before, "Malformed model endpoint tenant index", token).ConfigureAwait(false);
            await ExecuteAsync("DROP TABLE model_endpoints;", token).ConfigureAwait(false);

            if (_Settings.Type == DatabaseTypeEnum.Sqlite)
            {
                await ExecuteAsync(CompatibleTableSql(), token).ConfigureAwait(false);
                await ExecuteAsync("CREATE INDEX idx_model_endpoints_tenant ON model_endpoints (length(tenant_id));", token).ConfigureAwait(false);
                await AssertRejectedAsync(scenarioRunner, before, "Expression model endpoint tenant index", token).ConfigureAwait(false);
                await ExecuteAsync("DROP TABLE model_endpoints;", token).ConfigureAwait(false);
            }

            await ExecuteAsync(CompatibleTableSql(), token).ConfigureAwait(false);
            using (DatabaseDriver driver = scenarioRunner.CreateDriver())
            {
                driver.MigrationCheckpoint = (seen, ordinal) =>
                {
                    if (seen == version && ordinal == 1) throw new StopException();
                };
                bool stopped = false;
                try { await driver.InitializeAsync(token).ConfigureAwait(false); }
                catch (StopException) { stopped = true; }
                DatabaseAssert.True(stopped, "Model endpoint partial index checkpoint was reached");
            }
            MigrationScenarioRunner.AssertHistory(before, await scenarioRunner.ReadHistoryAsync(token).ConfigureAwait(false));
            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                DatabaseAssert.Equal(version, await driver.GetSchemaVersionAsync(token).ConfigureAwait(false), "Model endpoint partial restart completes migration");
            }
        }

        private async Task AssertRejectedAsync(MigrationScenarioRunner scenarioRunner, Dictionary<int, string> before,
            string label, CancellationToken token)
        {
            bool rejected = false;
            try
            {
                using (DatabaseDriver driver = scenarioRunner.CreateDriver())
                    await driver.InitializeAsync(token).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("model_endpoints schema", StringComparison.Ordinal))
            {
                rejected = true;
            }
            DatabaseAssert.True(rejected, label);
            MigrationScenarioRunner.AssertHistory(before, await scenarioRunner.ReadHistoryAsync(token).ConfigureAwait(false));
        }

        private string CompatibleTableSql()
        {
            return _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => "CREATE TABLE model_endpoints (id TEXT PRIMARY KEY, tenant_id TEXT, user_id TEXT, name TEXT NOT NULL DEFAULT '', kind TEXT NOT NULL, provider TEXT NOT NULL, base_url TEXT NOT NULL DEFAULT '', api_key TEXT, model TEXT, dimensionality INTEGER NOT NULL DEFAULT 0, timeout_ms INTEGER NOT NULL DEFAULT 120000, enabled INTEGER NOT NULL DEFAULT 0, health_status TEXT NOT NULL DEFAULT 'Unknown', last_health_check_utc TEXT, last_health_error TEXT, last_latency_ms INTEGER, health_history_json TEXT, scope TEXT NOT NULL DEFAULT 'TenantWide', created_utc TEXT NOT NULL, last_update_utc TEXT NOT NULL);",
                DatabaseTypeEnum.Postgresql => "CREATE TABLE model_endpoints (id TEXT PRIMARY KEY, tenant_id TEXT, user_id TEXT, name TEXT NOT NULL DEFAULT '', kind TEXT NOT NULL, provider TEXT NOT NULL, base_url TEXT NOT NULL DEFAULT '', api_key TEXT, model TEXT, dimensionality INTEGER NOT NULL DEFAULT 0, timeout_ms INTEGER NOT NULL DEFAULT 120000, enabled BOOLEAN NOT NULL DEFAULT FALSE, health_status TEXT NOT NULL DEFAULT 'Unknown', last_health_check_utc TIMESTAMPTZ NULL, last_health_error TEXT, last_latency_ms INTEGER, health_history_json TEXT, scope TEXT NOT NULL DEFAULT 'TenantWide', created_utc TIMESTAMPTZ NOT NULL, last_update_utc TIMESTAMPTZ NOT NULL);",
                DatabaseTypeEnum.Mysql => "CREATE TABLE model_endpoints (id VARCHAR(450) CHARACTER SET utf8mb4 NOT NULL, tenant_id VARCHAR(450) CHARACTER SET utf8mb4 NULL, user_id VARCHAR(450) CHARACTER SET utf8mb4 NULL, name TEXT NOT NULL, kind VARCHAR(64) NOT NULL, provider VARCHAR(64) NOT NULL, base_url TEXT NOT NULL, api_key TEXT NULL, model TEXT NULL, dimensionality INT NOT NULL DEFAULT 0, timeout_ms INT NOT NULL DEFAULT 120000, enabled TINYINT(1) NOT NULL DEFAULT 0, health_status VARCHAR(64) NOT NULL DEFAULT 'Unknown', last_health_check_utc DATETIME(6) NULL, last_health_error TEXT NULL, last_latency_ms INT NULL, health_history_json LONGTEXT NULL, scope VARCHAR(32) NOT NULL DEFAULT 'TenantWide', created_utc DATETIME(6) NOT NULL, last_update_utc DATETIME(6) NOT NULL, PRIMARY KEY (id));",
                DatabaseTypeEnum.SqlServer => "CREATE TABLE model_endpoints (id NVARCHAR(450) NOT NULL PRIMARY KEY, tenant_id NVARCHAR(450) NULL, user_id NVARCHAR(450) NULL, name NVARCHAR(450) NOT NULL DEFAULT '', kind NVARCHAR(64) NOT NULL, provider NVARCHAR(64) NOT NULL, base_url NVARCHAR(2000) NOT NULL DEFAULT '', api_key NVARCHAR(4000) NULL, model NVARCHAR(450) NULL, dimensionality INT NOT NULL DEFAULT 0, timeout_ms INT NOT NULL DEFAULT 120000, enabled BIT NOT NULL DEFAULT 0, health_status NVARCHAR(64) NOT NULL DEFAULT 'Unknown', last_health_check_utc DATETIME2 NULL, last_health_error NVARCHAR(4000) NULL, last_latency_ms INT NULL, health_history_json NVARCHAR(MAX) NULL, scope NVARCHAR(32) NOT NULL DEFAULT 'TenantWide', created_utc DATETIME2 NOT NULL, last_update_utc DATETIME2 NOT NULL);",
                _ => throw new NotSupportedException()
            };
        }

        private string IncompatibleTableSql()
        {
            return _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => "CREATE TABLE model_endpoints (id INTEGER PRIMARY KEY);",
                DatabaseTypeEnum.Postgresql => "CREATE TABLE model_endpoints (id INTEGER PRIMARY KEY);",
                DatabaseTypeEnum.Mysql => "CREATE TABLE model_endpoints (id INT NOT NULL PRIMARY KEY);",
                DatabaseTypeEnum.SqlServer => "CREATE TABLE model_endpoints (id INT NOT NULL PRIMARY KEY);",
                _ => throw new NotSupportedException()
            };
        }

        private string EnabledDefaultMismatchSql()
        {
            return _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => CompatibleTableSql().Replace("enabled INTEGER NOT NULL DEFAULT 0", "enabled INTEGER NOT NULL DEFAULT 1", StringComparison.Ordinal),
                DatabaseTypeEnum.Postgresql => CompatibleTableSql().Replace("enabled BOOLEAN NOT NULL DEFAULT FALSE", "enabled BOOLEAN NOT NULL DEFAULT TRUE", StringComparison.Ordinal),
                DatabaseTypeEnum.Mysql => CompatibleTableSql().Replace("enabled TINYINT(1) NOT NULL DEFAULT 0", "enabled TINYINT(1) NOT NULL DEFAULT 1", StringComparison.Ordinal),
                DatabaseTypeEnum.SqlServer => CompatibleTableSql().Replace("enabled BIT NOT NULL DEFAULT 0", "enabled BIT NOT NULL DEFAULT 1", StringComparison.Ordinal),
                _ => throw new NotSupportedException()
            };
        }

        private string RequiredExtraColumnSql()
        {
            return _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => "ALTER TABLE model_endpoints ADD COLUMN required_marker TEXT NOT NULL;",
                DatabaseTypeEnum.Postgresql => "ALTER TABLE model_endpoints ADD COLUMN required_marker TEXT NOT NULL;",
                DatabaseTypeEnum.Mysql => "ALTER TABLE model_endpoints ADD COLUMN required_marker TEXT NOT NULL;",
                DatabaseTypeEnum.SqlServer => "ALTER TABLE model_endpoints ADD required_marker NVARCHAR(64) NOT NULL;",
                _ => throw new NotSupportedException()
            };
        }

        private string BadTenantIndexSql()
        {
            return _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => "CREATE INDEX idx_model_endpoints_tenant ON model_endpoints (tenant_id) WHERE tenant_id IS NOT NULL;",
                DatabaseTypeEnum.Postgresql => "CREATE INDEX idx_model_endpoints_tenant ON model_endpoints (tenant_id) WHERE tenant_id IS NOT NULL;",
                DatabaseTypeEnum.Mysql => "CREATE INDEX idx_model_endpoints_tenant ON model_endpoints (name(191));",
                DatabaseTypeEnum.SqlServer => "CREATE INDEX idx_model_endpoints_tenant ON model_endpoints (tenant_id) WHERE tenant_id IS NOT NULL;",
                _ => throw new NotSupportedException()
            };
        }

        private async Task ExecuteAsync(string sql, CancellationToken token)
        {
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        private async Task<bool> TableExistsAsync(CancellationToken token)
        {
            string sql = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='model_endpoints';",
                DatabaseTypeEnum.Postgresql => "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=current_schema() AND table_name='model_endpoints';",
                DatabaseTypeEnum.Mysql => "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='model_endpoints';",
                DatabaseTypeEnum.SqlServer => "SELECT COUNT(*) FROM sys.tables WHERE name='model_endpoints';",
                _ => throw new NotSupportedException()
            };
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) == 1L;
                }
            }
        }
    }
}
