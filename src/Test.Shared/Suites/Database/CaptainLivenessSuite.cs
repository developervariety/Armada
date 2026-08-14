namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for captain process-liveness vs output-heartbeat semantics -- the invariant
    /// behind the stall-detection fix. Process-liveness (LastProcessAliveUtc), refreshed while an
    /// agent's process is merely alive, must NOT advance the output heartbeat (LastHeartbeatUtc),
    /// which stall detection measures. If it did, a live-but-silent agent would never be detected
    /// as stalled.
    /// </summary>
    public sealed class CaptainLivenessSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Captain Liveness suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("process_alive_sets_liveness_timestamp", "UpdateProcessAlive sets the liveness timestamp", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Captain captain = await CreateCaptainAsync(db);
                    AssertNull(captain.LastProcessAliveUtc, "liveness starts null");

                    await db.Captains.UpdateProcessAliveAsync(captain.Id);

                    Captain? read = await db.Captains.ReadAsync(captain.Id);
                    AssertNotNull(read);
                    AssertNotNull(read!.LastProcessAliveUtc, "liveness set after UpdateProcessAlive");
                }
            }));

            cases.Add(CaseAsync("process_alive_does_not_advance_output_heartbeat", "UpdateProcessAlive does not advance the output heartbeat", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Captain captain = await CreateCaptainAsync(db);

                    // Establish an output heartbeat, then capture it.
                    await db.Captains.UpdateHeartbeatAsync(captain.Id);
                    Captain? afterHeartbeat = await db.Captains.ReadAsync(captain.Id);
                    AssertNotNull(afterHeartbeat!.LastHeartbeatUtc, "heartbeat set");
                    DateTime heartbeatBefore = afterHeartbeat.LastHeartbeatUtc!.Value;

                    // Refreshing process liveness must leave the output heartbeat untouched.
                    await db.Captains.UpdateProcessAliveAsync(captain.Id);

                    Captain? afterAlive = await db.Captains.ReadAsync(captain.Id);
                    AssertNotNull(afterAlive!.LastProcessAliveUtc, "liveness set");
                    AssertNotNull(afterAlive.LastHeartbeatUtc, "heartbeat still present");
                    AssertEqual(heartbeatBefore, afterAlive.LastHeartbeatUtc!.Value, "output heartbeat must be unchanged by a liveness refresh");
                }
            }));

            cases.Add(CaseAsync("heartbeat_still_advances_independently", "UpdateHeartbeat still advances the output heartbeat", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Captain captain = await CreateCaptainAsync(db);

                    await db.Captains.UpdateHeartbeatAsync(captain.Id);
                    Captain? read = await db.Captains.ReadAsync(captain.Id);
                    AssertNotNull(read!.LastHeartbeatUtc, "heartbeat set by UpdateHeartbeat");
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: "Database.CaptainLiveness",
                displayName: "Captain Liveness",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<Captain> CreateCaptainAsync(DatabaseDriver db)
        {
            TenantMetadata tenant = new TenantMetadata("Liveness " + Guid.NewGuid().ToString("N").Substring(0, 6));
            await db.Tenants.CreateAsync(tenant);

            Captain captain = new Captain();
            captain.TenantId = tenant.Id;
            captain.Name = "cap-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            return await db.Captains.CreateAsync(captain);
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Database.CaptainLiveness",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
