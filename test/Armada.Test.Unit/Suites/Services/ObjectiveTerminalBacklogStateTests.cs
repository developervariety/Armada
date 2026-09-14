namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database;
    using Armada.Core.Database.Mysql;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using PostgresqlTableQueries = Armada.Core.Database.Postgresql.Queries.TableQueries;
    using SqliteTableQueries = Armada.Core.Database.Sqlite.Queries.TableQueries;
    using SqlServerTableQueries = Armada.Core.Database.SqlServer.Queries.TableQueries;

    /// <summary>
    /// A terminal objective must never rest in a dispatchable backlog state, whichever path made it terminal.
    /// </summary>
    public class ObjectiveTerminalBacklogStateTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Objective Terminal Backlog State";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A manual completion moves a ReadyForDispatch objective to Inbox in the same write", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                AuthContext auth = Auth();
                Objective created = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Manual completion",
                    Status = ObjectiveStatusEnum.Planned,
                    BacklogState = ObjectiveBacklogStateEnum.ReadyForDispatch
                }).ConfigureAwait(false);

                Objective updated = await objectives.UpdateAsync(auth, created.Id, new ObjectiveUpsertRequest
                {
                    Status = ObjectiveStatusEnum.Completed
                }).ConfigureAwait(false);

                AssertEqual(ObjectiveBacklogStateEnum.Inbox, updated.BacklogState,
                    "The returned objective must leave the dispatchable backlog state.");
                Objective stored = (await testDb.Driver.Objectives.ReadAsync(created.Id).ConfigureAwait(false))!;
                AssertEqual(ObjectiveStatusEnum.Completed, stored.Status);
                AssertEqual(ObjectiveBacklogStateEnum.Inbox, stored.BacklogState,
                    "The stored row must carry the terminal status and Inbox together.");
                AssertNotNull(stored.CompletedUtc);
            });

            await RunTest("A manual cancellation that also names a dispatchable backlog state still rests in Inbox", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                AuthContext auth = Auth();
                Objective created = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Manual cancellation",
                    Status = ObjectiveStatusEnum.Scoped,
                    BacklogState = ObjectiveBacklogStateEnum.Triaged
                }).ConfigureAwait(false);

                Objective updated = await objectives.UpdateAsync(auth, created.Id, new ObjectiveUpsertRequest
                {
                    Status = ObjectiveStatusEnum.Cancelled,
                    BacklogState = ObjectiveBacklogStateEnum.ReadyForDispatch
                }).ConfigureAwait(false);

                AssertEqual(ObjectiveBacklogStateEnum.Inbox, updated.BacklogState);
                Objective stored = (await testDb.Driver.Objectives.ReadAsync(created.Id).ConfigureAwait(false))!;
                AssertEqual(ObjectiveBacklogStateEnum.Inbox, stored.BacklogState);
            });

            await RunTest("A rescue link on a completed objective does not reopen a dispatchable backlog state", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                AuthContext auth = Auth();
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = auth.TenantId,
                    UserId = auth.UserId,
                    Title = "Completed before its rescue linked",
                    Status = ObjectiveStatusEnum.Completed,
                    BacklogState = ObjectiveBacklogStateEnum.Inbox
                }).ConfigureAwait(false);
                Voyage rescue = await testDb.Driver.Voyages.CreateAsync(new Voyage("Rescue")
                {
                    TenantId = auth.TenantId,
                    UserId = auth.UserId,
                    Status = VoyageStatusEnum.Open
                }).ConfigureAwait(false);

                Objective linked = await objectives.LinkVoyageAsync(auth, objective.Id, rescue.Id, isRescueLink: true).ConfigureAwait(false);

                AssertTrue(linked.VoyageIds.Contains(rescue.Id), "The rescue voyage stays in the lineage.");
                AssertEqual(ObjectiveStatusEnum.Completed, linked.Status);
                AssertEqual(ObjectiveBacklogStateEnum.Inbox, linked.BacklogState,
                    "A recovery link must not move a completed objective back into Dispatched.");
                Objective stored = (await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false))!;
                AssertEqual(ObjectiveBacklogStateEnum.Inbox, stored.BacklogState);
            });

            await RunTest("An imported terminal objective never persists a dispatchable backlog state", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                AuthContext auth = Auth();
                Objective created = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Imported cancellation",
                    Status = ObjectiveStatusEnum.Planned,
                    BacklogState = ObjectiveBacklogStateEnum.ReadyForDispatch
                }).ConfigureAwait(false);

                created.Status = ObjectiveStatusEnum.Cancelled;
                created.BacklogState = ObjectiveBacklogStateEnum.ReadyForDispatch;
                Objective persisted = await objectives.PersistImportedAsync(auth, created).ConfigureAwait(false);

                AssertEqual(ObjectiveBacklogStateEnum.Inbox, persisted.BacklogState);
                Objective stored = (await testDb.Driver.Objectives.ReadAsync(created.Id).ConfigureAwait(false))!;
                AssertEqual(ObjectiveBacklogStateEnum.Inbox, stored.BacklogState);
            });

            await RunTest("Service reads never surface a contradictory stored row as ReadyForDispatch", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                AuthContext auth = Auth();
                Objective contradictory = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = auth.TenantId,
                    UserId = auth.UserId,
                    Title = "Completed but ReadyForDispatch",
                    Status = ObjectiveStatusEnum.Completed,
                    BacklogState = ObjectiveBacklogStateEnum.ReadyForDispatch
                }).ConfigureAwait(false);
                Objective valid = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = auth.TenantId,
                    UserId = auth.UserId,
                    Title = "Planned and ReadyForDispatch",
                    Status = ObjectiveStatusEnum.Planned,
                    BacklogState = ObjectiveBacklogStateEnum.ReadyForDispatch
                }).ConfigureAwait(false);

                EnumerationResult<Objective> ready = await objectives.EnumerateAsync(auth, new ObjectiveQuery
                {
                    BacklogState = ObjectiveBacklogStateEnum.ReadyForDispatch,
                    PageSize = 100
                }).ConfigureAwait(false);

                AssertEqual(1, ready.TotalRecords, "Only the valid active row is dispatchable.");
                AssertEqual(valid.Id, ready.Objects[0].Id);
                Objective read = (await objectives.ReadAsync(auth, contradictory.Id).ConfigureAwait(false))!;
                AssertEqual(ObjectiveBacklogStateEnum.Inbox, read.BacklogState);
                Objective validRead = (await objectives.ReadAsync(auth, valid.Id).ConfigureAwait(false))!;
                AssertEqual(ObjectiveBacklogStateEnum.ReadyForDispatch, validRead.BacklogState,
                    "A valid active row keeps its backlog state.");
            });

            await RunTest("Every provider migrates contradictory terminal rows and leaves valid rows unchanged", async () =>
            {
                SchemaMigration sqlite = RequireTerminalBacklogMigration(SqliteTableQueries.GetMigrations(), 92, "SQLite");
                RequireTerminalBacklogMigration(PostgresqlTableQueries.GetMigrations(), 93, "PostgreSQL");
                RequireTerminalBacklogMigration(SqlServerTableQueries.GetMigrations(), 87, "SQL Server");
                System.Reflection.MethodInfo mysqlGetMigrations = typeof(MysqlDatabaseDriver).GetMethod(
                    "GetMigrations",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                RequireTerminalBacklogMigration(
                    (List<SchemaMigration>)mysqlGetMigrations.Invoke(null, Array.Empty<object>())!, 84, "MySQL");

                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Dictionary<string, ObjectiveBacklogStateEnum> expected = new Dictionary<string, ObjectiveBacklogStateEnum>();
                async Task SeedAsync(ObjectiveStatusEnum status, ObjectiveBacklogStateEnum state, ObjectiveBacklogStateEnum after)
                {
                    Objective row = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = status + " " + state,
                        Status = status,
                        BacklogState = state
                    }).ConfigureAwait(false);
                    expected[row.Id] = after;
                }

                await SeedAsync(ObjectiveStatusEnum.Completed, ObjectiveBacklogStateEnum.ReadyForDispatch, ObjectiveBacklogStateEnum.Inbox).ConfigureAwait(false);
                await SeedAsync(ObjectiveStatusEnum.Completed, ObjectiveBacklogStateEnum.Dispatched, ObjectiveBacklogStateEnum.Inbox).ConfigureAwait(false);
                await SeedAsync(ObjectiveStatusEnum.Cancelled, ObjectiveBacklogStateEnum.Triaged, ObjectiveBacklogStateEnum.Inbox).ConfigureAwait(false);
                await SeedAsync(ObjectiveStatusEnum.Completed, ObjectiveBacklogStateEnum.Inbox, ObjectiveBacklogStateEnum.Inbox).ConfigureAwait(false);
                await SeedAsync(ObjectiveStatusEnum.Planned, ObjectiveBacklogStateEnum.ReadyForDispatch, ObjectiveBacklogStateEnum.ReadyForDispatch).ConfigureAwait(false);
                await SeedAsync(ObjectiveStatusEnum.InProgress, ObjectiveBacklogStateEnum.Dispatched, ObjectiveBacklogStateEnum.Dispatched).ConfigureAwait(false);
                await SeedAsync(ObjectiveStatusEnum.Released, ObjectiveBacklogStateEnum.ReadyForDispatch, ObjectiveBacklogStateEnum.ReadyForDispatch).ConfigureAwait(false);
                Dictionary<string, DateTime> updatedBefore = new Dictionary<string, DateTime>();
                foreach (string id in expected.Keys)
                    updatedBefore[id] = (await testDb.Driver.Objectives.ReadAsync(id).ConfigureAwait(false))!.LastUpdateUtc;

                using (SqliteConnection connection = new SqliteConnection(testDb.ConnectionString))
                {
                    await connection.OpenAsync().ConfigureAwait(false);
                    foreach (string statement in sqlite.Statements)
                    {
                        using (SqliteCommand command = connection.CreateCommand())
                        {
                            command.CommandText = statement;
                            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }
                }

                foreach (KeyValuePair<string, ObjectiveBacklogStateEnum> row in expected)
                {
                    Objective stored = (await testDb.Driver.Objectives.ReadAsync(row.Key).ConfigureAwait(false))!;
                    AssertEqual(row.Value, stored.BacklogState, stored.Title);
                    AssertEqual(updatedBefore[row.Key], stored.LastUpdateUtc, stored.Title + " keeps its update time");
                }
            });

            await RunTest("The shared rule changes only terminal objectives outside Inbox", () =>
            {
                foreach (ObjectiveStatusEnum status in Enum.GetValues<ObjectiveStatusEnum>())
                {
                    foreach (ObjectiveBacklogStateEnum state in Enum.GetValues<ObjectiveBacklogStateEnum>())
                    {
                        Objective objective = new Objective { Title = "Rule", Status = status, BacklogState = state };
                        bool terminal = status == ObjectiveStatusEnum.Completed || status == ObjectiveStatusEnum.Cancelled;
                        bool changed = ObjectiveLifecycleRules.ApplyTerminalBacklogState(objective);

                        AssertEqual(terminal, ObjectiveLifecycleRules.IsTerminalStatus(status), status.ToString());
                        AssertEqual(terminal && state != ObjectiveBacklogStateEnum.Inbox, changed, status + "/" + state);
                        AssertEqual(terminal ? ObjectiveBacklogStateEnum.Inbox : state, objective.BacklogState, status + "/" + state);
                    }
                }

                return Task.CompletedTask;
            });
        }

        private SchemaMigration RequireTerminalBacklogMigration(List<SchemaMigration> migrations, int version, string provider)
        {
            SchemaMigration? migration = migrations.FirstOrDefault(item => item.Version == version);
            AssertNotNull(migration, provider + " must register terminal backlog migration " + version + ".");
            AssertTrue(migration!.Statements.Any(statement =>
                    statement.Contains("backlog_state", StringComparison.OrdinalIgnoreCase)
                    && statement.Contains("'Inbox'", StringComparison.Ordinal)
                    && statement.Contains("'Completed'", StringComparison.Ordinal)
                    && statement.Contains("'Cancelled'", StringComparison.Ordinal)),
                provider + " terminal backlog migration must move Completed and Cancelled rows to Inbox.");
            return migration;
        }

        private static AuthContext Auth()
        {
            return AuthContext.Authenticated(
                Armada.Core.Constants.DefaultTenantId,
                Armada.Core.Constants.DefaultUserId,
                false,
                true,
                "UnitTest");
        }
    }
}
