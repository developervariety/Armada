namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Microsoft.Data.SqlClient;
    using Microsoft.Data.Sqlite;
    using MySqlConnector;
    using Npgsql;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Shared provider behavior for fleet action definitions and durable run snapshots.</summary>
    public sealed class FleetActionDatabaseSuite : IArmadaTestSuite
    {
        /// <inheritdoc />
        public TestSuiteDescriptor Build() => new TestSuiteDescriptor("Database.FleetAction", "Fleet Action Database", new List<TestCaseDescriptor>
        {
            Case("definition_crud_is_exact_owner_scoped", DefinitionOwnerAsync),
            Case("request_replay_returns_immutable_snapshot", RequestReplayAsync),
            Case("revision_compare_exchange_has_one_winner", CompareExchangeAsync),
            Case("active_enumeration_excludes_terminal_and_other_owner_runs", ActiveEnumerationAsync),
            Case("startup_schema_guard_rejects_missing_required_column", MalformedSchemaAsync)
        });

        private static TestCaseDescriptor Case(string id, Func<Task> body) => new TestCaseDescriptor("Database.FleetAction", id, id.Replace('_', ' '), _ => body(), new List<string> { TestTags.Positive });

        private static async Task DefinitionOwnerAsync()
        {
            using (TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                FleetAction action = NewAction("ten_fleet", "usr_owner");
                FleetAction created = await database.Driver.FleetActions.CreateAsync(action).ConfigureAwait(false);
                AssertEqual(action.Id, created.Id);
                AssertNotNull(await database.Driver.FleetActions.ReadAsync(action.TenantId, action.UserId, action.Id).ConfigureAwait(false));
                AssertNull(await database.Driver.FleetActions.ReadAsync(action.TenantId, "usr_other", action.Id).ConfigureAwait(false));
                AssertNull(await database.Driver.FleetActions.ReadAsync("ten_other", action.UserId, action.Id).ConfigureAwait(false));
                AssertEqual(1, (await database.Driver.FleetActions.EnumerateAsync(action.TenantId, action.UserId).ConfigureAwait(false)).Count);
                action.Name = "Changed"; action.LastUpdateUtc = DateTime.UtcNow.AddSeconds(1);
                AssertTrue(await database.Driver.FleetActions.UpdateAsync(action).ConfigureAwait(false));
                AssertEqual("Changed", (await database.Driver.FleetActions.ReadAsync(action.TenantId, action.UserId, action.Id).ConfigureAwait(false))!.Name);
                AssertFalse(await database.Driver.FleetActions.DeleteAsync(action.TenantId, "usr_other", action.Id).ConfigureAwait(false));
                AssertTrue(await database.Driver.FleetActions.DeleteAsync(action.TenantId, action.UserId, action.Id).ConfigureAwait(false));
            }
        }

        private static async Task RequestReplayAsync()
        {
            using (TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                FleetActionRun run = NewRun("ten_fleet", "usr_owner", "req_replay");
                FleetActionRun created = await database.Driver.FleetActionRuns.CreateOrReadAsync(run).ConfigureAwait(false);
                FleetActionRun duplicate = NewRun(run.TenantId, run.UserId, run.RequestId); duplicate.Snapshot.ActionName = "Changed definition"; duplicate.RequestFingerprint = run.RequestFingerprint;
                FleetActionRun replay = await database.Driver.FleetActionRuns.CreateOrReadAsync(duplicate).ConfigureAwait(false);
                AssertEqual(created.Id, replay.Id); AssertEqual("Frozen action", replay.Snapshot.ActionName); AssertEqual("Rendered prompt", replay.Snapshot.Targets[0].RenderedPrompt);
                FleetActionRun changedInput = NewRun(run.TenantId, run.UserId, run.RequestId); changedInput.RequestFingerprint = new string('b', 64);
                FleetActionRun conflict = await database.Driver.FleetActionRuns.CreateOrReadAsync(changedInput).ConfigureAwait(false);
                AssertEqual(created.Id, conflict.Id); AssertEqual(new string('a', 64), conflict.RequestFingerprint);
                AssertNull(await database.Driver.FleetActionRuns.ReadAsync(run.TenantId, "usr_other", run.Id).ConfigureAwait(false));
                AssertNull(await database.Driver.FleetActionRuns.ReadByRequestIdAsync("ten_other", run.UserId, run.RequestId).ConfigureAwait(false));
                AssertEqual(created.Id, (await database.Driver.FleetActionRuns.ReadByRequestIdAsync(run.TenantId, run.UserId, run.RequestId).ConfigureAwait(false))!.Id);
                FleetActionRun raceOne = NewRun(run.TenantId, run.UserId, "req_race");
                FleetActionRun raceTwo = NewRun(run.TenantId, run.UserId, "req_race"); raceTwo.Snapshot.ActionName = "Raced duplicate";
                Task<FleetActionRun> first = database.Driver.FleetActionRuns.CreateOrReadAsync(raceOne);
                Task<FleetActionRun> second = database.Driver.FleetActionRuns.CreateOrReadAsync(raceTwo);
                FleetActionRun[] raced = await Task.WhenAll(first, second).ConfigureAwait(false);
                AssertEqual(raced[0].Id, raced[1].Id); AssertEqual("Frozen action", (await database.Driver.FleetActionRuns.ReadByRequestIdAsync(run.TenantId, run.UserId, "req_race").ConfigureAwait(false))!.Snapshot.ActionName);
            }
        }

        private static async Task CompareExchangeAsync()
        {
            using (TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                FleetActionRun run = await database.Driver.FleetActionRuns.CreateOrReadAsync(NewRun("ten_fleet", "usr_owner", "req_cas")).ConfigureAwait(false);
                FleetActionRunState dispatching = NextState(FleetActionTargetStatusEnum.Dispatching, run.Snapshot.Targets[0].TargetId);
                FleetActionRunState skipped = NextState(FleetActionTargetStatusEnum.Skipped, run.Snapshot.Targets[0].TargetId);
                Task<FleetActionRun?> first = database.Driver.FleetActionRuns.TryCompareExchangeStateAsync(run.TenantId, run.UserId, run.Id, run.Revision, dispatching);
                Task<FleetActionRun?> second = database.Driver.FleetActionRuns.TryCompareExchangeStateAsync(run.TenantId, run.UserId, run.Id, run.Revision, skipped);
                FleetActionRun?[] results = await Task.WhenAll(first, second).ConfigureAwait(false);
                AssertEqual(1, CountNotNull(results));
                FleetActionRun saved = (await database.Driver.FleetActionRuns.ReadAsync(run.TenantId, run.UserId, run.Id).ConfigureAwait(false))!;
                AssertEqual(1L, saved.Revision); AssertEqual("Rendered prompt", saved.Snapshot.Targets[0].RenderedPrompt);
                AssertNull(await database.Driver.FleetActionRuns.TryCompareExchangeStateAsync(run.TenantId, run.UserId, run.Id, run.Revision, dispatching).ConfigureAwait(false));
                AssertNull(await database.Driver.FleetActionRuns.TryCompareExchangeStateAsync(run.TenantId, "usr_wrong", run.Id, 1, dispatching).ConfigureAwait(false));
                FleetActionRunState reset = NextState(FleetActionTargetStatusEnum.Pending, run.Snapshot.Targets[0].TargetId);
                await AssertThrowsAsync<ArgumentException>(() => database.Driver.FleetActionRuns.TryCompareExchangeStateAsync(run.TenantId, run.UserId, run.Id, 1, reset));
            }
        }

        private static async Task ActiveEnumerationAsync()
        {
            using (TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                FleetActionRun activeRow = NewRun("ten_fleet", "usr_owner", "req_active"); activeRow.Id = "far_c"; activeRow.CreatedUtc = DateTime.UtcNow.AddMinutes(-2);
                FleetActionRun active = await database.Driver.FleetActionRuns.CreateOrReadAsync(activeRow).ConfigureAwait(false);
                FleetActionRun terminal = await database.Driver.FleetActionRuns.CreateOrReadAsync(NewRun("ten_fleet", "usr_owner", "req_terminal")).ConfigureAwait(false);
                FleetActionRunState terminalState = new FleetActionRunState { Status = FleetActionRunStatusEnum.Failed, TargetStates = terminal.State.TargetStates };
                FleetActionRun? failed = await database.Driver.FleetActionRuns.TryCompareExchangeStateAsync(terminal.TenantId, terminal.UserId, terminal.Id, terminal.Revision, terminalState).ConfigureAwait(false);
                AssertNotNull(failed);
                FleetActionRunState reopened = new FleetActionRunState { Status = FleetActionRunStatusEnum.Running, StartedUtc = DateTime.UtcNow, TargetStates = terminal.State.TargetStates };
                await AssertThrowsAsync<ArgumentException>(() => database.Driver.FleetActionRuns.TryCompareExchangeStateAsync(terminal.TenantId, terminal.UserId, terminal.Id, failed!.Revision, reopened));
                FleetActionRun completed = await database.Driver.FleetActionRuns.CreateOrReadAsync(NewRun("ten_fleet", "usr_owner", "req_completed")).ConfigureAwait(false);
                FleetActionRun? claimed = await database.Driver.FleetActionRuns.TryCompareExchangeStateAsync(completed.TenantId, completed.UserId, completed.Id, completed.Revision, NextState(FleetActionTargetStatusEnum.Dispatching, completed.Snapshot.Targets[0].TargetId)).ConfigureAwait(false);
                AssertNotNull(claimed);
                FleetActionRunState completedState = TerminalState(FleetActionRunStatusEnum.Completed, FleetActionTargetStatusEnum.Completed, completed.Snapshot.Targets[0].TargetId);
                FleetActionRun? completedRun = await database.Driver.FleetActionRuns.TryCompareExchangeStateAsync(completed.TenantId, completed.UserId, completed.Id, claimed!.Revision, completedState).ConfigureAwait(false);
                AssertNotNull(completedRun);
                FleetActionRun uncertain = await database.Driver.FleetActionRuns.CreateOrReadAsync(NewRun("ten_fleet", "usr_owner", "req_uncertain")).ConfigureAwait(false);
                FleetActionRunState uncertainState = TerminalState(FleetActionRunStatusEnum.Uncertain, FleetActionTargetStatusEnum.Uncertain, uncertain.Snapshot.Targets[0].TargetId);
                FleetActionRun? uncertainRun = await database.Driver.FleetActionRuns.TryCompareExchangeStateAsync(uncertain.TenantId, uncertain.UserId, uncertain.Id, uncertain.Revision, uncertainState).ConfigureAwait(false);
                AssertNotNull(uncertainRun);
                await AssertThrowsAsync<ArgumentException>(() => database.Driver.FleetActionRuns.TryCompareExchangeStateAsync(uncertain.TenantId, uncertain.UserId, uncertain.Id, uncertainRun!.Revision, NextState(FleetActionTargetStatusEnum.Pending, uncertain.Snapshot.Targets[0].TargetId)));
                FleetActionRun otherOwnerRow = NewRun("ten_other", "usr_other", "req_other"); otherOwnerRow.Id = "far_a"; otherOwnerRow.CreatedUtc = DateTime.UtcNow;
                FleetActionRun otherOwner = await database.Driver.FleetActionRuns.CreateOrReadAsync(otherOwnerRow).ConfigureAwait(false);
                FleetActionRun anotherOwnerRow = NewRun("ten_third", "usr_third", "req_third"); anotherOwnerRow.Id = "far_b"; anotherOwnerRow.CreatedUtc = DateTime.UtcNow.AddMinutes(-1);
                FleetActionRun anotherOwner = await database.Driver.FleetActionRuns.CreateOrReadAsync(anotherOwnerRow).ConfigureAwait(false);
                List<FleetActionRun> firstPage = await database.Driver.FleetActionRuns.EnumerateActiveAsync(2).ConfigureAwait(false);
                List<FleetActionRun> secondPage = await database.Driver.FleetActionRuns.EnumerateActiveAsync(2, firstPage[1].Id).ConfigureAwait(false);
                List<FleetActionRun> finalPage = await database.Driver.FleetActionRuns.EnumerateActiveAsync(2, secondPage[0].Id).ConfigureAwait(false);
                AssertEqual(2, firstPage.Count); AssertEqual(1, secondPage.Count); AssertEqual(0, finalPage.Count);
                AssertEqual("far_a", firstPage[0].Id); AssertEqual("far_b", firstPage[1].Id); AssertEqual("far_c", secondPage[0].Id);
                HashSet<string> pagedIds = new HashSet<string>();
                foreach (FleetActionRun row in firstPage) pagedIds.Add(row.Id);
                foreach (FleetActionRun row in secondPage) pagedIds.Add(row.Id);
                AssertEqual(3, pagedIds.Count);
                AssertTrue(firstPage.Exists(row => row.Id == active.Id) || secondPage.Exists(row => row.Id == active.Id));
                AssertFalse(firstPage.Exists(row => row.Id == terminal.Id) || secondPage.Exists(row => row.Id == terminal.Id));
                AssertFalse(firstPage.Exists(row => row.Id == completed.Id) || secondPage.Exists(row => row.Id == completed.Id));
                AssertFalse(firstPage.Exists(row => row.Id == uncertain.Id) || secondPage.Exists(row => row.Id == uncertain.Id));
                AssertTrue(firstPage.Exists(row => row.Id == otherOwner.Id) || secondPage.Exists(row => row.Id == otherOwner.Id));
                AssertTrue(firstPage.Exists(row => row.Id == anotherOwner.Id) || secondPage.Exists(row => row.Id == anotherOwner.Id));
                AssertEqual(4, (await database.Driver.FleetActionRuns.EnumerateAsync("ten_fleet", "usr_owner").ConfigureAwait(false)).Count);
            }
        }

        private static async Task MalformedSchemaAsync()
        {
            using (TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            using (DbConnection connection = CreateConnection(database))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                await ExecuteAsync(connection, "ALTER TABLE fleet_actions DROP COLUMN name;").ConfigureAwait(false);
                try
                {
                    await AssertThrowsAsync<InvalidOperationException>(() => FleetActionSchema.EnsureAsync(connection, null, TestDatabaseConfig.Type, default));
                }
                finally
                {
                    await ExecuteAsync(connection, RestoreNameColumnSql()).ConfigureAwait(false);
                }
            }
        }

        private static DbConnection CreateConnection(TestDatabase database)
        {
            return TestDatabaseConfig.Type switch
            {
                DatabaseTypeEnum.Sqlite => new SqliteConnection(database.ConnectionString),
                DatabaseTypeEnum.Postgresql => new NpgsqlConnection(database.ConnectionString),
                DatabaseTypeEnum.Mysql => new MySqlConnection(database.ConnectionString),
                DatabaseTypeEnum.SqlServer => new SqlConnection(database.ConnectionString),
                _ => throw new NotSupportedException()
            };
        }

        private static string RestoreNameColumnSql()
        {
            return TestDatabaseConfig.Type switch
            {
                DatabaseTypeEnum.Sqlite => "ALTER TABLE fleet_actions ADD COLUMN name TEXT NOT NULL DEFAULT '';",
                DatabaseTypeEnum.Postgresql => "ALTER TABLE fleet_actions ADD COLUMN name TEXT NOT NULL DEFAULT '';",
                DatabaseTypeEnum.Mysql => "ALTER TABLE fleet_actions ADD COLUMN name VARCHAR(255) NOT NULL DEFAULT '';",
                DatabaseTypeEnum.SqlServer => "ALTER TABLE fleet_actions ADD name NVARCHAR(255) NOT NULL DEFAULT N'';",
                _ => throw new NotSupportedException()
            };
        }

        private static async Task ExecuteAsync(DbConnection connection, string sql)
        {
            using (DbCommand command = connection.CreateCommand()) { command.CommandText = sql; await command.ExecuteNonQueryAsync().ConfigureAwait(false); }
        }

        private static FleetAction NewAction(string tenantId, string userId) => new FleetAction { TenantId = tenantId, UserId = userId, Name = "Action", Active = true, PromptTemplate = "Do work", Concurrency = 1 };
        private static FleetActionRun NewRun(string tenantId, string userId, string requestId)
        {
            FleetActionRun run = new FleetActionRun { TenantId = tenantId, UserId = userId, ActionId = "fac_test", RequestId = requestId, RequestFingerprint = new string('a', 64) };
            run.Snapshot.ActionName = "Frozen action"; run.Snapshot.PromptTemplate = "Do work";
            run.Snapshot.Targets.Add(new FleetActionRunTarget { VesselId = "vsl_snapshot", VesselTenantIdSnapshot = tenantId, VesselUserIdSnapshot = userId, VesselNameSnapshot = "Snapshot vessel", RenderedPrompt = "Rendered prompt" });
            run.State.TargetStates.Add(new FleetActionRunTargetState { TargetId = run.Snapshot.Targets[0].TargetId, Status = FleetActionTargetStatusEnum.Pending });
            return run;
        }

        private static FleetActionRunState NextState(FleetActionTargetStatusEnum status, string targetId) => new FleetActionRunState { Status = FleetActionRunStatusEnum.Running, StartedUtc = DateTime.UtcNow, TargetStates = new List<FleetActionRunTargetState> { new FleetActionRunTargetState { TargetId = targetId, Status = status, DispatchStartedUtc = DateTime.UtcNow } } };
        private static FleetActionRunState TerminalState(FleetActionRunStatusEnum runStatus, FleetActionTargetStatusEnum targetStatus, string targetId) => new FleetActionRunState { Status = runStatus, StartedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow, TargetStates = new List<FleetActionRunTargetState> { new FleetActionRunTargetState { TargetId = targetId, Status = targetStatus, DispatchStartedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow } } };
        private static int CountNotNull(FleetActionRun?[] values) { int count = 0; foreach (FleetActionRun? value in values) if (value != null) count++; return count; }
    }
}
