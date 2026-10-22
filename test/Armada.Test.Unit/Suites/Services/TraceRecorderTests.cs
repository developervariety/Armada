namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Archive;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Behaviour of the trace recorder: what a trace walks to after the raw rows are gone, idempotent
    /// rewrites, trace expiry, short-id lookup, recording before data expiry deletes a row, and archive upload.
    /// </summary>
    public class TraceRecorderTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Trace Recorder";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("A swept mission's chain walks to its landing and deployment after the raw rows are deleted", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TraceRecorder recorder = new TraceRecorder(db, QuietLogging());
                    LandedWork work = await CreateLandedWorkAsync(db).ConfigureAwait(false);

                    await db.Events.CreateAsync(new ArmadaEvent("mission.completed", "done") { EntityType = "mission", EntityId = work.Mission.Id, MissionId = work.Mission.Id, VoyageId = work.Voyage.Id }).ConfigureAwait(false);
                    await db.Events.CreateAsync(new ArmadaEvent("mission.status_changed", "rescue started") { EntityType = "mission", EntityId = work.Rescue.Id, MissionId = work.Rescue.Id }).ConfigureAwait(false);

                    TraceSweepResult sweep = await recorder.SweepAsync(1000).ConfigureAwait(false);
                    AssertTrue(sweep.Done, "one sweep reaches the newest event");
                    AssertEqual(2, sweep.EventsRead, "the sweep reads both events");
                    AssertTrue(sweep.RecordsWritten >= 6, "the mission, rescue, voyage, merge entry, check run and deployment are recorded: " + sweep.RecordsWritten);

                    await db.Deployments.DeleteAsync(work.Deployment.Id).ConfigureAwait(false);
                    await db.MergeEntries.DeleteAsync(work.Merge.Id).ConfigureAwait(false);
                    await db.CheckRuns.DeleteAsync(work.Check.Id).ConfigureAwait(false);
                    await db.Missions.DeleteAsync(work.Rescue.Id).ConfigureAwait(false);
                    await db.Missions.DeleteAsync(work.Mission.Id).ConfigureAwait(false);
                    await db.Voyages.DeleteAsync(work.Voyage.Id).ConfigureAwait(false);

                    TraceChain chain = await recorder.TraceAsync(work.Mission.Id).ConfigureAwait(false);
                    AssertEqual(work.Mission.Id, chain.ResolvedId);
                    AssertEqual("table", chain.Source, "the chain comes from the trace table once the rows are gone");
                    AssertEqual(work.Mission.Id, chain.Records[0].Id, "the requested record is first");
                    AssertEqual("commit-work", chain.Records[0].CommitSha);
                    AssertEqual("commit-work", chain.Records[0].LandedSha, "a mission whose merge entry landed names its landed commit");

                    TraceDocument? merge = chain.Records.FirstOrDefault(record => record.Id == work.Merge.Id);
                    AssertNotNull(merge, "the chain reaches the merge entry");
                    AssertEqual(MergeStatusEnum.Landed.ToString(), merge!.Status);
                    AssertEqual("commit-work", merge.LandedSha);

                    TraceDocument? deployment = chain.Records.FirstOrDefault(record => record.Id == work.Deployment.Id);
                    AssertNotNull(deployment, "the chain reaches the deployment");
                    AssertEqual("image@sha256:deployed", deployment!.ArtifactIdentity);

                    TraceDocument? check = chain.Records.FirstOrDefault(record => record.Id == work.Check.Id);
                    AssertNotNull(check, "the chain reaches the check run");
                    AssertEqual(41, check!.Check!.Passed);
                    AssertEqual(1, check.Check.Failed);

                    AssertNotNull(chain.Records.FirstOrDefault(record => record.Id == work.Voyage.Id), "the chain reaches the voyage");
                    TraceDocument? rescue = chain.Records.FirstOrDefault(record => record.Id == work.Rescue.Id);
                    AssertNotNull(rescue, "the chain reaches the rescue mission");
                    AssertTrue(rescue!.Links.Any(link => link.Id == work.Mission.Id && link.Relation == "parent"), "the rescue links to the mission it rescued");
                    AssertEqual(0, chain.MissingIds.Count, "no linked record is missing: " + String.Join(", ", chain.MissingIds));

                    TraceChain fromDeployment = await recorder.TraceAsync(work.Deployment.Id).ConfigureAwait(false);
                    AssertTrue(fromDeployment.Records.Any(record => record.Id == work.Mission.Id), "the walk also runs from the deployment back to its mission");
                }
            });

            await RunTest("The backfill records history that has no events, in resumable batches", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TraceRecorder recorder = new TraceRecorder(db, QuietLogging());
                    Vessel vessel = await db.Vessels.CreateAsync(new Vessel("trace-backfill-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    DateTime old = DateTime.UtcNow.AddDays(-80);
                    List<string> ids = new List<string>();
                    for (int index = 0; index < 3; index++)
                    {
                        Mission mission = await db.Missions.CreateAsync(new Mission("old work " + index, "")
                        {
                            VesselId = vessel.Id,
                            Status = MissionStatusEnum.Complete,
                            CreatedUtc = old.AddMinutes(index)
                        }).ConfigureAwait(false);
                        ids.Add(mission.Id);
                    }
                    MergeEntry merge = await db.MergeEntries.CreateAsync(new MergeEntry("armada/old-work") { MissionId = ids[0], VesselId = vessel.Id, Status = MergeStatusEnum.Landed }).ConfigureAwait(false);

                    TraceBackfillResult first = await recorder.BackfillAsync(DateTime.UtcNow.AddDays(-90), 2).ConfigureAwait(false);
                    AssertFalse(first.Done, "a batch that spends its budget is not done");
                    AssertEqual(2, first.RowsRead);
                    TraceBackfillResult second = await recorder.BackfillAsync(DateTime.UtcNow.AddDays(-90), 100).ConfigureAwait(false);
                    AssertTrue(second.Done, "the next batch resumes and reaches the newest row");
                    TraceBackfillResult third = await recorder.BackfillAsync(DateTime.UtcNow.AddDays(-90), 100).ConfigureAwait(false);
                    AssertTrue(third.RowsRead <= 1, "a finished backfill revisits at most the row at its cursor: " + third.RowsRead);

                    foreach (string id in ids)
                        AssertNotNull(await db.TraceRecords.ReadAsync(id).ConfigureAwait(false), "every old mission is recorded without any event: " + id);
                    AssertNotNull(await db.TraceRecords.ReadAsync(merge.Id).ConfigureAwait(false), "the merge entry of an old mission is recorded");
                    AssertEqual(4L, await db.TraceRecords.CountAsync().ConfigureAwait(false), "each record is stored once across batches");
                }
            });

            await RunTest("Recording a record again replaces it and keeps one copy of each link", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TraceRecorder recorder = new TraceRecorder(db, QuietLogging());
                    LandedWork work = await CreateLandedWorkAsync(db).ConfigureAwait(false);

                    TraceRecord? first = await recorder.RecordAsync(TraceEntityKindEnum.Mission, work.Mission.Id).ConfigureAwait(false);
                    Mission stored = (await db.Missions.ReadAsync(work.Mission.Id).ConfigureAwait(false))!;
                    stored.FailureReason = "rewritten";
                    await db.Missions.UpdateAsync(stored).ConfigureAwait(false);
                    TraceRecord? second = await recorder.RecordAsync(TraceEntityKindEnum.Mission, work.Mission.Id).ConfigureAwait(false);

                    AssertNotNull(first);
                    AssertNotNull(second);
                    AssertEqual(1L, await db.TraceRecords.CountAsync().ConfigureAwait(false), "two writes of one id store one record");
                    TraceRecord? read = await db.TraceRecords.ReadAsync(work.Mission.Id).ConfigureAwait(false);
                    AssertEqual("rewritten", read!.Document.FailureReason, "the later write wins");
                    List<TraceLink> toVoyage = await db.TraceRecords.EnumerateLinksToAsync(work.Voyage.Id, 50).ConfigureAwait(false);
                    AssertEqual(1, toVoyage.Count, "a rewrite does not duplicate the mission's link to its voyage");

                    await db.MergeEntries.DeleteAsync(work.Merge.Id).ConfigureAwait(false);
                    await db.Missions.DeleteAsync(work.Mission.Id).ConfigureAwait(false);
                    TraceRecord? afterDelete = await recorder.RecordAsync(TraceEntityKindEnum.Mission, work.Mission.Id).ConfigureAwait(false);
                    AssertNull(afterDelete, "a record whose source row is gone is not rebuilt");
                    AssertEqual("rewritten", (await db.TraceRecords.ReadAsync(work.Mission.Id).ConfigureAwait(false))!.Document.FailureReason, "the stored record is kept when its source row is gone");
                }
            });

            await RunTest("Trace expiry removes records older than the retention and their links", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TraceRecorder recorder = new TraceRecorder(db, QuietLogging());
                    await db.TraceRecords.UpsertAsync(BuildRecord("msn_expired", DateTime.UtcNow.AddDays(-400), "vyg_shared")).ConfigureAwait(false);
                    await db.TraceRecords.UpsertAsync(BuildRecord("msn_retained", DateTime.UtcNow.AddDays(-300), "vyg_shared")).ConfigureAwait(false);

                    AssertEqual(0, await recorder.ExpireAsync(0).ConfigureAwait(false), "retention 0 keeps every record");
                    int deleted = await recorder.ExpireAsync(365).ConfigureAwait(false);

                    AssertEqual(1, deleted, "only the record past retention is deleted");
                    AssertNull(await db.TraceRecords.ReadAsync("msn_expired").ConfigureAwait(false));
                    AssertNotNull(await db.TraceRecords.ReadAsync("msn_retained").ConfigureAwait(false));
                    List<TraceLink> toVoyage = await db.TraceRecords.EnumerateLinksToAsync("vyg_shared", 50).ConfigureAwait(false);
                    AssertEqual(1, toVoyage.Count, "the expired record's links go with it");
                    AssertEqual("msn_retained", toVoyage[0].Id);
                }
            });

            await RunTest("A short id resolves with or without its kind prefix and an ambiguous one lists candidates", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TraceRecorder recorder = new TraceRecorder(db, QuietLogging());
                    await db.TraceRecords.UpsertAsync(BuildRecord("msn_abc12345-first", DateTime.UtcNow, null)).ConfigureAwait(false);
                    await db.TraceRecords.UpsertAsync(BuildRecord("msn_abc12399-second", DateTime.UtcNow, null)).ConfigureAwait(false);
                    await db.TraceRecords.UpsertAsync(BuildRecord("msnXabc12345-wildcard", DateTime.UtcNow, null)).ConfigureAwait(false);

                    TraceChain prefixed = await recorder.TraceAsync("msn_abc12345").ConfigureAwait(false);
                    AssertEqual("msn_abc12345-first", prefixed.ResolvedId, "a short id with its prefix resolves; '_' is not a wildcard");

                    TraceChain bare = await recorder.TraceAsync("abc12399").ConfigureAwait(false);
                    AssertEqual("msn_abc12399-second", bare.ResolvedId, "a short id without its prefix resolves");

                    TraceChain ambiguous = await recorder.TraceAsync("msn_abc123").ConfigureAwait(false);
                    AssertNull(ambiguous.ResolvedId, "an ambiguous short id resolves to none");
                    AssertEqual(2, ambiguous.Candidates.Count, "both matches are listed");

                    TraceChain tooShort = await recorder.TraceAsync("msn_ab").ConfigureAwait(false);
                    AssertNull(tooShort.ResolvedId, "a short id below the minimum length resolves to none");
                    AssertEqual(0, tooShort.Candidates.Count);
                }
            });

            await RunTest("The expiry observer records a row's last state before the row is deleted", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TraceRecorder recorder = new TraceRecorder(db, QuietLogging());
                    TraceExpiryObserver observer = new TraceExpiryObserver(recorder);
                    LandedWork work = await CreateLandedWorkAsync(db).ConfigureAwait(false);
                    await recorder.RecordAsync(TraceEntityKindEnum.Mission, work.Mission.Id).ConfigureAwait(false);

                    Mission changed = (await db.Missions.ReadAsync(work.Mission.Id).ConfigureAwait(false))!;
                    changed.FailureReason = "last state";
                    await db.Missions.UpdateAsync(changed).ConfigureAwait(false);
                    await observer.OnRowExpiringAsync("missions", new Dictionary<string, object?> { ["id"] = work.Mission.Id }, CancellationToken.None).ConfigureAwait(false);
                    await observer.OnRowExpiringAsync("events", new Dictionary<string, object?> { ["entity_type"] = "deployment", ["entity_id"] = work.Deployment.Id }, CancellationToken.None).ConfigureAwait(false);
                    await observer.OnRowExpiringAsync("signals", new Dictionary<string, object?> { ["id"] = "sig_ignored" }, CancellationToken.None).ConfigureAwait(false);

                    await db.MergeEntries.DeleteAsync(work.Merge.Id).ConfigureAwait(false);
                    await db.Missions.DeleteAsync(work.Mission.Id).ConfigureAwait(false);
                    await db.Deployments.DeleteAsync(work.Deployment.Id).ConfigureAwait(false);

                    TraceRecord? mission = await db.TraceRecords.ReadAsync(work.Mission.Id).ConfigureAwait(false);
                    AssertEqual("last state", mission!.Document.FailureReason, "an expiring row is recorded again, so the trace holds its last state");
                    TraceRecord? deployment = await db.TraceRecords.ReadAsync(work.Deployment.Id).ConfigureAwait(false);
                    AssertNotNull(deployment, "an expiring event records the untraced record it names");
                    AssertEqual("image@sha256:deployed", deployment!.Document.ArtifactIdentity);
                    AssertEqual(2L, await db.TraceRecords.CountAsync().ConfigureAwait(false), "a row of an untraced table writes nothing");
                }
            });

            await RunTest("An ended record is uploaded to the archive once and keeps a verified pointer", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    FakeArchiveSink sink = new FakeArchiveSink();
                    ArchiveSettings archive = new ArchiveSettings { Enabled = true, Endpoint = "https://archive.example", Bucket = "bucket", CredentialsFile = "/nonexistent/credentials", KeyPrefix = "armada" };
                    TraceRecorder recorder = new TraceRecorder(db, QuietLogging())
                    {
                        ArchiveSettingsProvider = () => archive,
                        ArchiveSinkFactory = _ => sink
                    };
                    LandedWork work = await CreateLandedWorkAsync(db).ConfigureAwait(false);

                    await recorder.RecordAsync(TraceEntityKindEnum.Voyage, work.Voyage.Id).ConfigureAwait(false);
                    AssertEqual(0, sink.Puts.Count, "a record that has not ended stays local");

                    Mission ended = (await db.Missions.ReadAsync(work.Mission.Id).ConfigureAwait(false))!;
                    ended.CompletedUtc = DateTime.UtcNow;
                    await db.Missions.UpdateAsync(ended).ConfigureAwait(false);
                    await recorder.RecordAsync(TraceEntityKindEnum.Mission, work.Mission.Id).ConfigureAwait(false);
                    await recorder.RecordAsync(TraceEntityKindEnum.Mission, work.Mission.Id).ConfigureAwait(false);

                    AssertEqual(1, sink.Puts.Count, "an unchanged ended record is uploaded once");
                    string expectedKey = ArchiveKeys.Trace("armada", ended.CreatedUtc, work.Mission.Id);
                    AssertEqual(expectedKey, sink.Puts[0], "the record goes to the trace/ key layout");
                    TraceRecord? stored = await db.TraceRecords.ReadAsync(work.Mission.Id).ConfigureAwait(false);
                    TraceArchivePointer pointer = stored!.Document.Archive.Single();
                    AssertEqual(expectedKey, pointer.Key);
                    AssertEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sink.Objects[expectedKey])).ToLowerInvariant(), pointer.Sha256, "the pointer holds the verified hash");

                    await db.MergeEntries.DeleteAsync(work.Merge.Id).ConfigureAwait(false);
                    await db.Missions.DeleteAsync(work.Mission.Id).ConfigureAwait(false);
                    await db.TraceRecords.DeleteAsync(new[] { work.Mission.Id }).ConfigureAwait(false);
                    TraceChain chain = await recorder.TraceAsync(work.Voyage.Id).ConfigureAwait(false);
                    AssertTrue(chain.MissingIds.Contains(work.Mission.Id), "the mission is gone from the table and the database");
                    AssertTrue(chain.ArchivedKeys.Contains(expectedKey), "the trace names the archived copy of the missing record");
                }
            });

            await RunTest("Expiry of a mission row through the archive wiring leaves a trace record that points to the archive and reads back after the row is gone", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    LoggingModule logging = QuietLogging();
                    string staging = Path.Combine(Path.GetTempPath(), "armada_trace_expiry_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        Mission mission = await db.Missions.CreateAsync(new Mission("expiring work", "")
                        {
                            Status = MissionStatusEnum.Complete,
                            BranchName = "armada/expiring-work",
                            FailureReason = "kept in the trace"
                        }).ConfigureAwait(false);
                        mission.CompletedUtc = DateTime.UtcNow.AddDays(-40);
                        await db.Missions.UpdateAsync(mission).ConfigureAwait(false);

                        FakeArchiveSink sink = new FakeArchiveSink();
                        ArchiveSettings archive = new ArchiveSettings { Enabled = true, Endpoint = "https://archive.example", Bucket = "bucket", CredentialsFile = "/nonexistent/credentials", KeyPrefix = "armada" };
                        DataExpiryService expiry = new DataExpiryService(logging, db, 30, 0) { StagingDirectory = staging };
                        TraceRecorder recorder = new TraceRecorder(db, logging);
                        HistoryArchiveWiring.Connect(expiry, recorder, () => archive, _ => sink);

                        DataExpiryResult result = await expiry.PurgeExpiredDataAsync().ConfigureAwait(false);

                        AssertEqual(1, result.Deleted("missions"), "the expired mission row is deleted: " + result);
                        AssertNull(await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false), "the mission row is gone");
                        TraceRecord? trace = await db.TraceRecords.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertNotNull(trace, "the expiring mission left a trace record");
                        AssertEqual("kept in the trace", trace!.Document.FailureReason, "the trace record holds the row's last state");
                        TraceArchivePointer pointer = trace.Document.Archive.Single();
                        AssertTrue(pointer.Key.StartsWith("armada/trace/", StringComparison.Ordinal) && sink.Objects.ContainsKey(pointer.Key), "the trace record points to its archived copy: " + pointer.Key);
                        AssertTrue(sink.Objects.Keys.Any(k => k.StartsWith("armada/raw/db/missions/", StringComparison.Ordinal)), "the mission row itself is exported to the archive");

                        await db.TraceRecords.DeleteAsync(new[] { mission.Id }).ConfigureAwait(false);
                        TraceChain chain = await recorder.TraceAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual("archive", chain.Source, "a record gone from the table and the database is read from the archive");
                        AssertEqual("kept in the trace", chain.Records.Single(r => r.Id == mission.Id).FailureReason, "the trace returns the archived record's content, not only its key");
                    }
                    finally
                    {
                        if (Directory.Exists(staging)) Directory.Delete(staging, true);
                    }
                }
            });
        }

        private static LoggingModule QuietLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static TraceRecord BuildRecord(string id, DateTime lastUpdateUtc, string? voyageId)
        {
            TraceDocument document = new TraceDocument
            {
                Id = id,
                Kind = TraceEntityKindEnum.Mission,
                Status = "Complete",
                CreatedUtc = lastUpdateUtc,
                LastUpdateUtc = lastUpdateUtc
            };
            if (voyageId != null) document.Links.Add(new TraceLink { Kind = TraceEntityKindEnum.Voyage, Id = voyageId, Relation = "voyage" });
            return new TraceRecord
            {
                Id = id,
                Kind = TraceEntityKindEnum.Mission,
                Status = "Complete",
                SubjectCreatedUtc = lastUpdateUtc,
                SubjectLastUpdateUtc = lastUpdateUtc,
                Document = document
            };
        }

        private static async Task<LandedWork> CreateLandedWorkAsync(SqliteDatabaseDriver db)
        {
            Vessel vessel = await db.Vessels.CreateAsync(new Vessel("trace-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
            Voyage voyage = await db.Voyages.CreateAsync(new Voyage("trace voyage") { Status = VoyageStatusEnum.Complete }).ConfigureAwait(false);
            Mission mission = await db.Missions.CreateAsync(new Mission("traced work", "")
            {
                VesselId = vessel.Id,
                VoyageId = voyage.Id,
                Status = MissionStatusEnum.Complete,
                BranchName = "armada/traced-work",
                CommitHash = "commit-work",
                StartFromRef = "main",
                Persona = "Worker",
                AgentOutput = "Work is done."
            }).ConfigureAwait(false);
            Mission rescue = await db.Missions.CreateAsync(new Mission("rescue of traced work", "")
            {
                VesselId = vessel.Id,
                ParentMissionId = mission.Id,
                Status = MissionStatusEnum.Complete
            }).ConfigureAwait(false);
            MergeEntry merge = await db.MergeEntries.CreateAsync(new MergeEntry("armada/traced-work")
            {
                MissionId = mission.Id,
                VesselId = vessel.Id,
                Status = MergeStatusEnum.Landed
            }).ConfigureAwait(false);
            CheckRun check = await db.CheckRuns.CreateAsync(new CheckRun
            {
                MissionId = mission.Id,
                VesselId = vessel.Id,
                Status = CheckRunStatusEnum.Failed,
                ExitCode = 1,
                TestSummary = new CheckRunTestSummary { Total = 42, Passed = 41, Failed = 1 }
            }).ConfigureAwait(false);
            Deployment deployment = await db.Deployments.CreateAsync(new Deployment
            {
                VesselId = vessel.Id,
                MissionId = mission.Id,
                SourceCommit = "commit-work",
                ArtifactIdentity = "image@sha256:deployed"
            }).ConfigureAwait(false);
            return new LandedWork
            {
                Voyage = voyage,
                Mission = mission,
                Rescue = rescue,
                Merge = merge,
                Check = check,
                Deployment = deployment
            };
        }

        private sealed class LandedWork
        {
            public Voyage Voyage { get; set; } = null!;
            public Mission Mission { get; set; } = null!;
            public Mission Rescue { get; set; } = null!;
            public MergeEntry Merge { get; set; } = null!;
            public CheckRun Check { get; set; } = null!;
            public Deployment Deployment { get; set; } = null!;
        }
    }
}
