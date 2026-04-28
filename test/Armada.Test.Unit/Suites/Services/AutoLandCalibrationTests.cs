namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    public class AutoLandCalibrationTests : TestSuite
    {
        public override string Name => "Auto Land Calibration";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Increment_StartsAtZero_GoesToOne", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    Vessel vessel = new Vessel("cal-v1", "https://github.com/test/cal1.git");
                    vessel = await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Vessel? before = await db.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(before, "vessel read before increment");
                    AssertEqual(0, before!.AutoLandCalibrationLandedCount, "fresh vessel starts at 0");

                    await db.Vessels.IncrementCalibrationCounterAsync(vessel.Id).ConfigureAwait(false);

                    Vessel? after = await db.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(after, "vessel read after increment");
                    AssertEqual(1, after!.AutoLandCalibrationLandedCount, "counter after one increment");
                }
            });

            await RunTest("Increment_PerVessel_DoesNotAffectOthers", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    Vessel v1 = new Vessel("cal-isolated-a", "https://github.com/test/iso-a.git");
                    v1 = await db.Vessels.CreateAsync(v1).ConfigureAwait(false);
                    Vessel v2 = new Vessel("cal-isolated-b", "https://github.com/test/iso-b.git");
                    v2 = await db.Vessels.CreateAsync(v2).ConfigureAwait(false);

                    await db.Vessels.IncrementCalibrationCounterAsync(v1.Id).ConfigureAwait(false);
                    await db.Vessels.IncrementCalibrationCounterAsync(v1.Id).ConfigureAwait(false);

                    Vessel? r1 = await db.Vessels.ReadAsync(v1.Id).ConfigureAwait(false);
                    Vessel? r2 = await db.Vessels.ReadAsync(v2.Id).ConfigureAwait(false);
                    AssertNotNull(r1, "vessel 1 read");
                    AssertNotNull(r2, "vessel 2 read");
                    AssertEqual(2, r1!.AutoLandCalibrationLandedCount, "incremented vessel count");
                    AssertEqual(0, r2!.AutoLandCalibrationLandedCount, "other vessel unchanged");
                }
            });

            await RunTest("Increment_NeverResetsOnUpdate", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    Vessel vessel = new Vessel("cal-persist", "https://github.com/test/persist.git");
                    vessel = await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    await db.Vessels.IncrementCalibrationCounterAsync(vessel.Id).ConfigureAwait(false);

                    Vessel? loaded = await db.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(loaded, "vessel read");
                    AssertEqual(1, loaded!.AutoLandCalibrationLandedCount, "counter after increment");

                    loaded.Name = "cal-persist-renamed";
                    await db.Vessels.UpdateAsync(loaded).ConfigureAwait(false);

                    Vessel? reread = await db.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(reread, "vessel reread after update");
                    AssertEqual(1, reread!.AutoLandCalibrationLandedCount, "counter survives update");
                }
            });
        }
    }
}
