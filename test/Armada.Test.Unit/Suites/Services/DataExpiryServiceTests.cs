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

                    DataExpiryService service = new DataExpiryService(logging, testDb.Driver, 0, 0);
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

                    DataExpiryService service = new DataExpiryService(logging, db, 30, 0);
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

                    DataExpiryService service = new DataExpiryService(logging, db, 30, 0);
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

                    DataExpiryService service = new DataExpiryService(logging, db, 30, 0);
                    await service.PurgeExpiredDataAsync();

                    AssertNull(await db.Events.ReadAsync(oldEvent.Id));
                    AssertNotNull(await db.Events.ReadAsync(recentEvent.Id));
                }
            });

            await RunTest("PurgeExpiredDataAsync RemovesProductionFactsOlderThanTheirRetention", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    DateTime expired = DateTime.UtcNow.AddDays(-400);
                    DateTime retained = DateTime.UtcNow.AddDays(-300);

                    await db.MissionAttemptFacts.CreateAsync(new MissionAttemptFact { MissionId = "msn_old", RootMissionId = "msn_old", CreatedUtc = expired });
                    await db.MissionAttemptFacts.CreateAsync(new MissionAttemptFact { MissionId = "msn_new", RootMissionId = "msn_new", CreatedUtc = retained });
                    await db.PreparationClaimObservations.CreateAsync(new PreparationClaimObservation { ObjectiveId = "obj_x", ClaimId = "old", EvidenceFingerprint = new string('a', 64), CreatedUtc = expired });
                    await db.PreparationClaimObservations.CreateAsync(new PreparationClaimObservation { ObjectiveId = "obj_x", ClaimId = "new", EvidenceFingerprint = new string('a', 64), CreatedUtc = retained });
                    await db.LaneStateTransitions.CreateAsync(new LaneStateTransition { LaneKey = "old", Capacity = 1, ValidForSeconds = 60, CreatedUtc = expired });
                    await db.LaneStateTransitions.CreateAsync(new LaneStateTransition { LaneKey = "new", Capacity = 1, ValidForSeconds = 60, CreatedUtc = retained });

                    DataExpiryResult result = await new DataExpiryService(logging, db, 30, 365).PurgeExpiredDataAsync();

                    AssertEqual(1, result.Deleted("mission_attempt_facts"));
                    AssertEqual(1, result.Deleted("preparation_claim_observations"));
                    AssertEqual(1, result.Deleted("lane_state_transitions"));
                    ProductionFactQuery all = new ProductionFactQuery();
                    AssertEqual("msn_new", (await db.MissionAttemptFacts.EnumerateAsync(all)).Items.Single().MissionId);
                    AssertEqual("new", (await db.PreparationClaimObservations.EnumerateAsync(all)).Items.Single().ClaimId);
                    AssertEqual("new", (await db.LaneStateTransitions.EnumerateAsync(all)).Items.Single().LaneKey);

                    DataExpiryResult kept = await new DataExpiryService(logging, db, 30, 0).PurgeExpiredDataAsync();
                    AssertTrue(kept.Deleted("mission_attempt_facts") == null, "fact retention 0 keeps facts forever");
                    AssertEqual(1, (await db.MissionAttemptFacts.EnumerateAsync(all)).Items.Count);
                }
            });

            await RunTest("PurgeExpiredDataAsync RemovesRequestHistoryOlderThanItsRetention", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    DateTime now = DateTime.UtcNow;

                    RequestHistoryEntry expired = new RequestHistoryEntry { Route = "/api/v1/expired", CreatedUtc = now.AddDays(-10) };
                    RequestHistoryEntry insideRetention = new RequestHistoryEntry { Route = "/api/v1/retained", CreatedUtc = now.AddDays(-7).AddHours(1) };
                    await db.RequestHistory.CreateAsync(expired, new RequestHistoryDetail { RequestHistoryId = expired.Id, RequestBodyText = "expired body" });
                    await db.RequestHistory.CreateAsync(insideRetention, new RequestHistoryDetail { RequestHistoryId = insideRetention.Id, RequestBodyText = "retained body" });

                    DataExpiryResult disabled = await new DataExpiryService(logging, db, 0, 0, 0).PurgeExpiredDataAsync();
                    AssertTrue(disabled.Deleted("request_history") == null, "request-history retention 0 keeps request history forever");
                    AssertNotNull(await db.RequestHistory.ReadAsync(expired.Id), "request history is kept while its retention is off");

                    DataExpiryResult result = await new DataExpiryService(logging, db, 0, 0, 7).PurgeExpiredDataAsync();

                    AssertEqual(1, result.Deleted("request_history"), "one expired request is purged: " + result);
                    AssertEqual(1, result.Deleted("request_history_detail"), "its detail row is purged with it: " + result);
                    AssertNull(await db.RequestHistory.ReadAsync(expired.Id), "the expired request is gone");
                    RequestHistoryRecord? kept = await db.RequestHistory.ReadAsync(insideRetention.Id);
                    AssertNotNull(kept, "a request inside the retention period is kept");
                    AssertEqual("retained body", kept!.Detail?.RequestBodyText, "the kept request keeps its detail");
                    AssertTrue(result.Deleted("events") == null, "data retention 0 leaves operational tables unpurged: " + result);
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
                            DataExpiryService service = new DataExpiryService(logging, testDb.Driver, 30, 0);
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
                    foreach (string table in new[] { "voyages=0", "missions=0", "signals=0", "events=0", "docks=0", "merge_entries=0", "kept_incident_latest=0", "kept_tombstones=0", "kept_reversals=0" })
                        AssertTrue(summaries[0].Contains(table), "the summary names " + table + ": " + summaries[0]);
                }
            });
        }
    }
}
