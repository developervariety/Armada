namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
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
    /// Tests for the read-only mission recovery report built from recorded counters, rescues, incidents, runbook
    /// executions and recovery events.
    /// </summary>
    public class MissionRecoveryReportTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Mission Recovery Report";

        private static readonly string _Tenant = Armada.Core.Constants.DefaultTenantId;
        private static readonly string _User = Armada.Core.Constants.DefaultUserId;
        private static readonly AuthContext _Admin = AuthContext.Authenticated(_Tenant, _User, true, true, "Test");
        private static readonly AuthContext _TenantAdmin = AuthContext.Authenticated(_Tenant, _User, false, true, "Test");
        private static readonly AuthContext _Member = AuthContext.Authenticated(_Tenant, _User, false, false, "Test");

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static MissionRecoveryReportService CreateService(SqliteDatabaseDriver db, ArmadaSettings? settings = null)
        {
            return new MissionRecoveryReportService(db, CreateLogging(), settings ?? new ArmadaSettings());
        }

        private static async Task<Vessel> CreateVesselAsync(SqliteDatabaseDriver db)
        {
            Vessel vessel = new Vessel("recovery-report-vessel-" + Guid.NewGuid().ToString("N"), "https://github.com/test/recovery.git");
            vessel.TenantId = _Tenant;
            vessel.UserId = _User;
            return await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateMissionAsync(SqliteDatabaseDriver db, string? vesselId, string? parentMissionId, string? userId, DateTime createdUtc)
        {
            Mission mission = new Mission("recovery mission", "recovery");
            mission.TenantId = _Tenant;
            mission.UserId = userId;
            mission.VesselId = vesselId;
            mission.ParentMissionId = parentMissionId;
            mission.Status = parentMissionId == null ? MissionStatusEnum.Failed : MissionStatusEnum.InProgress;
            mission.CreatedUtc = createdUtc;
            return await db.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task CreateEventAsync(SqliteDatabaseDriver db, string missionId, string eventType, string message, DateTime createdUtc, string? tenantId)
        {
            ArmadaEvent evt = new ArmadaEvent(eventType, message);
            evt.TenantId = tenantId;
            evt.UserId = tenantId == null ? null : _User;
            evt.MissionId = missionId;
            evt.EntityType = "mission";
            evt.EntityId = missionId;
            evt.CreatedUtc = createdUtc;
            await db.Events.CreateAsync(evt).ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Report shows recorded counters and the current budgets without changing them", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.AutonomousRecovery.MaxMissionRecoveryAttempts = 1;
                    settings.MaxLandingRetries = 3;

                    Mission mission = new Mission("counted", "counted");
                    mission.Status = MissionStatusEnum.Failed;
                    mission.FailureReason = "gate failed with token=abcdefghijk123";
                    mission.RecoveryAttempts = 1;
                    mission.LandingRetryCount = 2;
                    mission.LastRecoveryActionUtc = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);

                    MissionRecoveryReport report = await CreateService(testDb.Driver, settings).GetForMissionAsync(_Admin, mission).ConfigureAwait(false);

                    AssertEqual(1, report.RecoveryAttempts, "Recorded attempts");
                    AssertEqual(1, report.MaxRecoveryAttempts, "Current budget");
                    AssertTrue(report.RecoveryBudgetExhausted, "Attempts reached the budget");
                    AssertEqual(2, report.LandingRetryCount, "Landing retries");
                    AssertEqual(3, report.MaxLandingRetries, "Landing retry limit");
                    AssertEqual(mission.LastRecoveryActionUtc, report.LastRecoveryActionUtc, "Last recovery action");
                    AssertFalse(report.IsRescue, "Not a rescue");
                    AssertFalse(report.FailureReason!.Contains("abcdefghijk123", StringComparison.Ordinal), "Failure reason is redacted");
                    AssertEqual(1, mission.RecoveryAttempts, "Reading the report does not change the mission");
                }
            }).ConfigureAwait(false);

            await RunTest("Rescues are the vessel missions whose parent is this mission, in the caller's scope", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver).ConfigureAwait(false);
                    DateTime baseUtc = new DateTime(2026, 9, 13, 9, 0, 0, DateTimeKind.Utc);
                    Mission failed = await CreateMissionAsync(testDb.Driver, vessel.Id, null, _User, baseUtc).ConfigureAwait(false);
                    Mission ownRescue = await CreateMissionAsync(testDb.Driver, vessel.Id, failed.Id, _User, baseUtc.AddMinutes(5)).ConfigureAwait(false);
                    Mission unownedRescue = await CreateMissionAsync(testDb.Driver, vessel.Id, failed.Id, null, baseUtc.AddMinutes(1)).ConfigureAwait(false);
                    await CreateMissionAsync(testDb.Driver, vessel.Id, null, _User, baseUtc.AddMinutes(2)).ConfigureAwait(false);

                    MissionRecoveryReportService service = CreateService(testDb.Driver);

                    MissionRecoveryReport admin = await service.GetForMissionAsync(_Admin, failed).ConfigureAwait(false);
                    AssertEqual(2, admin.Rescues.Count, "Admin sees both rescues and no unrelated mission");
                    AssertEqual(unownedRescue.Id, admin.Rescues[0].MissionId, "Oldest rescue first");
                    AssertEqual(ownRescue.Id, admin.Rescues[1].MissionId, "Newer rescue second");

                    MissionRecoveryReport tenantAdmin = await service.GetForMissionAsync(_TenantAdmin, failed).ConfigureAwait(false);
                    AssertEqual(2, tenantAdmin.Rescues.Count, "Tenant admin sees both rescues");

                    MissionRecoveryReport member = await service.GetForMissionAsync(_Member, failed).ConfigureAwait(false);
                    AssertEqual(1, member.Rescues.Count, "An ordinary user sees only rescues they own");
                    AssertEqual(ownRescue.Id, member.Rescues[0].MissionId, "Owned rescue");

                    AuthContext otherTenant = AuthContext.Authenticated("ten_recovery_other", "usr_recovery_other", false, true, "Test");
                    MissionRecoveryReport other = await service.GetForMissionAsync(otherTenant, failed).ConfigureAwait(false);
                    AssertEqual(0, other.Rescues.Count, "Another tenant sees no rescues");

                    MissionRecoveryReport rescueReport = await service.GetForMissionAsync(_Admin, ownRescue).ConfigureAwait(false);
                    AssertTrue(rescueReport.IsRescue, "A rescue reports itself as a rescue");
                    AssertEqual(failed.Id, rescueReport.ParentMissionId, "Rescue parent");
                }
            }).ConfigureAwait(false);

            await RunTest("A mission without a vessel names why rescues are not listed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(testDb.Driver, null, null, _User, DateTime.UtcNow).ConfigureAwait(false);
                    MissionRecoveryReport report = await CreateService(testDb.Driver).GetForMissionAsync(_Admin, mission).ConfigureAwait(false);
                    AssertEqual(0, report.Rescues.Count, "No rescues listed");
                    AssertNotNull(report.RescuesUnavailableReason, "The reason is named, not an empty list alone");
                }
            }).ConfigureAwait(false);

            await RunTest("Incidents and their runbook executions are listed in the caller's scope with redacted notes", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(testDb.Driver, null, null, _User, DateTime.UtcNow).ConfigureAwait(false);
                    IncidentService incidents = new IncidentService(testDb.Driver);
                    Incident incident = await incidents.CreateAsync(_TenantAdmin, new IncidentUpsertRequest
                    {
                        Title = "Mission failed",
                        MissionId = mission.Id,
                        RecoveryNotes = "Autonomous rescue dispatched; password=supersecret34"
                    }).ConfigureAwait(false);

                    RunbookService runbooks = new RunbookService(testDb.Driver, CreateLogging());
                    Runbook runbook = await runbooks.CreateAsync(_TenantAdmin, new RunbookUpsertRequest
                    {
                        FileName = "recovery-report-test.md",
                        Title = "Recovery report test",
                        OverviewMarkdown = "Recovery report test runbook."
                    }).ConfigureAwait(false);
                    await runbooks.StartExecutionAsync(_TenantAdmin, runbook.Id, new RunbookExecutionStartRequest
                    {
                        Title = "Recovery run",
                        IncidentId = incident.Id
                    }).ConfigureAwait(false);

                    MissionRecoveryReportService service = CreateService(testDb.Driver);
                    MissionRecoveryReport report = await service.GetForMissionAsync(_TenantAdmin, mission).ConfigureAwait(false);

                    AssertNull(report.IncidentsUnavailableReason, "Incidents are readable");
                    AssertEqual(1, report.Incidents.Count, "One incident");
                    AssertEqual(incident.Id, report.Incidents[0].IncidentId, "Incident identity");
                    AssertFalse(report.Incidents[0].RecoveryNotes!.Contains("supersecret34", StringComparison.Ordinal), "Notes are redacted");
                    AssertEqual(1, report.Incidents[0].RunbookExecutions.Count, "One linked runbook execution");
                    AssertEqual(runbook.Id, report.Incidents[0].RunbookExecutions[0].RunbookId, "Execution runbook");

                    AuthContext otherTenant = AuthContext.Authenticated("ten_recovery_other", "usr_recovery_other", false, true, "Test");
                    MissionRecoveryReport other = await service.GetForMissionAsync(otherTenant, mission).ConfigureAwait(false);
                    AssertEqual(0, other.Incidents.Count, "Another tenant sees no incidents");
                }
            }).ConfigureAwait(false);

            await RunTest("Only recovery events are listed, redacted, and only in the caller's scope", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(testDb.Driver, null, null, _User, DateTime.UtcNow).ConfigureAwait(false);
                    DateTime now = DateTime.UtcNow;
                    await CreateEventAsync(testDb.Driver, mission.Id, "autonomous_recovery.rescue_dispatched", "Rescue dispatched with token=abcdefghijk999", now.AddMinutes(-3), _Tenant).ConfigureAwait(false);
                    await CreateEventAsync(testDb.Driver, mission.Id, "landing_drain.enqueued", "Landing drain enqueued", now.AddMinutes(-2), _Tenant).ConfigureAwait(false);
                    await CreateEventAsync(testDb.Driver, mission.Id, "mission.completed", "Mission completed", now.AddMinutes(-1), _Tenant).ConfigureAwait(false);
                    await CreateEventAsync(testDb.Driver, mission.Id, "mission.landing_retry", "Unscoped landing retry", now, null).ConfigureAwait(false);

                    MissionRecoveryReportService service = CreateService(testDb.Driver);

                    MissionRecoveryReport member = await service.GetForMissionAsync(_Member, mission).ConfigureAwait(false);
                    List<string> memberTypes = member.Events.Select(item => item.EventType).ToList();
                    AssertEqual(2, member.Events.Count, "Two scoped recovery events; completion and the unscoped retry are excluded");
                    AssertEqual("landing_drain.enqueued", memberTypes[0], "Newest first");
                    AssertEqual("autonomous_recovery.rescue_dispatched", memberTypes[1], "Older second");
                    AssertFalse(member.Events[1].Message.Contains("abcdefghijk999", StringComparison.Ordinal), "Messages are redacted");

                    MissionRecoveryReport admin = await service.GetForMissionAsync(_Admin, mission).ConfigureAwait(false);
                    AssertEqual(3, admin.Events.Count, "An unscoped admin also sees the unscoped retry event");

                    AuthContext otherTenant = AuthContext.Authenticated("ten_recovery_other", "usr_recovery_other", false, true, "Test");
                    MissionRecoveryReport other = await service.GetForMissionAsync(otherTenant, mission).ConfigureAwait(false);
                    AssertEqual(0, other.Events.Count, "Another tenant sees no events");
                }
            }).ConfigureAwait(false);

            await RunTest("A full event window is reported so an older recovery event is not read as absent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(testDb.Driver, null, null, _User, DateTime.UtcNow).ConfigureAwait(false);
                    DateTime start = new DateTime(2026, 9, 13, 8, 0, 0, DateTimeKind.Utc);
                    await CreateEventAsync(testDb.Driver, mission.Id, "autonomous_recovery.rescue_dispatched", "Old rescue", start, _Tenant).ConfigureAwait(false);
                    for (int i = 1; i <= MissionRecoveryReportService.EventWindow; i++)
                    {
                        await CreateEventAsync(testDb.Driver, mission.Id, "mission.heartbeat_note", "noise " + i, start.AddSeconds(i), _Tenant).ConfigureAwait(false);
                    }

                    MissionRecoveryReport report = await CreateService(testDb.Driver).GetForMissionAsync(_TenantAdmin, mission).ConfigureAwait(false);
                    AssertTrue(report.EventsWindowFull, "The window is full");
                    AssertEqual(0, report.Events.Count, "The older recovery event is outside the window");
                }
            }).ConfigureAwait(false);
        }
    }
}
