namespace Armada.Test.Unit.Suites.Recovery
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Round-trip tests for the v38 schema migration: confirms the new merge-entry
    /// classification columns and mission recovery-attempt counters survive a write
    /// and read through the SQLite backend.
    /// </summary>
    public class SchemaMigrationV38RoundTripTests : TestSuite
    {
        /// <summary>Test suite name.</summary>
        public override string Name => "Schema Migration V38 Round Trip";

        /// <summary>Run the round-trip cases.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("MergeEntry_NewFields_RoundTripThroughDatabase", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MergeEntry entry = new MergeEntry("captain-branch-v38", "main");
                    entry.MergeFailureClass = MergeFailureClassEnum.TextConflict;
                    entry.ConflictedFiles = "[\"src/Foo.cs\",\"src/Bar.cs\"]";
                    entry.MergeFailureSummary = "merge: 2 files in conflict";
                    entry.DiffLineCount = 42;

                    await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    MergeEntry? readBack = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "merge entry should be readable after create");
                    AssertEqual(MergeFailureClassEnum.TextConflict, readBack!.MergeFailureClass!.Value, "MergeFailureClass should round-trip");
                    AssertEqual("[\"src/Foo.cs\",\"src/Bar.cs\"]", readBack.ConflictedFiles, "ConflictedFiles should round-trip");
                    AssertEqual("merge: 2 files in conflict", readBack.MergeFailureSummary, "MergeFailureSummary should round-trip");
                    AssertEqual(42, readBack.DiffLineCount, "DiffLineCount should round-trip");
                }
            });

            await RunTest("Mission_NewFields_RoundTripThroughDatabase", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("v38 Fleet");
                    await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("v38 Vessel", "https://github.com/test/v38");
                    vessel.FleetId = fleet.Id;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Voyage voyage = new Voyage("v38 Voyage", "round trip");
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    DateTime recoveryActionTime = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
                    Mission mission = new Mission("v38 Mission", "test recovery fields");
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vessel.Id;
                    mission.RecoveryAttempts = 2;
                    mission.LastRecoveryActionUtc = recoveryActionTime;

                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "mission should be readable after create");
                    AssertEqual(2, readBack!.RecoveryAttempts, "RecoveryAttempts should round-trip");
                    AssertNotNull(readBack.LastRecoveryActionUtc, "LastRecoveryActionUtc should round-trip");
                    AssertEqual(recoveryActionTime, readBack.LastRecoveryActionUtc!.Value, "LastRecoveryActionUtc value should match");
                }
            });

            await RunTest("MergeEntry_NewFields_NullDefaultsPersistAsNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MergeEntry entry = new MergeEntry("captain-branch-defaults", "main");
                    await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    MergeEntry? readBack = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "merge entry should be readable");
                    AssertNull(readBack!.MergeFailureClass, "MergeFailureClass should default to null");
                    AssertNull(readBack.ConflictedFiles, "ConflictedFiles should default to null");
                    AssertNull(readBack.MergeFailureSummary, "MergeFailureSummary should default to null");
                    AssertEqual(0, readBack.DiffLineCount, "DiffLineCount should default to 0");
                }
            });
        }
    }
}
