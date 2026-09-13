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
    /// Native captain memory migration proof: the tables are absent before the version, an interrupted
    /// run records no version, the completed run leaves older history untouched, and the stored record
    /// survives a reopen with its tags, its tenant fence and its key uniqueness.
    /// </summary>
    internal sealed class MemoryMigrationTests
    {
        private readonly DatabaseSettings _Settings;
        private sealed class StopException : Exception { }

        internal MemoryMigrationTests(DatabaseSettings settings) { _Settings = settings; }

        internal async Task VerifyAsync(CancellationToken token)
        {
            int version = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => 86, DatabaseTypeEnum.Postgresql => 87,
                DatabaseTypeEnum.Mysql => 78, DatabaseTypeEnum.SqlServer => 81,
                _ => throw new NotSupportedException()
            };

            await StopAtAsync(version, -1, token).ConfigureAwait(false);
            MigrationScenarioRunner history = new MigrationScenarioRunner(_Settings);
            Dictionary<int, string> before = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(version - 1, System.Linq.Enumerable.Max(before.Keys), "Memory version is the next pending version");
            DatabaseAssert.True(!await TableExistsAsync(token).ConfigureAwait(false), "Memories are absent before the migration");

            await StopAtAsync(version, 0, token).ConfigureAwait(false);
            Dictionary<int, string> interrupted = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(before.Count, interrupted.Count, "An interrupted memory migration records no version");
            MigrationScenarioRunner.AssertHistory(before, interrupted);

            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Dictionary<int, string> committed = await history.ReadHistoryAsync(token).ConfigureAwait(false);
                DatabaseAssert.Equal(before.Count + 1, committed.Count, "The restarted run commits exactly the memory version");
                MigrationScenarioRunner.AssertHistory(before, committed);
                DatabaseAssert.True(await TableExistsAsync(token).ConfigureAwait(false), "Memories exist after the migration");

                Memory memory = new Memory();
                memory.TenantId = "ten_migration";
                memory.UserId = "usr_migration";
                memory.Key = "build/日本語";
                memory.Topic = "build";
                memory.Summary = "unicode summary 日本語";
                memory.Content = "Unicode content 日本語";
                memory.Salience = 0.75;
                memory.Tags = new List<string> { "alpha", "日本語" };
                Memory created = await driver.Memories.CreateAsync(memory, token).ConfigureAwait(false);

                Memory stored = DatabaseAssert.NotNull(await driver.Memories.ReadAsync(created.Id, token).ConfigureAwait(false), "Record retained");
                DatabaseAssert.Equal("Unicode content 日本語", stored.Content, "Unicode content");
                DatabaseAssert.Equal("build/日本語", stored.Key, "Unicode key");
                DatabaseAssert.Equal(0.75, stored.Salience, "Salience");
                DatabaseAssert.Equal(2, stored.Tags.Count, "Tags");
                DatabaseAssert.True(stored.CreatedUtc.Kind == DateTimeKind.Utc, "Timestamps read back as UTC");

                Memory duplicate = new Memory();
                duplicate.TenantId = "ten_migration";
                duplicate.UserId = "usr_migration";
                duplicate.Key = "build/日本語";
                duplicate.Content = "duplicate";
                bool rejected = false;
                try { await driver.Memories.CreateAsync(duplicate, token).ConfigureAwait(false); }
                catch (DbException) { rejected = true; }
                DatabaseAssert.True(rejected, "The storage rejects a duplicate key inside a tenant");

                stored.Content = "corrected";
                stored.Version = stored.Version + 1;
                DatabaseAssert.True(await driver.Memories.UpdateAsync(stored, 1, token).ConfigureAwait(false), "Guarded update applies at the stored version");
                DatabaseAssert.True(!await driver.Memories.UpdateAsync(stored, 1, token).ConfigureAwait(false), "Guarded update refuses a stale version");

                await driver.InitializeAsync(token).ConfigureAwait(false);
                MigrationScenarioRunner.AssertHistory(committed, await history.ReadHistoryAsync(token).ConfigureAwait(false));

                DatabaseAssert.True(await driver.Memories.DeleteAsync("ten_migration", created.Id, token).ConfigureAwait(false), "Delete removes the record");
                DatabaseAssert.Equal(0L, await TagCountAsync(token).ConfigureAwait(false), "Deleting the record removes its tags");
            }

            Console.WriteLine("PASS memory migration: absent before, interrupted run uncommitted, committed once, Unicode round trip, key uniqueness, guarded update, cascade delete");
        }

        private async Task<bool> TableExistsAsync(CancellationToken token)
        {
            string sql = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('memories','memory_tags');",
                DatabaseTypeEnum.Postgresql => "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=current_schema() AND table_name IN ('memories','memory_tags');",
                DatabaseTypeEnum.Mysql => "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name IN ('memories','memory_tags');",
                DatabaseTypeEnum.SqlServer => "SELECT COUNT(*) FROM sys.tables WHERE name IN ('memories','memory_tags');",
                _ => throw new NotSupportedException()
            };
            return Convert.ToInt64(await ScalarAsync(sql, token).ConfigureAwait(false)) == 2L;
        }

        private async Task<long> TagCountAsync(CancellationToken token)
        {
            return Convert.ToInt64(await ScalarAsync("SELECT COUNT(*) FROM memory_tags;", token).ConfigureAwait(false));
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

        private async Task StopAtAsync(int version, int ordinal, CancellationToken token)
        {
            using (DatabaseDriver driver = CreateDriver())
            {
                driver.MigrationCheckpoint = (current, statement) =>
                {
                    if (current == version && statement == ordinal) throw new StopException();
                };
                try { await driver.InitializeAsync(token).ConfigureAwait(false); throw new Exception("Memory fixture checkpoint was not reached"); }
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
