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

    /// <summary>Provider-backed malformed Harbor enrollment schema rejection proof.</summary>
    internal sealed class HarborRunnerEnrollmentSchemaGuardTests
    {
        private readonly DatabaseSettings _Settings;
        private sealed class StopException : Exception { }

        internal HarborRunnerEnrollmentSchemaGuardTests(DatabaseSettings settings)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            int version = VersionFor(_Settings.Type);
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
                DatabaseAssert.True(stopped, "Harbor migration pre-apply checkpoint was reached");
            }

            Dictionary<int, string> before = await scenarioRunner.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.True(!before.ContainsKey(version) && (before.Count == 0 || System.Linq.Enumerable.Max(before.Keys) < version),
                "Harbor migration remains pending for schema guard fixture");

            string[] fixtures = new[]
            {
                NullabilityTableSql(_Settings.Type),
                TypeTableSql(_Settings.Type),
                DefaultTableSql(_Settings.Type),
                CompositeKeyTableSql(_Settings.Type)
            };
            string[] fixtureNames = new[] { "nullability", "type", "default", "primary key" };
            for (int fixtureIndex = 0; fixtureIndex < fixtures.Length; fixtureIndex++)
            {
                await ExecuteAsync(fixtures[fixtureIndex], token).ConfigureAwait(false);
                await AssertRejectedAsync(scenarioRunner, before, fixtureNames[fixtureIndex], token).ConfigureAwait(false);
                await ExecuteAsync("DROP TABLE harbor_runner_enrollments;", token).ConfigureAwait(false);
            }

            await ExecuteAsync(CompatibleTableSql(_Settings.Type), token).ConfigureAwait(false);
            using (DatabaseDriver compatibleDriver = scenarioRunner.CreateDriver())
                await compatibleDriver.InitializeAsync(token).ConfigureAwait(false);
            Dictionary<int, string> afterCompatible = await scenarioRunner.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.True(afterCompatible.ContainsKey(version),
                "Equivalent Harbor table allows migration replay");
            MigrationScenarioRunner.AssertHistory(before, afterCompatible);
        }

        private async Task AssertRejectedAsync(MigrationScenarioRunner scenarioRunner, Dictionary<int, string> before, string fixtureName, CancellationToken token)
        {

            bool rejected = false;
            try
            {
                using (DatabaseDriver driver = scenarioRunner.CreateDriver())
                    await driver.InitializeAsync(token).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("Harbor runner enrollment schema", StringComparison.Ordinal))
            {
                rejected = true;
            }
            DatabaseAssert.True(rejected, "Malformed Harbor enrollment schema is rejected: " + fixtureName);
            MigrationScenarioRunner.AssertHistory(before, await scenarioRunner.ReadHistoryAsync(token).ConfigureAwait(false));
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

        private static int VersionFor(DatabaseTypeEnum type)
        {
            return type switch
            {
                DatabaseTypeEnum.Sqlite => 90,
                DatabaseTypeEnum.Postgresql => 91,
                DatabaseTypeEnum.Mysql => 82,
                DatabaseTypeEnum.SqlServer => 85,
                _ => throw new NotSupportedException()
            };
        }

        private static string CompatibleTableSql(DatabaseTypeEnum type)
        {
            return type switch
            {
                DatabaseTypeEnum.Sqlite => "CREATE TABLE harbor_runner_enrollments (runner_id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL, user_id TEXT NOT NULL, auth_method TEXT NOT NULL, credential_id TEXT, generation INTEGER NOT NULL, active INTEGER NOT NULL DEFAULT 1, created_utc TEXT NOT NULL, last_update_utc TEXT NOT NULL, revoked_utc TEXT, revoked_by_user_id TEXT);",
                DatabaseTypeEnum.Postgresql => "CREATE TABLE harbor_runner_enrollments (runner_id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL, user_id TEXT NOT NULL, auth_method TEXT NOT NULL, credential_id TEXT NULL, generation BIGINT NOT NULL, active BOOLEAN NOT NULL DEFAULT TRUE, created_utc TIMESTAMPTZ NOT NULL, last_update_utc TIMESTAMPTZ NOT NULL, revoked_utc TIMESTAMPTZ NULL, revoked_by_user_id TEXT NULL);",
                DatabaseTypeEnum.Mysql => "CREATE TABLE harbor_runner_enrollments (runner_id VARCHAR(450) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, tenant_id VARCHAR(450) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, user_id VARCHAR(450) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, auth_method VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, credential_id VARCHAR(450) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, generation BIGINT NOT NULL, active TINYINT(1) NOT NULL DEFAULT 1, created_utc DATETIME(6) NOT NULL, last_update_utc DATETIME(6) NOT NULL, revoked_utc DATETIME(6) NULL, revoked_by_user_id VARCHAR(450) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, PRIMARY KEY (runner_id));",
                DatabaseTypeEnum.SqlServer => "CREATE TABLE harbor_runner_enrollments (runner_id NVARCHAR(450) NOT NULL PRIMARY KEY, tenant_id NVARCHAR(450) NOT NULL, user_id NVARCHAR(450) NOT NULL, auth_method NVARCHAR(64) NOT NULL, credential_id NVARCHAR(450) NULL, generation BIGINT NOT NULL, active BIT NOT NULL DEFAULT 1, created_utc DATETIME2 NOT NULL, last_update_utc DATETIME2 NOT NULL, revoked_utc DATETIME2 NULL, revoked_by_user_id NVARCHAR(450) NULL);",
                _ => throw new NotSupportedException()
            };
        }

        private static string NullabilityTableSql(DatabaseTypeEnum type)
        {
            return CompatibleTableSql(type).Replace(
                type == DatabaseTypeEnum.Mysql ? "tenant_id VARCHAR(450) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL" :
                type == DatabaseTypeEnum.SqlServer ? "tenant_id NVARCHAR(450) NOT NULL" : "tenant_id TEXT NOT NULL",
                type == DatabaseTypeEnum.SqlServer ? "tenant_id NVARCHAR(450) NULL" :
                type == DatabaseTypeEnum.Mysql ? "tenant_id VARCHAR(450) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL" : "tenant_id TEXT",
                StringComparison.Ordinal);
        }

        private static string TypeTableSql(DatabaseTypeEnum type)
        {
            string expected = type == DatabaseTypeEnum.Mysql ? "generation BIGINT" :
                type == DatabaseTypeEnum.SqlServer ? "generation BIGINT" :
                type == DatabaseTypeEnum.Postgresql ? "generation BIGINT" : "generation INTEGER";
            string incompatible = type == DatabaseTypeEnum.Sqlite ? "generation TEXT" : "generation INTEGER";
            return CompatibleTableSql(type).Replace(expected, incompatible, StringComparison.Ordinal);
        }

        private static string DefaultTableSql(DatabaseTypeEnum type)
        {
            return CompatibleTableSql(type).Replace(
                type == DatabaseTypeEnum.Postgresql ? "active BOOLEAN NOT NULL DEFAULT TRUE" :
                type == DatabaseTypeEnum.Mysql ? "active TINYINT(1) NOT NULL DEFAULT 1" :
                type == DatabaseTypeEnum.SqlServer ? "active BIT NOT NULL DEFAULT 1" : "active INTEGER NOT NULL DEFAULT 1",
                type == DatabaseTypeEnum.Postgresql ? "active BOOLEAN NOT NULL DEFAULT FALSE" :
                type == DatabaseTypeEnum.Mysql ? "active TINYINT(1) NOT NULL DEFAULT 0" :
                type == DatabaseTypeEnum.SqlServer ? "active BIT NOT NULL DEFAULT 0" : "active INTEGER NOT NULL DEFAULT 0",
                StringComparison.Ordinal);
        }

        private static string CompositeKeyTableSql(DatabaseTypeEnum type)
        {
            string sql = CompatibleTableSql(type);
            if (type == DatabaseTypeEnum.Mysql)
                return sql.Replace("PRIMARY KEY (runner_id)", "PRIMARY KEY (runner_id, generation)", StringComparison.Ordinal);
            string runnerColumn = type == DatabaseTypeEnum.Mysql ? "runner_id VARCHAR(450) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL PRIMARY KEY" :
                type == DatabaseTypeEnum.SqlServer ? "runner_id NVARCHAR(450) NOT NULL PRIMARY KEY" : "runner_id TEXT NOT NULL PRIMARY KEY";
            sql = sql.Replace(runnerColumn, runnerColumn.Replace(" PRIMARY KEY", String.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            return sql.Substring(0, sql.Length - 2) + ", PRIMARY KEY (runner_id, tenant_id));";
        }
    }
}
