namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for the signal database methods: create, read, recipient-filtered enumeration
    /// (unread-only and all), recent enumeration with a limit, unread-by-type enumeration and the index
    /// that serves it, and marking a signal read. Includes a negative case for reading a missing signal.
    /// Each case runs against its own fresh SQLite store.
    /// </summary>
    public sealed class SignalDatabaseSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Database.SignalDatabase";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Signal Database suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("create_async_returns_signal", "CreateAsync returns signal", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Signal signal = new Signal(SignalTypeEnum.Assignment, "{\"test\":true}");
                    Signal result = await db.Signals.CreateAsync(signal);

                    AssertNotNull(result);
                    AssertEqual(SignalTypeEnum.Assignment, result.Type);
                }
            }));

            cases.Add(CaseAsync("read_async_returns_created_signal", "ReadAsync returns created signal", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Signal signal = new Signal(SignalTypeEnum.Heartbeat);
                    await db.Signals.CreateAsync(signal);

                    Signal? result = await db.Signals.ReadAsync(signal.Id);
                    AssertNotNull(result);
                    AssertEqual(signal.Id, result!.Id);
                    AssertFalse(result.Read);
                }
            }));

            cases.Add(CaseAsync("enumerate_by_recipient_async_filters_correctly", "EnumerateByRecipientAsync filters correctly", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Captain captain = new Captain("receiver");
                    await db.Captains.CreateAsync(captain);

                    Signal s1 = new Signal(SignalTypeEnum.Nudge, "msg1");
                    s1.ToCaptainId = captain.Id;
                    Signal s2 = new Signal(SignalTypeEnum.Nudge, "msg2");
                    s2.ToCaptainId = captain.Id;
                    s2.Read = true;
                    Signal s3 = new Signal(SignalTypeEnum.Nudge, "msg3");

                    await db.Signals.CreateAsync(s1);
                    await db.Signals.CreateAsync(s2);
                    await db.Signals.CreateAsync(s3);

                    List<Signal> unread = await db.Signals.EnumerateByRecipientAsync(captain.Id, unreadOnly: true);
                    AssertEqual(1, unread.Count);

                    List<Signal> all = await db.Signals.EnumerateByRecipientAsync(captain.Id, unreadOnly: false);
                    AssertEqual(2, all.Count);
                }
            }));

            cases.Add(CaseAsync("enumerate_recent_async_returns_limited", "EnumerateRecentAsync returns limited", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    for (int i = 0; i < 5; i++)
                    {
                        await db.Signals.CreateAsync(new Signal(SignalTypeEnum.Nudge, "msg" + i));
                    }

                    List<Signal> recent = await db.Signals.EnumerateRecentAsync(3);
                    AssertEqual(3, recent.Count);
                }
            }));

            cases.Add(CaseAsync("mark_read_async_sets_read_flag", "MarkReadAsync sets read flag", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Signal signal = new Signal(SignalTypeEnum.Mail, "test");
                    await db.Signals.CreateAsync(signal);
                    AssertFalse(signal.Read);

                    await db.Signals.MarkReadAsync(signal.Id);

                    Signal? result = await db.Signals.ReadAsync(signal.Id);
                    AssertTrue(result!.Read);
                }
            }));

            // Audit addition: read of a missing signal id must return null (not-found path).
            cases.Add(CaseAsync("read_async_missing_returns_null", "ReadAsync missing returns null", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Signal? result = await db.Signals.ReadAsync("sig_nonexistent");
                    AssertNull(result);
                }
            }));

            // The mailbox and wake pollers read one page of the unread signals of one type while unread signals of
            // other types pile up; the page holds only that type, newest first, bounded by the page size.
            cases.Add(CaseAsync("enumerate_unread_by_type_returns_newest_unread_of_that_type", "EnumerateAsync by type and unread returns the newest unread signals of that type", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    DateTime baseUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

                    for (int i = 0; i < 30; i++)
                    {
                        Signal progress = new Signal(SignalTypeEnum.Progress, "progress" + i);
                        progress.CreatedUtc = baseUtc.AddMinutes(100 + i);
                        await db.Signals.CreateAsync(progress);
                    }

                    Signal readWake = new Signal(SignalTypeEnum.Wake, "read wake");
                    readWake.Read = true;
                    readWake.CreatedUtc = baseUtc.AddMinutes(50);
                    await db.Signals.CreateAsync(readWake);

                    Signal oldestWake = new Signal(SignalTypeEnum.Wake, "wake 1");
                    oldestWake.CreatedUtc = baseUtc.AddMinutes(1);
                    Signal middleWake = new Signal(SignalTypeEnum.Wake, "wake 2");
                    middleWake.CreatedUtc = baseUtc.AddMinutes(2);
                    Signal newestWake = new Signal(SignalTypeEnum.Wake, "wake 3");
                    newestWake.CreatedUtc = baseUtc.AddMinutes(3);
                    await db.Signals.CreateAsync(oldestWake);
                    await db.Signals.CreateAsync(newestWake);
                    await db.Signals.CreateAsync(middleWake);

                    Signal mail = new Signal(SignalTypeEnum.Mail, "mail");
                    mail.CreatedUtc = baseUtc.AddMinutes(4);
                    await db.Signals.CreateAsync(mail);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.SignalType = SignalTypeEnum.Wake.ToString();
                    query.UnreadOnly = true;
                    EnumerationResult<Signal> page = await db.Signals.EnumerateAsync(query);

                    AssertEqual(3L, page.TotalRecords, "unread Wake count");
                    AssertEqual(2, page.Objects.Count, "page size bound");
                    AssertEqual(newestWake.Id, page.Objects[0].Id, "newest unread Wake first");
                    AssertEqual(middleWake.Id, page.Objects[1].Id, "second-newest unread Wake second");
                }
            }));

            // The unread-by-type count and page must be served by the type/read/created index; a plan that scans the
            // signals table makes every poll read every signal ever recorded.
            cases.Add(CaseAsync("unread_by_type_query_plan_uses_type_read_index", "Unread-by-type count and page search the type/read/created index", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    using (SqliteConnection conn = new SqliteConnection(testDb.ConnectionString))
                    {
                        await conn.OpenAsync();
                        string[] statements =
                        {
                            "SELECT COUNT(*) FROM signals WHERE type = @type AND read = 0;",
                            "SELECT * FROM signals WHERE type = @type AND read = 0 ORDER BY created_utc DESC LIMIT 50 OFFSET 0;"
                        };

                        foreach (string statement in statements)
                        {
                            List<string> details = new List<string>();
                            using (SqliteCommand cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = "EXPLAIN QUERY PLAN " + statement;
                                cmd.Parameters.AddWithValue("@type", SignalTypeEnum.Wake.ToString());
                                using (SqliteDataReader reader = await cmd.ExecuteReaderAsync())
                                {
                                    while (await reader.ReadAsync())
                                        details.Add(reader.GetString(reader.GetOrdinal("detail")));
                                }
                            }

                            string plan = String.Join(" | ", details);
                            AssertContains("idx_signals_type_read_created", plan, "plan for " + statement);
                            foreach (string detail in details)
                                AssertFalse(detail.StartsWith("SCAN signals", StringComparison.Ordinal), "plan scans signals for " + statement + ": " + plan);
                        }
                    }
                }
            }, sqliteOnly: true));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Signal Database",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body, bool sqliteOnly = false)
        {
            // A case that reads the SQLite query plan through a raw SqliteConnection cannot run against a server
            // provider's connection string.
            bool skip = sqliteOnly && !TestDatabaseConfig.IsSqlite;
            string? skipReason = skip ? "Case reads the SQLite query plan; not applicable to server providers." : null;

            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag },
                skip: skip,
                skipReason: skipReason);
        }

        #endregion
    }
}
