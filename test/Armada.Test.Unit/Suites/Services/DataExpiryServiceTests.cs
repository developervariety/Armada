namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    public class DataExpiryServiceTests : TestSuite
    {
        public override string Name => "Data Expiry Service";

        protected override async Task RunTestsAsync()
        {
            await RunTest("PurgeExpiredDataAsync DisabledWhenRetentionZero", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    Voyage oldVoyage = new Voyage("Old Voyage");
                    oldVoyage.Status = VoyageStatusEnum.Complete;
                    oldVoyage.CompletedUtc = DateTime.UtcNow.AddDays(-60);
                    await testDb.Driver.Voyages.CreateAsync(oldVoyage);

                    DataExpiryService service = new DataExpiryService(logging, testDb.Driver, 0);
                    DataExpiryResult result = await service.PurgeExpiredDataAsync();

                    AssertEqual(0, result.Total);
                    AssertEqual(0, result.Tables.Count, "a disabled run purges no table");
                    AssertNotNull(await testDb.Driver.Voyages.ReadAsync(oldVoyage.Id));
                }
            });

            await RunTest("PurgeExpiredDataAsync RemovesOldCompletedVoyagesAndMissions", async () =>
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

                    DataExpiryService service = new DataExpiryService(logging, db, 30);
                    DataExpiryResult result = await service.PurgeExpiredDataAsync();

                    AssertEqual(1, result.Deleted("voyages"));
                    AssertEqual(1, result.Deleted("missions"));

                    AssertNull(await db.Voyages.ReadAsync(oldVoyage.Id));
                    AssertNull(await db.Missions.ReadAsync(oldMission.Id));

                    AssertNotNull(await db.Voyages.ReadAsync(recentVoyage.Id));
                }
            });

            await RunTest("PurgeExpiredDataAsync RemovesOldReadSignals", async () =>
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

                    DataExpiryService service = new DataExpiryService(logging, db, 30);
                    DataExpiryResult result = await service.PurgeExpiredDataAsync();

                    AssertEqual(1, result.Deleted("signals"));
                    AssertNull(await db.Signals.ReadAsync(oldSignal.Id));
                    AssertNotNull(await db.Signals.ReadAsync(recentSignal.Id));
                }
            });

            await RunTest("PurgeExpiredDataAsync RemovesOldEvents", async () =>
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

                    DataExpiryService service = new DataExpiryService(logging, db, 30);
                    await service.PurgeExpiredDataAsync();

                    AssertNull(await db.Events.ReadAsync(oldEvent.Id));
                    AssertNotNull(await db.Events.ReadAsync(recentEvent.Id));
                }
            });

            await RunTest("PurgeExpiredDataAsync LogsOneSummaryWithPerTableCountsOnEveryRun", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    string logPath = Path.Combine(Path.GetTempPath(), "armada_data_expiry_" + Guid.NewGuid().ToString("N") + ".log");
                    List<string> summaries;
                    try
                    {
                        using (LoggingModule logging = new LoggingModule(logPath, FileLoggingMode.SingleLogFile, false))
                        {
                            logging.Settings.EnableConsole = false;
                            DataExpiryService service = new DataExpiryService(logging, testDb.Driver, 30);
                            await service.PurgeExpiredDataAsync();
                            await logging.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        summaries = File.ReadAllLines(logPath).Where(line => line.Contains("data expiry summary:")).ToList();
                    }
                    finally
                    {
                        if (File.Exists(logPath)) File.Delete(logPath);
                    }

                    AssertEqual(1, summaries.Count, "a run that deletes nothing still logs its summary");
                    foreach (string table in new[] { "voyages=0", "missions=0", "signals=0", "events=0", "docks=0", "merge_entries=0" })
                        AssertTrue(summaries[0].Contains(table), "the summary names " + table + ": " + summaries[0]);
                }
            });
        }
    }
}
