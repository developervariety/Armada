namespace Armada.Test.Unit.Suites.Recovery
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using Microsoft.Data.Sqlite;
    using SyslogLogging;

    /// <summary>
    /// Regression coverage for the rescue Worker-to-TestEngineer handoff release path,
    /// the stalled-rescue stuck-voyage detector, and the non-rescue pipeline regression guard.
    ///
    /// Handoff tests prove the gate by observing AssignmentState transitions rather than
    /// a successful agent launch (no OnLaunchAgent handler is configured in the unit-test
    /// harness). The key invariant:
    ///   - Before branch stamp: AssignmentState == WaitingForDependency (dep check failed)
    ///   - After branch stamp:  AssignmentState == Provisioning (dep check passed, gate cleared)
    /// </summary>
    public class RescueVoyageHandoffTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Rescue Voyage Handoff";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            // --- Test 1: Worker WorkProduced + branch stamp unlocks TestEngineer ---

            await RunTest("WorkerWorkProduced_BranchStamped_TestEngineerReleasedToIdle", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_rescue_release", "usr_rescue_release").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_rescue_release", "usr_rescue_release").ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb, vessel).ConfigureAwait(false);

                // Worker has finished and recorded a branch name.
                Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Title = "Worker mission",
                    Status = MissionStatusEnum.WorkProduced,
                    Persona = "Worker",
                    BranchName = "armada/rescue-worker/msn_test"
                }).ConfigureAwait(false);

                // TestEngineer created before the handoff stamped its branch.
                Mission testEngineer = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Title = "TestEngineer mission",
                    Status = MissionStatusEnum.Pending,
                    Persona = "TestEngineer",
                    DependsOnMissionId = worker.Id,
                    BranchName = null
                }).ConfigureAwait(false);

                await CreateIdleCaptainAsync(testDb, "rescue-idle-captain").ConfigureAwait(false);

                ArmadaSettings settings = CreateSettings();
                MissionService missionSvc = CreateMissionService(testDb.Driver, settings, new LoggingModule(), new StubGitService());

                // The TestEngineer was created with a null branch after the Worker had already
                // reached WorkProduced (the creation-order race). The first assignment pass must
                // self-heal the missed handoff in place -- stamping the Worker branch and clearing
                // the dependency gate -- rather than parking the dependent at WaitingForDependency
                // forever. TryAssignAsync returns false only because no OnLaunchAgent handler is
                // configured in this harness, but the AssignmentState must advance to Provisioning,
                // proving the dependency gate cleared on the self-heal pass.
                bool assignResult = await missionSvc.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);
                AssertFalse(assignResult, "TryAssignAsync returns false in the test harness (no launch handler), which is expected.");

                Mission? healed = await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false);
                AssertEqual(MissionAssignmentStateEnum.Provisioning, healed!.AssignmentState,
                    "TestEngineer must self-heal to Provisioning on the first pass, not park at WaitingForDependency.");
                AssertEqual(worker.BranchName, healed.BranchName,
                    "The self-heal must stamp the upstream Worker branch onto the TestEngineer.");
            }).ConfigureAwait(false);

            // Variant: stranded shape -- TestEngineer BranchName is null while Worker has one.
            // Proves the stranded-shape detection and self-heal path.

            await RunTest("WorkerWorkProduced_DependentNullBranch_StrandedThenSelfHeal", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_rescue_strand", "usr_rescue_strand").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_rescue_strand", "usr_rescue_strand").ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb, vessel).ConfigureAwait(false);

                Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Title = "Worker mission",
                    Status = MissionStatusEnum.WorkProduced,
                    Persona = "Worker",
                    BranchName = "armada/rescue-worker/msn_stranded"
                }).ConfigureAwait(false);

                // Stranded: null BranchName while Worker has a branch set.
                Mission dependent = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Title = "TestEngineer stranded",
                    Status = MissionStatusEnum.Pending,
                    Persona = "TestEngineer",
                    DependsOnMissionId = worker.Id,
                    BranchName = null
                }).ConfigureAwait(false);

                await CreateIdleCaptainAsync(testDb, "rescue-idle-captain-strand").ConfigureAwait(false);
                MissionService missionSvc = CreateMissionService(testDb.Driver, CreateSettings(), new LoggingModule(), new StubGitService());

                // Stranded shape: the dependent carries no branch while its WorkProduced same-vessel
                // dependency does. The assignment pass must lazily run the missed handoff (stamp the
                // branch + inject prior-stage context) and clear the dependency gate in the same pass.
                bool healedResult = await missionSvc.TryAssignAsync(dependent, vessel).ConfigureAwait(false);
                AssertFalse(healedResult, "TryAssignAsync returns false in the test harness (no launch handler), which is expected.");

                Mission? healed = await testDb.Driver.Missions.ReadAsync(dependent.Id).ConfigureAwait(false);
                AssertEqual(MissionAssignmentStateEnum.Provisioning, healed!.AssignmentState,
                    "The stranded dependent must self-heal to Provisioning, not stay WaitingForDependency.");
                AssertEqual(worker.BranchName, healed.BranchName,
                    "The self-heal must stamp the upstream branch onto the stranded dependent.");
            }).ConfigureAwait(false);

            // --- Test 2: TestEngineer WorkProduced -> Judge released ---

            await RunTest("TestEngineerWorkProduced_JudgePending_JudgeReleasedAfterHandoffStamp", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_rescue_judge", "usr_rescue_judge").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_rescue_judge", "usr_rescue_judge").ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb, vessel).ConfigureAwait(false);

                Mission testEngineer = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Title = "TestEngineer mission",
                    Status = MissionStatusEnum.WorkProduced,
                    Persona = "TestEngineer",
                    BranchName = "armada/rescue-te/msn_te_branch"
                }).ConfigureAwait(false);

                // Judge created before the handoff stamp.
                Mission judge = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Title = "Judge mission",
                    Status = MissionStatusEnum.Pending,
                    Persona = "Judge",
                    DependsOnMissionId = testEngineer.Id,
                    BranchName = null
                }).ConfigureAwait(false);

                await CreateIdleCaptainAsync(testDb, "rescue-idle-judge-captain").ConfigureAwait(false);
                MissionService missionSvc = CreateMissionService(testDb.Driver, CreateSettings(), new LoggingModule(), new StubGitService());

                // The Judge depends on a WorkProduced TestEngineer whose branch was never propagated
                // (null Judge branch). The TestEngineer->Judge handoff self-heals generically on the
                // first assignment pass: the upstream branch is stamped and the dependency gate
                // clears (Provisioning), rather than parking the Judge at WaitingForDependency.
                bool releasedResult = await missionSvc.TryAssignAsync(judge, vessel).ConfigureAwait(false);
                AssertFalse(releasedResult, "TryAssignAsync returns false in the test harness (no launch handler), which is expected.");

                Mission? released = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                AssertEqual(MissionAssignmentStateEnum.Provisioning, released!.AssignmentState,
                    "Judge must self-heal to Provisioning on the first pass once its TestEngineer is WorkProduced.");
                AssertEqual(testEngineer.BranchName, released.BranchName,
                    "The self-heal must stamp the upstream TestEngineer branch onto the Judge stage.");
            }).ConfigureAwait(false);

            // --- Test 3: Stalled-rescue stuck detection ---

            await RunTest("StalledRescue_OpenVoyageNoLiveMissions_OpensHighIncident", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_rescue_stuck", "usr_rescue_stuck").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_rescue_stuck", "usr_rescue_stuck").ConfigureAwait(false);

                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Stalled rescue voyage")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Open
                }).ConfigureAwait(false);

                // Worker: WorkProduced with null BranchName (skips landing-drain enqueue path).
                Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Title = "Rescue worker",
                    Status = MissionStatusEnum.WorkProduced,
                    Persona = "Worker",
                    BranchName = null
                }).ConfigureAwait(false);

                // TestEngineer stuck in WaitingForDependency -- not a live status.
                Mission waitingTE = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Title = "TestEngineer waiting",
                    Status = MissionStatusEnum.Pending,
                    Persona = "TestEngineer",
                    AssignmentState = MissionAssignmentStateEnum.WaitingForDependency
                }).ConfigureAwait(false);

                // Backdate all timestamps to 10 minutes ago so the quiet-minutes gate fires.
                // The DB layer always overrides LastUpdateUtc to DateTime.UtcNow on Create/Update,
                // so raw SQL is the only way to inject an old timestamp.
                await BackdateTimestampsAsync(testDb, voyage.Id, new List<string> { worker.Id, waitingTE.Id }, 10).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestratorWithLandingDrain(
                    testDb.Driver,
                    new NullAdmiralService(),
                    incidents,
                    new RunbookService(testDb.Driver, new LoggingModule()),
                    BuildStuckDetectionSettings());

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated("ten_rescue_stuck", "usr_rescue_stuck", false, true, "UnitTest");
                EnumerationResult<Incident> page = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    VoyageId = voyage.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);

                AssertEqual(1, page.Objects.Count, "Exactly one stuck-voyage incident must be opened.");
                AssertEqual(IncidentSeverityEnum.High, page.Objects[0].Severity, "Stuck-voyage incident must be High severity.");
                AssertContains("no live missions", page.Objects[0].Summary ?? "", "Incident summary must note no live missions.");
            }).ConfigureAwait(false);

            // Prove churned LastUpdateUtc does not suppress de-dup: a second sweep on the same
            // stuck shape must NOT open a duplicate incident (HasOpenStuckVoyageIncidentAsync guard).

            await RunTest("StalledRescue_RepeatedSweep_NoDuplicateIncident", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_rescue_dedup", "usr_rescue_dedup").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_rescue_dedup", "usr_rescue_dedup").ConfigureAwait(false);

                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Stalled dedup voyage")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Open
                }).ConfigureAwait(false);

                Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Title = "Rescue worker dedup",
                    Status = MissionStatusEnum.WorkProduced,
                    Persona = "Worker",
                    BranchName = null
                }).ConfigureAwait(false);

                Mission waitingTE = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Title = "TestEngineer waiting dedup",
                    Status = MissionStatusEnum.Pending,
                    Persona = "TestEngineer",
                    AssignmentState = MissionAssignmentStateEnum.WaitingForDependency
                }).ConfigureAwait(false);

                await BackdateTimestampsAsync(testDb, voyage.Id, new List<string> { worker.Id, waitingTE.Id }, 10).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestratorWithLandingDrain(
                    testDb.Driver,
                    new NullAdmiralService(),
                    incidents,
                    new RunbookService(testDb.Driver, new LoggingModule()),
                    BuildStuckDetectionSettings());

                // First sweep: should open one incident.
                await orchestrator.SweepAsync().ConfigureAwait(false);

                // Second sweep on the same shape: must NOT open a duplicate.
                await orchestrator.SweepAsync().ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated("ten_rescue_dedup", "usr_rescue_dedup", false, true, "UnitTest");
                EnumerationResult<Incident> page = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    VoyageId = voyage.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);

                AssertEqual(1, page.Objects.Count,
                    "Repeated sweeps on the same stuck shape must not open duplicate incidents.");
            }).ConfigureAwait(false);

            // --- Test 4: Non-rescue regression guard ---

            await RunTest("NormalPipeline_HandoffAlreadyPrepared_TestEngineerClearsGate", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_normal_pipe", "usr_normal_pipe").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_normal_pipe", "usr_normal_pipe").ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb, vessel).ConfigureAwait(false);

                // Normal (non-rescue) Worker with a branch.
                Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Title = "Normal worker",
                    Status = MissionStatusEnum.WorkProduced,
                    Persona = "Worker",
                    BranchName = "armada/normal-worker/msn_normal"
                }).ConfigureAwait(false);

                // Handoff already prepared: branch pre-stamped on the TestEngineer.
                Mission testEngineer = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Title = "Normal TestEngineer",
                    Status = MissionStatusEnum.Pending,
                    Persona = "TestEngineer",
                    DependsOnMissionId = worker.Id,
                    BranchName = worker.BranchName
                }).ConfigureAwait(false);

                await CreateIdleCaptainAsync(testDb, "normal-pipe-captain").ConfigureAwait(false);
                MissionService missionSvc = CreateMissionService(testDb.Driver, CreateSettings(), new LoggingModule(), new StubGitService());

                // The dependency gate must be clear immediately (no stamp needed).
                bool result = await missionSvc.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);
                AssertFalse(result, "TryAssignAsync returns false in the test harness (no launch handler), which is expected.");

                Mission? after = await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false);
                AssertEqual(MissionAssignmentStateEnum.Provisioning, after!.AssignmentState,
                    "Normal pipeline TestEngineer must clear the dependency gate (reach Provisioning) when handoff is already prepared.");
            }).ConfigureAwait(false);

            await RunTest("NormalPipeline_OpenVoyageWithLiveMissions_NotFlaggedAsStuck", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_live_voyage", "usr_live_voyage").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_live_voyage", "usr_live_voyage").ConfigureAwait(false);

                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Live voyage")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Open
                }).ConfigureAwait(false);

                // Live InProgress mission -- voyage must NOT be flagged as stuck.
                Mission liveMission = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Title = "Active worker",
                    Status = MissionStatusEnum.InProgress,
                    Persona = "Worker"
                }).ConfigureAwait(false);

                // Backdate so the quiet-minutes gate would fire if the live-mission check failed.
                await BackdateTimestampsAsync(testDb, voyage.Id, new List<string> { liveMission.Id }, 10).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestratorWithLandingDrain(
                    testDb.Driver,
                    new NullAdmiralService(),
                    incidents,
                    new RunbookService(testDb.Driver, new LoggingModule()),
                    BuildStuckDetectionSettings());

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated("ten_live_voyage", "usr_live_voyage", false, true, "UnitTest");
                EnumerationResult<Incident> page = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    VoyageId = voyage.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);

                AssertEqual(0, page.Objects.Count, "Open voyage with a live InProgress mission must not be flagged as stuck.");
            }).ConfigureAwait(false);

            // --- Negative / edge paths ---

            await RunTest("DependencyStillPending_TestEngineerStaysWaitingForDependency", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_pending_dep", "usr_pending_dep").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_pending_dep", "usr_pending_dep").ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb, vessel).ConfigureAwait(false);

                // Worker is still Pending (not yet WorkProduced).
                Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Title = "Pending worker",
                    Status = MissionStatusEnum.Pending,
                    Persona = "Worker"
                }).ConfigureAwait(false);

                Mission testEngineer = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Title = "TestEngineer waiting",
                    Status = MissionStatusEnum.Pending,
                    Persona = "TestEngineer",
                    DependsOnMissionId = worker.Id
                }).ConfigureAwait(false);

                await CreateIdleCaptainAsync(testDb, "pending-dep-captain").ConfigureAwait(false);
                MissionService missionSvc = CreateMissionService(testDb.Driver, CreateSettings(), new LoggingModule(), new StubGitService());

                bool result = await missionSvc.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);
                AssertFalse(result, "TestEngineer must not be assigned while its dependency is still Pending.");

                Mission? after = await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false);
                AssertEqual(MissionAssignmentStateEnum.WaitingForDependency, after!.AssignmentState,
                    "TestEngineer must remain WaitingForDependency when the worker has not yet produced work.");
            }).ConfigureAwait(false);

            await RunTest("CrossVesselDependency_WorkProducedUpstream_DependentRemainsWaiting", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_xvessel", "usr_xvessel").ConfigureAwait(false);

                Vessel vesselA = await testDb.Driver.Vessels.CreateAsync(new Vessel
                {
                    TenantId = "ten_xvessel",
                    UserId = "usr_xvessel",
                    Name = "vessel-a",
                    RepoUrl = "file:///tmp/a.git",
                    LocalPath = "C:\\tmp\\a",
                    WorkingDirectory = "C:\\tmp\\a",
                    DefaultBranch = "main"
                }).ConfigureAwait(false);

                Vessel vesselB = await testDb.Driver.Vessels.CreateAsync(new Vessel
                {
                    TenantId = "ten_xvessel",
                    UserId = "usr_xvessel",
                    Name = "vessel-b",
                    RepoUrl = "file:///tmp/b.git",
                    LocalPath = "C:\\tmp\\b",
                    WorkingDirectory = "C:\\tmp\\b",
                    DefaultBranch = "main"
                }).ConfigureAwait(false);

                // Upstream worker on vessel A is WorkProduced.
                Mission upstream = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = "ten_xvessel",
                    UserId = "usr_xvessel",
                    VesselId = vesselA.Id,
                    Title = "Upstream worker (vessel A)",
                    Status = MissionStatusEnum.WorkProduced,
                    Persona = "Worker",
                    BranchName = "armada/xvessel-worker/msn_a"
                }).ConfigureAwait(false);

                // Downstream TestEngineer on vessel B depends on the upstream (cross-vessel dep).
                Mission downstream = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = "ten_xvessel",
                    UserId = "usr_xvessel",
                    VesselId = vesselB.Id,
                    Title = "Downstream TE (vessel B)",
                    Status = MissionStatusEnum.Pending,
                    Persona = "TestEngineer",
                    DependsOnMissionId = upstream.Id,
                    BranchName = upstream.BranchName
                }).ConfigureAwait(false);

                await CreateIdleCaptainAsync(testDb, "xvessel-captain").ConfigureAwait(false);
                MissionService missionSvc = CreateMissionService(testDb.Driver, CreateSettings(), new LoggingModule(), new StubGitService());

                bool result = await missionSvc.TryAssignAsync(downstream, vesselB).ConfigureAwait(false);
                AssertFalse(result, "Cross-vessel dep in WorkProduced must not release the downstream (must wait for Complete).");

                Mission? after = await testDb.Driver.Missions.ReadAsync(downstream.Id).ConfigureAwait(false);
                AssertEqual(MissionAssignmentStateEnum.WaitingForDependency, after!.AssignmentState,
                    "Cross-vessel downstream must stay WaitingForDependency until upstream is Complete.");
            }).ConfigureAwait(false);

            await RunTest("NoIdleCaptain_HandoffPrepared_TestEngineerNotAssigned", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_no_captain", "usr_no_captain").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_no_captain", "usr_no_captain").ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb, vessel).ConfigureAwait(false);

                Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Title = "Worker ready",
                    Status = MissionStatusEnum.WorkProduced,
                    Persona = "Worker",
                    BranchName = "armada/worker/msn_no_cpt"
                }).ConfigureAwait(false);

                // Handoff already prepared but no idle captain registered.
                Mission testEngineer = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Title = "TestEngineer ready, no captain",
                    Status = MissionStatusEnum.Pending,
                    Persona = "TestEngineer",
                    DependsOnMissionId = worker.Id,
                    BranchName = worker.BranchName
                }).ConfigureAwait(false);

                // No captain created -- DB is empty of idle captains.
                MissionService missionSvc = CreateMissionService(testDb.Driver, CreateSettings(), new LoggingModule(), new StubGitService());

                bool result = await missionSvc.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);
                AssertFalse(result, "TryAssignAsync must return false when no idle captain is available.");

                Mission? after = await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false);
                AssertEqual(MissionAssignmentStateEnum.WaitingForIdleCaptain, after!.AssignmentState,
                    "Mission must show WaitingForIdleCaptain when handoff is ready but no captain is free.");
            }).ConfigureAwait(false);
        }

        #region Private-Methods

        private static MissionService CreateMissionService(
            DatabaseDriver database,
            ArmadaSettings settings,
            LoggingModule logging,
            StubGitService git)
        {
            DockService docks = new DockService(logging, database, settings, git);
            CaptainService captains = new CaptainService(logging, database, settings, git, docks);
            return new MissionService(logging, database, settings, docks, captains, null, git);
        }

        private static AutonomousRecoveryOrchestrator CreateOrchestratorWithLandingDrain(
            DatabaseDriver database,
            IAdmiralService admiral,
            IncidentService incidents,
            RunbookService runbooks,
            ArmadaSettings settings)
        {
            return new AutonomousRecoveryOrchestrator(
                database,
                admiral,
                incidents,
                runbooks,
                settings,
                new LoggingModule(),
                new NullMergeQueueService(),
                new StubGitService(),
                new AutoLandEvaluator(),
                new NullConventionChecker(),
                new NullCriticalTriggerEvaluator());
        }

        private static ArmadaSettings CreateSettings()
        {
            string uniquePart = Guid.NewGuid().ToString("N");
            return new ArmadaSettings
            {
                DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_rescue_docks_" + uniquePart),
                ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_rescue_repos_" + uniquePart)
            };
        }

        private static ArmadaSettings BuildStuckDetectionSettings()
        {
            ArmadaSettings settings = CreateSettings();
            settings.AutonomousRecovery.LandingDrainEnabled = true;
            settings.AutonomousRecovery.StuckOpenVoyageMinutes = 5;
            return settings;
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb, string tenantId, string userId)
        {
            return await testDb.Driver.Vessels.CreateAsync(new Vessel
            {
                TenantId = tenantId,
                UserId = userId,
                Name = "rescue-handoff-vessel",
                RepoUrl = "file:///tmp/rescue.git",
                LocalPath = Path.Combine(Path.GetTempPath(), "rescue-repos"),
                WorkingDirectory = Path.Combine(Path.GetTempPath(), "rescue-repos"),
                DefaultBranch = "main"
            }).ConfigureAwait(false);
        }

        private static async Task<Voyage> CreateOpenVoyageAsync(TestDatabase testDb, Vessel vessel)
        {
            return await testDb.Driver.Voyages.CreateAsync(new Voyage("Rescue voyage")
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                Status = VoyageStatusEnum.Open
            }).ConfigureAwait(false);
        }

        private static async Task<Captain> CreateIdleCaptainAsync(TestDatabase testDb, string name)
        {
            return await testDb.Driver.Captains.CreateAsync(new Captain(name)
            {
                State = CaptainStateEnum.Idle
            }).ConfigureAwait(false);
        }

        private static async Task EnsureTenantAndUserAsync(TestDatabase testDb, string tenantId, string userId)
        {
            TenantMetadata? existing = await testDb.Driver.Tenants.ReadAsync(tenantId).ConfigureAwait(false);
            if (existing == null)
            {
                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata
                {
                    Id = tenantId,
                    Name = tenantId
                }).ConfigureAwait(false);
            }

            UserMaster? existingUser = await testDb.Driver.Users.ReadByIdAsync(userId).ConfigureAwait(false);
            if (existingUser == null)
            {
                await testDb.Driver.Users.CreateAsync(new UserMaster
                {
                    Id = userId,
                    TenantId = tenantId,
                    Email = userId + "@armada.test",
                    PasswordSha256 = UserMaster.ComputePasswordHash("password"),
                    IsTenantAdmin = true
                }).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Directly back-dates the churn-stable progress columns in SQLite to a past timestamp so
        /// the quiet-minutes gate in DetectStuckOpenVoyageAsync fires during the test sweep. The
        /// detector anchors elapsed time on ComputeChurnStableProgressUtc -- the most recent mission
        /// started_utc/completed_utc, falling back to the voyage created_utc -- rather than the
        /// churn-prone last_update_utc, so this raw-SQL update back-dates those stable columns (plus
        /// last_update_utc, which the database layer always overrides to DateTime.UtcNow on
        /// Create/Update). Raw SQL is the only way to inject an old timestamp in unit tests.
        /// </summary>
        private static async Task BackdateTimestampsAsync(
            TestDatabase testDb,
            string voyageId,
            List<string> missionIds,
            int minutesBack)
        {
            string oldTimestamp = DateTime.UtcNow.AddMinutes(-minutesBack)
                .ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture);

            using SqliteConnection conn = new SqliteConnection(testDb.ConnectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE voyages SET created_utc = @ts, last_update_utc = @ts WHERE id = @id;";
                cmd.Parameters.AddWithValue("@ts", oldTimestamp);
                cmd.Parameters.AddWithValue("@id", voyageId);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            foreach (string missionId in missionIds)
            {
                using SqliteCommand cmd = conn.CreateCommand();
                cmd.CommandText =
                    "UPDATE missions SET created_utc = @ts, started_utc = @ts, completed_utc = @ts, last_update_utc = @ts WHERE id = @id;";
                cmd.Parameters.AddWithValue("@ts", oldTimestamp);
                cmd.Parameters.AddWithValue("@id", missionId);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        #endregion

        #region Private-Stubs

        private sealed class NullAdmiralService : IAdmiralService
        {
            /// <inheritdoc />
            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            /// <inheritdoc />
            public Func<Captain, Task>? OnStopAgent { get; set; }
            /// <inheritdoc />
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            /// <inheritdoc />
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            /// <inheritdoc />
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            /// <inheritdoc />
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            /// <inheritdoc />
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            /// <inheritdoc />
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            /// <inheritdoc />
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
                => throw new NotImplementedException();

            /// <inheritdoc />
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();

            /// <inheritdoc />
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
                => throw new NotImplementedException();

            /// <inheritdoc />
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();

            /// <inheritdoc />
            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
                => throw new NotImplementedException();

            /// <inheritdoc />
            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => Task.FromResult<Pipeline?>(null);

            /// <inheritdoc />
            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
                => Task.FromResult(new ArmadaStatus());

            /// <inheritdoc />
            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
                => Task.CompletedTask;

            /// <inheritdoc />
            public Task RecallAllAsync(CancellationToken token = default)
                => Task.CompletedTask;

            /// <inheritdoc />
            public Task HealthCheckAsync(CancellationToken token = default)
                => Task.CompletedTask;

            /// <inheritdoc />
            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => Task.CompletedTask;

            /// <inheritdoc />
            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
                => Task.CompletedTask;
        }

        private sealed class NullMergeQueueService : IMergeQueueService
        {
            /// <inheritdoc />
            public Task<MergeEntry> EnqueueAsync(MergeEntry entry, CancellationToken token = default)
                => throw new NotImplementedException();

            /// <inheritdoc />
            public Task ProcessQueueAsync(CancellationToken token = default)
                => Task.CompletedTask;

            /// <inheritdoc />
            public Task CancelAsync(string entryId, string? tenantId = null, CancellationToken token = default)
                => Task.CompletedTask;

            /// <inheritdoc />
            public Task<List<MergeEntry>> ListAsync(string? tenantId = null, CancellationToken token = default)
                => Task.FromResult(new List<MergeEntry>());

            /// <inheritdoc />
            public Task<MergeEntry?> ProcessSingleAsync(string entryId, string? tenantId = null, CancellationToken token = default)
                => Task.FromResult<MergeEntry?>(null);

            /// <inheritdoc />
            public Task ProcessEntryByIdAsync(string entryId, CancellationToken token = default)
                => Task.CompletedTask;

            /// <inheritdoc />
            public Task<int> ReconcileLandingStateMachineAsync(CancellationToken token = default)
                => Task.FromResult(0);

            /// <inheritdoc />
            public Task<int> RecoverInFlightLandingsAsync(CancellationToken token = default)
                => Task.FromResult(0);

            /// <inheritdoc />
            public Task<MergeEntry?> GetAsync(string entryId, string? tenantId = null, CancellationToken token = default)
                => Task.FromResult<MergeEntry?>(null);

            /// <inheritdoc />
            public Task<bool> DeleteAsync(string entryId, string? tenantId = null, CancellationToken token = default)
                => Task.FromResult(false);

            /// <inheritdoc />
            public Task<MergeQueuePurgeResult> DeleteMultipleAsync(List<string> entryIds, string? tenantId = null, CancellationToken token = default)
                => Task.FromResult(new MergeQueuePurgeResult());

            /// <inheritdoc />
            public Task<int> PurgeTerminalAsync(string? vesselId = null, MergeStatusEnum? status = null, string? tenantId = null, CancellationToken token = default)
                => Task.FromResult(0);

            /// <inheritdoc />
            public Task<int> ReconcilePullRequestEntriesAsync(CancellationToken token = default)
                => Task.FromResult(0);

            /// <inheritdoc />
            public Task<bool> TryOpenPullRequestForRecoveryAsync(string mergeEntryId, CancellationToken token = default)
                => Task.FromResult(false);

            /// <inheritdoc />
            public Task<bool> HasActiveMergeEntryForMissionAsync(string missionId, CancellationToken token = default)
                => Task.FromResult(false);

            /// <inheritdoc />
            public Task<SafetyNetEnqueueResult> TrySafetyNetEnqueueAsync(
                Mission mission,
                Vessel vessel,
                string? unifiedDiff,
                IAutoLandEvaluator autoLandEvaluator,
                IConventionChecker conventionChecker,
                ICriticalTriggerEvaluator criticalTriggerEvaluator,
                CancellationToken token = default)
                => Task.FromResult(new SafetyNetEnqueueResult(SafetyNetEnqueueOutcomeEnum.SkippedNoBranch));
        }

        private sealed class NullConventionChecker : IConventionChecker
        {
            /// <inheritdoc />
            public ConventionCheckResult Check(string unifiedDiff) => new ConventionCheckResult();
        }

        private sealed class NullCriticalTriggerEvaluator : ICriticalTriggerEvaluator
        {
            /// <inheritdoc />
            public CriticalTriggerResult Evaluate(string unifiedDiff, ConventionCheckResult conventionResult) => new CriticalTriggerResult();
        }

        #endregion
    }
}
