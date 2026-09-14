namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// A ledger that records a higher version without a lower known one must refuse startup.
    /// The fixture installs the real schema, then removes two ledger rows below the maximum to
    /// reproduce a migration that landed after a higher version was already applied. It restores
    /// exactly the rows it removed before proving a normal restart; it never invents a row.
    /// </summary>
    internal sealed class SkippedMigrationVersionTests
    {
        private readonly DatabaseSettings _Settings;

        internal SkippedMigrationVersionTests(DatabaseSettings settings) { _Settings = settings; }

        internal async Task VerifyAsync(CancellationToken token)
        {
            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false)) { }

            MigrationScenarioRunner history = new MigrationScenarioRunner(_Settings);
            Dictionary<int, string> installed = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            List<int> versions = installed.Keys.OrderBy(v => v).ToList();
            DatabaseAssert.True(versions.Count >= 3, "Installed ledger has enough versions to remove two below the maximum");
            int belowMaximum = versions[versions.Count - 2];
            int middle = versions[versions.Count / 2];
            List<int> removed = new List<int> { middle, belowMaximum };

            List<LedgerRow> saved = new List<LedgerRow>();
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                foreach (int version in removed)
                {
                    using (DbCommand read = connection.CreateCommand())
                    {
                        read.CommandText = "SELECT version,description,applied_utc FROM schema_migrations WHERE version=" + version + ";";
                        using (DbDataReader reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false))
                        {
                            DatabaseAssert.True(await reader.ReadAsync(token).ConfigureAwait(false), "Ledger row exists before removal " + version);
                            saved.Add(new LedgerRow { Version = version, Description = reader.GetValue(1), AppliedUtc = reader.GetValue(2) });
                        }
                    }
                    using (DbCommand delete = connection.CreateCommand())
                    {
                        delete.CommandText = "DELETE FROM schema_migrations WHERE version=" + version + ";";
                        DatabaseAssert.Equal(1, await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false), "Removed one ledger row " + version);
                    }
                }
            }

            Dictionary<int, string> gapped = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                SkippedMigrationVersionsException? refusal = null;
                using (DatabaseDriver driver = CreateDriver())
                {
                    try { await driver.InitializeAsync(token).ConfigureAwait(false); }
                    catch (SkippedMigrationVersionsException ex) { refusal = ex; }
                }
                Dictionary<int, string> afterStart = await history.ReadHistoryAsync(token).ConfigureAwait(false);
                if (refusal == null)
                    throw new Exception("Startup " + attempt + " completed silently with known version(s) "
                        + String.Join(", ", removed) + " unapplied below v" + versions[versions.Count - 1]
                        + "; ledger still lacks them: " + String.Join(", ", removed.Where(v => !afterStart.ContainsKey(v))));
                DatabaseAssert.Equal(String.Join(",", removed), String.Join(",", refusal.MissingVersions), "Refusal lists every skipped version ascending");
                DatabaseAssert.Equal(versions[versions.Count - 1], refusal.AppliedMaximum, "Refusal names the applied maximum");
                DatabaseAssert.Equal(_Settings.Type, refusal.Provider, "Refusal names the provider");
                DatabaseAssert.True(refusal.Message.StartsWith("skipped_migration_versions:", StringComparison.Ordinal), "Refusal carries its stable name");
                foreach (int version in removed)
                    DatabaseAssert.True(refusal.Message.Contains("v" + version, StringComparison.Ordinal), "Refusal message names v" + version);
                DatabaseAssert.Equal(gapped.Count, afterStart.Count, "Refused startup records no version (attempt " + attempt + ")");
                MigrationScenarioRunner.AssertHistory(gapped, afterStart);
            }

            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                foreach (LedgerRow row in saved)
                {
                    using (DbCommand insert = connection.CreateCommand())
                    {
                        insert.CommandText = "INSERT INTO schema_migrations (version,description,applied_utc) VALUES (@v,@d,@t);";
                        Add(insert, "@v", row.Version);
                        Add(insert, "@d", row.Description);
                        Add(insert, "@t", row.AppliedUtc);
                        await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
            }

            using (DatabaseDriver driver = CreateDriver())
            {
                await driver.InitializeAsync(token).ConfigureAwait(false);
            }
            Dictionary<int, string> restored = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(installed.Count, restored.Count, "Restored ledger count");
            MigrationScenarioRunner.AssertHistory(installed, restored);
            Console.WriteLine("PASS skipped migration versions: startup refused twice naming "
                + String.Join(", ", removed.Select(v => "v" + v)) + " with unchanged ledger; restored ledger restarts");
        }

        private DatabaseDriver CreateDriver()
        {
            LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
            return DatabaseDriverFactory.Create(_Settings, logging);
        }

        private static void Add(DbCommand command, string name, object value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        private sealed class LedgerRow
        {
            internal int Version { get; set; }
            internal object Description { get; set; } = "";
            internal object AppliedUtc { get; set; } = "";
        }
    }
}
