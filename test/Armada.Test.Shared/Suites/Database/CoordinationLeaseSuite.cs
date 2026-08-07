namespace Armada.Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for the durable coordination-lease store, which provides restart-safe,
    /// multi-instance-safe mutual exclusion via atomic compare-and-swap with TTL takeover.
    /// Covers the happy path (acquire/renew/release/read/purge) and the failure paths that make
    /// the lease safe (contention blocks a second holder; a stale holder cannot renew or release;
    /// an expired lease can be taken over).
    /// </summary>
    public sealed class CoordinationLeaseSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Coordination Lease suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("acquire_on_free_name_succeeds", "Acquire on a free name succeeds", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    bool acquired = await db.CoordinationLeases.TryAcquireAsync("merge-queue:process", "holder-a", TimeSpan.FromMinutes(5));
                    AssertTrue(acquired, "first acquire");

                    CoordinationLease? lease = await db.CoordinationLeases.ReadAsync("merge-queue:process");
                    AssertNotNull(lease);
                    AssertEqual("holder-a", lease!.Holder);
                }
            }));

            cases.Add(CaseAsync("second_holder_blocked_while_held", "A second holder is blocked while the lease is held", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    AssertTrue(await db.CoordinationLeases.TryAcquireAsync("lock", "holder-a", TimeSpan.FromMinutes(5)), "holder-a acquire");

                    bool second = await db.CoordinationLeases.TryAcquireAsync("lock", "holder-b", TimeSpan.FromMinutes(5));
                    AssertFalse(second, "holder-b must not acquire while holder-a holds the lease");

                    CoordinationLease? lease = await db.CoordinationLeases.ReadAsync("lock");
                    AssertEqual("holder-a", lease!.Holder);
                }
            }));

            cases.Add(CaseAsync("reacquire_by_same_holder_succeeds", "Re-acquire by the same holder succeeds", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    AssertTrue(await db.CoordinationLeases.TryAcquireAsync("lock", "holder-a", TimeSpan.FromMinutes(5)));
                    AssertTrue(await db.CoordinationLeases.TryAcquireAsync("lock", "holder-a", TimeSpan.FromMinutes(5)), "same holder re-acquire");
                }
            }));

            cases.Add(CaseAsync("expired_lease_taken_over", "An expired lease can be taken over by another holder", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    // Acquire with an already-elapsed TTL so the lease is immediately eligible for takeover.
                    AssertTrue(await db.CoordinationLeases.TryAcquireAsync("lock", "dead-holder", TimeSpan.Zero));

                    bool taken = await db.CoordinationLeases.TryAcquireAsync("lock", "holder-b", TimeSpan.FromMinutes(5));
                    AssertTrue(taken, "an expired lease must be acquirable by a new holder");

                    CoordinationLease? lease = await db.CoordinationLeases.ReadAsync("lock");
                    AssertEqual("holder-b", lease!.Holder);
                }
            }));

            cases.Add(CaseAsync("renew_by_holder_extends", "Renew by the holder extends the lease", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    AssertTrue(await db.CoordinationLeases.TryAcquireAsync("lock", "holder-a", TimeSpan.FromMinutes(1)));
                    CoordinationLease? before = await db.CoordinationLeases.ReadAsync("lock");

                    bool renewed = await db.CoordinationLeases.TryRenewAsync("lock", "holder-a", TimeSpan.FromMinutes(10));
                    AssertTrue(renewed, "holder renew");

                    CoordinationLease? after = await db.CoordinationLeases.ReadAsync("lock");
                    AssertTrue(after!.ExpiresUtc >= before!.ExpiresUtc, "expiry must not move backward on renew");
                }
            }));

            cases.Add(CaseAsync("renew_by_non_holder_fails", "Renew by a non-holder fails", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    AssertTrue(await db.CoordinationLeases.TryAcquireAsync("lock", "holder-a", TimeSpan.FromMinutes(5)));

                    bool renewed = await db.CoordinationLeases.TryRenewAsync("lock", "holder-b", TimeSpan.FromMinutes(10));
                    AssertFalse(renewed, "a non-holder must not be able to renew");
                }
            }));

            cases.Add(CaseAsync("release_by_holder_frees_lease", "Release by the holder frees the lease", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    AssertTrue(await db.CoordinationLeases.TryAcquireAsync("lock", "holder-a", TimeSpan.FromMinutes(5)));

                    await db.CoordinationLeases.ReleaseAsync("lock", "holder-a");
                    AssertNull(await db.CoordinationLeases.ReadAsync("lock"), "lease must be gone after release");

                    bool reacquired = await db.CoordinationLeases.TryAcquireAsync("lock", "holder-b", TimeSpan.FromMinutes(5));
                    AssertTrue(reacquired, "a freed lease is acquirable by anyone");
                }
            }));

            cases.Add(CaseAsync("release_by_non_holder_is_noop", "Release by a non-holder does not free the lease", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    AssertTrue(await db.CoordinationLeases.TryAcquireAsync("lock", "holder-a", TimeSpan.FromMinutes(5)));

                    await db.CoordinationLeases.ReleaseAsync("lock", "holder-b");
                    CoordinationLease? lease = await db.CoordinationLeases.ReadAsync("lock");
                    AssertNotNull(lease, "a non-holder release must not free the lease");
                    AssertEqual("holder-a", lease!.Holder);
                }
            }));

            cases.Add(CaseAsync("read_missing_returns_null", "Read of a missing lease returns null", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    AssertNull(await db.CoordinationLeases.ReadAsync("nonexistent"));
                }
            }));

            cases.Add(CaseAsync("purge_expired_removes_only_expired", "Purge removes only expired leases", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    await db.CoordinationLeases.TryAcquireAsync("live", "holder-a", TimeSpan.FromMinutes(5));
                    await db.CoordinationLeases.TryAcquireAsync("dead", "holder-b", TimeSpan.Zero);

                    int purged = await db.CoordinationLeases.PurgeExpiredAsync();
                    AssertTrue(purged >= 1, "at least the expired lease is purged");
                    AssertNotNull(await db.CoordinationLeases.ReadAsync("live"), "the live lease survives");
                    AssertNull(await db.CoordinationLeases.ReadAsync("dead"), "the expired lease is gone");
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: "Database.CoordinationLease",
                displayName: "Coordination Lease",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Database.CoordinationLease",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
