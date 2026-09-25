namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="DataExpiryService"/>: retention-driven purge of old completed
    /// voyages, missions, read signals, and events through the configured database driver. The
    /// disabled-retention case asserts the no-op guard; positive cases assert removal of aged records
    /// while sparing recent ones.
    /// </summary>
    public sealed class DataExpiryServiceSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the DataExpiryService suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("purge_disabled_when_retention_zero", "PurgeExpiredDataAsync DisabledWhenRetentionZero", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    Voyage oldVoyage = new Voyage("Old Voyage");
                    oldVoyage.Status = VoyageStatusEnum.Complete;
                    oldVoyage.CompletedUtc = DateTime.UtcNow.AddDays(-60);
                    await testDb.Driver.Voyages.CreateAsync(oldVoyage);

                    DataExpiryService service = new DataExpiryService(logging, testDb.Driver, 0, 0);
                    DataExpiryResult result = await service.PurgeExpiredDataAsync();

                    AssertEqual(0, result.Total);
                    AssertEqual(0, result.Tables.Count, "a disabled run purges no table");
                    AssertNotNull(await testDb.Driver.Voyages.ReadAsync(oldVoyage.Id));
                }
            }));

            cases.Add(CaseAsync("purge_removes_old_completed_voyages_and_missions", "PurgeExpiredDataAsync RemovesOldCompletedVoyagesAndMissions", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    Voyage oldVoyage = new Voyage("Old Voyage");
                    oldVoyage.Status = VoyageStatusEnum.Complete;
                    oldVoyage.CompletedUtc = DateTime.UtcNow.AddDays(-60);
                    await db.Voyages.CreateAsync(oldVoyage);

                    Mission oldMission = new Mission("Old Mission");
                    oldMission.VoyageId = oldVoyage.Id;
                    oldMission.Status = MissionStatusEnum.Complete;
                    oldMission.CompletedUtc = DateTime.UtcNow.AddDays(-60);
                    await db.Missions.CreateAsync(oldMission);

                    Voyage recentVoyage = new Voyage("Recent Voyage");
                    recentVoyage.Status = VoyageStatusEnum.Complete;
                    recentVoyage.CompletedUtc = DateTime.UtcNow.AddDays(-5);
                    await db.Voyages.CreateAsync(recentVoyage);

                    DataExpiryService service = new DataExpiryService(logging, db, 30, 0);
                    DataExpiryResult result = await service.PurgeExpiredDataAsync();

                    AssertTrue(result.Total > 0);
                    AssertEqual(1, result.Deleted("voyages"));
                    AssertEqual(1, result.Deleted("missions"));

                    AssertNull(await db.Voyages.ReadAsync(oldVoyage.Id));
                    AssertNull(await db.Missions.ReadAsync(oldMission.Id));

                    AssertNotNull(await db.Voyages.ReadAsync(recentVoyage.Id));
                }
            }));

            cases.Add(CaseAsync("purge_removes_old_read_signals", "PurgeExpiredDataAsync RemovesOldReadSignals", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    Signal oldSignal = new Signal(SignalTypeEnum.Nudge, "old");
                    oldSignal.Read = true;
                    oldSignal.CreatedUtc = DateTime.UtcNow.AddDays(-60);
                    await db.Signals.CreateAsync(oldSignal);

                    Signal recentSignal = new Signal(SignalTypeEnum.Nudge, "recent");
                    await db.Signals.CreateAsync(recentSignal);

                    DataExpiryService service = new DataExpiryService(logging, db, 30, 0);
                    DataExpiryResult result = await service.PurgeExpiredDataAsync();

                    AssertEqual(1, result.Deleted("signals"));
                    AssertNull(await db.Signals.ReadAsync(oldSignal.Id));
                    AssertNotNull(await db.Signals.ReadAsync(recentSignal.Id));
                }
            }));

            cases.Add(CaseAsync("purge_removes_old_events", "PurgeExpiredDataAsync RemovesOldEvents", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaEvent oldEvent = new ArmadaEvent("test.event", "Old event");
                    oldEvent.CreatedUtc = DateTime.UtcNow.AddDays(-60);
                    await db.Events.CreateAsync(oldEvent);

                    ArmadaEvent recentEvent = new ArmadaEvent("test.event", "Recent event");
                    await db.Events.CreateAsync(recentEvent);

                    DataExpiryService service = new DataExpiryService(logging, db, 30, 0);
                    await service.PurgeExpiredDataAsync();

                    AssertNull(await db.Events.ReadAsync(oldEvent.Id));
                    AssertNotNull(await db.Events.ReadAsync(recentEvent.Id));
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.DataExpiryService",
                displayName: "Data Expiry Service",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.DataExpiryService",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
