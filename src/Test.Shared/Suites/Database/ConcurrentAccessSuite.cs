namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Microsoft.Data.Sqlite;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors verifying the SQLite driver tolerates concurrent access: many parallel creates,
    /// many parallel reads of the same row, and interleaved create/enumerate operations all
    /// completing without loss or corruption. These are positive concurrency-tolerance cases; there
    /// is no invalid-input or rejection path to exercise, so the suite carries no negative cases.
    /// </summary>
    public sealed class ConcurrentAccessSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Concurrent Access suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("concurrent_creates_all_succeed", "Concurrent creates all succeed", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    int count = 20;
                    Task<Fleet>[] tasks = new Task<Fleet>[count];

                    for (int i = 0; i < count; i++)
                    {
                        int idx = i;
                        tasks[i] = db.Fleets.CreateAsync(new Fleet("Concurrent Fleet " + idx));
                    }

                    Fleet[] results = await Task.WhenAll(tasks);

                    AssertEqual(count, results.Length);
                    List<Fleet> allFleets = await db.Fleets.EnumerateAsync();
                    AssertEqual(count, allFleets.Count);
                }
            }));

            cases.Add(CaseAsync("concurrent_reads_all_succeed", "Concurrent reads all succeed", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Fleet fleet = new Fleet("Read Target");
                    await db.Fleets.CreateAsync(fleet);

                    int count = 20;
                    Task<Fleet?>[] tasks = new Task<Fleet?>[count];

                    for (int i = 0; i < count; i++)
                    {
                        tasks[i] = db.Fleets.ReadAsync(fleet.Id);
                    }

                    Fleet?[] results = await Task.WhenAll(tasks);

                    foreach (Fleet? result in results)
                    {
                        AssertNotNull(result);
                        AssertEqual("Read Target", result!.Name);
                    }
                }
            }));

            cases.Add(CaseAsync("concurrent_mixed_operations_all_succeed", "Concurrent mixed operations all succeed", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;

                    for (int i = 0; i < 5; i++)
                    {
                        await db.Fleets.CreateAsync(new Fleet("Initial " + i));
                    }

                    List<Task> tasks = new List<Task>();
                    for (int i = 0; i < 10; i++)
                    {
                        int idx = i;
                        tasks.Add(db.Fleets.CreateAsync(new Fleet("Mixed " + idx)));
                        tasks.Add(db.Fleets.EnumerateAsync());
                    }

                    await Task.WhenAll(tasks);

                    List<Fleet> allFleets = await db.Fleets.EnumerateAsync();
                    AssertEqual(15, allFleets.Count);
                }
            }));

            cases.Add(SqliteCaseAsync("sqlite_file_gate_orders_direct_and_stored_writes_without_blocking_reads", "SQLite file gate orders direct and stored writes without blocking reads", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (TestDatabase otherDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    SqliteConnectionStringBuilder alternateBuilder = new SqliteConnectionStringBuilder(testDb.ConnectionString) { DefaultTimeout = 45 };
                    using (DatabaseDriver alternate = new SqliteDatabaseDriver(alternateBuilder.ConnectionString, logging))
                    using (SqliteConnection setup = new SqliteConnection(testDb.ConnectionString))
                    {
                        await setup.OpenAsync().ConfigureAwait(false);
                        using (SqliteCommand command = setup.CreateCommand())
                        {
                            command.CommandText = "CREATE TABLE write_trace (sequence INTEGER PRIMARY KEY AUTOINCREMENT, path TEXT NOT NULL); " +
                                "CREATE TRIGGER trace_fleet AFTER INSERT ON fleets WHEN NEW.name='gate-direct' BEGIN INSERT INTO write_trace(path) VALUES('direct'); END; " +
                                "CREATE TRIGGER trace_history AFTER INSERT ON request_history WHEN NEW.route='/gate/stored' BEGIN INSERT INTO write_trace(path) VALUES('stored'); END;";
                            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }

                        IDisposable held = await SqliteWriteGate.EnterAsync(setup, CancellationToken.None).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("SQLite gate did not return a lease.");
                        List<Task> pending = new List<Task>();
                        Task<Fleet>? direct = null;
                        Task<RequestHistoryRecord>? stored = null;
                        try
                        {
                            TaskCompletionSource<bool> firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            direct = StartIndependent(() => testDb.Driver.Fleets.CreateAsync(new Fleet("gate-direct")), firstStarted);
                            pending.Add(direct);
                            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                            AssertFalse(direct.IsCompleted, "Direct fleet write must wait for the held file gate");

                            RequestHistoryEntry entry = new RequestHistoryEntry
                            {
                                TenantId = "ten_gate", Method = "GET", Route = "/gate/stored", RouteTemplate = "/gate/stored",
                                StatusCode = 200, DurationMs = 1, IsSuccess = true, CreatedUtc = DateTime.UtcNow
                            };
                            RequestHistoryDetail detail = new RequestHistoryDetail { RequestHistoryId = entry.Id, RequestBodyText = "{}" };
                            TaskCompletionSource<bool> secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            stored = StartIndependent(() => alternate.RequestHistory.CreateAsync(entry, detail), secondStarted);
                            pending.Add(stored);
                            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                            AssertFalse(stored.IsCompleted, "Stored request-history write must wait for the same file gate across connection strings");

                            List<Fleet> whileHeld = await testDb.Driver.Fleets.EnumerateAsync().ConfigureAwait(false);
                            AssertFalse(whileHeld.Exists(item => item.Name == "gate-direct"), "A read must complete while a write waits");
                            await otherDb.Driver.Fleets.CreateAsync(new Fleet("different-file-write")).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                        }
                        finally
                        {
                            held.Dispose();
                            await DrainAsync(pending).ConfigureAwait(false);
                        }

                        if (direct == null || stored == null) throw new InvalidOperationException("The queued write operations did not start.");
                        await Task.WhenAll(direct, stored).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                        using (SqliteCommand trace = setup.CreateCommand())
                        {
                            trace.CommandText = "SELECT group_concat(path, ',') FROM (SELECT path FROM write_trace ORDER BY sequence);";
                            object? traceValue = await trace.ExecuteScalarAsync().ConfigureAwait(false);
                            AssertEqual("direct,stored", traceValue?.ToString());
                        }
                    }
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: "Database.ConcurrentAccess",
                displayName: "Concurrent Access",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Database.ConcurrentAccess",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        private static Task<T> StartIndependent<T>(Func<Task<T>> operation, TaskCompletionSource<bool> started)
        {
            Task<Task<T>> outer;
            using (ExecutionContext.SuppressFlow())
            {
                outer = Task.Factory.StartNew(() =>
                {
                    Task<T> pending = operation();
                    started.TrySetResult(true);
                    return pending;
                }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            }
            return outer.Unwrap();
        }

        private static async Task DrainAsync(List<Task> tasks)
        {
            if (tasks.Count == 0) return;
            try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
            catch (Exception exception)
            {
                // The test body checks operation failures; cleanup must still report an unfinished operation.
                if (tasks.Exists(task => !task.IsCompleted))
                    throw new InvalidOperationException("Queued SQLite test work did not finish during cleanup.", exception);
            }
        }

        private static TestCaseDescriptor SqliteCaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            if (TestDatabaseConfig.IsSqlite) return CaseAsync(caseId, displayName, tag, body);
            return new TestCaseDescriptor("Database.ConcurrentAccess", caseId, displayName, _ => Task.CompletedTask,
                new List<string> { tag }, true, "Requires SQLite file-gate behavior.");
        }

        #endregion
    }
}
