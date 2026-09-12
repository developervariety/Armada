namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Database.Mysql;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;
    using MysqlTableQueries = Armada.Core.Database.Mysql.Queries.TableQueries;
    using PostgresqlTableQueries = Armada.Core.Database.Postgresql.Queries.TableQueries;
    using SqliteTableQueries = Armada.Core.Database.Sqlite.Queries.TableQueries;
    using SqlServerTableQueries = Armada.Core.Database.SqlServer.Queries.TableQueries;

    /// <summary>
    /// Tests durable Judge follow-up capture and late merge association.
    /// </summary>
    public class JudgeFollowUpPersistenceTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Judge Follow-up Persistence";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("All providers register the durable follow-up migration", () =>
            {
                AssertMigration(SqliteTableQueries.GetMigrations(), 80, "SQLite");
                AssertMigration(PostgresqlTableQueries.GetMigrations(), 82, "PostgreSQL");
                AssertMigration(SqlServerTableQueries.GetMigrations(), 76, "SQL Server");

                AssertContains("judge_follow_ups", MysqlTableQueries.MigrationV73Statements[0]);
                System.Reflection.MethodInfo? mysqlGetMigrations = typeof(MysqlDatabaseDriver).GetMethod(
                    "GetMigrations",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                AssertNotNull(mysqlGetMigrations, "MySQL must expose its migration registration list internally");
                List<SchemaMigration> mysql = (List<SchemaMigration>)mysqlGetMigrations!.Invoke(null, Array.Empty<object>())!;
                AssertMigration(mysql, 73, "MySQL");
                return Task.CompletedTask;
            });

            await RunTest("Capture persists without a predecessor merge entry and is idempotent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    JudgeFollowUpService service = new JudgeFollowUpService(testDb.Driver, logging);
                    Mission judge = BuildJudgeMission("msn_reviewed-no-merge", "vsl_follow-up-one");

                    JudgeFollowUp first = await service.CaptureAsync(
                        judge, "PASS", "- Add the missing boundary test.").ConfigureAwait(false);
                    JudgeFollowUp second = await service.CaptureAsync(
                        judge, "PASS", "- Add the missing boundary test.").ConfigureAwait(false);

                    AssertEqual(first.Id, second.Id, "Repeated completion handling must keep one canonical record");
                    AssertNull(second.MergeEntryId, "A missing merge entry must not discard or fake the association");
                    List<JudgeFollowUp> pending = await testDb.Driver.JudgeFollowUps.EnumeratePendingAsync().ConfigureAwait(false);
                    AssertEqual(1, pending.Count, "The Judge mission is the idempotency key");
                    AssertEqual(judge.Id, pending[0].JudgeMissionId);
                    AssertContains("missing boundary test", pending[0].SuggestedFollowUps ?? String.Empty);
                }
            });

            await RunTest("Bounded backfill records actionable and explicit-none sections exactly once", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    DateTime fromUtc = DateTime.UtcNow.AddHours(-2);
                    Mission actionable = new Mission("Backfill actionable", "Review")
                    {
                        Persona = "Judge",
                        Status = Armada.Core.Enums.MissionStatusEnum.Complete,
                        AgentOutput = "[ARMADA:VERDICT] PASS\n\n## Suggested Follow-ups\n- Add the missed guard.",
                        CreatedUtc = DateTime.UtcNow.AddHours(-1)
                    };
                    Mission explicitNone = new Mission("Backfill none", "Review")
                    {
                        Persona = "Judge",
                        Status = Armada.Core.Enums.MissionStatusEnum.Complete,
                        AgentOutput = "[ARMADA:VERDICT] PASS\n\n## Suggested Follow-ups\n(none)",
                        CreatedUtc = DateTime.UtcNow.AddMinutes(-50)
                    };
                    Mission compatibleLabel = new Mission("Backfill compatible label", "Review")
                    {
                        Persona = "Judge",
                        Status = Armada.Core.Enums.MissionStatusEnum.Complete,
                        AgentOutput = "[ARMADA:VERDICT] PASS\n\n**Tracked follow-ups (non-blocking):** - Preserve this real-world variant.",
                        CreatedUtc = DateTime.UtcNow.AddMinutes(-45)
                    };
                    Mission missingSection = new Mission("Backfill missing", "Review")
                    {
                        Persona = "Judge",
                        Status = Armada.Core.Enums.MissionStatusEnum.Complete,
                        AgentOutput = "[ARMADA:VERDICT] PASS",
                        CreatedUtc = DateTime.UtcNow.AddMinutes(-40)
                    };
                    actionable = await testDb.Driver.Missions.CreateAsync(actionable).ConfigureAwait(false);
                    explicitNone = await testDb.Driver.Missions.CreateAsync(explicitNone).ConfigureAwait(false);
                    compatibleLabel = await testDb.Driver.Missions.CreateAsync(compatibleLabel).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(missingSection).ConfigureAwait(false);

                    JudgeFollowUpBackfillService service = new JudgeFollowUpBackfillService(testDb.Driver, logging);
                    JudgeFollowUpBackfillService.Result preview = await service.RunAsync(
                        fromUtc, DateTime.UtcNow.AddMinutes(1), true, 10).ConfigureAwait(false);
                    AssertEqual(3, preview.WouldCreate, "Dry run must report every recognized missing durable row");
                    AssertEqual(0, preview.Created, "Dry run must not claim that it wrote rows");
                    AssertEqual(2, preview.Actionable);
                    AssertEqual(1, preview.ExplicitNone);
                    AssertEqual(0, (await testDb.Driver.JudgeFollowUps.EnumeratePendingAsync().ConfigureAwait(false)).Count);

                    JudgeFollowUpBackfillService.Result first = await service.RunAsync(
                        fromUtc, DateTime.UtcNow.AddMinutes(1), false, 10).ConfigureAwait(false);
                    AssertEqual(3, first.Created);
                    AssertEqual(0, first.Errors);
                    AssertFalse(first.Incomplete);
                    AssertNotNull(await testDb.Driver.JudgeFollowUps.ReadByJudgeMissionAsync(actionable.Id).ConfigureAwait(false));
                    AssertNotNull(await testDb.Driver.JudgeFollowUps.ReadByJudgeMissionAsync(compatibleLabel.Id).ConfigureAwait(false));
                    JudgeFollowUp? noneRow = await testDb.Driver.JudgeFollowUps
                        .ReadByJudgeMissionAsync(explicitNone.Id).ConfigureAwait(false);
                    AssertNotNull(noneRow, "Explicit (none) is durable reconciliation evidence");
                    AssertNull(noneRow!.SuggestedFollowUps);

                    JudgeFollowUpBackfillService.Result second = await service.RunAsync(
                        fromUtc, DateTime.UtcNow.AddMinutes(1), false, 10).ConfigureAwait(false);
                    AssertEqual(0, second.Created, "A repeat pass must create no rows");
                    AssertEqual(3, second.AlreadyPresent);
                }
            });

            await RunTest("Post-work replay recovers a failed FAIL follow-up write", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArmadaSettings settings = new ArmadaSettings();
                    StubGitService git = new StubGitService();
                    IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, docks, captains);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("replay-captain")).ConfigureAwait(false);
                    Mission judge = new Mission("Judge replay", "Review the work")
                    {
                        Persona = "Judge",
                        Status = Armada.Core.Enums.MissionStatusEnum.Failed,
                        AgentOutput = "[ARMADA:VERDICT] FAIL\n\n## Suggested Follow-ups\n- Preserve this failed-review action.",
                        FailureReason = "Judge verdict: FAIL"
                    };
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    IJudgeFollowUpMethods inner = testDb.Driver.JudgeFollowUps;
                    FailOnceJudgeFollowUps failOnce = new FailOnceJudgeFollowUps(inner);
                    typeof(DatabaseDriver).GetProperty(nameof(DatabaseDriver.JudgeFollowUps))!
                        .SetValue(testDb.Driver, failOnce);

                    await AssertThrowsAsync<InvalidOperationException>(
                        () => missionService.HandleCompletionAsync(captain, judge.Id),
                        "The first durable write is intentionally faulted").ConfigureAwait(false);
                    await missionService.HandleCompletionAsync(captain, judge.Id).ConfigureAwait(false);

                    List<JudgeFollowUp> pending = await inner.EnumeratePendingAsync().ConfigureAwait(false);
                    AssertEqual(1, pending.Count, "The post-work idempotency path must replay the durable write");
                    AssertEqual("FAIL", pending[0].JudgeVerdict);
                    AssertContains("failed-review action", pending[0].SuggestedFollowUps ?? String.Empty);
                }
            });

            await RunTest("Capture associates immediately when the reviewed merge entry exists", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    JudgeFollowUpService service = new JudgeFollowUpService(testDb.Driver, logging);
                    Mission judge = BuildJudgeMission("msn_reviewed-existing", "vsl_follow-up-two");
                    MergeEntry entry = new MergeEntry("reviewed-branch", "main")
                    {
                        MissionId = judge.DependsOnMissionId,
                        VesselId = judge.VesselId
                    };
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    JudgeFollowUp followUp = await service.CaptureAsync(
                        judge, "NEEDS_REVISION", null).ConfigureAwait(false);

                    AssertEqual(entry.Id, followUp.MergeEntryId, "Capture should associate after the durable write");
                    MergeEntry? reloaded = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertEqual(true, reloaded!.AuditDeepPicked);
                    AssertEqual("Pending", reloaded.AuditDeepVerdict);
                    AssertContains("NEEDS_REVISION", reloaded.AuditDeepNotes ?? String.Empty);

                    MergeEntry newer = new MergeEntry("newer-judge-branch", "main")
                    {
                        MissionId = judge.Id,
                        VesselId = judge.VesselId,
                        CreatedUtc = DateTime.UtcNow.AddMinutes(1)
                    };
                    newer = await testDb.Driver.MergeEntries.CreateAsync(newer).ConfigureAwait(false);
                    JudgeFollowUp repeated = await service.CaptureAsync(judge, "NEEDS_REVISION", null).ConfigureAwait(false);
                    AssertEqual(entry.Id, repeated.MergeEntryId, "Repeated capture must not move an existing association");
                    MergeEntry? unflaggedNewer = await testDb.Driver.MergeEntries.ReadAsync(newer.Id).ConfigureAwait(false);
                    AssertFalse(unflaggedNewer!.AuditDeepPicked == true, "Repeated capture must not flag a second merge entry");
                }
            });

            await RunTest("Reviewed mission delivery takes precedence over a newer Judge delivery", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    JudgeFollowUpService service = new JudgeFollowUpService(testDb.Driver, logging);
                    Mission judge = BuildJudgeMission("msn_reviewed-preferred", "vsl_preferred");
                    MergeEntry reviewed = new MergeEntry("reviewed", "main")
                    {
                        MissionId = judge.DependsOnMissionId,
                        VesselId = judge.VesselId,
                        CreatedUtc = DateTime.UtcNow.AddMinutes(-1)
                    };
                    reviewed = await testDb.Driver.MergeEntries.CreateAsync(reviewed).ConfigureAwait(false);
                    MergeEntry judgeDelivery = new MergeEntry("judge", "main")
                    {
                        MissionId = judge.Id,
                        VesselId = judge.VesselId,
                        CreatedUtc = DateTime.UtcNow
                    };
                    await testDb.Driver.MergeEntries.CreateAsync(judgeDelivery).ConfigureAwait(false);

                    JudgeFollowUp followUp = await service.CaptureAsync(judge, "PASS", "- Verify preference.").ConfigureAwait(false);

                    AssertEqual(reviewed.Id, followUp.MergeEntryId, "Worker delivery is authoritative when both delivery records exist");
                }
            });

            await RunTest("A later merge enqueue associates the durable follow-up", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    JudgeFollowUpService followUpService = new JudgeFollowUpService(testDb.Driver, logging);
                    Mission judge = BuildJudgeMission("msn_reviewed-late", "vsl_follow-up-three");
                    JudgeFollowUp followUp = await followUpService.CaptureAsync(
                        judge, "PASS", "- Preserve accepted work on rescue.").ConfigureAwait(false);
                    AssertNull(followUp.MergeEntryId);
                    followUp.AuditVerdict = "Concern";
                    followUp.AuditNotes = "Verified before delivery metadata arrived.";
                    followUp.AuditCompletedUtc = DateTime.UtcNow;
                    await testDb.Driver.JudgeFollowUps.UpdateAsync(followUp).ConfigureAwait(false);

                    ArmadaSettings settings = new ArmadaSettings();
                    MergeQueueService mergeQueue = new MergeQueueService(
                        logging,
                        testDb.Driver,
                        settings,
                        new StubGitService(),
                        new MergeFailureClassifier());
                    MergeEntry entry = new MergeEntry("judge-branch", "main")
                    {
                        MissionId = judge.Id,
                        VesselId = judge.VesselId
                    };
                    entry = await mergeQueue.EnqueueAsync(entry).ConfigureAwait(false);

                    JudgeFollowUp? reloaded = await testDb.Driver.JudgeFollowUps.ReadAsync(followUp.Id).ConfigureAwait(false);
                    AssertEqual(entry.Id, reloaded!.MergeEntryId, "The merge queue must reconcile a Judge-first race");
                    MergeEntry? mirrored = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertEqual("Concern", mirrored!.AuditDeepVerdict, "Late association must preserve a completed audit verdict");
                    AssertEqual("Verified before delivery metadata arrived.", mirrored.AuditDeepNotes);
                    AssertNotNull(mirrored.AuditDeepCompletedUtc);
                }
            });

            await RunTest("Concurrent audit completion and association preserve both fields", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    JudgeFollowUpService service = new JudgeFollowUpService(testDb.Driver, logging);
                    JudgeFollowUp followUp = BuildFollowUp(
                        "msn_judge-race", "msn_reviewed-race", "vsl_race", DateTime.UtcNow);
                    followUp = await testDb.Driver.JudgeFollowUps.UpsertAsync(followUp).ConfigureAwait(false);
                    MergeEntry entry = new MergeEntry("race-branch", "main")
                    {
                        MissionId = followUp.ReviewedMissionId,
                        VesselId = followUp.VesselId
                    };
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    Task associate = service.AssociateForMergeEntryAsync(entry);
                    Task complete = service.CompleteAuditAsync(
                        followUp.Id, "Concern", "Concurrent audit.", null, DateTime.UtcNow);
                    await Task.WhenAll(associate, complete).ConfigureAwait(false);

                    JudgeFollowUp? canonical = await testDb.Driver.JudgeFollowUps.ReadAsync(followUp.Id).ConfigureAwait(false);
                    AssertEqual(entry.Id, canonical!.MergeEntryId, "Atomic association must survive audit completion");
                    AssertEqual("Concern", canonical.AuditVerdict, "Field-specific audit completion must survive association");
                    AssertEqual("Concurrent audit.", canonical.AuditNotes);
                    MergeEntry? mirror = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertEqual("Concern", mirror!.AuditDeepVerdict, "The item lock must keep the merge mirror current");
                    AssertEqual("Concurrent audit.", mirror.AuditDeepNotes);
                    AssertNotNull(mirror.AuditDeepCompletedUtc);
                }
            });

            await RunTest("Pending enumeration is oldest first and filters by vessel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    JudgeFollowUp older = BuildFollowUp("msn_judge-old", "msn_reviewed-old", "vsl_filter", DateTime.UtcNow.AddHours(-2));
                    JudgeFollowUp newer = BuildFollowUp("msn_judge-new", "msn_reviewed-new", "vsl_filter", DateTime.UtcNow.AddHours(-1));
                    JudgeFollowUp other = BuildFollowUp("msn_judge-other", "msn_reviewed-other", "vsl_other", DateTime.UtcNow.AddHours(-3));
                    await testDb.Driver.JudgeFollowUps.UpsertAsync(newer).ConfigureAwait(false);
                    await testDb.Driver.JudgeFollowUps.UpsertAsync(other).ConfigureAwait(false);
                    await testDb.Driver.JudgeFollowUps.UpsertAsync(older).ConfigureAwait(false);

                    List<JudgeFollowUp> pending = await testDb.Driver.JudgeFollowUps
                        .EnumeratePendingAsync("vsl_filter")
                        .ConfigureAwait(false);

                    AssertEqual(2, pending.Count);
                    AssertEqual(older.JudgeMissionId, pending[0].JudgeMissionId);
                    AssertEqual(newer.JudgeMissionId, pending[1].JudgeMissionId);
                }
            });
        }

        private void AssertMigration(List<SchemaMigration> migrations, int version, string provider)
        {
            SchemaMigration? migration = migrations.Find(candidate => candidate.Version == version);
            AssertNotNull(migration, provider + " must register the durable follow-up migration");
            AssertTrue(migration!.Statements.Any(statement => statement.Contains("judge_follow_ups", StringComparison.Ordinal)),
                provider + " migration must create the durable follow-up table");
        }

        private static Mission BuildJudgeMission(string reviewedMissionId, string vesselId)
        {
            Mission judge = new Mission("Judge the change", "Review the accepted work");
            judge.Persona = "Judge";
            judge.DependsOnMissionId = reviewedMissionId;
            judge.VesselId = vesselId;
            judge.VoyageId = "vyg_follow-up";
            return judge;
        }

        private static JudgeFollowUp BuildFollowUp(
            string judgeMissionId,
            string reviewedMissionId,
            string vesselId,
            DateTime createdUtc)
        {
            return new JudgeFollowUp
            {
                JudgeMissionId = judgeMissionId,
                ReviewedMissionId = reviewedMissionId,
                VesselId = vesselId,
                JudgeVerdict = "PASS",
                SuggestedFollowUps = "- Follow up.",
                AuditVerdict = "Pending",
                CreatedUtc = createdUtc,
                LastUpdateUtc = createdUtc
            };
        }

        private sealed class FailOnceJudgeFollowUps : IJudgeFollowUpMethods
        {
            private readonly IJudgeFollowUpMethods _Inner;
            private bool _ShouldFail = true;

            public FailOnceJudgeFollowUps(IJudgeFollowUpMethods inner) => _Inner = inner;

            public Task<JudgeFollowUp> UpsertAsync(JudgeFollowUp followUp, CancellationToken token = default)
            {
                if (_ShouldFail)
                {
                    _ShouldFail = false;
                    throw new InvalidOperationException("simulated durable follow-up write failure");
                }
                return _Inner.UpsertAsync(followUp, token);
            }

            public Task<JudgeFollowUp?> ReadAsync(string id, CancellationToken token = default) => _Inner.ReadAsync(id, token);
            public Task<JudgeFollowUp?> ReadByJudgeMissionAsync(string judgeMissionId, CancellationToken token = default) => _Inner.ReadByJudgeMissionAsync(judgeMissionId, token);
            public Task<JudgeFollowUp?> ReadByMergeEntryAsync(string mergeEntryId, CancellationToken token = default) => _Inner.ReadByMergeEntryAsync(mergeEntryId, token);
            public Task<JudgeFollowUp> UpdateAsync(JudgeFollowUp followUp, CancellationToken token = default) => _Inner.UpdateAsync(followUp, token);
            public Task<bool> TryAssociateAsync(string followUpId, string mergeEntryId, CancellationToken token = default) => _Inner.TryAssociateAsync(followUpId, mergeEntryId, token);
            public Task<JudgeFollowUp> CompleteAuditAsync(string id, string verdict, string notes, string? recommendedAction, DateTime completedUtc, CancellationToken token = default) =>
                _Inner.CompleteAuditAsync(id, verdict, notes, recommendedAction, completedUtc, token);
            public Task<List<JudgeFollowUp>> EnumeratePendingAsync(string? vesselId = null, CancellationToken token = default) => _Inner.EnumeratePendingAsync(vesselId, token);
            public Task<List<JudgeFollowUp>> EnumerateUnassociatedAsync(string? vesselId = null, CancellationToken token = default) => _Inner.EnumerateUnassociatedAsync(vesselId, token);
            public Task<List<JudgeFollowUp>> EnumerateUnassociatedByReviewedMissionAsync(string reviewedMissionId, CancellationToken token = default) => _Inner.EnumerateUnassociatedByReviewedMissionAsync(reviewedMissionId, token);
            public Task<List<JudgeFollowUp>> EnumerateUnassociatedByJudgeMissionAsync(string judgeMissionId, CancellationToken token = default) => _Inner.EnumerateUnassociatedByJudgeMissionAsync(judgeMissionId, token);
        }
    }
}
