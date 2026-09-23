namespace Armada.Test.Unit.Suites.Database
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    public class CaptainDatabaseTests : TestSuite
    {
        public override string Name => "Captain Database";

        protected override async Task RunTestsAsync()
        {
            await RunTest("CreateAsync returns captain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
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
            });

            await RunTest("ReadAsync returns created captain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Captain captain = new Captain("read-test");
                    await db.Captains.CreateAsync(captain);

                    Captain? result = await db.Captains.ReadAsync(captain.Id);
                    AssertNotNull(result);
                    AssertEqual(captain.Id, result!.Id);
                    AssertEqual(CaptainStateEnum.Idle, result.State);
                }
            });

            await RunTest("ReadByNameAsync returns correct captain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Captain captain = new Captain("name-lookup");
                    await db.Captains.CreateAsync(captain);

                    Captain? result = await db.Captains.ReadByNameAsync("name-lookup");
                    AssertNotNull(result);
                    AssertEqual(captain.Id, result!.Id);
                }
            });

            await RunTest("UpdateAsync modifies captain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
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
            });

            await RunTest("UpdateAsync clears captain model when empty string is assigned", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Captain captain = new Captain("clear-model-test");
                    captain.Model = "gpt-5.4";
                    await db.Captains.CreateAsync(captain);

                    captain.Model = "";
                    await db.Captains.UpdateAsync(captain);

                    Captain? result = await db.Captains.ReadAsync(captain.Id);
                    AssertNull(result!.Model);
                }
            });

            await RunTest("Provider credential columns round-trip through create and update", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Captain captain = new Captain("credential-test");
                    captain.ApiKey = "captain-key-not-a-real-credential";
                    captain.ApiBaseUrl = "https://proxy.example.test";
                    await db.Captains.CreateAsync(captain);

                    Captain? read = await db.Captains.ReadAsync(captain.Id);
                    AssertEqual("captain-key-not-a-real-credential", read!.ApiKey);
                    AssertEqual("https://proxy.example.test", read.ApiBaseUrl);

                    read.ApiKey = "rotated-key-not-a-real-credential";
                    read.ApiBaseUrl = null;
                    await db.Captains.UpdateAsync(read);

                    Captain? updated = await db.Captains.ReadAsync(captain.Id);
                    AssertEqual("rotated-key-not-a-real-credential", updated!.ApiKey);
                    AssertNull(updated.ApiBaseUrl);
                }
            });

            await RunTest("DeleteAsync removes captain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Captain captain = new Captain("to-delete");
                    await db.Captains.CreateAsync(captain);

                    await db.Captains.DeleteAsync(captain.Id);
                    AssertNull(await db.Captains.ReadAsync(captain.Id));
                }
            });

            await RunTest("EnumerateByStateAsync filters correctly", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Captain c1 = new Captain("idle-1");
                    Captain c2 = new Captain("working-1");
                    c2.State = CaptainStateEnum.Working;
                    Captain c3 = new Captain("idle-2");

                    await db.Captains.CreateAsync(c1);
                    await db.Captains.CreateAsync(c2);
                    await db.Captains.CreateAsync(c3);

                    List<Captain> idle = await db.Captains.EnumerateByStateAsync(CaptainStateEnum.Idle);
                    AssertEqual(2, idle.Count);

                    List<Captain> working = await db.Captains.EnumerateByStateAsync(CaptainStateEnum.Working);
                    AssertEqual(1, working.Count);
                }
            });

            await RunTest("UpdateStateAsync changes state", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Captain captain = new Captain("state-test");
                    await db.Captains.CreateAsync(captain);

                    await db.Captains.UpdateStateAsync(captain.Id, CaptainStateEnum.Stalled);

                    Captain? result = await db.Captains.ReadAsync(captain.Id);
                    AssertEqual(CaptainStateEnum.Stalled, result!.State);
                }
            });

            await RunTest("UpdateHeartbeatAsync sets timestamp", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Captain captain = new Captain("heartbeat-test");
                    await db.Captains.CreateAsync(captain);
                    AssertNull(captain.LastHeartbeatUtc);

                    await db.Captains.UpdateHeartbeatAsync(captain.Id);

                    Captain? result = await db.Captains.ReadAsync(captain.Id);
                    AssertNotNull(result!.LastHeartbeatUtc);
                }
            });

            // Stall detection measures the age of LastHeartbeatUtc, which only real agent output may
            // advance. The process-liveness loop writes LastProcessAliveUtc instead, so a silent but
            // running agent still ages toward the stall threshold. Each case seeds an old output
            // heartbeat so a newer write is distinguishable without waiting on the clock.

            await RunTest("UpdateProcessAliveAsync does not advance the output heartbeat", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Captain captain = new Captain("liveness-captain");
                    captain.LastHeartbeatUtc = DateTime.UtcNow.AddHours(-1);
                    await db.Captains.CreateAsync(captain);

                    Captain? seeded = await db.Captains.ReadAsync(captain.Id);
                    AssertNotNull(seeded, "Captain should exist after create");
                    AssertTrue(seeded!.LastHeartbeatUtc.HasValue, "Seeded output heartbeat should be stored");
                    DateTime heartbeatBefore = seeded.LastHeartbeatUtc!.Value;

                    await db.Captains.UpdateProcessAliveAsync(captain.Id);

                    Captain? afterAlive = await db.Captains.ReadAsync(captain.Id);
                    AssertNotNull(afterAlive, "Captain should exist after process-alive update");
                    AssertTrue(afterAlive!.LastProcessAliveUtc.HasValue, "Process-alive timestamp should be set");
                    AssertEqual(
                        heartbeatBefore.ToString("O"),
                        afterAlive.LastHeartbeatUtc!.Value.ToString("O"),
                        "Output heartbeat must be unchanged by a process-alive update");
                    AssertTrue(
                        afterAlive.LastProcessAliveUtc!.Value > heartbeatBefore,
                        "Process-alive timestamp should be newer than the old output heartbeat");
                }
            });

            await RunTest("UpdateHeartbeatAsync advances an old output heartbeat", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Captain captain = new Captain("heartbeat-advance-captain");
                    captain.LastHeartbeatUtc = DateTime.UtcNow.AddHours(-1);
                    await db.Captains.CreateAsync(captain);

                    Captain? seeded = await db.Captains.ReadAsync(captain.Id);
                    DateTime oldBeat = seeded!.LastHeartbeatUtc!.Value;

                    await db.Captains.UpdateHeartbeatAsync(captain.Id);

                    Captain? advanced = await db.Captains.ReadAsync(captain.Id);
                    AssertTrue(
                        advanced!.LastHeartbeatUtc!.Value > oldBeat,
                        "Real agent output must advance the output heartbeat");
                }
            });

            await RunTest("Process-liveness column round-trips through the captain mapper", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Captain captain = new Captain("liveness-roundtrip-captain");
                    await db.Captains.CreateAsync(captain);

                    Captain? fresh = await db.Captains.ReadAsync(captain.Id);
                    AssertNotNull(fresh, "Captain should exist");
                    AssertFalse(
                        fresh!.LastProcessAliveUtc.HasValue,
                        "A captain that has never been observed alive carries no process-alive time");

                    await db.Captains.UpdateProcessAliveAsync(captain.Id);

                    Captain? observed = await db.Captains.ReadAsync(captain.Id);
                    AssertTrue(
                        observed!.LastProcessAliveUtc.HasValue,
                        "Process-alive time should survive the round-trip through the mapper");
                }
            });

            await RunTest("ExistsAsync works correctly", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Captain captain = new Captain("exists-test");
                    await db.Captains.CreateAsync(captain);

                    AssertTrue(await db.Captains.ExistsAsync(captain.Id));
                    AssertFalse(await db.Captains.ExistsAsync("cpt_nonexistent"));
                }
            });
        }
    }
}
