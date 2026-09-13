namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for the read-only mission auto-land report built from the vessel predicate, recorded auto-land events and
    /// the mission's merge entry.
    /// </summary>
    public class MissionAutoLandReportTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Mission Auto Land Report";

        private static readonly string _Tenant = Armada.Core.Constants.DefaultTenantId;
        private static readonly string _User = Armada.Core.Constants.DefaultUserId;
        private static readonly AuthContext _Admin = AuthContext.Authenticated(_Tenant, _User, true, true, "Test");
        private static readonly AuthContext _Member = AuthContext.Authenticated(_Tenant, _User, false, false, "Test");

        private static MissionAutoLandReportService CreateService(SqliteDatabaseDriver db)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new MissionAutoLandReportService(db, logging);
        }

        private static async Task<Mission> CreateMissionAsync(SqliteDatabaseDriver db, string? predicateJson)
        {
            Vessel vessel = new Vessel("autoland-report-" + Guid.NewGuid().ToString("N"), "https://github.com/test/autoland.git");
            vessel.TenantId = _Tenant;
            vessel.UserId = _User;
            vessel.AutoLandPredicate = predicateJson;
            vessel = await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);
            Mission mission = new Mission("auto-land mission", "auto-land");
            mission.TenantId = _Tenant;
            mission.UserId = _User;
            mission.VesselId = vessel.Id;
            return await db.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task CreateEventAsync(SqliteDatabaseDriver db, Mission mission, string eventType, string? payload, DateTime createdUtc, bool scoped = true)
        {
            ArmadaEvent evt = new ArmadaEvent(eventType, "auto-land decision");
            evt.TenantId = scoped ? mission.TenantId : null;
            evt.UserId = scoped ? mission.UserId : null;
            evt.MissionId = mission.Id;
            evt.VesselId = mission.VesselId;
            evt.EntityType = "merge_entry";
            evt.EntityId = "mrg_report";
            evt.Payload = payload;
            evt.CreatedUtc = createdUtc;
            await db.Events.CreateAsync(evt).ConfigureAwait(false);
        }

        private static async Task<MergeEntry> CreateEntryAsync(SqliteDatabaseDriver db, Mission mission, string branch, DateTime createdUtc)
        {
            MergeEntry entry = new MergeEntry(branch, "main");
            entry.TenantId = mission.TenantId;
            entry.UserId = mission.UserId;
            entry.MissionId = mission.Id;
            entry.VesselId = mission.VesselId;
            entry.CreatedUtc = createdUtc;
            entry.LastUpdateUtc = createdUtc;
            return await db.MergeEntries.CreateAsync(entry).ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A scoped reader gets this mission's merge entry, not a newer entry of another mission in the tenant", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MissionAutoLandReportService service = CreateService(testDb.Driver);
                    Mission first = await CreateMissionAsync(testDb.Driver, null).ConfigureAwait(false);
                    Mission second = await CreateMissionAsync(testDb.Driver, null).ConfigureAwait(false);
                    DateTime baseUtc = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
                    MergeEntry firstEntry = await CreateEntryAsync(testDb.Driver, first, "armada/first", baseUtc).ConfigureAwait(false);
                    MergeEntry secondEntry = await CreateEntryAsync(testDb.Driver, second, "armada/second", baseUtc.AddMinutes(5)).ConfigureAwait(false);

                    AuthContext tenantAdmin = AuthContext.Authenticated(_Tenant, _User, false, true, "Test");
                    foreach (AuthContext scope in new AuthContext[] { _Admin, tenantAdmin, _Member })
                    {
                        MissionAutoLandReport firstReport = await service.GetForMissionAsync(scope, first).ConfigureAwait(false);
                        AssertEqual(firstEntry.Id, firstReport.LatestMergeEntry?.EntryId, "First mission entry for " + scope.IsAdmin + "/" + scope.IsTenantAdmin);
                        MissionAutoLandReport secondReport = await service.GetForMissionAsync(scope, second).ConfigureAwait(false);
                        AssertEqual(secondEntry.Id, secondReport.LatestMergeEntry?.EntryId, "Second mission entry for " + scope.IsAdmin + "/" + scope.IsTenantAdmin);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("The report reads a decision recorded by the real landing-drain safety net", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArmadaSettings settings = new ArmadaSettings();
                    MergeQueueService mergeQueue = new MergeQueueService(logging, testDb.Driver, settings, new StubGitService(), new Armada.Core.Recovery.MergeFailureClassifier());

                    Mission mission = await CreateMissionAsync(testDb.Driver, "{\"Enabled\":true,\"MaxAddedLines\":40}").ConfigureAwait(false);
                    mission.BranchName = "armada/safety-net/branch";
                    Vessel vessel = (await testDb.Driver.Vessels.ReadAsync(mission.VesselId!).ConfigureAwait(false))!;

                    // No diff: the safety net records a skipped decision with its reason and the predicate.
                    SafetyNetEnqueueResult enqueue = await mergeQueue.TrySafetyNetEnqueueAsync(
                        mission, vessel, null, new AutoLandEvaluator(), new ConventionChecker(), new CriticalTriggerEvaluator()).ConfigureAwait(false);
                    AssertNotNull(enqueue.Entry, "The safety net enqueues an entry");

                    MissionAutoLandReport report = await CreateService(testDb.Driver).GetForMissionAsync(_Member, mission).ConfigureAwait(false);
                    AssertEqual(RecordedHistoryStateEnum.Recorded, report.DecisionState, "A real writer's payload is readable");
                    AssertEqual(AutoLandDecisionOutcomeEnum.Skipped, report.LatestDecision!.Outcome, "Skipped decision");
                    AssertEqual(enqueue.Entry!.Id, report.LatestDecision.MergeEntryId, "Decision names the enqueued entry");
                    AssertEqual(40, report.LatestDecision.PredicateAtDecision!.MaxAddedLines, "Predicate recorded at decision time");
                    AssertNotNull(report.LatestDecision.Reason, "The skip reason is kept");
                    AssertEqual(enqueue.Entry.Id, report.LatestMergeEntry!.EntryId, "The enqueued entry is visible to the owner");
                }
            }).ConfigureAwait(false);
            await RunTest("The latest recorded decision, predicate and merge entry audit are reported", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(testDb.Driver, "{\"Enabled\":true,\"MaxAddedLines\":50}").ConfigureAwait(false);
                    DateTime now = DateTime.UtcNow;
                    AutoLandPredicate predicate = new AutoLandPredicate { Enabled = true, MaxAddedLines = 50 };
                    await CreateEventAsync(testDb.Driver, mission, MissionAutoLandReportService.TriggeredEventType,
                        JsonSerializer.Serialize(new { entryId = "mrg_older", missionId = mission.Id, predicate }), now.AddMinutes(-5)).ConfigureAwait(false);
                    await CreateEventAsync(testDb.Driver, mission, MissionAutoLandReportService.SkippedEventType,
                        JsonSerializer.Serialize(new { entryId = "mrg_newer", missionId = mission.Id, reason = "maxAddedLines:120>50 token=abcdefghijk12", predicate }), now).ConfigureAwait(false);

                    MergeEntry entry = new MergeEntry("armada/autoland/branch", "main");
                    entry.TenantId = _Tenant;
                    entry.UserId = _User;
                    entry.MissionId = mission.Id;
                    entry.VesselId = mission.VesselId;
                    entry.AuditLane = "Deferred";
                    entry.AuditDeepPicked = true;
                    entry.AuditDeepVerdict = "Pending";
                    await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    MissionAutoLandReport report = await CreateService(testDb.Driver).GetForMissionAsync(_Member, mission).ConfigureAwait(false);

                    AssertTrue(report.PredicateConfigured, "Predicate configured");
                    AssertEqual(50, report.CurrentPredicate!.MaxAddedLines, "Current predicate parsed");
                    AssertEqual(RecordedHistoryStateEnum.Recorded, report.DecisionState, "Decision recorded");
                    AssertEqual(AutoLandDecisionOutcomeEnum.Skipped, report.LatestDecision!.Outcome, "The newest decision wins");
                    AssertEqual("mrg_newer", report.LatestDecision.MergeEntryId, "Decision entry");
                    AssertFalse(report.LatestDecision.Reason!.Contains("abcdefghijk12", StringComparison.Ordinal), "Reason is redacted");
                    AssertEqual(50, report.LatestDecision.PredicateAtDecision!.MaxAddedLines, "Predicate at decision");
                    AssertEqual(entry.Id, report.LatestMergeEntry!.EntryId, "Merge entry");
                    AssertEqual("Deferred", report.LatestMergeEntry.AuditLane, "Audit lane");
                    AssertEqual("Pending", report.LatestMergeEntry.AuditDeepVerdict, "Deep verdict");
                }
            }).ConfigureAwait(false);

            await RunTest("No decision is NotRecorded; malformed or tied latest decisions are Unavailable", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MissionAutoLandReportService service = CreateService(testDb.Driver);

                    Mission empty = await CreateMissionAsync(testDb.Driver, null).ConfigureAwait(false);
                    MissionAutoLandReport none = await service.GetForMissionAsync(_Admin, empty).ConfigureAwait(false);
                    AssertEqual(RecordedHistoryStateEnum.NotRecorded, none.DecisionState, "No events");
                    AssertFalse(none.PredicateConfigured, "No predicate");
                    AssertNull(none.LatestMergeEntry, "No merge entry");

                    Mission malformed = await CreateMissionAsync(testDb.Driver, null).ConfigureAwait(false);
                    DateTime now = DateTime.UtcNow;
                    await CreateEventAsync(testDb.Driver, malformed, MissionAutoLandReportService.TriggeredEventType,
                        JsonSerializer.Serialize(new { entryId = "mrg_ok" }), now.AddMinutes(-1)).ConfigureAwait(false);
                    await CreateEventAsync(testDb.Driver, malformed, MissionAutoLandReportService.SkippedEventType, "{not json", now).ConfigureAwait(false);
                    MissionAutoLandReport bad = await service.GetForMissionAsync(_Admin, malformed).ConfigureAwait(false);
                    AssertEqual(RecordedHistoryStateEnum.Unavailable, bad.DecisionState, "Malformed newest decision");
                    AssertNull(bad.LatestDecision, "The older triggered decision is not reported instead");

                    Mission tied = await CreateMissionAsync(testDb.Driver, null).ConfigureAwait(false);
                    DateTime same = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
                    await CreateEventAsync(testDb.Driver, tied, MissionAutoLandReportService.TriggeredEventType, JsonSerializer.Serialize(new { entryId = "mrg_a" }), same).ConfigureAwait(false);
                    await CreateEventAsync(testDb.Driver, tied, MissionAutoLandReportService.SkippedEventType, JsonSerializer.Serialize(new { entryId = "mrg_b", reason = "disabled" }), same).ConfigureAwait(false);
                    MissionAutoLandReport tie = await service.GetForMissionAsync(_Admin, tied).ConfigureAwait(false);
                    AssertEqual(RecordedHistoryStateEnum.Unavailable, tie.DecisionState, "Tied decisions");

                    Mission unparsable = await CreateMissionAsync(testDb.Driver, "{broken").ConfigureAwait(false);
                    MissionAutoLandReport broken = await service.GetForMissionAsync(_Admin, unparsable).ConfigureAwait(false);
                    AssertTrue(broken.PredicateConfigured, "A configured predicate is reported as configured");
                    AssertNull(broken.CurrentPredicate, "An unparsable predicate is not shown");
                    AssertNotNull(broken.PredicateUnavailableReason, "The reason is named");
                }
            }).ConfigureAwait(false);

            await RunTest("Unscoped historical auto-land events and another tenant's records are not visible to scoped readers", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MissionAutoLandReportService service = CreateService(testDb.Driver);
                    Mission mission = await CreateMissionAsync(testDb.Driver, "{\"Enabled\":true}").ConfigureAwait(false);
                    await CreateEventAsync(testDb.Driver, mission, MissionAutoLandReportService.TriggeredEventType,
                        JsonSerializer.Serialize(new { entryId = "mrg_legacy" }), DateTime.UtcNow, scoped: false).ConfigureAwait(false);

                    MissionAutoLandReport member = await service.GetForMissionAsync(_Member, mission).ConfigureAwait(false);
                    AssertEqual(RecordedHistoryStateEnum.NotRecorded, member.DecisionState, "An unscoped legacy event is invisible to a scoped reader");

                    MissionAutoLandReport admin = await service.GetForMissionAsync(_Admin, mission).ConfigureAwait(false);
                    AssertEqual(RecordedHistoryStateEnum.Recorded, admin.DecisionState, "An unscoped admin still sees it");

                    AuthContext otherTenant = AuthContext.Authenticated("ten_autoland_other", "usr_autoland_other", false, true, "Test");
                    MissionAutoLandReport other = await service.GetForMissionAsync(otherTenant, mission).ConfigureAwait(false);
                    AssertNull(other.CurrentPredicate, "Another tenant cannot read the vessel predicate");
                    AssertNotNull(other.PredicateUnavailableReason, "The reason is named");
                    AssertEqual(RecordedHistoryStateEnum.NotRecorded, other.DecisionState, "Another tenant sees no decision");
                }
            }).ConfigureAwait(false);
        }
    }
}
