namespace Armada.Test.Unit.Suites.Database
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Round-trip persistence tests for the auto-recovery schema columns:
    /// merge_entries.merge_failure_class / conflicted_files / merge_failure_summary
    /// and missions.recovery_attempts / last_recovery_action_utc.
    /// Verifies the v38 migration is reachable, columns are wired through CRUD,
    /// and reader mapping preserves values.
    /// </summary>
    public class RecoveryColumnsPersistenceTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Recovery Columns Persistence";

        /// <summary>Run all recovery-column persistence tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await MergeEntry_RecoveryColumns_RoundTripAllValues();
            await MergeEntry_RecoveryColumns_DefaultsAreNull();
            await Mission_RecoveryColumns_RoundTripAllValues();
            await Mission_RecoveryColumns_DefaultsAreZeroAndNull();
            await Mission_RecoveryColumns_UpdateIncrementsAndStampsTimestamp();
        }

        private async Task MergeEntry_RecoveryColumns_RoundTripAllValues()
        {
            await RunTest("MergeEntry_RecoveryColumns_RoundTripAllValues", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Vessel vessel = await db.Vessels.CreateAsync(new Vessel("rec-vsl-1", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    MergeEntry entry = new MergeEntry("branch-rec-1", "main")
                    {
                        VesselId = vessel.Id,
                        Status = MergeStatusEnum.Failed,
                        MergeFailureClass = MergeFailureClass.TextConflict,
                        ConflictedFiles = "[\"src/Foo.cs\",\"src/Bar.cs\"]",
                        MergeFailureSummary = "fold conflict in 2 files (Foo.cs, Bar.cs)"
                    };
                    await db.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    MergeEntry? read = await db.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertNotNull(read);
                    AssertEqual((int)MergeFailureClass.TextConflict, (int)read!.MergeFailureClass!.Value);
                    AssertEqual("[\"src/Foo.cs\",\"src/Bar.cs\"]", read.ConflictedFiles);
                    AssertEqual("fold conflict in 2 files (Foo.cs, Bar.cs)", read.MergeFailureSummary);

                    // Update path: change classifier verdict and confirm UPDATE persists it.
                    read.MergeFailureClass = MergeFailureClass.StaleBase;
                    read.ConflictedFiles = null;
                    read.MergeFailureSummary = "rebase needed";
                    await db.MergeEntries.UpdateAsync(read).ConfigureAwait(false);

                    MergeEntry? reread = await db.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertNotNull(reread);
                    AssertEqual((int)MergeFailureClass.StaleBase, (int)reread!.MergeFailureClass!.Value);
                    AssertNull(reread.ConflictedFiles);
                    AssertEqual("rebase needed", reread.MergeFailureSummary);
                }
            });
        }

        private async Task MergeEntry_RecoveryColumns_DefaultsAreNull()
        {
            await RunTest("MergeEntry_RecoveryColumns_DefaultsAreNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Vessel vessel = await db.Vessels.CreateAsync(new Vessel("rec-vsl-2", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    MergeEntry entry = new MergeEntry("branch-rec-2", "main")
                    {
                        VesselId = vessel.Id,
                        Status = MergeStatusEnum.Queued
                    };
                    await db.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    MergeEntry? read = await db.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertNotNull(read);
                    AssertNull(read!.MergeFailureClass);
                    AssertNull(read.ConflictedFiles);
                    AssertNull(read.MergeFailureSummary);
                }
            });
        }

        private async Task Mission_RecoveryColumns_RoundTripAllValues()
        {
            await RunTest("Mission_RecoveryColumns_RoundTripAllValues", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Vessel vessel = await db.Vessels.CreateAsync(new Vessel("rec-vsl-3", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    Voyage voyage = await db.Voyages.CreateAsync(new Voyage("rec voyage", "rec voyage")).ConfigureAwait(false);

                    DateTime stamp = new DateTime(2026, 4, 29, 12, 34, 56, DateTimeKind.Utc);
                    Mission mission = new Mission("rec mission", "rec mission desc")
                    {
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        RecoveryAttempts = 2,
                        LastRecoveryActionUtc = stamp
                    };
                    await db.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Mission? read = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(read);
                    AssertEqual(2, read!.RecoveryAttempts);
                    AssertNotNull(read.LastRecoveryActionUtc);
                    AssertEqual(stamp, read.LastRecoveryActionUtc!.Value);
                }
            });
        }

        private async Task Mission_RecoveryColumns_DefaultsAreZeroAndNull()
        {
            await RunTest("Mission_RecoveryColumns_DefaultsAreZeroAndNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Vessel vessel = await db.Vessels.CreateAsync(new Vessel("rec-vsl-4", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    Voyage voyage = await db.Voyages.CreateAsync(new Voyage("rec voyage 4", "rec voyage 4")).ConfigureAwait(false);

                    Mission mission = new Mission("rec mission 4", "rec mission 4 desc")
                    {
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id
                    };
                    await db.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Mission? read = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(read);
                    AssertEqual(0, read!.RecoveryAttempts);
                    AssertNull(read.LastRecoveryActionUtc);
                }
            });
        }

        private async Task Mission_RecoveryColumns_UpdateIncrementsAndStampsTimestamp()
        {
            await RunTest("Mission_RecoveryColumns_UpdateIncrementsAndStampsTimestamp", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Vessel vessel = await db.Vessels.CreateAsync(new Vessel("rec-vsl-5", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    Voyage voyage = await db.Voyages.CreateAsync(new Voyage("rec voyage 5", "rec voyage 5")).ConfigureAwait(false);

                    Mission mission = new Mission("rec mission 5", "rec mission 5 desc")
                    {
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id
                    };
                    await db.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Mission? read = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(read);
                    DateTime stamp = new DateTime(2026, 4, 29, 1, 2, 3, DateTimeKind.Utc);
                    read!.RecoveryAttempts = 1;
                    read.LastRecoveryActionUtc = stamp;
                    await db.Missions.UpdateAsync(read).ConfigureAwait(false);

                    Mission? reread = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(reread);
                    AssertEqual(1, reread!.RecoveryAttempts);
                    AssertNotNull(reread.LastRecoveryActionUtc);
                    AssertEqual(stamp, reread.LastRecoveryActionUtc!.Value);
                }
            });
        }
    }
}
