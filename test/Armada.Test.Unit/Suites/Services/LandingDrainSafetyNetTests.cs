namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
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
    /// Regression tests for the landing-drain safety-net sweep in AutonomousRecoveryOrchestrator.
    /// </summary>
    public class LandingDrainSafetyNetTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Landing Drain Safety Net";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("SweepAsync_JudgePassedWorkProduced_EnqueuesBranch", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);
                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/worker-1", "Worker").ConfigureAwait(false);
                Mission judge = await CreateCompleteJudgeAsync(testDb, vessel, voyage, worker.Id, pass: true).ConfigureAwait(false);

                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, mergeQueue);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, mergeQueue.EnqueueCalls.Count, "Expected one safety-net enqueue.");
                AssertEqual(worker.Id, mergeQueue.EnqueueCalls[0].MissionId, "Enqueue should target the Worker mission.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_JudgeProgressSignalPass_WorkProduced_EnqueuesBranch", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);
                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/worker-progress", "Worker").ConfigureAwait(false);

                Mission judge = new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    DependsOnMissionId = worker.Id,
                    Persona = "Judge",
                    Title = "Judge mission",
                    Status = MissionStatusEnum.Complete,
                    AgentOutput =
                        "## Completeness\nok\n## Correctness\nok\n## Tests\nok\n## Failure Modes\nok\n" +
                        "## Verdict\nPASS\n[verdict] PASS\nthe standalone verdict line was dropped before exit\n",
                    CompletedUtc = DateTime.UtcNow,
                    LastUpdateUtc = DateTime.UtcNow
                };
                await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, mergeQueue);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, mergeQueue.EnqueueCalls.Count, "A Judge salvaged from a [verdict] PASS progress signal should still count as passed.");
                AssertEqual(worker.Id, mergeQueue.EnqueueCalls[0].MissionId, "Enqueue should target the Worker mission.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_AlreadyEnqueued_DoesNotDoubleEnqueue", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);
                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/worker-2", "Worker").ConfigureAwait(false);
                await CreateCompleteJudgeAsync(testDb, vessel, voyage, worker.Id, pass: true).ConfigureAwait(false);

                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                mergeQueue.ActiveMissionIds.Add(worker.Id);
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, mergeQueue);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(0, mergeQueue.EnqueueCalls.Count, "Active merge entry should prevent a second enqueue.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_PredicateFail_FlagsForReview", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                vessel.AutoLandPredicate = "{\"enabled\":true,\"maxFiles\":1}";
                await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);
                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/worker-3", "Worker").ConfigureAwait(false);
                await CreateCompleteJudgeAsync(testDb, vessel, voyage, worker.Id, pass: true).ConfigureAwait(false);

                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                StubGitService git = new StubGitService
                {
                    DiffResult = "+++ b/src/A.cs\n+line1\n+++ b/src/B.cs\n+line2\n"
                };
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, mergeQueue, git: git);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, mergeQueue.EnqueueCalls.Count);
                AssertEqual(SafetyNetEnqueueOutcomeEnum.EnqueuedFlaggedForReview, mergeQueue.LastOutcome, "Over-cap diff should flag for review.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_FailedLeafJudge_DispatchesRescue", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);
                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/worker-4", "Worker").ConfigureAwait(false);

                Mission judge = new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    DependsOnMissionId = worker.Id,
                    Persona = "Judge",
                    Title = "Judge stage",
                    Status = MissionStatusEnum.Failed,
                    FailureReason = "Judge verdict: NEEDS_REVISION",
                    ReviewComment = "Add regression coverage.",
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-5),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-5)
                };
                await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, new RecordingMergeQueueService(), admiral);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "Failed leaf judge should dispatch a Worker rescue.");
                AssertEqual("Worker", admiral.DispatchedMissions[0].Persona);
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_AllTerminal_CompletesOpenVoyage", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);

                await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Persona = "Worker",
                    Title = "Done worker",
                    Status = MissionStatusEnum.Complete,
                    CompletedUtc = DateTime.UtcNow.AddHours(-1),
                    LastUpdateUtc = DateTime.UtcNow.AddHours(-1)
                }).ConfigureAwait(false);

                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, new RecordingMergeQueueService());

                await orchestrator.SweepAsync().ConfigureAwait(false);

                Voyage? updated = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                AssertNotNull(updated);
                AssertEqual(VoyageStatusEnum.Complete, updated!.Status, "Idle voyage with only terminal missions should complete.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_StuckOpenVoyage_OpensIncident", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Stuck voyage", "No progress")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Open,
                    LastUpdateUtc = DateTime.UtcNow.AddHours(-2)
                }).ConfigureAwait(false);

                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/stuck-worker", "Worker").ConfigureAwait(false);

                Mission judge = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    DependsOnMissionId = worker.Id,
                    Persona = "Judge",
                    Title = "Stalled judge",
                    Status = MissionStatusEnum.Pending
                }).ConfigureAwait(false);

                DateTime staleUtc = DateTime.UtcNow.AddHours(-2);
                // The generic quiet-voyage clock now anchors on a churn-stable timestamp (the most
                // recent mission start/completion) rather than LastUpdateUtc, so stamp the
                // WorkProduced worker's progress timestamps stale to age the voyage past threshold.
                // No captain is created, so the structural stranded-rescue self-heal branch is
                // skipped and this exercises the generic quiet path.
                worker.StartedUtc = staleUtc;
                worker.CompletedUtc = staleUtc;
                await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);
                await SetMissionLastUpdateUtcAsync(testDb, worker.Id, staleUtc).ConfigureAwait(false);
                await SetMissionLastUpdateUtcAsync(testDb, judge.Id, staleUtc).ConfigureAwait(false);
                await SetVoyageLastUpdateUtcAsync(testDb, voyage.Id, staleUtc).ConfigureAwait(false);

                ArmadaSettings settings = new ArmadaSettings();
                settings.AutonomousRecovery.StuckOpenVoyageMinutes = 30;

                IncidentService incidents = new IncidentService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(
                    testDb.Driver,
                    new RecordingMergeQueueService(),
                    settings: settings,
                    incidents: incidents);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");
                EnumerationResult<Incident> page = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    VoyageId = voyage.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);

                AssertEqual(1, page.Objects.Count, "Stuck open voyage should open an incident.");
                AssertContains("no live missions", page.Objects[0].Summary ?? "", "Incident summary should describe the stall.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_LiveMissionPresent_SkipsDrain", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);

                // A judge-passed, land-ready Worker that would normally be enqueued...
                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/worker-live", "Worker").ConfigureAwait(false);
                await CreateCompleteJudgeAsync(testDb, vessel, voyage, worker.Id, pass: true).ConfigureAwait(false);

                // ...but a sibling mission is still live, so the whole voyage must be left alone.
                await CreateInProgressMissionAsync(testDb, vessel, voyage, "Worker").ConfigureAwait(false);

                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, mergeQueue);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(0, mergeQueue.EnqueueCalls.Count, "Voyage with a live mission must not be drained.");

                Voyage? updated = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                AssertNotNull(updated);
                AssertEqual(VoyageStatusEnum.Open, updated!.Status, "Voyage with a live mission must stay Open.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_EnqueueThrows_IsolatesMissionAndContinues", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);

                // Two independent judge-passed land-ready Workers in the same idle voyage.
                Mission failing = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/worker-throw", "Worker").ConfigureAwait(false);
                await CreateCompleteJudgeAsync(testDb, vessel, voyage, failing.Id, pass: true).ConfigureAwait(false);

                Mission healthy = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/worker-ok", "Worker").ConfigureAwait(false);
                await CreateCompleteJudgeAsync(testDb, vessel, voyage, healthy.Id, pass: true).ConfigureAwait(false);

                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService
                {
                    ThrowForMissionId = failing.Id
                };
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, mergeQueue);

                // Must not throw: one mission's enqueue failure is isolated.
                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, mergeQueue.EnqueueCalls.Count, "Healthy mission should still enqueue after a sibling throws.");
                AssertEqual(healthy.Id, mergeQueue.EnqueueCalls[0].MissionId, "Surviving enqueue should be the healthy mission.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_StuckOpenVoyageSweptTwice_OpensSingleIncident", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Stuck voyage", "No progress")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Open,
                    LastUpdateUtc = DateTime.UtcNow.AddHours(-2)
                }).ConfigureAwait(false);

                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/stuck-worker-2", "Worker").ConfigureAwait(false);
                Mission judge = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    DependsOnMissionId = worker.Id,
                    Persona = "Judge",
                    Title = "Stalled judge",
                    Status = MissionStatusEnum.Pending
                }).ConfigureAwait(false);

                DateTime staleUtc = DateTime.UtcNow.AddHours(-2);
                // Churn-stable anchor: stamp the worker's start/completion stale so the generic
                // quiet path ages past threshold. No captain -> structural self-heal branch skipped.
                worker.StartedUtc = staleUtc;
                worker.CompletedUtc = staleUtc;
                await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);
                await SetMissionLastUpdateUtcAsync(testDb, worker.Id, staleUtc).ConfigureAwait(false);
                await SetMissionLastUpdateUtcAsync(testDb, judge.Id, staleUtc).ConfigureAwait(false);
                await SetVoyageLastUpdateUtcAsync(testDb, voyage.Id, staleUtc).ConfigureAwait(false);

                ArmadaSettings settings = new ArmadaSettings();
                settings.AutonomousRecovery.StuckOpenVoyageMinutes = 30;

                IncidentService incidents = new IncidentService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(
                    testDb.Driver,
                    new RecordingMergeQueueService(),
                    settings: settings,
                    incidents: incidents);

                await orchestrator.SweepAsync().ConfigureAwait(false);
                await orchestrator.SweepAsync().ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");
                EnumerationResult<Incident> page = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    VoyageId = voyage.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);

                AssertEqual(1, page.Objects.Count, "Repeated sweeps must not open duplicate stuck-voyage incidents.");
            }).ConfigureAwait(false);

            // Structural stalled-rescue detection: a Pending dependent whose same-vessel,
            // handoff-eligible upstream never propagated its branch must be detected even when every
            // LastUpdateUtc is recent (the WaitingForDependency churn that previously reset the quiet
            // clock). With an idle captain present and a no-op self-heal (RecordingAdmiral.HealthCheck
            // does not re-assign), the detector must escalate to a High incident.
            await RunTest("SweepAsync_StrandedRescueDependent_IdleCaptainRecentUpdates_OpensIncidentAfterSelfHeal", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);

                // Upstream Worker reached WorkProduced with a branch; timestamps are fresh.
                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/stranded-worker", "Worker").ConfigureAwait(false);

                // Downstream TestEngineer is Pending, depends on the worker, but never received the
                // handoff branch (the creation-order race) -- the stranded shape.
                Mission testEngineer = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    DependsOnMissionId = worker.Id,
                    Persona = "TestEngineer",
                    Title = "Stranded test engineer",
                    Status = MissionStatusEnum.Pending,
                    AssignmentState = MissionAssignmentStateEnum.WaitingForDependency,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                // An idle captain is available, so the stranded dependent could run once healed.
                await testDb.Driver.Captains.CreateAsync(new Captain
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Name = "idle-captain",
                    State = CaptainStateEnum.Idle
                }).ConfigureAwait(false);

                // A long quiet threshold proves detection does NOT depend on the LastUpdateUtc clock:
                // every timestamp is fresh, so the generic quiet path could never trip here.
                ArmadaSettings settings = new ArmadaSettings();
                settings.AutonomousRecovery.StuckOpenVoyageMinutes = 600;

                IncidentService incidents = new IncidentService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(
                    testDb.Driver,
                    new RecordingMergeQueueService(),
                    settings: settings,
                    incidents: incidents);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");
                EnumerationResult<Incident> page = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    VoyageId = voyage.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);

                AssertEqual(1, page.Objects.Count, "Stranded-rescue shape must open one incident despite recent LastUpdateUtc.");
                AssertEqual(IncidentSeverityEnum.High, page.Objects[0].Severity, "A stranded rescue dependent is a High-severity stall.");
                AssertContains("stranded after self-heal", page.Objects[0].Summary ?? "", "Incident must describe the post-self-heal stranded shape.");
                AssertContains(testEngineer.Id, page.Objects[0].Summary ?? "", "Incident must name the stranded dependent.");
                AssertContains("no live missions", page.Objects[0].Summary ?? "", "Summary must keep the de-dup marker shared with the generic path.");

                // The self-heal attempt must have fired before escalation.
                List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByVoyageAsync(voyage.Id, 100).ConfigureAwait(false);
                AssertTrue(
                    events.Any(item => item.EventType == "landing_drain.stuck_rescue_self_heal"),
                    "Detector must attempt a self-heal before opening the incident.");
            }).ConfigureAwait(false);

            // Gate guard: the structural path requires an idle captain. With no idle captain and all
            // timestamps fresh, neither the structural nor the generic quiet path may fire -- the
            // detector must not raise a false-positive incident on a freshly-progressing voyage.
            await RunTest("SweepAsync_StrandedRescueDependent_NoIdleCaptainRecentUpdates_NoIncident", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);

                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/stranded-worker-2", "Worker").ConfigureAwait(false);
                worker.StartedUtc = DateTime.UtcNow;
                worker.CompletedUtc = DateTime.UtcNow;
                await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);

                await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    DependsOnMissionId = worker.Id,
                    Persona = "TestEngineer",
                    Title = "Stranded test engineer, no captain",
                    Status = MissionStatusEnum.Pending,
                    AssignmentState = MissionAssignmentStateEnum.WaitingForDependency,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                // No captain created -> structural self-heal branch is gated off.
                ArmadaSettings settings = new ArmadaSettings();
                settings.AutonomousRecovery.StuckOpenVoyageMinutes = 30;

                IncidentService incidents = new IncidentService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(
                    testDb.Driver,
                    new RecordingMergeQueueService(),
                    settings: settings,
                    incidents: incidents);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");
                EnumerationResult<Incident> page = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    VoyageId = voyage.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);

                AssertEqual(0, page.Objects.Count, "A freshly-progressing voyage with no idle captain must not raise a stuck incident.");
            }).ConfigureAwait(false);

            await RunTest("TryLoadSafetyNetDiff_WithDockWorktree_DiffsDocWorktreePath", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);

                // Create a dock with a distinct worktree path so DiffCalls can be inspected.
                Dock dock = await testDb.Driver.Docks.CreateAsync(new Dock
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    WorktreePath = "C:\\tmp\\dock-worktree-diff-test",
                    BranchName = "armada/worker-dock-diff",
                    Active = true
                }).ConfigureAwait(false);

                Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Persona = "Worker",
                    Title = "Worker mission",
                    BranchName = "armada/worker-dock-diff",
                    DockId = dock.Id,
                    Status = MissionStatusEnum.WorkProduced,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                await CreateCompleteJudgeAsync(testDb, vessel, voyage, worker.Id, pass: true).ConfigureAwait(false);

                StubGitService git = new StubGitService { DiffResult = "+++ b/src/A.cs\n+line1\n" };
                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, mergeQueue, git: git);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, mergeQueue.EnqueueCalls.Count, "Expected one safety-net enqueue.");
                AssertTrue(
                    git.DiffCalls.Contains("C:\\tmp\\dock-worktree-diff-test"),
                    "Diff must use the dock worktree path, not the vessel working directory.");
                AssertFalse(
                    git.DiffCalls.Contains(vessel.WorkingDirectory ?? String.Empty),
                    "Diff must not fall back to vessel WorkingDirectory when dock worktree is available.");
            }).ConfigureAwait(false);

            await RunTest("TryLoadSafetyNetDiff_NoDock_FallsBackToVesselPath", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);

                // Mission has no DockId -- the fallback to vessel.WorkingDirectory must fire.
                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/worker-no-dock", "Worker").ConfigureAwait(false);
                await CreateCompleteJudgeAsync(testDb, vessel, voyage, worker.Id, pass: true).ConfigureAwait(false);

                StubGitService git = new StubGitService { DiffResult = "+++ b/src/A.cs\n+line1\n" };
                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, mergeQueue, git: git);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, mergeQueue.EnqueueCalls.Count, "Expected one safety-net enqueue.");
                AssertTrue(
                    git.DiffCalls.Contains(vessel.WorkingDirectory!),
                    "Without a dock worktree, diff must fall back to vessel WorkingDirectory.");
            }).ConfigureAwait(false);

            await RunTest("TryLoadSafetyNetDiff_DockRowMissing_FallsBackToVesselPath", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);

                // DockId points at a row that does not exist (dock pruned/deleted). ReadAsync returns
                // null, so the resolver must fall back to the vessel working directory rather than
                // diffing a null/empty path.
                Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Persona = "Worker",
                    Title = "Worker mission",
                    BranchName = "armada/worker-missing-dock",
                    DockId = "dck_does_not_exist",
                    Status = MissionStatusEnum.WorkProduced,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);
                await CreateCompleteJudgeAsync(testDb, vessel, voyage, worker.Id, pass: true).ConfigureAwait(false);

                StubGitService git = new StubGitService { DiffResult = "+++ b/src/A.cs\n+line1\n" };
                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, mergeQueue, git: git);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, mergeQueue.EnqueueCalls.Count, "Expected one safety-net enqueue.");
                AssertTrue(
                    git.DiffCalls.Contains(vessel.WorkingDirectory!),
                    "A missing dock row must fall back to the vessel WorkingDirectory.");
            }).ConfigureAwait(false);

            await RunTest("TryLoadSafetyNetDiff_DockWorktreePathEmpty_FallsBackToVesselPath", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);

                // Dock row exists but has no worktree path (e.g. worktree already removed). The
                // resolver must treat a whitespace WorktreePath as "no worktree" and fall back.
                Dock dock = await testDb.Driver.Docks.CreateAsync(new Dock
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    WorktreePath = "   ",
                    BranchName = "armada/worker-empty-worktree",
                    Active = true
                }).ConfigureAwait(false);

                Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Persona = "Worker",
                    Title = "Worker mission",
                    BranchName = "armada/worker-empty-worktree",
                    DockId = dock.Id,
                    Status = MissionStatusEnum.WorkProduced,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);
                await CreateCompleteJudgeAsync(testDb, vessel, voyage, worker.Id, pass: true).ConfigureAwait(false);

                StubGitService git = new StubGitService { DiffResult = "+++ b/src/A.cs\n+line1\n" };
                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, mergeQueue, git: git);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, mergeQueue.EnqueueCalls.Count, "Expected one safety-net enqueue.");
                AssertTrue(
                    git.DiffCalls.Contains(vessel.WorkingDirectory!),
                    "An empty dock worktree path must fall back to the vessel WorkingDirectory.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_DiffUnavailableEmpty_FlagsForReview", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                vessel.AutoLandPredicate = "{\"enabled\":true,\"maxFiles\":10}";
                await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);
                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/worker-empty-diff", "Worker").ConfigureAwait(false);
                await CreateCompleteJudgeAsync(testDb, vessel, voyage, worker.Id, pass: true).ConfigureAwait(false);

                // An empty diff is exactly the pre-fix failure mode (main-checkout diff was always
                // empty). The safe direction is flag-for-review, never silent auto-land.
                StubGitService git = new StubGitService { DiffResult = "" };
                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, mergeQueue, git: git);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, mergeQueue.EnqueueCalls.Count, "Expected one safety-net enqueue.");
                AssertEqual(
                    SafetyNetEnqueueOutcomeEnum.EnqueuedFlaggedForReview,
                    mergeQueue.LastOutcome,
                    "An empty/unavailable diff must flag for review, not auto-land.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_DiffLoadThrows_FlagsForReviewAndContinues", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                vessel.AutoLandPredicate = "{\"enabled\":true,\"maxFiles\":10}";
                await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);
                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/worker-diff-throws", "Worker").ConfigureAwait(false);
                await CreateCompleteJudgeAsync(testDb, vessel, voyage, worker.Id, pass: true).ConfigureAwait(false);

                // When the diff genuinely cannot be loaded (git throws), TryLoadSafetyNetDiffAsync must
                // swallow the exception and return null so the sweep keeps running, and the null diff
                // must still drive the safe-direction flag-for-review (work is enqueued, not lost).
                StubGitService git = new StubGitService { ShouldThrowOnDiff = true };
                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, mergeQueue, git: git);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, mergeQueue.EnqueueCalls.Count, "A diff-load failure must not lose the branch; it should still enqueue.");
                AssertEqual(
                    SafetyNetEnqueueOutcomeEnum.EnqueuedFlaggedForReview,
                    mergeQueue.LastOutcome,
                    "A diff that cannot be loaded must flag for review, not auto-land.");
            }).ConfigureAwait(false);
        }

        #region Helpers

        private static async Task SetMissionLastUpdateUtcAsync(TestDatabase testDb, string missionId, DateTime lastUpdateUtc)
        {
            using (SqliteConnection conn = new SqliteConnection(testDb.ConnectionString))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE missions SET last_update_utc = @last_update_utc WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", missionId);
                    cmd.Parameters.AddWithValue(
                        "@last_update_utc",
                        lastUpdateUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }

        private static async Task SetVoyageLastUpdateUtcAsync(TestDatabase testDb, string voyageId, DateTime lastUpdateUtc)
        {
            using (SqliteConnection conn = new SqliteConnection(testDb.ConnectionString))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE voyages SET last_update_utc = @last_update_utc WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", voyageId);
                    cmd.Parameters.AddWithValue(
                        "@last_update_utc",
                        lastUpdateUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }

        private sealed class RecordingMergeQueueService : IMergeQueueService
        {
            public List<MergeEntry> EnqueueCalls { get; } = new List<MergeEntry>();
            public HashSet<string> ActiveMissionIds { get; } = new HashSet<string>(StringComparer.Ordinal);
            public SafetyNetEnqueueOutcomeEnum LastOutcome { get; private set; }

            /// <summary>When set, TrySafetyNetEnqueueAsync throws for this mission id (simulates a transient failure).</summary>
            public string? ThrowForMissionId { get; set; }

            public Task<MergeEntry> EnqueueAsync(MergeEntry entry, CancellationToken token = default)
            {
                EnqueueCalls.Add(entry);
                return Task.FromResult(entry);
            }

            public Task<bool> HasActiveMergeEntryForMissionAsync(string missionId, CancellationToken token = default)
            {
                return Task.FromResult(ActiveMissionIds.Contains(missionId));
            }

            public Task<SafetyNetEnqueueResult> TrySafetyNetEnqueueAsync(
                Mission mission,
                Vessel vessel,
                string? unifiedDiff,
                IAutoLandEvaluator autoLandEvaluator,
                IConventionChecker conventionChecker,
                ICriticalTriggerEvaluator criticalTriggerEvaluator,
                CancellationToken token = default)
            {
                if (ThrowForMissionId != null && String.Equals(ThrowForMissionId, mission.Id, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("simulated enqueue failure for " + mission.Id);
                }

                if (HasActiveMergeEntryForMissionAsync(mission.Id, token).Result)
                {
                    return Task.FromResult(new SafetyNetEnqueueResult(SafetyNetEnqueueOutcomeEnum.AlreadyEnqueued, null));
                }

                MergeEntry entry = new MergeEntry(mission.BranchName ?? "branch", vessel.DefaultBranch ?? "main");
                entry.MissionId = mission.Id;
                entry.VesselId = vessel.Id;
                entry.Status = MergeStatusEnum.Queued;

                AutoLandPredicate? predicate = vessel.GetAutoLandPredicate();
                SafetyNetEnqueueOutcomeEnum outcome = SafetyNetEnqueueOutcomeEnum.Enqueued;
                string? detail = null;
                if (predicate is { Enabled: true })
                {
                    // Mirror MergeQueueService.TrySafetyNetEnqueueAsync: a null/empty diff means the
                    // delta could not be loaded, so take the safe direction and flag for review rather
                    // than auto-landing an unknown-size branch. Only a loadable, in-cap diff auto-lands.
                    string diff = unifiedDiff ?? String.Empty;
                    if (String.IsNullOrWhiteSpace(diff))
                    {
                        outcome = SafetyNetEnqueueOutcomeEnum.EnqueuedFlaggedForReview;
                        detail = "diff unavailable for auto-land predicate";
                        entry.AuditDeepPicked = true;
                    }
                    else
                    {
                        EvaluationResult result = autoLandEvaluator.Evaluate(diff, predicate);
                        if (result is EvaluationResult.Fail fail)
                        {
                            outcome = SafetyNetEnqueueOutcomeEnum.EnqueuedFlaggedForReview;
                            detail = fail.Reason;
                            entry.AuditDeepPicked = true;
                        }
                    }
                }

                LastOutcome = outcome;
                EnqueueCalls.Add(entry);
                return Task.FromResult(new SafetyNetEnqueueResult(outcome, entry, detail));
            }

            public Task ProcessQueueAsync(CancellationToken token = default) => Task.CompletedTask;
            public Task CancelAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.CompletedTask;
            public Task<List<MergeEntry>> ListAsync(string? tenantId = null, CancellationToken token = default) => Task.FromResult(new List<MergeEntry>());
            public Task<MergeEntry?> ProcessSingleAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult<MergeEntry?>(null);
            public Task ProcessEntryByIdAsync(string entryId, CancellationToken token = default) => Task.CompletedTask;
            public Task<MergeEntry?> GetAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult<MergeEntry?>(null);
            public Task<bool> DeleteAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult(false);
            public Task<MergeQueuePurgeResult> DeleteMultipleAsync(List<string> entryIds, string? tenantId = null, CancellationToken token = default) => Task.FromResult(new MergeQueuePurgeResult());
            public Task<int> PurgeTerminalAsync(string? vesselId = null, MergeStatusEnum? status = null, string? tenantId = null, CancellationToken token = default) => Task.FromResult(0);
            public Task<int> ReconcilePullRequestEntriesAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<int> ReconcileLandingStateMachineAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<int> RecoverInFlightLandingsAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<bool> TryOpenPullRequestForRecoveryAsync(string mergeEntryId, CancellationToken token = default) => Task.FromResult(false);
        }

        private static AutonomousRecoveryOrchestrator CreateDrainOrchestrator(
            DatabaseDriver database,
            RecordingMergeQueueService mergeQueue,
            RecordingAdmiralService? admiral = null,
            ArmadaSettings? settings = null,
            IncidentService? incidents = null,
            StubGitService? git = null)
        {
            RecordingAdmiralService admiralService = admiral ?? new RecordingAdmiralService(database);
            IncidentService incidentService = incidents ?? new IncidentService(database);
            RunbookService runbooks = new RunbookService(database, new LoggingModule());
            StubGitService gitService = git ?? new StubGitService { DiffResult = "+++ b/src/A.cs\n+line1\n" };

            return new AutonomousRecoveryOrchestrator(
                database,
                admiralService,
                incidentService,
                runbooks,
                settings ?? new ArmadaSettings(),
                new LoggingModule(),
                mergeQueue,
                gitService,
                new AutoLandEvaluator(),
                new ConventionChecker(),
                new CriticalTriggerEvaluator());
        }

        private static async Task EnsureTenantAndUserAsync(TestDatabase testDb)
        {
            TenantMetadata? existingTenant = await testDb.Driver.Tenants.ReadAsync("ten_drain").ConfigureAwait(false);
            if (existingTenant == null)
            {
                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata
                {
                    Id = "ten_drain",
                    Name = "ten_drain"
                }).ConfigureAwait(false);
            }

            UserMaster? existingUser = await testDb.Driver.Users.ReadByIdAsync("usr_drain").ConfigureAwait(false);
            if (existingUser == null)
            {
                await testDb.Driver.Users.CreateAsync(new UserMaster
                {
                    Id = "usr_drain",
                    TenantId = "ten_drain",
                    Email = "usr_drain@armada.test",
                    PasswordSha256 = UserMaster.ComputePasswordHash("password"),
                    IsTenantAdmin = true
                }).ConfigureAwait(false);
            }
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb)
        {
            Vessel vessel = new Vessel
            {
                TenantId = "ten_drain",
                UserId = "usr_drain",
                Name = "Drain Vessel",
                RepoUrl = "file:///tmp/drain.git",
                LocalPath = "C:\\tmp\\drain",
                WorkingDirectory = "C:\\tmp\\drain",
                DefaultBranch = "main"
            };
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<Voyage> CreateOpenVoyageAsync(TestDatabase testDb)
        {
            return await testDb.Driver.Voyages.CreateAsync(new Voyage("Drain voyage", "Landing drain test")
            {
                TenantId = "ten_drain",
                UserId = "usr_drain",
                Status = VoyageStatusEnum.Open,
                LastUpdateUtc = DateTime.UtcNow
            }).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateWorkProducedMissionAsync(
            TestDatabase testDb,
            Vessel vessel,
            Voyage voyage,
            string branchName,
            string persona)
        {
            Mission mission = new Mission
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                VesselId = vessel.Id,
                VoyageId = voyage.Id,
                Persona = persona,
                Title = persona + " mission",
                BranchName = branchName,
                Status = MissionStatusEnum.WorkProduced,
                LastUpdateUtc = DateTime.UtcNow
            };
            return await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateInProgressMissionAsync(
            TestDatabase testDb,
            Vessel vessel,
            Voyage voyage,
            string persona)
        {
            Mission mission = new Mission
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                VesselId = vessel.Id,
                VoyageId = voyage.Id,
                Persona = persona,
                Title = persona + " in-progress mission",
                Status = MissionStatusEnum.InProgress,
                LastUpdateUtc = DateTime.UtcNow
            };
            return await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateCompleteJudgeAsync(
            TestDatabase testDb,
            Vessel vessel,
            Voyage voyage,
            string dependsOnMissionId,
            bool pass)
        {
            string output = pass
                ? "## Completeness\nok\n## Correctness\nok\n## Tests\nok\n## Failure Modes\nok\n## Verdict\nPASS\n[ARMADA:VERDICT] PASS"
                : "## Verdict\nFAIL\n[ARMADA:VERDICT] FAIL";

            Mission judge = new Mission
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                VesselId = vessel.Id,
                VoyageId = voyage.Id,
                DependsOnMissionId = dependsOnMissionId,
                Persona = "Judge",
                Title = "Judge mission",
                Status = MissionStatusEnum.Complete,
                AgentOutput = output,
                CompletedUtc = DateTime.UtcNow,
                LastUpdateUtc = DateTime.UtcNow
            };
            return await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);
        }

        private sealed class RecordingAdmiralService : IAdmiralService
        {
            private readonly DatabaseDriver _Database;

            public RecordingAdmiralService(DatabaseDriver database)
            {
                _Database = database;
            }

            public List<Mission> DispatchedMissions { get; } = new List<Mission>();

            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();

            public async Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
            {
                Mission created = await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                DispatchedMissions.Add(created);
                return created;
            }

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => Task.FromResult<Pipeline?>(null);

            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
                => Task.FromResult(new ArmadaStatus());

            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
                => Task.CompletedTask;

            public Task RecallAllAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task HealthCheckAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
                => Task.CompletedTask;
        }

        #endregion
    }
}
