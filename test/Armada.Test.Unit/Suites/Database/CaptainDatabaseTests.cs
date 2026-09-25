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

        }
    }
}
