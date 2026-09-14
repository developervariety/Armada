namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Settings;

    /// <summary>
    /// Endpoint-link and Harbor enrollment migrations in shipped order. Assertions name the specific versions
    /// under test, so later migrations do not change the result.
    /// </summary>
    internal sealed class HarborRunnerEnrollmentCombinedMigrationTests
    {
        private readonly DatabaseSettings _Settings;
        private sealed class StopException : Exception { }

        internal HarborRunnerEnrollmentCombinedMigrationTests(DatabaseSettings settings)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Run the captain endpoint-link and Harbor enrollment migrations in their shipped order with a fault in
        /// each, an incompatible partial Harbor table between restarts, and a final restart that must preserve
        /// applied history, durable rows and compare-and-set behavior.
        /// </summary>
        internal async Task VerifyAsync(CancellationToken token)
        {
            int harborVersion = VersionFor(_Settings.Type);
            int linkVersion = LinkVersionFor(_Settings.Type);
            DatabaseAssert.Equal(harborVersion - 1, linkVersion, "Harbor enrollment migration directly follows the captain endpoint-link migration");
            MigrationScenarioRunner scenarioRunner = new MigrationScenarioRunner(_Settings);

            await StopAtAsync(scenarioRunner, linkVersion, 0, "captain endpoint-link", token).ConfigureAwait(false);
            Dictionary<int, string> beforeLink = await scenarioRunner.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(linkVersion - 1, MaxVersion(beforeLink), "Interrupted endpoint-link migration is not recorded");

            await StopAtAsync(scenarioRunner, harborVersion, 1, "Harbor enrollment", token).ConfigureAwait(false);
            Dictionary<int, string> beforeHarbor = await scenarioRunner.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(linkVersion, MaxVersion(beforeHarbor), "Restart records the endpoint-link migration but not the interrupted Harbor migration");
            MigrationScenarioRunner.AssertHistory(beforeLink, beforeHarbor);

            await ExecuteAsync("DROP TABLE IF EXISTS harbor_runner_enrollments;", token).ConfigureAwait(false);
            await ExecuteAsync(IncompatiblePartialTableSql(_Settings.Type), token).ConfigureAwait(false);
            string rejection = String.Empty;
            try
            {
                using (DatabaseDriver driver = scenarioRunner.CreateDriver())
                    await driver.InitializeAsync(token).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("Harbor runner enrollment schema", StringComparison.Ordinal))
            {
                rejection = exception.Message;
            }
            DatabaseAssert.True(rejection.Length > 0, "Incompatible partial Harbor table is rejected after the endpoint-link migration");
            MigrationScenarioRunner.AssertHistory(beforeHarbor, await scenarioRunner.ReadHistoryAsync(token).ConfigureAwait(false));
            DatabaseAssert.Equal(linkVersion, MaxVersion(await scenarioRunner.ReadHistoryAsync(token).ConfigureAwait(false)), "Rejected Harbor table does not advance history");
            await ExecuteAsync("DROP TABLE harbor_runner_enrollments;", token).ConfigureAwait(false);

            string runnerId = "hbr_combined_" + Guid.NewGuid().ToString("N");
            Dictionary<int, string> completed;
            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                DatabaseAssert.True(await driver.GetSchemaVersionAsync(token).ConfigureAwait(false) >= harborVersion, "Combined restart reaches at least the Harbor version");
                completed = await scenarioRunner.ReadHistoryAsync(token).ConfigureAwait(false);
                DatabaseAssert.True(completed.ContainsKey(linkVersion), "Combined restart records the endpoint-link migration");
                DatabaseAssert.True(completed.ContainsKey(harborVersion), "Combined restart records the Harbor migration");
                MigrationScenarioRunner.AssertHistory(beforeHarbor, completed);
                DatabaseAssert.True(await driver.HarborRunnerEnrollments.TryEnrollAsync(NewEnrollment(runnerId, 1), 0, token).ConfigureAwait(false), "First enrollment wins after combined restart");
                DatabaseAssert.True(!await driver.HarborRunnerEnrollments.TryEnrollAsync(NewEnrollment(runnerId, 1), 0, token).ConfigureAwait(false), "Stale expected generation cannot replace an active enrollment");
                DatabaseAssert.True(await driver.HarborRunnerEnrollments.TryRevokeAsync(runnerId, 1, "usr_combined_admin", DateTime.UtcNow, token).ConfigureAwait(false), "Current generation revokes");
                DatabaseAssert.True(!await driver.HarborRunnerEnrollments.TryRevokeAsync(runnerId, 1, "usr_combined_admin", DateTime.UtcNow, token).ConfigureAwait(false), "Stale generation cannot revoke twice");
            }

            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                HarborRunnerEnrollment? stored = await reopened.HarborRunnerEnrollments.ReadAsync(runnerId, token).ConfigureAwait(false);
                DatabaseAssert.True(stored != null, "Revoked enrollment survives restart");
                DatabaseAssert.True(!stored!.Active, "Revocation survives restart");
                DatabaseAssert.Equal(2L, stored.Generation, "Revocation generation survives restart");
                DatabaseAssert.True(!await reopened.HarborRunnerEnrollments.TryEnrollAsync(NewEnrollment(runnerId, 1), 0, token).ConfigureAwait(false), "Pre-revocation generation cannot re-enroll after restart");
                DatabaseAssert.True(await reopened.HarborRunnerEnrollments.TryEnrollAsync(NewEnrollment(runnerId, 3), 2, token).ConfigureAwait(false), "Current generation re-enrolls after restart");
            }
            MigrationScenarioRunner.AssertHistory(completed, await scenarioRunner.ReadHistoryAsync(token).ConfigureAwait(false));
        }

        private async Task StopAtAsync(MigrationScenarioRunner scenarioRunner, int version, int ordinal, string name, CancellationToken token)
        {
            bool stopped = false;
            using (DatabaseDriver driver = scenarioRunner.CreateDriver())
            {
                driver.MigrationCheckpoint = (seen, seenOrdinal) =>
                {
                    if (seen == version && seenOrdinal == ordinal) throw new StopException();
                };
                try { await driver.InitializeAsync(token).ConfigureAwait(false); }
                catch (StopException) { stopped = true; }
            }
            DatabaseAssert.True(stopped, name + " migration fault point was reached");
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

        private static int MaxVersion(Dictionary<int, string> history)
        {
            int max = 0;
            foreach (int version in history.Keys) if (version > max) max = version;
            return max;
        }

        private static string IncompatiblePartialTableSql(DatabaseTypeEnum type)
        {
            return type switch
            {
                DatabaseTypeEnum.Sqlite => "CREATE TABLE harbor_runner_enrollments (runner_id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL);",
                DatabaseTypeEnum.Postgresql => "CREATE TABLE harbor_runner_enrollments (runner_id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL);",
                DatabaseTypeEnum.Mysql => "CREATE TABLE harbor_runner_enrollments (runner_id VARCHAR(191) NOT NULL PRIMARY KEY, tenant_id VARCHAR(191) NOT NULL);",
                DatabaseTypeEnum.SqlServer => "CREATE TABLE harbor_runner_enrollments (runner_id NVARCHAR(450) NOT NULL PRIMARY KEY, tenant_id NVARCHAR(450) NOT NULL);",
                _ => throw new NotSupportedException()
            };
        }

        private static HarborRunnerEnrollment NewEnrollment(string runnerId, long generation)
        {
            DateTime now = DateTime.UtcNow;
            return new HarborRunnerEnrollment
            {
                RunnerId = runnerId,
                TenantId = "ten_combined",
                UserId = "usr_combined",
                AuthMethod = "Bearer",
                CredentialId = "crd_combined",
                Generation = generation,
                Active = true,
                CreatedUtc = now,
                LastUpdateUtc = now
            };
        }

        private static int LinkVersionFor(DatabaseTypeEnum type)
        {
            return type switch
            {
                DatabaseTypeEnum.Sqlite => 89,
                DatabaseTypeEnum.Postgresql => 90,
                DatabaseTypeEnum.Mysql => 81,
                DatabaseTypeEnum.SqlServer => 84,
                _ => throw new NotSupportedException()
            };
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
    }
}
