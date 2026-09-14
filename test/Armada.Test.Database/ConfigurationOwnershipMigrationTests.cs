namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Configuration ownership migration proof: the ownership columns are absent before the version,
    /// an interrupted run records no version, the completed run leaves older history untouched, a
    /// persona written before the migration reads back tenant-wide with no owner, and a new
    /// user-specific record keeps a Unicode owner across a reopen and a repeated initialization.
    /// </summary>
    internal sealed class ConfigurationOwnershipMigrationTests
    {
        private readonly DatabaseSettings _Settings;
        private sealed class StopException : Exception { }

        internal ConfigurationOwnershipMigrationTests(DatabaseSettings settings) { _Settings = settings; }

        internal async Task VerifyAsync(CancellationToken token)
        {
            int version = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => 98, DatabaseTypeEnum.Postgresql => 99,
                DatabaseTypeEnum.Mysql => 90, DatabaseTypeEnum.SqlServer => 93,
                _ => throw new NotSupportedException()
            };

            // Version numbers are reserved per change and may land out of order, so the proof checks
            // that this version is pending and is the only one the run commits, not that it is contiguous.
            await StopAtAsync(version, -1, token).ConfigureAwait(false);
            MigrationScenarioRunner history = new MigrationScenarioRunner(_Settings);
            Dictionary<int, string> before = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.True(!before.ContainsKey(version), "Ownership version is pending before the migration");
            DatabaseAssert.True(System.Linq.Enumerable.Max(before.Keys) < version, "Every earlier version is applied before the ownership version");
            DatabaseAssert.Equal(0L, await OwnershipColumnCountAsync(token).ConfigureAwait(false), "Ownership columns are absent before the migration");

            string legacyId = "prs_legacy_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            await InsertLegacyPersonaAsync(legacyId, token).ConfigureAwait(false);

            await StopAtAsync(version, 0, token).ConfigureAwait(false);
            Dictionary<int, string> interrupted = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(before.Count, interrupted.Count, "An interrupted ownership migration records no version");
            MigrationScenarioRunner.AssertHistory(before, interrupted);

            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Dictionary<int, string> committed = await history.ReadHistoryAsync(token).ConfigureAwait(false);
                // Later versions reserved by other changes may commit in the same run, so the proof
                // checks that the ownership version committed and no earlier version appeared.
                DatabaseAssert.True(committed.ContainsKey(version), "The restarted run commits the ownership version");
                DatabaseAssert.True(System.Linq.Enumerable.All(committed.Keys, key => before.ContainsKey(key) || key >= version),
                    "The restarted run commits no version below the ownership version");
                MigrationScenarioRunner.AssertHistory(before, committed);
                DatabaseAssert.Equal(6L, await OwnershipColumnCountAsync(token).ConfigureAwait(false), "Every owned table has both ownership columns");

                Persona legacy = DatabaseAssert.NotNull(await driver.Personas.ReadAsync(legacyId, token).ConfigureAwait(false), "Legacy persona retained");
                DatabaseAssert.Equal(OwnershipScopeEnum.TenantWide, legacy.OwnershipScope, "A record written before ownership is tenant-wide");
                DatabaseAssert.True(legacy.UserId == null, "A record written before ownership has no owning user");
                DatabaseAssert.Equal("legacy-persona", legacy.Name, "Legacy fields are unchanged");

                string userId = "usr_ownership_日本語";
                Persona owned = new Persona("owned-persona-日本語", "persona.worker")
                {
                    TenantId = Armada.Core.Constants.DefaultTenantId,
                    UserId = userId,
                    OwnershipScope = OwnershipScopeEnum.UserSpecific
                };
                await driver.Personas.CreateAsync(owned, token).ConfigureAwait(false);

                await driver.InitializeAsync(token).ConfigureAwait(false);
                MigrationScenarioRunner.AssertHistory(committed, await history.ReadHistoryAsync(token).ConfigureAwait(false));

                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    Persona stored = DatabaseAssert.NotNull(await reopened.Personas.ReadAsync(owned.Id, token).ConfigureAwait(false), "Owned persona retained across a reopen");
                    DatabaseAssert.Equal(userId, stored.UserId, "Unicode owner");
                    DatabaseAssert.Equal(OwnershipScopeEnum.UserSpecific, stored.OwnershipScope, "User-specific scope");
                    await reopened.Personas.DeleteAsync(owned.Id, token).ConfigureAwait(false);
                    await reopened.Personas.DeleteAsync(legacyId, token).ConfigureAwait(false);
                }
            }

            Console.WriteLine("PASS configuration ownership migration: absent before, interrupted run uncommitted, committed once, legacy record tenant-wide, Unicode owner round trip, idempotent restart");
        }

        private async Task InsertLegacyPersonaAsync(string id, CancellationToken token)
        {
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO personas (id, tenant_id, name, description, prompt_template_name, is_built_in, active, created_utc, last_update_utc) "
                        + "VALUES (@id, @tenant, @name, @description, @template, @builtIn, @active, @created, @updated);";
                    AddParameter(command, "@id", id);
                    AddParameter(command, "@tenant", Armada.Core.Constants.DefaultTenantId);
                    AddParameter(command, "@name", "legacy-persona");
                    AddParameter(command, "@description", "written before ownership");
                    AddParameter(command, "@template", "persona.worker");

                    DateTime now = DateTime.UtcNow;
                    bool textual = _Settings.Type == DatabaseTypeEnum.Sqlite || _Settings.Type == DatabaseTypeEnum.SqlServer;
                    bool numericFlags = _Settings.Type == DatabaseTypeEnum.Sqlite || _Settings.Type == DatabaseTypeEnum.Mysql;
                    AddParameter(command, "@builtIn", numericFlags ? (object)0 : false);
                    AddParameter(command, "@active", numericFlags ? (object)1 : true);
                    AddParameter(command, "@created", textual ? (object)now.ToString("o") : now);
                    AddParameter(command, "@updated", textual ? (object)now.ToString("o") : now);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        private async Task<long> OwnershipColumnCountAsync(CancellationToken token)
        {
            string tables = "'personas','pipelines','prompt_templates'";
            string columns = "'user_id','ownership_scope'";
            if (_Settings.Type == DatabaseTypeEnum.Sqlite)
            {
                long count = 0;
                foreach (string table in new[] { "personas", "pipelines", "prompt_templates" })
                {
                    count += Convert.ToInt64(await ScalarAsync(
                        "SELECT COUNT(*) FROM pragma_table_info('" + table + "') WHERE name IN (" + columns + ");", token).ConfigureAwait(false));
                }
                return count;
            }

            string sql = _Settings.Type switch
            {
                DatabaseTypeEnum.Postgresql => "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=current_schema() AND table_name IN (" + tables + ") AND column_name IN (" + columns + ");",
                DatabaseTypeEnum.Mysql => "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name IN (" + tables + ") AND column_name IN (" + columns + ");",
                DatabaseTypeEnum.SqlServer => "SELECT COUNT(*) FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id WHERE t.name IN (" + tables + ") AND c.name IN (" + columns + ");",
                _ => throw new NotSupportedException()
            };
            return Convert.ToInt64(await ScalarAsync(sql, token).ConfigureAwait(false));
        }

        private async Task<object?> ScalarAsync(string sql, CancellationToken token)
        {
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    return await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                }
            }
        }

        private static void AddParameter(DbCommand command, string name, object value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        private async Task StopAtAsync(int version, int ordinal, CancellationToken token)
        {
            using (DatabaseDriver driver = CreateDriver())
            {
                driver.MigrationCheckpoint = (current, statement) =>
                {
                    if (current == version && statement == ordinal) throw new StopException();
                };
                try { await driver.InitializeAsync(token).ConfigureAwait(false); throw new Exception("Ownership fixture checkpoint was not reached"); }
                catch (StopException) { }
            }
        }

        private DatabaseDriver CreateDriver()
        {
            LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
            return DatabaseDriverFactory.Create(_Settings, logging);
        }
    }
}
