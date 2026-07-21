namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
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

            await RunTest("SweepAsync_MemoryConsolidatorWorkProduced_CompletesWithoutEnqueue", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);

                // A reflection mission that stayed WorkProduced (its landing short-circuit never ran)
                // must NOT be enqueued by the drain: enqueuing would create an unmergeable entry that
                // reconciles the mission to a spurious LandingFailed. It must complete instead.
                Mission consolidator = await CreateWorkProducedMissionAsync(
                    testDb, vessel, voyage, "armada/reflection-1", "MemoryConsolidator").ConfigureAwait(false);

                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, mergeQueue);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(0, mergeQueue.EnqueueCalls.Count, "MemoryConsolidator mission must never be enqueued for git landing.");

                Mission? updated = await testDb.Driver.Missions.ReadAsync(consolidator.Id).ConfigureAwait(false);
                AssertNotNull(updated);
                AssertEqual(MissionStatusEnum.Complete, updated!.Status, "MemoryConsolidator mission must complete without landing.");
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

            await RunTest("SweepAsync_WorkProducedWithPendingDependent_DoesNotOpenIncident", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Healthy handoff", "Worker produced, judge waiting")
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
                    Title = "Waiting judge",
                    Status = MissionStatusEnum.Pending,
                    AssignmentState = MissionAssignmentStateEnum.WaitingForDependency
                }).ConfigureAwait(false);

                DateTime staleUtc = DateTime.UtcNow.AddHours(-2);
                // A healthy mid-handoff voyage has a WorkProduced mission awaiting the next stage.
                // The quiet clock may be stale, but this must NOT open a stuck-voyage incident.
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

                AssertEqual(0, page.Objects.Count, "Healthy mid-handoff voyage must not open a stuck-voyage incident.");
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

            await RunTest("SweepAsync_WorkProducedWithPendingDependent_SweptTwice_DoesNotOpenIncident", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Healthy handoff", "Worker produced, judge waiting")
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
                    Title = "Waiting judge",
                    Status = MissionStatusEnum.Pending,
                    AssignmentState = MissionAssignmentStateEnum.WaitingForDependency
                }).ConfigureAwait(false);

                DateTime staleUtc = DateTime.UtcNow.AddHours(-2);
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

                AssertEqual(0, page.Objects.Count, "Repeated sweeps of a healthy mid-handoff voyage must not open any incident.");
            }).ConfigureAwait(false);

            // A Pending/WaitingForDependency dependent during a normal handoff must NOT be treated
            // as a stuck voyage, even when an idle captain is present and the quiet clock is long.
            await RunTest("SweepAsync_StrandedRescueDependent_IdleCaptainRecentUpdates_DoesNotOpenIncident", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);

                // Upstream Worker reached WorkProduced with a branch; timestamps are fresh.
                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/stranded-worker", "Worker").ConfigureAwait(false);

                // Downstream TestEngineer is Pending, depends on the worker, but never received the
                // handoff branch (the creation-order race) -- this is now a normal handoff gap, not
                // a stuck-voyage signal.
                await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    DependsOnMissionId = worker.Id,
                    Persona = "TestEngineer",
                    Title = "Waiting test engineer",
                    Status = MissionStatusEnum.Pending,
                    AssignmentState = MissionAssignmentStateEnum.WaitingForDependency,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                // An idle captain is available, but that does not make the handoff gap a stall.
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

                AssertEqual(0, page.Objects.Count, "WaitingForDependency during a handoff must not open a stuck-voyage incident.");
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

            await RunTest("SweepAsync_AllTerminalStalledVoyage_OpensIncident", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Genuinely stalled", "All missions stalled")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Open,
                    LastUpdateUtc = DateTime.UtcNow.AddHours(-2)
                }).ConfigureAwait(false);

                // Two Assigned missions with dead processes: terminal/stalled with no forward path.
                Mission assignedDead1 = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Persona = "Worker",
                    Title = "Assigned worker with dead process",
                    Status = MissionStatusEnum.Assigned,
                    ProcessId = 999999,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                Mission assignedDead2 = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Persona = "Judge",
                    Title = "Assigned judge with dead process",
                    Status = MissionStatusEnum.Assigned,
                    ProcessId = 999998,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                DateTime staleUtc = DateTime.UtcNow.AddHours(-2);
                assignedDead1.StartedUtc = staleUtc;
                assignedDead2.StartedUtc = staleUtc;
                await testDb.Driver.Missions.UpdateAsync(assignedDead1).ConfigureAwait(false);
                await testDb.Driver.Missions.UpdateAsync(assignedDead2).ConfigureAwait(false);
                await SetMissionLastUpdateUtcAsync(testDb, assignedDead1.Id, staleUtc).ConfigureAwait(false);
                await SetMissionLastUpdateUtcAsync(testDb, assignedDead2.Id, staleUtc).ConfigureAwait(false);
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

                AssertEqual(1, page.Objects.Count, "A genuinely all-terminal-stalled voyage must open a stuck-voyage incident.");
                AssertContains("no live missions", page.Objects[0].Summary ?? "", "Incident summary should describe the stall.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_AssignedWithLiveProcess_DoesNotOpenIncident", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Live captain", "Assigned captain is running")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Open,
                    LastUpdateUtc = DateTime.UtcNow.AddHours(-2)
                }).ConfigureAwait(false);

                // Use the current process as a guaranteed live process for the assigned captain.
                int liveProcessId = Process.GetCurrentProcess().Id;

                await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Persona = "Worker",
                    Title = "Assigned worker with live process",
                    Status = MissionStatusEnum.Assigned,
                    ProcessId = liveProcessId,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                DateTime staleUtc = DateTime.UtcNow.AddHours(-2);
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

                AssertEqual(0, page.Objects.Count, "A voyage with a live Assigned captain must not open a stuck-voyage incident.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_CompletedVoyage_ClosesOpenStuckVoyageIncident", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Completing", "Will complete")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Open,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                // All missions terminal so the voyage completes during the drain sweep.
                await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Persona = "Worker",
                    Title = "Completed worker",
                    Status = MissionStatusEnum.Complete,
                    CompletedUtc = DateTime.UtcNow,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Persona = "Judge",
                    Title = "Completed judge",
                    Status = MissionStatusEnum.Complete,
                    CompletedUtc = DateTime.UtcNow,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");

                // Pre-seed an open stuck-voyage incident.
                Incident openIncident = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                {
                    Title = "Stuck open voyage: " + voyage.Title,
                    Summary = "Voyage " + voyage.Id + " has no live missions and no progress.",
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.High,
                    VoyageId = voyage.Id
                }).ConfigureAwait(false);

                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(
                    testDb.Driver,
                    new RecordingMergeQueueService(),
                    incidents: incidents);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                Voyage? updated = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                AssertNotNull(updated);
                AssertEqual(VoyageStatusEnum.Complete, updated!.Status, "Voyage with all terminal missions should complete.");

                Incident? closed = await incidents.ReadAsync(auth, openIncident.Id).ConfigureAwait(false);
                AssertNotNull(closed);
                AssertEqual(IncidentStatusEnum.Closed, closed!.Status, "Open stuck-voyage incident must close when voyage completes.");
            }).ConfigureAwait(false);

            // The quiet clock must anchor on real forward progress (mission start/completion), NOT on
            // LastUpdateUtc, which WaitingForDependency assignment retries churn every health cycle.
            // Here every last_update_utc is fresh ("now") but the only real progress is stale, so the
            // churn-stable anchor must still age past the threshold and open the incident. If the
            // detector regressed to keying on last_update_utc, the quiet clock would read ~0 and no
            // incident would open.
            await RunTest("SweepAsync_StalledVoyageWithChurnedLastUpdate_OpensIncidentAnchoredOnProgress", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Churned but stalled", "Fresh last_update, stale progress")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Open,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                // Assigned with a dead process: terminal/stalled with no forward path.
                Mission assignedDead = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Persona = "Worker",
                    Title = "Assigned worker with dead process",
                    Status = MissionStatusEnum.Assigned,
                    ProcessId = 999997,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                // Real progress is two hours stale, but every last_update_utc stays fresh ("now") to
                // simulate the per-cycle churn the fix must ignore.
                DateTime staleUtc = DateTime.UtcNow.AddHours(-2);
                assignedDead.StartedUtc = staleUtc;
                await testDb.Driver.Missions.UpdateAsync(assignedDead).ConfigureAwait(false);
                await SetMissionLastUpdateUtcAsync(testDb, assignedDead.Id, DateTime.UtcNow).ConfigureAwait(false);
                await SetVoyageLastUpdateUtcAsync(testDb, voyage.Id, DateTime.UtcNow).ConfigureAwait(false);

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

                AssertEqual(1, page.Objects.Count, "Stale real progress must open an incident even when last_update_utc is freshly churned.");
                AssertContains("no live missions", page.Objects[0].Summary ?? "", "Incident summary should describe the stall.");
            }).ConfigureAwait(false);

            // Boundary guard: a terminal/stalled voyage whose most recent real progress is still
            // within StuckOpenVoyageMinutes must NOT open an incident yet -- the quiet clock has not
            // elapsed. This pins the `quietMinutes < threshold` early return.
            await RunTest("SweepAsync_StalledVoyageWithinQuietThreshold_DoesNotOpenIncident", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Recently stalled", "Just stopped progressing")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Open,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                Mission assignedDead = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Persona = "Worker",
                    Title = "Assigned worker with dead process",
                    Status = MissionStatusEnum.Assigned,
                    ProcessId = 999996,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                // Most recent real progress is five minutes ago -- well under the 30-minute threshold.
                DateTime recentUtc = DateTime.UtcNow.AddMinutes(-5);
                assignedDead.StartedUtc = recentUtc;
                await testDb.Driver.Missions.UpdateAsync(assignedDead).ConfigureAwait(false);
                // Make last_update_utc old to prove the clock does NOT key on it (otherwise the old
                // last_update would falsely trip the threshold).
                await SetMissionLastUpdateUtcAsync(testDb, assignedDead.Id, DateTime.UtcNow.AddHours(-2)).ConfigureAwait(false);
                await SetVoyageLastUpdateUtcAsync(testDb, voyage.Id, DateTime.UtcNow.AddHours(-2)).ConfigureAwait(false);

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

                AssertEqual(0, page.Objects.Count, "A stall still within the quiet threshold must not open an incident yet.");
            }).ConfigureAwait(false);

            // An Assigned mission whose tracked process id is null has no live captain -- the
            // null-process branch of the liveness check must classify it as stalled (not running),
            // so a genuinely quiet voyage opens the incident.
            await RunTest("SweepAsync_AssignedWithNullProcess_OpensIncident", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Assigned no process", "Assigned but no tracked pid")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Open,
                    LastUpdateUtc = DateTime.UtcNow.AddHours(-2)
                }).ConfigureAwait(false);

                // Assigned but ProcessId is null (captain never spawned, or pid cleared on exit).
                Mission assignedNoPid = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Persona = "Worker",
                    Title = "Assigned worker with no process id",
                    Status = MissionStatusEnum.Assigned,
                    ProcessId = null,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                DateTime staleUtc = DateTime.UtcNow.AddHours(-2);
                assignedNoPid.StartedUtc = staleUtc;
                await testDb.Driver.Missions.UpdateAsync(assignedNoPid).ConfigureAwait(false);
                await SetMissionLastUpdateUtcAsync(testDb, assignedNoPid.Id, staleUtc).ConfigureAwait(false);
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

                AssertEqual(1, page.Objects.Count, "An Assigned mission with no live process must be treated as stalled and open an incident.");
            }).ConfigureAwait(false);

            // A Pending mission whose assignment pipeline FAILED has no forward path (it is not one of
            // the expected wait states), so a quiet voyage holding only such a mission must open an
            // incident. This pins the negative branch of the waiting-for-assignment shield.
            await RunTest("SweepAsync_PendingAssignmentFailed_OpensIncident", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Pending assignment failed", "No forward path")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Open,
                    LastUpdateUtc = DateTime.UtcNow.AddHours(-2)
                }).ConfigureAwait(false);

                Mission stuckPending = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Persona = "Worker",
                    Title = "Pending mission whose assignment failed",
                    Status = MissionStatusEnum.Pending,
                    AssignmentState = MissionAssignmentStateEnum.Failed,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                DateTime staleUtc = DateTime.UtcNow.AddHours(-2);
                stuckPending.StartedUtc = staleUtc;
                await testDb.Driver.Missions.UpdateAsync(stuckPending).ConfigureAwait(false);
                await SetMissionLastUpdateUtcAsync(testDb, stuckPending.Id, staleUtc).ConfigureAwait(false);
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

                AssertEqual(1, page.Objects.Count, "A Pending mission with a Failed assignment state has no forward path and must open an incident.");
            }).ConfigureAwait(false);

            // The auto-close only mitigates stuck-voyage incidents (those carrying the "no live
            // missions" marker). An unrelated open incident on the same completing voyage must be
            // left untouched.
            await RunTest("SweepAsync_CompletedVoyage_LeavesUnrelatedIncidentOpen", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Completing with side incident", "Will complete")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Open,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Persona = "Worker",
                    Title = "Completed worker",
                    Status = MissionStatusEnum.Complete,
                    CompletedUtc = DateTime.UtcNow,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");

                // An open incident on the voyage that is NOT a stuck-voyage incident (no marker).
                Incident unrelated = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                {
                    Title = "Operator review needed",
                    Summary = "Voyage " + voyage.Id + " flagged for manual operator review.",
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.Medium,
                    VoyageId = voyage.Id
                }).ConfigureAwait(false);

                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(
                    testDb.Driver,
                    new RecordingMergeQueueService(),
                    incidents: incidents);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                Voyage? updated = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                AssertNotNull(updated);
                AssertEqual(VoyageStatusEnum.Complete, updated!.Status, "Voyage with all terminal missions should complete.");

                Incident? stillOpen = await incidents.ReadAsync(auth, unrelated.Id).ConfigureAwait(false);
                AssertNotNull(stillOpen);
                AssertEqual(IncidentStatusEnum.Open, stillOpen!.Status, "An incident without the stuck-voyage marker must not be auto-closed on completion.");
            }).ConfigureAwait(false);

            // Auto-close is scoped to voyages that reach Complete. A voyage that drains to Failed
            // (a terminal mission failed) must leave any pre-existing stuck-voyage incident open for
            // human review rather than silently self-mitigating.
            await RunTest("SweepAsync_VoyageDrainsToFailed_DoesNotCloseStuckIncident", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Draining to failed", "One mission failed")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Open,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                // All terminal, but one is Failed -> the voyage drains to Failed, not Complete.
                await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Persona = "Worker",
                    Title = "Failed worker",
                    Status = MissionStatusEnum.Failed,
                    FailureReason = "needs human review",
                    CompletedUtc = DateTime.UtcNow,
                    LastUpdateUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                // Block rescue dispatch so the failed mission cannot spawn a fresh live rescue that
                // would keep the voyage non-terminal; the drain must still settle it to Failed.
                ArmadaSettings settings = new ArmadaSettings();
                settings.AutonomousRecovery.DispatchRescueMissions = false;

                IncidentService incidents = new IncidentService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");

                // Pre-seed an open stuck-voyage incident (carries the marker).
                Incident stuck = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                {
                    Title = "Stuck open voyage: " + voyage.Title,
                    Summary = "Voyage " + voyage.Id + " has no live missions and no progress.",
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.High,
                    VoyageId = voyage.Id
                }).ConfigureAwait(false);

                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(
                    testDb.Driver,
                    new RecordingMergeQueueService(),
                    settings: settings,
                    incidents: incidents);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                Voyage? updated = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                AssertNotNull(updated);
                AssertEqual(VoyageStatusEnum.Failed, updated!.Status, "A voyage with a failed terminal mission should drain to Failed.");

                Incident? stillOpen = await incidents.ReadAsync(auth, stuck.Id).ConfigureAwait(false);
                AssertNotNull(stillOpen);
                AssertEqual(IncidentStatusEnum.Open, stillOpen!.Status, "A voyage that drains to Failed must not auto-close its stuck-voyage incident.");
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
