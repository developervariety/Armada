namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;
    using SyslogLogging;

    /// <summary>
    /// Tests for recorded definition-of-done evaluations and the read-only mission report built from them.
    /// </summary>
    public class DefinitionOfDoneReportTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Definition Of Done Report";

        private static readonly AuthContext _Admin = AuthContext.Authenticated("default", "default", true, true, "Test");

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_dodreport_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_dodreport_repos_" + Guid.NewGuid().ToString("N"));
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_dodreport_logs_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        private static void DeleteDirectories(ArmadaSettings settings)
        {
            foreach (string path in new[] { settings.DocksDirectory, settings.ReposDirectory, settings.LogDirectory })
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
        }

        private static async Task<Captain> CreateWorkingMissionAsync(SqliteDatabaseDriver db, string persona, string? description = null)
        {
            Vessel vessel = new Vessel("dod-report-vessel-" + Guid.NewGuid().ToString("N"), "https://github.com/test/repo.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_dodreport_bare_" + Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_dodreport_work_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Captain captain = new Captain("dod-report-captain");
            captain.State = CaptainStateEnum.Working;
            await db.Captains.CreateAsync(captain).ConfigureAwait(false);

            Dock dock = new Dock(vessel.Id);
            dock.CaptainId = captain.Id;
            dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_dodreport_wt_" + Guid.NewGuid().ToString("N"));
            dock.BranchName = "armada/dod-report/msn_test";
            dock.Active = true;
            await db.Docks.CreateAsync(dock).ConfigureAwait(false);

            Mission mission = new Mission("DoD report mission", description ?? "Implement a change.");
            mission.Status = MissionStatusEnum.InProgress;
            mission.Persona = persona;
            mission.CaptainId = captain.Id;
            mission.DockId = dock.Id;
            mission.VesselId = vessel.Id;
            await db.Missions.CreateAsync(mission).ConfigureAwait(false);

            captain.CurrentMissionId = mission.Id;
            captain.CurrentDockId = dock.Id;
            await db.Captains.UpdateAsync(captain).ConfigureAwait(false);
            return captain;
        }

        private static MissionService CreateMissionService(SqliteDatabaseDriver db, ArmadaSettings settings, LoggingModule logging, DefinitionOfDoneSettings dodSettings)
        {
            StubGitService git = new StubGitService();
            IDockService docks = new DockService(logging, db, settings, git);
            ICaptainService captains = new CaptainService(logging, db, settings, git, docks);
            MissionService missions = new MissionService(logging, db, settings, docks, captains, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
            missions.DefinitionOfDone = new DefinitionOfDoneGate(dodSettings, db, logging);
            return missions;
        }

        private static async Task<Mission> CreateBareMissionAsync(SqliteDatabaseDriver db)
        {
            Mission mission = new Mission("history mission", "history");
            await db.Missions.CreateAsync(mission).ConfigureAwait(false);
            return mission;
        }

        private static async Task CreateEvaluationEventAsync(SqliteDatabaseDriver db, string missionId, string payload, DateTime createdUtc, string? tenantId = null, string? userId = null)
        {
            ArmadaEvent evt = new ArmadaEvent(DefinitionOfDoneEvaluationRecord.EventType, "Definition-of-done evaluation");
            evt.MissionId = missionId;
            evt.EntityType = "mission";
            evt.EntityId = missionId;
            evt.TenantId = tenantId;
            evt.UserId = userId;
            evt.Payload = payload;
            evt.CreatedUtc = createdUtc;
            await db.Events.CreateAsync(evt).ConfigureAwait(false);
        }

        private static string PassedPayload()
        {
            return JsonSerializer.Serialize(new DefinitionOfDoneEvaluationRecord { Outcome = DefinitionOfDoneEvaluationOutcomeEnum.Passed });
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Completion records a skipped gate evaluation and the report shows it as Skipped", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    try
                    {
                        LoggingModule logging = CreateLogging();
                        MissionService missions = CreateMissionService(testDb.Driver, settings, logging, new DefinitionOfDoneSettings { Enabled = false });
                        Captain captain = await CreateWorkingMissionAsync(testDb.Driver, "Worker").ConfigureAwait(false);
                        string missionId = captain.CurrentMissionId!;

                        await missions.HandleCompletionAsync(captain).ConfigureAwait(false);

                        Mission stored = (await testDb.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false))!;
                        AssertEqual(MissionStatusEnum.WorkProduced, stored.Status, "A skipped gate does not change completion");

                        DefinitionOfDoneReportService reports = new DefinitionOfDoneReportService(testDb.Driver, logging, () => missions.DefinitionOfDone);
                        MissionDefinitionOfDoneReport report = await reports.GetForMissionAsync(_Admin, stored).ConfigureAwait(false);

                        AssertEqual(RecordedHistoryStateEnum.Recorded, report.HistoryState, "Evaluation should be recorded");
                        AssertEqual(DefinitionOfDoneEvaluationOutcomeEnum.Skipped, report.LatestEvaluation!.Outcome, "Skipped is not Passed");
                        AssertEqual("DoD gate is disabled", report.LatestEvaluation.SkippedReason, "Skip reason");
                        AssertEqual(captain.Id, report.LatestEvaluation.CaptainId, "Captain identity");
                        AssertEqual(stored.DockId, report.LatestEvaluation.DockId, "Dock identity");
                        AssertNotNull(report.Configuration, "Configuration");
                        AssertFalse(report.Configuration!.Enabled, "Configuration reports disabled");
                        AssertEqual("DoD gate is disabled", report.Configuration.ExpectedSkipReason, "Configuration shares the skip rule");
                    }
                    finally
                    {
                        DeleteDirectories(settings);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Completion records a failed gate evaluation and the mission still fails", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    try
                    {
                        LoggingModule logging = CreateLogging();
                        DefinitionOfDoneSettings dod = new DefinitionOfDoneSettings { Enabled = true };
                        MissionService missions = CreateMissionService(testDb.Driver, settings, logging, dod);
                        Captain captain = await CreateWorkingMissionAsync(testDb.Driver, "Worker").ConfigureAwait(false);
                        string missionId = captain.CurrentMissionId!;

                        // No workflow profile resolves, so the real gate fails with missing-commands.
                        await missions.HandleCompletionAsync(captain).ConfigureAwait(false);

                        Mission stored = (await testDb.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false))!;
                        AssertEqual(MissionStatusEnum.Failed, stored.Status, "Recording must not change the failure decision");

                        DefinitionOfDoneReportService reports = new DefinitionOfDoneReportService(testDb.Driver, logging, () => missions.DefinitionOfDone);
                        MissionDefinitionOfDoneReport report = await reports.GetForMissionAsync(_Admin, stored).ConfigureAwait(false);

                        AssertEqual(RecordedHistoryStateEnum.Recorded, report.HistoryState, "Evaluation should be recorded");
                        AssertEqual(DefinitionOfDoneEvaluationOutcomeEnum.Failed, report.LatestEvaluation!.Outcome, "Failed outcome");
                        AssertEqual("missing-commands", report.LatestEvaluation.CommandLabel, "Failing command label");
                        AssertEqual(DefinitionOfDoneFailureClassEnum.Infra, report.LatestEvaluation.FailureClass, "Failure class");
                        AssertTrue(report.Configuration!.MissingCommands, "Configuration reports the missing commands");
                        AssertNull(report.Configuration.ExpectedSkipReason, "Gate applies to the Worker persona");
                    }
                    finally
                    {
                        DeleteDirectories(settings);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("A stored TestFail record round-trips its failing test names and overflow flag", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                    DefinitionOfDoneEvaluationRecord record = new DefinitionOfDoneEvaluationRecord
                    {
                        Outcome = DefinitionOfDoneEvaluationOutcomeEnum.Failed,
                        CommandLabel = "unit-test",
                        ExitCode = 1,
                        FailureClass = DefinitionOfDoneFailureClassEnum.TestFail,
                        OutputTail = "--- OUTPUT TAIL ---",
                        FailedTestNames = new System.Collections.Generic.List<string> { "X.Y.FirstTest", "X.Y.SecondTest" },
                        FailedTestNamesOverflow = false
                    };
                    await CreateEvaluationEventAsync(testDb.Driver, mission.Id, JsonSerializer.Serialize(record), DateTime.UtcNow).ConfigureAwait(false);

                    DefinitionOfDoneReportService reports = new DefinitionOfDoneReportService(testDb.Driver, CreateLogging(), () => null);
                    MissionDefinitionOfDoneReport report = await reports.GetForMissionAsync(_Admin, mission).ConfigureAwait(false);

                    AssertEqual(RecordedHistoryStateEnum.Recorded, report.HistoryState, "The record is recorded");
                    AssertNotNull(report.LatestEvaluation!.FailedTestNames, "The failing test names survive storage");
                    AssertEqual(2, report.LatestEvaluation!.FailedTestNames!.Count, "Both names are read back");
                    AssertEqual("X.Y.FirstTest", report.LatestEvaluation.FailedTestNames[0], "First name round-trips");
                    AssertEqual("X.Y.SecondTest", report.LatestEvaluation.FailedTestNames[1], "Second name round-trips");
                    AssertFalse(report.LatestEvaluation.FailedTestNamesOverflow, "The overflow flag round-trips as false");
                }
            }).ConfigureAwait(false);

            await RunTest("Without an active gate the report says inactive even when reloaded settings enable DoD", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                    ArmadaSettings reloaded = new ArmadaSettings();
                    reloaded.DefinitionOfDone = new DefinitionOfDoneSettings { Enabled = true };

                    DefinitionOfDoneReportService reports = new DefinitionOfDoneReportService(testDb.Driver, CreateLogging(), () => null);
                    DefinitionOfDoneConfiguration configuration = (await reports.GetForMissionAsync(_Admin, mission).ConfigureAwait(false)).Configuration!;

                    AssertFalse(configuration.GateActive, "No gate is wired");
                    AssertFalse(configuration.Enabled, "Reloaded settings do not make an unwired gate enabled");
                    AssertEqual(DefinitionOfDoneReportService.InactiveGateReason, configuration.ExpectedSkipReason, "Inactive reason");
                    AssertTrue(reloaded.DefinitionOfDone.Enabled, "The reloaded settings object is not consulted");
                }
            }).ConfigureAwait(false);

            await RunTest("A mission with no evaluation event reports NotRecorded", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                    DefinitionOfDoneReportService reports = new DefinitionOfDoneReportService(testDb.Driver, CreateLogging(), () => null);
                    MissionDefinitionOfDoneReport report = await reports.GetForMissionAsync(_Admin, mission).ConfigureAwait(false);
                    AssertEqual(RecordedHistoryStateEnum.NotRecorded, report.HistoryState, "No record");
                    AssertNull(report.LatestEvaluation, "No evaluation is invented");
                }
            }).ConfigureAwait(false);

            await RunTest("A malformed latest record is Unavailable and never falls back to an older pass", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                    DateTime now = DateTime.UtcNow;
                    await CreateEvaluationEventAsync(testDb.Driver, mission.Id, PassedPayload(), now.AddMinutes(-10)).ConfigureAwait(false);
                    await CreateEvaluationEventAsync(testDb.Driver, mission.Id, "{not json", now).ConfigureAwait(false);

                    DefinitionOfDoneReportService reports = new DefinitionOfDoneReportService(testDb.Driver, CreateLogging(), () => null);
                    MissionDefinitionOfDoneReport report = await reports.GetForMissionAsync(_Admin, mission).ConfigureAwait(false);
                    AssertEqual(RecordedHistoryStateEnum.Unavailable, report.HistoryState, "Malformed latest");
                    AssertNull(report.LatestEvaluation, "The older pass must not be reported");
                    AssertNotNull(report.HistoryUnavailableReason, "Reason is named");
                }
            }).ConfigureAwait(false);

            await RunTest("Incomplete, unsupported or unknown records are Unavailable", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DefinitionOfDoneReportService reports = new DefinitionOfDoneReportService(testDb.Driver, CreateLogging(), () => null);

                    Mission versioned = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                    await CreateEvaluationEventAsync(testDb.Driver, versioned.Id,
                        "{\"SchemaVersion\":2,\"Outcome\":\"Passed\"}", DateTime.UtcNow).ConfigureAwait(false);
                    MissionDefinitionOfDoneReport versionReport = await reports.GetForMissionAsync(_Admin, versioned).ConfigureAwait(false);
                    AssertEqual(RecordedHistoryStateEnum.Unavailable, versionReport.HistoryState, "Future schema version");

                    Mission unknown = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                    await CreateEvaluationEventAsync(testDb.Driver, unknown.Id,
                        "{\"SchemaVersion\":1,\"Outcome\":\"Certified\"}", DateTime.UtcNow).ConfigureAwait(false);
                    MissionDefinitionOfDoneReport unknownReport = await reports.GetForMissionAsync(_Admin, unknown).ConfigureAwait(false);
                    AssertEqual(RecordedHistoryStateEnum.Unavailable, unknownReport.HistoryState, "Unknown outcome");

                    Mission numeric = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                    await CreateEvaluationEventAsync(testDb.Driver, numeric.Id,
                        "{\"SchemaVersion\":1,\"Outcome\":99}", DateTime.UtcNow).ConfigureAwait(false);
                    MissionDefinitionOfDoneReport numericReport = await reports.GetForMissionAsync(_Admin, numeric).ConfigureAwait(false);
                    AssertEqual(RecordedHistoryStateEnum.Unavailable, numericReport.HistoryState, "Undefined numeric outcome");

                    string times = ",\"StartedUtc\":\"2026-09-13T12:00:00Z\",\"CompletedUtc\":\"2026-09-13T12:00:01Z\"";
                    string[] rejected = new string[]
                    {
                        "{}",
                        "{\"schemaVersion\":1,\"outcome\":\"Passed\"" + times + "}",
                        "{\"Outcome\":\"Passed\"" + times + "}",
                        "{\"SchemaVersion\":1" + times + "}",
                        "{\"SchemaVersion\":1,\"Outcome\":\"Skipped, NotVerifiable\"" + times + "}",
                        "{\"SchemaVersion\":1,\"Outcome\":\"passed\"" + times + "}",
                        "{\"SchemaVersion\":1,\"Outcome\":\"Failed\",\"FailureClass\":\"Bogus\"" + times + "}",
                        "{\"SchemaVersion\":1,\"Outcome\":\"Failed\",\"FailureClass\":99" + times + "}",
                        "{\"SchemaVersion\":1,\"Outcome\":\"Passed\"}"
                    };
                    foreach (string payload in rejected)
                    {
                        Mission incomplete = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                        await CreateEvaluationEventAsync(testDb.Driver, incomplete.Id, payload, DateTime.UtcNow).ConfigureAwait(false);
                        MissionDefinitionOfDoneReport incompleteReport = await reports.GetForMissionAsync(_Admin, incomplete).ConfigureAwait(false);
                        AssertEqual(RecordedHistoryStateEnum.Unavailable, incompleteReport.HistoryState, "Payload must be Unavailable: " + payload);
                        AssertNull(incompleteReport.LatestEvaluation, "No defaulted evaluation for: " + payload);
                    }

                    Mission complete = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                    await CreateEvaluationEventAsync(testDb.Driver, complete.Id,
                        "{\"SchemaVersion\":1,\"Outcome\":\"Failed\",\"CommandLabel\":\"build\",\"FailureClass\":\"TestFail\"" + times + "}", DateTime.UtcNow).ConfigureAwait(false);
                    MissionDefinitionOfDoneReport completeReport = await reports.GetForMissionAsync(_Admin, complete).ConfigureAwait(false);
                    AssertEqual(RecordedHistoryStateEnum.Recorded, completeReport.HistoryState, "A complete record with exact names is Recorded");
                    AssertEqual(DefinitionOfDoneFailureClassEnum.TestFail, completeReport.LatestEvaluation!.FailureClass, "Failure class is read");
                }
            }).ConfigureAwait(false);

            await RunTest("Latest records sharing a timestamp are Unavailable", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                    DateTime same = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
                    await CreateEvaluationEventAsync(testDb.Driver, mission.Id, PassedPayload(), same).ConfigureAwait(false);
                    await CreateEvaluationEventAsync(testDb.Driver, mission.Id,
                        JsonSerializer.Serialize(new DefinitionOfDoneEvaluationRecord { Outcome = DefinitionOfDoneEvaluationOutcomeEnum.Failed }), same).ConfigureAwait(false);

                    DefinitionOfDoneReportService reports = new DefinitionOfDoneReportService(testDb.Driver, CreateLogging(), () => null);
                    MissionDefinitionOfDoneReport report = await reports.GetForMissionAsync(_Admin, mission).ConfigureAwait(false);
                    AssertEqual(RecordedHistoryStateEnum.Unavailable, report.HistoryState, "Ambiguous order");
                }
            }).ConfigureAwait(false);

            await RunTest("Evaluation history is read in the caller's tenant scope", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    await testDb.Driver.Tenants.CreateAsync(new TenantMetadata { Id = "ten_dod_a", Name = "ten_dod_a" }).ConfigureAwait(false);
                    await testDb.Driver.Tenants.CreateAsync(new TenantMetadata { Id = "ten_dod_b", Name = "ten_dod_b" }).ConfigureAwait(false);
                    Mission mission = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                    await CreateEvaluationEventAsync(testDb.Driver, mission.Id, PassedPayload(), DateTime.UtcNow, "ten_dod_a").ConfigureAwait(false);

                    DefinitionOfDoneReportService reports = new DefinitionOfDoneReportService(testDb.Driver, CreateLogging(), () => null);
                    AuthContext tenantA = AuthContext.Authenticated("ten_dod_a", "usr_a", false, true, "Test");
                    AuthContext tenantB = AuthContext.Authenticated("ten_dod_b", "usr_b", false, true, "Test");

                    MissionDefinitionOfDoneReport ownReport = await reports.GetForMissionAsync(tenantA, mission).ConfigureAwait(false);
                    AssertEqual(RecordedHistoryStateEnum.Recorded, ownReport.HistoryState, "Owning tenant sees its record");

                    MissionDefinitionOfDoneReport otherReport = await reports.GetForMissionAsync(tenantB, mission).ConfigureAwait(false);
                    AssertEqual(RecordedHistoryStateEnum.NotRecorded, otherReport.HistoryState, "Another tenant sees no record");
                }
            }).ConfigureAwait(false);

            await RunTest("Evaluation history for an ordinary user is read in tenant and user scope", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                    await CreateEvaluationEventAsync(testDb.Driver, mission.Id, PassedPayload(), DateTime.UtcNow,
                        Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId).ConfigureAwait(false);

                    DefinitionOfDoneReportService reports = new DefinitionOfDoneReportService(testDb.Driver, CreateLogging(), () => null);
                    AuthContext owner = AuthContext.Authenticated(Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId, false, false, "Test");
                    AuthContext colleague = AuthContext.Authenticated(Armada.Core.Constants.DefaultTenantId, "usr_dod_other", false, false, "Test");

                    MissionDefinitionOfDoneReport ownerReport = await reports.GetForMissionAsync(owner, mission).ConfigureAwait(false);
                    AssertEqual(RecordedHistoryStateEnum.Recorded, ownerReport.HistoryState, "The owning user sees the record");

                    MissionDefinitionOfDoneReport colleagueReport = await reports.GetForMissionAsync(colleague, mission).ConfigureAwait(false);
                    AssertEqual(RecordedHistoryStateEnum.NotRecorded, colleagueReport.HistoryState, "Another user in the tenant sees no record");
                }
            }).ConfigureAwait(false);

            await RunTest("Configuration selects the vessel profile before global and exposes no command text", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("dod-config-vessel", "https://github.com/test/config.git");
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                    {
                        Name = "Global Profile",
                        Scope = WorkflowProfileScopeEnum.Global,
                        BuildCommand = "global-build --token=globalsecret1",
                        IsDefault = true,
                        Active = true
                    }).ConfigureAwait(false);
                    WorkflowProfile vesselProfile = await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                    {
                        Name = "Vessel Profile",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        UnitTestCommand = "vessel-test --password=vesselsecret1",
                        Active = true
                    }).ConfigureAwait(false);

                    Mission mission = new Mission("config mission", "Implement.");
                    mission.VesselId = vessel.Id;
                    mission.Persona = "Worker";
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DefinitionOfDone = new DefinitionOfDoneSettings { Enabled = true };
                    DefinitionOfDoneReportService reports = new DefinitionOfDoneReportService(testDb.Driver, CreateLogging(), () => new DefinitionOfDoneGate(settings.DefinitionOfDone, testDb.Driver, CreateLogging()));
                    MissionDefinitionOfDoneReport report = await reports.GetForMissionAsync(_Admin, mission).ConfigureAwait(false);

                    DefinitionOfDoneConfiguration configuration = report.Configuration!;
                    AssertEqual(vesselProfile.Id, configuration.WorkflowProfileId, "Vessel scope wins over global");
                    AssertEqual(WorkflowProfileScopeEnum.Vessel, configuration.WorkflowProfileScope, "Selected scope");
                    AssertFalse(configuration.HasBuildCommand, "Vessel profile has no build command");
                    AssertTrue(configuration.HasUnitTestCommand, "Vessel profile has a unit-test command");
                    AssertFalse(configuration.MissingCommands, "Commands are present");

                    string json = JsonSerializer.Serialize(report);
                    AssertFalse(json.Contains("vessel-test", StringComparison.Ordinal), "Command text is not exposed");
                    AssertFalse(json.Contains("secret1", StringComparison.Ordinal), "Command secrets are not exposed");
                }
            }).ConfigureAwait(false);

            await RunTest("Doc-only marker and persona rules are reported without running the gate", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DefinitionOfDone = new DefinitionOfDoneSettings { Enabled = true };
                    DefinitionOfDoneReportService reports = new DefinitionOfDoneReportService(testDb.Driver, CreateLogging(), () => new DefinitionOfDoneGate(settings.DefinitionOfDone, testDb.Driver, CreateLogging()));

                    Mission judge = new Mission("judge", "Review.");
                    judge.Persona = "Judge";
                    await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);
                    DefinitionOfDoneConfiguration judgeConfig = (await reports.GetForMissionAsync(_Admin, judge).ConfigureAwait(false)).Configuration!;
                    AssertFalse(judgeConfig.PersonaApplies, "Judge is not an applied persona");
                    AssertEqual("persona 'Judge' is not in AppliedPersonas", judgeConfig.ExpectedSkipReason, "Persona skip reason");

                    Mission docOnly = new Mission("docs", "Update docs. " + settings.DefinitionOfDone.DocOnlyMarker);
                    docOnly.Persona = "Worker";
                    await testDb.Driver.Missions.CreateAsync(docOnly).ConfigureAwait(false);
                    DefinitionOfDoneConfiguration docConfig = (await reports.GetForMissionAsync(_Admin, docOnly).ConfigureAwait(false)).Configuration!;
                    AssertTrue(docConfig.DocOnlyMarkerPresent, "Marker detected");
                    AssertEqual("mission description contains doc-only opt-out marker", docConfig.ExpectedSkipReason, "Doc-only skip reason");
                }
            }).ConfigureAwait(false);

            await RunTest("Recorded failure output is redacted and bounded", () =>
            {
                string secretLine = "password=supersecret12\n";
                string longOutput = secretLine + new string('x', DefinitionOfDoneEvaluationRecord.MaxOutputTailLength * 2) + "\n" + secretLine;
                DefinitionOfDoneResult failed = DefinitionOfDoneResult.Fail("unit-test", 1, longOutput, DefinitionOfDoneFailureClassEnum.TestFail);
                DefinitionOfDoneEvaluationRecord record = DefinitionOfDoneEvaluationRecord.FromResult(failed, DateTime.UtcNow);

                AssertEqual(DefinitionOfDoneEvaluationOutcomeEnum.Failed, record.Outcome, "Failed outcome");
                AssertFalse(record.OutputTail!.Contains("supersecret12", StringComparison.Ordinal), "Secrets are redacted");
                AssertTrue(record.OutputTail.Length <= DefinitionOfDoneEvaluationRecord.MaxOutputTailLength, "Output, marker included, stays within the bound");
                AssertTrue(record.OutputTail.EndsWith("[REDACTED]\n", StringComparison.Ordinal), "The tail of the output is kept");
                AssertEqual(record.OutputTail, DefinitionOfDoneEvaluationRecord.BoundOutput(record.OutputTail), "Bounding its own result changes nothing");

                DefinitionOfDoneEvaluationRecord skipped = DefinitionOfDoneEvaluationRecord.FromResult(DefinitionOfDoneResult.Skipped("persona"), DateTime.UtcNow);
                AssertEqual(DefinitionOfDoneEvaluationOutcomeEnum.Skipped, skipped.Outcome, "Skip with Passed=true records Skipped");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Stored records the writer cannot produce are Unavailable, and writer-shaped records stay Recorded", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DefinitionOfDoneReportService reports = new DefinitionOfDoneReportService(testDb.Driver, CreateLogging(), () => null);
                    string times = ",\"StartedUtc\":\"2026-09-13T12:00:00Z\",\"CompletedUtc\":\"2026-09-13T12:00:01Z\"";
                    string[] contradictory = new string[]
                    {
                        "{\"SchemaVersion\":1,\"Outcome\":\"Passed\",\"FailureClass\":\"TestFail\"" + times + "}",
                        "{\"SchemaVersion\":1,\"Outcome\":\"Passed\",\"CommandLabel\":\"build\"" + times + "}",
                        "{\"SchemaVersion\":1,\"Outcome\":\"Passed\",\"ExitCode\":1" + times + "}",
                        "{\"SchemaVersion\":1,\"Outcome\":\"Passed\",\"OutputTail\":\"boom\"" + times + "}",
                        "{\"SchemaVersion\":1,\"Outcome\":\"Passed\",\"SkippedReason\":\"persona\"" + times + "}",
                        "{\"SchemaVersion\":1,\"Outcome\":\"Skipped\"" + times + "}",
                        "{\"SchemaVersion\":1,\"Outcome\":\"NotVerifiable\"" + times + "}",
                        "{\"SchemaVersion\":1,\"Outcome\":\"Skipped\",\"SkippedReason\":\"persona\",\"CommandLabel\":\"build\"" + times + "}",
                        "{\"SchemaVersion\":1,\"Outcome\":\"Failed\",\"FailureClass\":\"TestFail\"" + times + "}",
                        "{\"SchemaVersion\":1,\"Outcome\":\"Failed\",\"CommandLabel\":\"build\",\"SkippedReason\":\"persona\"" + times + "}",
                        "{\"SchemaVersion\":1,\"Outcome\":\"EvaluationError\",\"FailureClass\":\"Infra\"" + times + "}",
                        "{\"SchemaVersion\":1,\"Outcome\":\"Passed\",\"RecoveryAttempts\":-1" + times + "}"
                    };
                    foreach (string payload in contradictory)
                    {
                        Mission mission = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                        await CreateEvaluationEventAsync(testDb.Driver, mission.Id, payload, DateTime.UtcNow).ConfigureAwait(false);
                        MissionDefinitionOfDoneReport report = await reports.GetForMissionAsync(_Admin, mission).ConfigureAwait(false);
                        Console.WriteLine("CONTRADICTION " + payload + " -> " + report.HistoryState);
                        AssertEqual(RecordedHistoryStateEnum.Unavailable, report.HistoryState, "A record the writer cannot produce is Unavailable: " + payload);
                        AssertNull(report.LatestEvaluation, "No evaluation is reported for: " + payload);
                        AssertNotNull(report.HistoryUnavailableReason, "The reason is named for: " + payload);
                    }

                    DateTime started = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
                    List<DefinitionOfDoneEvaluationRecord> writerShaped = new List<DefinitionOfDoneEvaluationRecord>
                    {
                        DefinitionOfDoneEvaluationRecord.FromResult(DefinitionOfDoneResult.Pass(), started),
                        DefinitionOfDoneEvaluationRecord.FromResult(DefinitionOfDoneResult.Skipped("persona"), started),
                        DefinitionOfDoneEvaluationRecord.FromResult(DefinitionOfDoneResult.Fail("build", 2, "error", DefinitionOfDoneFailureClassEnum.Compile), started),
                        DefinitionOfDoneEvaluationRecord.NotVerifiable("the mission changed nothing"),
                        DefinitionOfDoneEvaluationRecord.EvaluationError(started, "InvalidOperationException")
                    };
                    foreach (DefinitionOfDoneEvaluationRecord record in writerShaped)
                    {
                        record.RecoveryAttempts = 2;
                        Mission mission = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                        await CreateEvaluationEventAsync(testDb.Driver, mission.Id, JsonSerializer.Serialize(record), DateTime.UtcNow).ConfigureAwait(false);
                        MissionDefinitionOfDoneReport report = await reports.GetForMissionAsync(_Admin, mission).ConfigureAwait(false);
                        AssertEqual(RecordedHistoryStateEnum.Recorded, report.HistoryState, "A writer-shaped " + record.Outcome + " record is Recorded");
                        AssertEqual(2, report.LatestEvaluation!.RecoveryAttempts, "Recovery attempts are read for " + record.Outcome);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("An oversized stored record is Unavailable before it is parsed, and a worst-case writer record is Recorded", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DefinitionOfDoneReportService reports = new DefinitionOfDoneReportService(testDb.Driver, CreateLogging(), () => null);

                    DefinitionOfDoneEvaluationRecord oversized = DefinitionOfDoneEvaluationRecord.FromResult(
                        DefinitionOfDoneResult.Fail("build", 1, null, DefinitionOfDoneFailureClassEnum.TestFail), DateTime.UtcNow);
                    oversized.OutputTail = new string('x', 300000);
                    string oversizedPayload = JsonSerializer.Serialize(oversized);
                    Mission large = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                    await CreateEvaluationEventAsync(testDb.Driver, large.Id, oversizedPayload, DateTime.UtcNow).ConfigureAwait(false);
                    MissionDefinitionOfDoneReport largeReport = await reports.GetForMissionAsync(_Admin, large).ConfigureAwait(false);
                    Console.WriteLine("OVERSIZED payload length " + oversizedPayload.Length + " -> " + largeReport.HistoryState + " (" + largeReport.HistoryUnavailableReason + ")");
                    AssertEqual(RecordedHistoryStateEnum.Unavailable, largeReport.HistoryState, "A payload far beyond anything the writer produces is Unavailable");
                    AssertTrue((largeReport.HistoryUnavailableReason ?? String.Empty).Contains("too large", StringComparison.Ordinal), "The reason names the size");

                    string unicodeId = new string('é', 450);
                    DefinitionOfDoneEvaluationRecord worstCase = DefinitionOfDoneEvaluationRecord.FromResult(
                        DefinitionOfDoneResult.Fail(new string('é', 1000), 1, new string('é', 20000), DefinitionOfDoneFailureClassEnum.TestFail), DateTime.UtcNow);
                    worstCase.CaptainId = unicodeId;
                    worstCase.DockId = unicodeId;
                    worstCase.BranchName = unicodeId;
                    worstCase.CommitHash = unicodeId;
                    string worstPayload = JsonSerializer.Serialize(worstCase);
                    Mission worst = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                    await CreateEvaluationEventAsync(testDb.Driver, worst.Id, worstPayload, DateTime.UtcNow).ConfigureAwait(false);
                    MissionDefinitionOfDoneReport worstReport = await reports.GetForMissionAsync(_Admin, worst).ConfigureAwait(false);
                    Console.WriteLine("WORST-CASE writer payload length " + worstPayload.Length + " -> " + worstReport.HistoryState);
                    AssertEqual(RecordedHistoryStateEnum.Recorded, worstReport.HistoryState, "The largest record the writer produces is Recorded");
                    AssertEqual(unicodeId, worstReport.LatestEvaluation!.CaptainId, "A full-length Unicode identifier is returned whole");
                }
            }).ConfigureAwait(false);

            await RunTest("Stored skipped reason and command label are redacted and bounded on write and read", async () =>
            {
                string secretReason = "token=abcdef123456 " + new string('r', 5000);
                string secretLabel = "password=supersecret12 " + new string('l', 5000);

                DefinitionOfDoneEvaluationRecord written = DefinitionOfDoneEvaluationRecord.FromResult(
                    DefinitionOfDoneResult.Fail(secretLabel, 1, "boom", DefinitionOfDoneFailureClassEnum.TestFail), DateTime.UtcNow);
                AssertFalse(written.CommandLabel!.Contains("supersecret12", StringComparison.Ordinal), "The writer redacts the command label");
                AssertTrue(written.CommandLabel.Length <= DefinitionOfDoneEvaluationRecord.MaxLabelLength, "The writer bounds the command label");

                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DefinitionOfDoneReportService reports = new DefinitionOfDoneReportService(testDb.Driver, CreateLogging(), () => null);
                    string times = ",\"StartedUtc\":\"2026-09-13T12:00:00Z\",\"CompletedUtc\":\"2026-09-13T12:00:01Z\"";

                    Mission skippedMission = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                    await CreateEvaluationEventAsync(testDb.Driver, skippedMission.Id,
                        "{\"SchemaVersion\":1,\"Outcome\":\"Skipped\",\"SkippedReason\":" + JsonSerializer.Serialize(secretReason) + times + "}", DateTime.UtcNow).ConfigureAwait(false);
                    MissionDefinitionOfDoneReport skippedReport = await reports.GetForMissionAsync(_Admin, skippedMission).ConfigureAwait(false);
                    string reason = skippedReport.LatestEvaluation?.SkippedReason ?? String.Empty;
                    Console.WriteLine("STORED REASON length " + reason.Length + " secretPresent=" + reason.Contains("abcdef123456", StringComparison.Ordinal));
                    AssertFalse(reason.Contains("abcdef123456", StringComparison.Ordinal), "A stored skipped reason is redacted on read");
                    AssertTrue(reason.Length <= DefinitionOfDoneEvaluationRecord.MaxLabelLength, "A stored skipped reason is bounded on read");

                    Mission failedMission = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                    await CreateEvaluationEventAsync(testDb.Driver, failedMission.Id,
                        "{\"SchemaVersion\":1,\"Outcome\":\"Failed\",\"FailureClass\":\"TestFail\",\"CommandLabel\":" + JsonSerializer.Serialize(secretLabel) + times + "}", DateTime.UtcNow).ConfigureAwait(false);
                    MissionDefinitionOfDoneReport failedReport = await reports.GetForMissionAsync(_Admin, failedMission).ConfigureAwait(false);
                    string label = failedReport.LatestEvaluation?.CommandLabel ?? String.Empty;
                    Console.WriteLine("STORED LABEL length " + label.Length + " secretPresent=" + label.Contains("supersecret12", StringComparison.Ordinal));
                    AssertFalse(label.Contains("supersecret12", StringComparison.Ordinal), "A stored command label is redacted on read");
                    AssertTrue(label.Length <= DefinitionOfDoneEvaluationRecord.MaxLabelLength, "A stored command label is bounded on read");
                }
            }).ConfigureAwait(false);

            await RunTest("A reversed evaluation time range is still Recorded, because a clock step can produce it", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DefinitionOfDoneReportService reports = new DefinitionOfDoneReportService(testDb.Driver, CreateLogging(), () => null);
                    Mission mission = await CreateBareMissionAsync(testDb.Driver).ConfigureAwait(false);
                    await CreateEvaluationEventAsync(testDb.Driver, mission.Id,
                        "{\"SchemaVersion\":1,\"Outcome\":\"Passed\",\"StartedUtc\":\"2026-09-13T12:00:01Z\",\"CompletedUtc\":\"2026-09-13T12:00:00Z\"}", DateTime.UtcNow).ConfigureAwait(false);
                    MissionDefinitionOfDoneReport report = await reports.GetForMissionAsync(_Admin, mission).ConfigureAwait(false);
                    Console.WriteLine("REVERSED times -> " + report.HistoryState);
                    AssertEqual(RecordedHistoryStateEnum.Recorded, report.HistoryState, "A reversed range is reported as recorded, not hidden");
                }
            }).ConfigureAwait(false);

            await RunTest("A Research mission that made no commit skips the gate on a red suite and hands off to its next stage", async () =>
            {
                ReadOnlyGateScenarioResult result = await RunReadOnlyGateScenarioAsync(
                    MissionModeEnum.Research, _ScenarioStartCommit, Array.Empty<string>(), LongReport()).ConfigureAwait(false);

                AssertEqual(MissionStatusEnum.WorkProduced, result.Mission.Status,
                    "A read-only mission with no commit must not fail on a red base suite. FailureReason: " + result.Mission.FailureReason);
                AssertNull(result.Mission.FailureReason, "No failure is recorded");
                AssertNotEqual(MissionStatusEnum.Cancelled, result.Dependent.Status, "The next stage is not cancelled");
                AssertEqual(result.Mission.BranchName, result.Dependent.BranchName, "The next stage was prepared by the handoff");
                AssertNotNull(result.LatestEvaluation, "The skip is recorded as an evaluation event");
                AssertEqual(DefinitionOfDoneEvaluationOutcomeEnum.Skipped, result.LatestEvaluation!.Outcome, "Recorded as Skipped");
                AssertTrue((result.LatestEvaluation.SkippedReason ?? String.Empty).StartsWith(DefinitionOfDoneGate.ReadOnlyNoCommitSkipReason, StringComparison.Ordinal),
                    "The skip reason is named. Reason: " + result.LatestEvaluation.SkippedReason);
                AssertContains("validation skipped: " + DefinitionOfDoneGate.ReadOnlyNoCommitSkipReason, result.Activity, "The activity log names the skip");
                AssertFalse(result.Activity.Contains("validation passed: definition-of-done gate", StringComparison.Ordinal), "A skip is not reported as a pass");
            }).ConfigureAwait(false);

            await RunTest("An Audit mission that made no commit skips the gate on a red suite the same way", async () =>
            {
                ReadOnlyGateScenarioResult result = await RunReadOnlyGateScenarioAsync(
                    MissionModeEnum.Audit, _ScenarioStartCommit, Array.Empty<string>(), LongReport()).ConfigureAwait(false);

                AssertEqual(MissionStatusEnum.WorkProduced, result.Mission.Status,
                    "An Audit mission with no commit must not fail on a red base suite. FailureReason: " + result.Mission.FailureReason);
                AssertNotEqual(MissionStatusEnum.Cancelled, result.Dependent.Status, "The next stage is not cancelled");
                AssertEqual(DefinitionOfDoneEvaluationOutcomeEnum.Skipped, result.LatestEvaluation!.Outcome, "Recorded as Skipped");
                AssertTrue((result.LatestEvaluation.SkippedReason ?? String.Empty).StartsWith(DefinitionOfDoneGate.ReadOnlyNoCommitSkipReason, StringComparison.Ordinal),
                    "The skip reason is named. Reason: " + result.LatestEvaluation.SkippedReason);
            }).ConfigureAwait(false);

            await RunTest("A Research mission that made a commit still runs the gate and fails on the red suite", async () =>
            {
                ReadOnlyGateScenarioResult result = await RunReadOnlyGateScenarioAsync(
                    MissionModeEnum.Research, "fedcba987654", new[] { "docs/report.md" }, LongReport()).ConfigureAwait(false);

                AssertEqual(MissionStatusEnum.Failed, result.Mission.Status, "A read-only mission that committed is still gated");
                AssertContains("unit-test", result.Mission.FailureReason ?? String.Empty, "The red unit-test command fails the gate");
                AssertEqual(MissionStatusEnum.Cancelled, result.Dependent.Status, "The next stage is cancelled as before");
                AssertEqual(DefinitionOfDoneEvaluationOutcomeEnum.Failed, result.LatestEvaluation!.Outcome, "Recorded as Failed");
            }).ConfigureAwait(false);

            await RunTest("An Implementation mission that made no commit is not exempted by the read-only rule", async () =>
            {
                ReadOnlyGateScenarioResult result = await RunReadOnlyGateScenarioAsync(
                    MissionModeEnum.Implementation, _ScenarioStartCommit, Array.Empty<string>(), "worker exited without a marker").ConfigureAwait(false);

                AssertEqual(MissionStatusEnum.Failed, result.Mission.Status, "An Implementation mission with no commit still fails");
                AssertContains("no_op_completion_detected", result.Mission.FailureReason ?? String.Empty, "The no-op rule still applies");
                AssertFalse(result.Activity.Contains(DefinitionOfDoneGate.ReadOnlyNoCommitSkipReason, StringComparison.Ordinal),
                    "The read-only skip never applies to an Implementation mission");
            }).ConfigureAwait(false);
        }

        private const string _ScenarioStartCommit = "abc123def456";

        private static string LongReport()
        {
            return "## Findings\n" + new string('x', 1200) + "\n[ARMADA:RESULT] COMPLETE";
        }

        private sealed class ReadOnlyGateScenarioResult
        {
            public Mission Mission { get; set; } = null!;
            public Mission Dependent { get; set; } = null!;
            public DefinitionOfDoneEvaluationRecord? LatestEvaluation { get; set; } = null;
            public string Activity { get; set; } = String.Empty;
        }

        /// <summary>
        /// Complete a first-stage mission whose vessel build passes and whose unit-test command fails, with a
        /// dependent Judge stage waiting on it. The dock start commit is fixed; the head commit and the changed
        /// files since dock start are the scenario's inputs.
        /// </summary>
        private static async Task<ReadOnlyGateScenarioResult> RunReadOnlyGateScenarioAsync(
            MissionModeEnum mode,
            string headCommit,
            IReadOnlyList<string> changedFilesSinceStart,
            string agentOutput)
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                ArmadaSettings settings = CreateSettings();
                try
                {
                    LoggingModule logging = CreateLogging();
                    StubGitService git = new StubGitService();
                    git.HeadCommitHashResult = headCommit;
                    git.ChangedFilesSinceResult = changedFilesSinceStart;
                    IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                    MissionService missions = new MissionService(logging, testDb.Driver, settings, docks, captains, git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    missions.DefinitionOfDone = new DefinitionOfDoneGate(new DefinitionOfDoneSettings { Enabled = true }, testDb.Driver, logging);
                    missions.OnGetMissionOutput = _ => agentOutput;
                    missions.OnMissionComplete = (m, d) => Task.CompletedTask;

                    Vessel vessel = new Vessel("dod-readonly-vessel-" + Guid.NewGuid().ToString("N"), "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, "bare");
                    vessel.WorkingDirectory = Path.Combine(settings.ReposDirectory, "work");
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                    {
                        Name = "Red Suite Profile",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = "exit 0",
                        UnitTestCommand = "exit 1",
                        IsDefault = true,
                        Active = true
                    }).ConfigureAwait(false);

                    Captain captain = new Captain("dod-readonly-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("dod-readonly-voyage")).ConfigureAwait(false);

                    Mission mission = new Mission("Read-only gate mission", "Report on the repository.");
                    mission.Mode = mode;
                    mission.Persona = "Worker";
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.StartedUtc = DateTime.UtcNow.AddMinutes(-5);
                    mission.VesselId = vessel.Id;
                    mission.VoyageId = voyage.Id;
                    mission.CaptainId = captain.Id;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.CaptainId = captain.Id;
                    dock.WorktreePath = Path.Combine(settings.DocksDirectory, "wt");
                    dock.BranchName = "armada/dod-readonly/stage";
                    dock.Active = true;
                    dock = await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);
                    Directory.CreateDirectory(dock.WorktreePath);

                    mission.DockId = dock.Id;
                    await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
                    captain.CurrentMissionId = mission.Id;
                    captain.CurrentDockId = dock.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    Mission dependent = new Mission("[Judge] Review the report", "Review.");
                    dependent.Mode = mode;
                    dependent.Persona = "Judge";
                    dependent.Status = MissionStatusEnum.Pending;
                    dependent.VesselId = vessel.Id;
                    dependent.VoyageId = voyage.Id;
                    dependent.DependsOnMissionId = mission.Id;
                    dependent = await testDb.Driver.Missions.CreateAsync(dependent).ConfigureAwait(false);

                    Directory.CreateDirectory(Path.Combine(settings.LogDirectory, "docks"));
                    await File.WriteAllTextAsync(Path.Combine(settings.LogDirectory, "docks", dock.Id + ".start"), _ScenarioStartCommit + "\n").ConfigureAwait(false);

                    await missions.HandleCompletionAsync(captain, mission.Id).ConfigureAwait(false);

                    ReadOnlyGateScenarioResult result = new ReadOnlyGateScenarioResult();
                    result.Mission = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    result.Dependent = (await testDb.Driver.Missions.ReadAsync(dependent.Id).ConfigureAwait(false))!;

                    DefinitionOfDoneReportService reports = new DefinitionOfDoneReportService(testDb.Driver, logging, () => missions.DefinitionOfDone);
                    MissionDefinitionOfDoneReport report = await reports.GetForMissionAsync(_Admin, result.Mission).ConfigureAwait(false);
                    result.LatestEvaluation = report.LatestEvaluation;

                    string logPath = Path.Combine(settings.LogDirectory, "missions", mission.Id + ".log");
                    result.Activity = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath).ConfigureAwait(false) : String.Empty;
                    return result;
                }
                finally
                {
                    DeleteDirectories(settings);
                }
            }
        }
    }
}
