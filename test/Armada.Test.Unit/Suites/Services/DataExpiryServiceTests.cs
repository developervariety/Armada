namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    public class DataExpiryServiceTests : TestSuite
    {
        public override string Name => "Data Expiry Service";

        protected override async Task RunTestsAsync()
        {
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

            await RunTest("PurgeExpiredDataAsync ArchivesExpiredRowsBeforeDeletingThemAndKeepsThemWithoutAnArchive", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    string staging = Path.Combine(Path.GetTempPath(), "armada_expiry_staging_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        RequestHistoryEntry expired = new RequestHistoryEntry { Route = "/api/v1/expired", CreatedUtc = DateTime.UtcNow.AddDays(-10) };
                        await db.RequestHistory.CreateAsync(expired, new RequestHistoryDetail { RequestHistoryId = expired.Id, RequestBodyText = "expired body" });
                        ArchiveSettings archive = new ArchiveSettings();
                        FakeArchiveSink sink = new FakeArchiveSink();
                        RecordingExpiryObserver observer = new RecordingExpiryObserver();
                        DataExpiryService service = new DataExpiryService(logging, db, 0, 0, 7)
                        {
                            ArchiveSettingsProvider = () => archive,
                            ArchiveSinkFactory = _ => sink,
                            StagingDirectory = staging
                        };
                        service.Observers.Add(observer);

                        DataExpiryResult kept = await service.PurgeExpiredDataAsync();
                        AssertEqual(0, kept.Total, "with no archive configured nothing is deleted");
                        AssertNotNull(await db.RequestHistory.ReadAsync(expired.Id), "the expired request is kept");
                        AssertEqual(0, observer.Rows.Count, "a run that deletes nothing reports no expiring row");

                        archive.Enabled = true;
                        archive.Endpoint = "http://archive.example.invalid";
                        archive.Bucket = "history";
                        archive.CredentialsFile = Path.Combine(staging, "credentials");
                        sink.FailKeysContaining = "/request_history_detail/";
                        bool threw = false;
                        try
                        {
                            await service.PurgeExpiredDataAsync();
                        }
                        catch (InvalidOperationException ex)
                        {
                            threw = ex.Message.Contains("not verified", StringComparison.Ordinal);
                        }
                        AssertTrue(threw, "an unverified export stops the run with a named failure");
                        AssertNotNull(await db.RequestHistory.ReadAsync(expired.Id), "rows whose export was not verified are kept");

                        sink.FailKeysContaining = null;
                        observer.Rows.Clear();
                        DataExpiryResult archived = await service.PurgeExpiredDataAsync();
                        AssertEqual(1, archived.Deleted("request_history"), "the archived request is deleted: " + archived);
                        AssertNull(await db.RequestHistory.ReadAsync(expired.Id), "the archived request is gone");

                        string key = sink.Objects.Keys.Single(k => k.StartsWith("armada/raw/db/request_history/", StringComparison.Ordinal));
                        AssertTrue(key.EndsWith(".jsonl.gz", StringComparison.Ordinal), "the export is gzip JSON lines: " + key);
                        string line;
                        using (System.IO.Compression.GZipStream gzip = new System.IO.Compression.GZipStream(new MemoryStream(sink.Objects[key]), System.IO.Compression.CompressionMode.Decompress))
                        using (StreamReader reader = new StreamReader(gzip))
                        {
                            line = reader.ReadToEnd().Trim();
                        }
                        AssertTrue(line.Contains(expired.Id, StringComparison.Ordinal) && line.Contains("/api/v1/expired", StringComparison.Ordinal), "the exported row carries its columns: " + line);
                        AssertTrue(sink.Objects.Keys.Any(k => k.StartsWith("armada/raw/db/request_history_detail/", StringComparison.Ordinal)), "the detail rows are exported too");
                        AssertTrue(observer.Rows.Contains("request_history:" + expired.Id), "the observer sees the expiring row: " + String.Join(", ", observer.Rows));
                        AssertFalse(Directory.Exists(staging) && Directory.EnumerateFiles(staging, "expiry-*").Any(), "no staging file is left behind");
                    }
                    finally
                    {
                        if (Directory.Exists(staging)) Directory.Delete(staging, true);
                    }
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
                    foreach (string table in new[] { "voyages=0", "missions=0", "signals=0", "events=0", "docks=0", "merge_entries=0", "kept_incident_latest=0", "kept_runbook_latest=0", "kept_tombstones=0", "kept_reversals=0" })
                        AssertTrue(summaries[0].Contains(table), "the summary names " + table + ": " + summaries[0]);
                }
            });
        }

        private sealed class RecordingExpiryObserver : IDataExpiryObserver
        {
            public List<string> Rows { get; } = new List<string>();

            public Task OnRowExpiringAsync(string table, IReadOnlyDictionary<string, object?> row, CancellationToken token)
            {
                Rows.Add(table + ":" + (row.TryGetValue("id", out object? id) ? id : "?"));
                return Task.CompletedTask;
            }
        }
    }
}
