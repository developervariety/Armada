namespace Armada.Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="DataExpiryService"/>: retention-driven purge of old completed
    /// voyages, missions, read signals, and events over a live SQLite store. The disabled-retention
    /// case asserts the no-op guard; positive cases assert removal of aged records while sparing
    /// recent ones.
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

                    DataExpiryService service = new DataExpiryService(logging, "Data Source=dummy;Mode=Memory;Cache=Shared", 0);
                    int deleted = await service.PurgeExpiredDataAsync();
                    AssertEqual(0, deleted);
                }
            }));

            cases.Add(CaseAsync("purge_removes_old_completed_voyages_and_missions", "PurgeExpiredDataAsync RemovesOldCompletedVoyagesAndMissions", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
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

                    DataExpiryService service = new DataExpiryService(logging, testDb.ConnectionString, 30);
                    int deleted = await service.PurgeExpiredDataAsync();

                    AssertTrue(deleted > 0);

                    AssertNull(await db.Voyages.ReadAsync(oldVoyage.Id));
                    AssertNull(await db.Missions.ReadAsync(oldMission.Id));

                    AssertNotNull(await db.Voyages.ReadAsync(recentVoyage.Id));
                }
            }));

            cases.Add(CaseAsync("purge_removes_old_read_signals", "PurgeExpiredDataAsync RemovesOldReadSignals", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    Signal oldSignal = new Signal(SignalTypeEnum.Nudge, "old");
                    oldSignal.Read = true;
                    oldSignal.CreatedUtc = DateTime.UtcNow.AddDays(-60);
                    await db.Signals.CreateAsync(oldSignal);

                    Signal recentSignal = new Signal(SignalTypeEnum.Nudge, "recent");
                    await db.Signals.CreateAsync(recentSignal);

                    DataExpiryService service = new DataExpiryService(logging, testDb.ConnectionString, 30);
                    await service.PurgeExpiredDataAsync();

                    AssertNotNull(await db.Signals.ReadAsync(recentSignal.Id));
                }
            }));

            cases.Add(CaseAsync("purge_removes_old_events", "PurgeExpiredDataAsync RemovesOldEvents", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaEvent oldEvent = new ArmadaEvent("test.event", "Old event");
                    oldEvent.CreatedUtc = DateTime.UtcNow.AddDays(-60);
                    await db.Events.CreateAsync(oldEvent);

                    ArmadaEvent recentEvent = new ArmadaEvent("test.event", "Recent event");
                    await db.Events.CreateAsync(recentEvent);

                    DataExpiryService service = new DataExpiryService(logging, testDb.ConnectionString, 30);
                    await service.PurgeExpiredDataAsync();

                    List<ArmadaEvent> remaining = await db.Events.EnumerateRecentAsync();
                    AssertTrue(remaining.Count >= 1);
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
