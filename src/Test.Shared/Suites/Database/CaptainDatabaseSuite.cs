namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for the captain database methods not covered by the captain suite: create
    /// with a model, update (including model clearing), and state and heartbeat updates. Each
    /// case runs against its own fresh SQLite store.
    /// </summary>
    public sealed class CaptainDatabaseSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Database.CaptainDatabase";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Captain Database suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("create_async_returns_captain", "CreateAsync returns captain", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Captain captain = new Captain("claude-1", AgentRuntimeEnum.ClaudeCode);
                    captain.Model = "gpt-5.4";
                    Captain result = await db.Captains.CreateAsync(captain);
                    Captain? read = await db.Captains.ReadAsync(captain.Id);

                    AssertNotNull(result);
                    AssertNotNull(read);
                    AssertEqual("claude-1", result.Name);
                    AssertEqual(AgentRuntimeEnum.ClaudeCode, result.Runtime);
                    AssertEqual("gpt-5.4", result.Model);
                    AssertEqual("gpt-5.4", read!.Model);
                }
            }));

            cases.Add(CaseAsync("update_async_modifies_captain", "UpdateAsync modifies captain", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Captain captain = new Captain("update-test");
                    await db.Captains.CreateAsync(captain);

                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = "msn_test";
                    captain.ProcessId = 12345;
                    captain.RecoveryAttempts = 2;
                    captain.Model = "gpt-5.4-mini";
                    await db.Captains.UpdateAsync(captain);

                    Captain? result = await db.Captains.ReadAsync(captain.Id);
                    AssertEqual(CaptainStateEnum.Working, result!.State);
                    AssertEqual("msn_test", result.CurrentMissionId);
                    AssertEqual(12345, result.ProcessId);
                    AssertEqual(2, result.RecoveryAttempts);
                    AssertEqual("gpt-5.4-mini", result.Model);
                }
            }));

            cases.Add(CaseAsync("update_async_clears_captain_model_when_empty_string_is_assigned", "UpdateAsync clears captain model when empty string is assigned", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Captain captain = new Captain("clear-model-test");
                    captain.Model = "gpt-5.4";
                    await db.Captains.CreateAsync(captain);

                    captain.Model = "";
                    await db.Captains.UpdateAsync(captain);

                    Captain? result = await db.Captains.ReadAsync(captain.Id);
                    AssertNull(result!.Model);
                }
            }));

            cases.Add(CaseAsync("update_state_async_changes_state", "UpdateStateAsync changes state", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Captain captain = new Captain("state-test");
                    await db.Captains.CreateAsync(captain);

                    await db.Captains.UpdateStateAsync(captain.Id, CaptainStateEnum.Stalled);

                    Captain? result = await db.Captains.ReadAsync(captain.Id);
                    AssertEqual(CaptainStateEnum.Stalled, result!.State);
                }
            }));

            cases.Add(CaseAsync("update_heartbeat_async_sets_timestamp", "UpdateHeartbeatAsync sets timestamp", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Captain captain = new Captain("heartbeat-test");
                    await db.Captains.CreateAsync(captain);
                    AssertNull(captain.LastHeartbeatUtc);

                    await db.Captains.UpdateHeartbeatAsync(captain.Id);

                    Captain? result = await db.Captains.ReadAsync(captain.Id);
                    AssertNotNull(result!.LastHeartbeatUtc);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Captain Database",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
