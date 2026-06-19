namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Memory;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests that stale terminal MemoryConsolidator missions do not block new reflection
    /// dispatches and that the evidence window advances on consolidator completion.
    /// </summary>
    public class ReflectionInFlightUnblockTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Reflection In-Flight Unblock";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("IsReflectionInFlightAsync_LandingFailed_ReturnsNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "inflight landingfailed vessel").ConfigureAwait(false);
                    await CreateConsolidatorMissionAsync(testDb.Driver, vessel.Id, MissionStatusEnum.LandingFailed).ConfigureAwait(false);

                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver);
                    Mission? inFlight = await dispatcher.IsReflectionInFlightAsync(vessel.Id).ConfigureAwait(false);
                    AssertNull(inFlight, "LandingFailed consolidator must not block dispatch");
                }
            });

            await RunTest("IsReflectionInFlightAsync_Failed_ReturnsNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "inflight failed vessel").ConfigureAwait(false);
                    await CreateConsolidatorMissionAsync(testDb.Driver, vessel.Id, MissionStatusEnum.Failed).ConfigureAwait(false);

                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver);
                    Mission? inFlight = await dispatcher.IsReflectionInFlightAsync(vessel.Id).ConfigureAwait(false);
                    AssertNull(inFlight, "Failed consolidator must not block dispatch");
                }
            });

            await RunTest("IsReflectionInFlightAsync_InProgress_ReturnsMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "inflight inprogress vessel").ConfigureAwait(false);
                    Mission mission = await CreateConsolidatorMissionAsync(testDb.Driver, vessel.Id, MissionStatusEnum.InProgress).ConfigureAwait(false);

                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver);
                    Mission? inFlight = await dispatcher.IsReflectionInFlightAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(inFlight, "InProgress consolidator must block dispatch");
                    AssertEqual(mission.Id, inFlight!.Id, "in-flight mission id");
                }
            });

            await RunTest("IsReflectionInFlightAsync_WorkProduced_ReturnsMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "inflight workproduced vessel").ConfigureAwait(false);
                    Mission mission = await CreateConsolidatorMissionAsync(testDb.Driver, vessel.Id, MissionStatusEnum.WorkProduced).ConfigureAwait(false);

                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver);
                    Mission? inFlight = await dispatcher.IsReflectionInFlightAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(inFlight, "WorkProduced consolidator must block dispatch");
                    AssertEqual(mission.Id, inFlight!.Id, "in-flight mission id");
                }
            });

            await RunTest("IsReflectionInFlightAsync_Cancelled_ReturnsNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "inflight cancelled vessel").ConfigureAwait(false);
                    await CreateConsolidatorMissionAsync(testDb.Driver, vessel.Id, MissionStatusEnum.Cancelled).ConfigureAwait(false);

                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver);
                    Mission? inFlight = await dispatcher.IsReflectionInFlightAsync(vessel.Id).ConfigureAwait(false);
                    AssertNull(inFlight, "Cancelled consolidator must not block dispatch");
                }
            });

            await RunTest("IsReflectionInFlightAsync_Complete_ReturnsNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "inflight complete vessel").ConfigureAwait(false);
                    await CreateConsolidatorMissionAsync(testDb.Driver, vessel.Id, MissionStatusEnum.Complete).ConfigureAwait(false);

                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver);
                    Mission? inFlight = await dispatcher.IsReflectionInFlightAsync(vessel.Id).ConfigureAwait(false);
                    AssertNull(inFlight, "Complete consolidator must not block dispatch");
                }
            });

            await RunTest("IsReflectionInFlightAsync_Pending_ReturnsMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "inflight pending vessel").ConfigureAwait(false);
                    Mission mission = await CreateConsolidatorMissionAsync(testDb.Driver, vessel.Id, MissionStatusEnum.Pending).ConfigureAwait(false);

                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver);
                    Mission? inFlight = await dispatcher.IsReflectionInFlightAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(inFlight, "Pending consolidator must block dispatch");
                    AssertEqual(mission.Id, inFlight!.Id, "in-flight mission id");
                }
            });

            await RunTest("IsReflectionInFlightAsync_Assigned_ReturnsMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "inflight assigned vessel").ConfigureAwait(false);
                    Mission mission = await CreateConsolidatorMissionAsync(testDb.Driver, vessel.Id, MissionStatusEnum.Assigned).ConfigureAwait(false);

                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver);
                    Mission? inFlight = await dispatcher.IsReflectionInFlightAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(inFlight, "Assigned consolidator must block dispatch");
                    AssertEqual(mission.Id, inFlight!.Id, "in-flight mission id");
                }
            });

            await RunTest("IsReflectionInFlightAsync_NoConsolidator_ReturnsNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "inflight none vessel").ConfigureAwait(false);

                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver);
                    Mission? inFlight = await dispatcher.IsReflectionInFlightAsync(vessel.Id).ConfigureAwait(false);
                    AssertNull(inFlight, "a vessel with no consolidator missions is never blocked");
                }
            });

            await RunTest("IsReflectionInFlightAsync_ActiveNonConsolidator_ReturnsNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "inflight worker vessel").ConfigureAwait(false);

                    Mission worker = new Mission("Regular worker mission");
                    worker.VesselId = vessel.Id;
                    worker.Persona = "Worker";
                    worker.Status = MissionStatusEnum.InProgress;
                    await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver);
                    Mission? inFlight = await dispatcher.IsReflectionInFlightAsync(vessel.Id).ConfigureAwait(false);
                    AssertNull(inFlight, "an active non-consolidator mission must not be treated as reflection in-flight");
                }
            });

            await RunTest("IsReflectionInFlightAsync_TerminalNewest_ActiveOlder_ReturnsActive", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "inflight mixed vessel").ConfigureAwait(false);

                    // Active mission created first (older CreatedUtc); a terminal consolidator
                    // created afterward (newer CreatedUtc) must NOT shadow the active one --
                    // the active-state filter is applied before the recency ordering.
                    Mission active = await CreateConsolidatorMissionAsync(testDb.Driver, vessel.Id, MissionStatusEnum.InProgress).ConfigureAwait(false);
                    await CreateConsolidatorMissionAsync(testDb.Driver, vessel.Id, MissionStatusEnum.Failed).ConfigureAwait(false);

                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver);
                    Mission? inFlight = await dispatcher.IsReflectionInFlightAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(inFlight, "an active consolidator must still block even when a newer terminal one exists");
                    AssertEqual(active.Id, inFlight!.Id, "the active mission is returned, not the newer terminal one");
                }
            });

            await RunTest("EvidenceWindow_AdvancesAfterConsolidatorCompletion", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "evidence window vessel").ConfigureAwait(false);

                    DateTime baseUtc = DateTime.UtcNow;

                    // Old evidence, completed before the consolidator
                    await ReflectionTestHelpers.CreateReflectionEvidenceMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        "old",
                        baseUtc.AddMinutes(-20)).ConfigureAwait(false);

                    // Consolidator that completed in the middle
                    Mission consolidator = await CreateConsolidatorMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        MissionStatusEnum.Complete,
                        baseUtc.AddMinutes(-10)).ConfigureAwait(false);
                    vessel.LastReflectionMissionId = consolidator.Id;
                    await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    // New evidence, completed after the consolidator
                    await ReflectionTestHelpers.CreateReflectionEvidenceMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        "new",
                        baseUtc.AddMinutes(-5)).ConfigureAwait(false);

                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver);
                    ReflectionDispatcher.EvidenceBundleResult bundle = await dispatcher.BuildEvidenceBundleAsync(
                        vessel,
                        null,
                        1000,
                        ReflectionMode.Consolidate).ConfigureAwait(false);

                    AssertEqual(1, bundle.EvidenceMissionCount, "default window must start after the completed consolidator and include only newer evidence");
                }
            });
        }

        private ReflectionDispatcher CreateDispatcher(DatabaseDriver database)
        {
            ArmadaSettings settings = new ArmadaSettings();
            return new ReflectionDispatcher(
                database,
                new NoOpAdmiralService(),
                settings,
                new ReflectionMemoryService(database));
        }

        private async Task<Mission> CreateConsolidatorMissionAsync(
            DatabaseDriver database,
            string vesselId,
            MissionStatusEnum status,
            DateTime? completedUtc = null)
        {
            Mission mission = new Mission("Consolidate learned facts");
            mission.VesselId = vesselId;
            mission.Persona = "MemoryConsolidator";
            mission.Status = status;
            mission.AgentOutput = ReflectionTestHelpers.BuildReflectionProposalAgentOutput("# Candidate");
            if (status == MissionStatusEnum.Complete && completedUtc.HasValue)
            {
                mission.CompletedUtc = completedUtc.Value;
            }

            return await database.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private sealed class NoOpAdmiralService : IAdmiralService
        {
            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task RecallAllAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task HealthCheckAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task HandleProcessExitAsync(
                int processId,
                int? exitCode,
                string captainId,
                string missionId,
                CancellationToken token = default)
                => throw new NotImplementedException();
        }
    }
}
