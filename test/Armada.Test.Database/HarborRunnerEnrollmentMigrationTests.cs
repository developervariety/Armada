namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Settings;

    /// <summary>Fresh and interrupted Harbor enrollment migration proofs.</summary>
    internal sealed class HarborRunnerEnrollmentMigrationTests
    {
        private readonly DatabaseSettings _Settings;
        private sealed class StopException : Exception { }

        internal HarborRunnerEnrollmentMigrationTests(DatabaseSettings settings)
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
                    if (seen == version && ordinal == 1) throw new StopException();
                };
                bool stopped = false;
                try { await driver.InitializeAsync(token).ConfigureAwait(false); }
                catch (StopException) { stopped = true; }
                DatabaseAssert.True(stopped, "Harbor enrollment partial migration checkpoint was reached");
            }
            Dictionary<int, string> partialHistory = await scenarioRunner.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.True(!partialHistory.ContainsKey(version), "Partial Harbor migration does not record an incomplete version");

            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                DatabaseAssert.Equal(version, await reopened.GetSchemaVersionAsync(token).ConfigureAwait(false), "Harbor enrollment migration restarts to target version");
                HarborRunnerEnrollment? missing = await reopened.HarborRunnerEnrollments.ReadAsync("hbr_migration_missing", token).ConfigureAwait(false);
                DatabaseAssert.True(missing == null, "Restarted Harbor enrollment table accepts reads");
            }
            MigrationScenarioRunner.AssertHistory(partialHistory, await scenarioRunner.ReadHistoryAsync(token).ConfigureAwait(false));
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
