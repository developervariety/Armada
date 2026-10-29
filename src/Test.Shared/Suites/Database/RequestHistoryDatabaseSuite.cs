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
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for request-history database operations: create/read round-tripping of an entry
    /// together with its detail row, filtered enumeration by route and success state, and retention
    /// pruning via DeleteByFilterAsync using a date cutoff. Positive cases cover persistence and the
    /// query/prune happy paths; negative audit cases cover a not-found read returning null and a
    /// filter that matches nothing deleting zero rows.
    /// </summary>
    public sealed class RequestHistoryDatabaseSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Database.RequestHistoryDatabase";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the RequestHistory Database suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("create_and_read_persist_entry_detail", "CreateAsync and ReadAsync persist entry detail", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;

                    RequestHistoryEntry entry = BuildEntry("GET", "/api/v1/fleets", 200, "usr_one");
                    RequestHistoryDetail detail = BuildDetail(entry.Id, "{\"route\":\"/api/v1/fleets\"}");

                    await db.RequestHistory.CreateAsync(entry, detail).ConfigureAwait(false);
                    RequestHistoryRecord? result = await db.RequestHistory.ReadAsync(entry.Id).ConfigureAwait(false);

                    AssertNotNull(result);
                    AssertEqual(entry.Id, result!.Entry.Id);
                    AssertEqual("/api/v1/fleets", result.Entry.Route);
                    AssertEqual(detail.RequestBodyText, result.Detail!.RequestBodyText);
                }
            }));

            cases.Add(CaseAsync("enumerate_filters_by_route_and_success_state", "EnumerateAsync filters by route and success state", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;

                    RequestHistoryEntry successEntry = BuildEntry("GET", "/api/v1/fleets", 200, "usr_one");
                    RequestHistoryEntry failureEntry = BuildEntry("POST", "/api/v1/missions", 500, "usr_two");
                    await db.RequestHistory.CreateAsync(successEntry, BuildDetail(successEntry.Id, "{\"ok\":true}")).ConfigureAwait(false);
                    await db.RequestHistory.CreateAsync(failureEntry, BuildDetail(failureEntry.Id, "{\"ok\":false}")).ConfigureAwait(false);

                    EnumerationResult<RequestHistoryEntry> routeFiltered = await db.RequestHistory.EnumerateAsync(new RequestHistoryQuery
                    {
                        Route = "/api/v1/missions",
                        PageNumber = 1,
                        PageSize = 25
                    }).ConfigureAwait(false);
                    AssertEqual(1, routeFiltered.Objects.Count);
                    AssertEqual(failureEntry.Id, routeFiltered.Objects[0].Id);

                    EnumerationResult<RequestHistoryEntry> successFiltered = await db.RequestHistory.EnumerateAsync(new RequestHistoryQuery
                    {
                        IsSuccess = true,
                        PageNumber = 1,
                        PageSize = 25
                    }).ConfigureAwait(false);
                    AssertEqual(1, successFiltered.Objects.Count);
                    AssertEqual(successEntry.Id, successFiltered.Objects[0].Id);
                }
            }));

            cases.Add(CaseAsync("enumerate_excludes_read_methods_in_the_page_and_its_total", "EnumerateAsync leaves out GET, HEAD and OPTIONS entries before it pages and counts", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;

                    DateTime baseUtc = DateTime.UtcNow.AddHours(-1);
                    string[] methods = { "POST", "get", "PUT", "HEAD", "DELETE", "OPTIONS", "GET", "PATCH" };
                    List<string> writeIds = new List<string>();
                    for (int i = 0; i < methods.Length; i++)
                    {
                        RequestHistoryEntry entry = BuildEntry(methods[i], "/api/v1/fleets", 200, "usr_one");
                        entry.CreatedUtc = baseUtc.AddMinutes(i);
                        await db.RequestHistory.CreateAsync(entry, null).ConfigureAwait(false);
                        if (methods[i] is "POST" or "PUT" or "DELETE" or "PATCH") writeIds.Add(entry.Id);
                    }

                    EnumerationResult<RequestHistoryEntry> all = await db.RequestHistory.EnumerateAsync(new RequestHistoryQuery { PageNumber = 1, PageSize = 25 }).ConfigureAwait(false);
                    AssertEqual(8, (int)all.TotalRecords, "every entry is listed without the flag");

                    EnumerationResult<RequestHistoryEntry> firstPage = await db.RequestHistory.EnumerateAsync(new RequestHistoryQuery
                    {
                        ExcludeReadMethods = true,
                        PageNumber = 1,
                        PageSize = 3
                    }).ConfigureAwait(false);
                    AssertEqual(4, (int)firstPage.TotalRecords, "the total counts only the writes");
                    AssertEqual(2, firstPage.TotalPages, "the pages cover only the writes");
                    AssertEqual(3, firstPage.Objects.Count, "the first page is full of writes");
                    AssertTrue(firstPage.Objects.TrueForAll(entry => writeIds.Contains(entry.Id)), "the first page holds only writes");

                    EnumerationResult<RequestHistoryEntry> secondPage = await db.RequestHistory.EnumerateAsync(new RequestHistoryQuery
                    {
                        ExcludeReadMethods = true,
                        PageNumber = 2,
                        PageSize = 3
                    }).ConfigureAwait(false);
                    AssertEqual(1, secondPage.Objects.Count, "the second page holds the last write");
                    AssertTrue(writeIds.Contains(secondPage.Objects[0].Id), "the last entry is a write");

                    EnumerationResult<RequestHistoryEntry> withMethod = await db.RequestHistory.EnumerateAsync(new RequestHistoryQuery
                    {
                        ExcludeReadMethods = true,
                        Method = "get",
                        PageNumber = 1,
                        PageSize = 25
                    }).ConfigureAwait(false);
                    AssertEqual(0, (int)withMethod.TotalRecords, "a method filter naming a read method finds nothing beside the exclusion");
                }
            }));

            cases.Add(CaseAsync("delete_by_filter_removes_matching_rows", "DeleteByFilterAsync removes matching request-history rows", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;

                    RequestHistoryEntry oldEntry = BuildEntry("GET", "/api/v1/status", 200, "usr_old");
                    oldEntry.CreatedUtc = DateTime.UtcNow.AddDays(-10);
                    RequestHistoryEntry newEntry = BuildEntry("GET", "/api/v1/status", 200, "usr_new");
                    newEntry.CreatedUtc = DateTime.UtcNow;

                    await db.RequestHistory.CreateAsync(oldEntry, BuildDetail(oldEntry.Id, "{\"age\":\"old\"}")).ConfigureAwait(false);
                    await db.RequestHistory.CreateAsync(newEntry, BuildDetail(newEntry.Id, "{\"age\":\"new\"}")).ConfigureAwait(false);

                    int deleted = await db.RequestHistory.DeleteByFilterAsync(new RequestHistoryQuery
                    {
                        ToUtc = DateTime.UtcNow.AddDays(-5)
                    }).ConfigureAwait(false);

                    AssertEqual(1, deleted);
                    AssertNull(await db.RequestHistory.ReadAsync(oldEntry.Id).ConfigureAwait(false));
                    AssertNotNull(await db.RequestHistory.ReadAsync(newEntry.Id).ConfigureAwait(false));
                }
            }));

            cases.Add(CaseAsync("read_non_existent_returns_null_audit", "ReadAsync NonExistent ReturnsNull", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    RequestHistoryRecord? result = await db.RequestHistory.ReadAsync("req_nonexistent").ConfigureAwait(false);
                    AssertNull(result);
                }
            }));

            cases.Add(CaseAsync("delete_by_filter_no_match_deletes_nothing_audit", "DeleteByFilterAsync NoMatch DeletesNothing", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;

                    RequestHistoryEntry entry = BuildEntry("GET", "/api/v1/fleets", 200, "usr_keep");
                    await db.RequestHistory.CreateAsync(entry, BuildDetail(entry.Id, "{\"keep\":true}")).ConfigureAwait(false);

                    int deleted = await db.RequestHistory.DeleteByFilterAsync(new RequestHistoryQuery
                    {
                        Route = "/does/not/match"
                    }).ConfigureAwait(false);

                    AssertEqual(0, deleted);
                    AssertNotNull(await db.RequestHistory.ReadAsync(entry.Id).ConfigureAwait(false));
                }
            }));

            cases.Add(SqliteCaseAsync("sqlite_queued_cancellation_and_transaction_fault_release_file_gate", "SQLite queued cancellation and transaction fault release file gate", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (SqliteConnection holder = new SqliteConnection(testDb.ConnectionString))
                {
                    await holder.OpenAsync().ConfigureAwait(false);
                    using (SqliteCommand trigger = holder.CreateCommand())
                    {
                        trigger.CommandText = "CREATE TRIGGER fail_gate_detail BEFORE INSERT ON request_history_detail " +
                            "BEGIN SELECT RAISE(ABORT, 'injected detail failure'); END;";
                        await trigger.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    IDisposable held = await SqliteWriteGate.EnterAsync(holder, CancellationToken.None).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("SQLite gate did not return a lease.");
                    List<Task> pending = new List<Task>();
                    try
                    {
                        RequestHistoryEntry cancelledEntry = BuildEntry("GET", "/gate/cancelled", 200, "usr_cancelled");
                        using (CancellationTokenSource cancellation = new CancellationTokenSource())
                        {
                            TaskCompletionSource<bool> cancelledStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            Task<RequestHistoryRecord> cancelled = StartIndependent(
                                () => testDb.Driver.RequestHistory.CreateAsync(cancelledEntry, BuildDetail(cancelledEntry.Id, "{}"), cancellation.Token),
                                cancelledStarted);
                            pending.Add(cancelled);
                            await cancelledStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                            AssertFalse(cancelled.IsCompleted, "Queued request-history write must wait before cancellation");
                            cancellation.Cancel();
                            try
                            {
                                await cancelled.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                                throw new InvalidOperationException("Queued request-history write did not cancel.");
                            }
                            catch (OperationCanceledException)
                            {
                            }
                        }

                        RequestHistoryEntry faultEntry = BuildEntry("POST", "/gate/fault", 200, "usr_fault");
                        TaskCompletionSource<bool> faultStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        Task<RequestHistoryRecord> faulted = StartIndependent(
                            () => testDb.Driver.RequestHistory.CreateAsync(faultEntry, BuildDetail(faultEntry.Id, "{}")), faultStarted);
                        pending.Add(faulted);
                        await faultStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                        AssertFalse(faulted.IsCompleted, "Faulting request-history write must wait for the held file gate");

                        TaskCompletionSource<bool> followerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        Task<Fleet> follower = StartIndependent(() => testDb.Driver.Fleets.CreateAsync(new Fleet("after-gate-fault")), followerStarted);
                        pending.Add(follower);
                        await followerStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                        AssertFalse(follower.IsCompleted, "Following direct write must queue behind request-history write");
                        held.Dispose();
                        try
                        {
                            await faulted.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                            throw new InvalidOperationException("Injected detail failure did not fail request-history create.");
                        }
                        catch (SqliteException exception)
                        {
                            AssertTrue(exception.Message.Contains("injected detail failure", StringComparison.Ordinal), "The detail trigger must cause the transaction failure");
                        }
                        await follower.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

                        AssertNull(await testDb.Driver.RequestHistory.ReadAsync(cancelledEntry.Id).ConfigureAwait(false));
                        AssertNull(await testDb.Driver.RequestHistory.ReadAsync(faultEntry.Id).ConfigureAwait(false));
                        using (SqliteCommand details = holder.CreateCommand())
                        {
                            details.CommandText = "SELECT COUNT(*) FROM request_history_detail WHERE request_history_id IN (@cancelled, @fault);";
                            details.Parameters.AddWithValue("@cancelled", cancelledEntry.Id);
                            details.Parameters.AddWithValue("@fault", faultEntry.Id);
                            object? count = await details.ExecuteScalarAsync().ConfigureAwait(false);
                            AssertEqual(0L, Convert.ToInt64(count));
                        }
                        List<Fleet> fleets = await testDb.Driver.Fleets.EnumerateAsync().ConfigureAwait(false);
                        AssertNotNull(fleets.Find(item => item.Name == "after-gate-fault"));
                    }
                    finally
                    {
                        held.Dispose();
                        await DrainAsync(pending).ConfigureAwait(false);
                    }
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "RequestHistory Database",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static RequestHistoryEntry BuildEntry(string method, string route, int statusCode, string userId)
        {
            return new RequestHistoryEntry
            {
                TenantId = "ten_request_history",
                UserId = userId,
                PrincipalDisplay = userId + "@armada",
                AuthMethod = "Session",
                Method = method,
                Route = route,
                RouteTemplate = route,
                QueryString = null,
                StatusCode = statusCode,
                DurationMs = 12.34,
                RequestSizeBytes = 42,
                ResponseSizeBytes = 84,
                RequestContentType = "application/json",
                ResponseContentType = "application/json",
                IsSuccess = statusCode >= 200 && statusCode < 400,
                CreatedUtc = DateTime.UtcNow
            };
        }

        private static RequestHistoryDetail BuildDetail(string requestHistoryId, string requestBody)
        {
            return new RequestHistoryDetail
            {
                RequestHistoryId = requestHistoryId,
                RequestHeadersJson = "{\"X-Test\":\"true\"}",
                ResponseHeadersJson = "{\"Content-Type\":\"application/json\"}",
                RequestBodyText = requestBody,
                ResponseBodyText = "{\"status\":\"ok\"}"
            };
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
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
            return new TestCaseDescriptor(SuiteId, caseId, displayName, _ => Task.CompletedTask,
                new List<string> { tag }, true, "Requires SQLite file-gate behavior.");
        }

        #endregion
    }
}
