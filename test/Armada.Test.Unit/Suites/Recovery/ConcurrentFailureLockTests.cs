namespace Armada.Test.Unit.Suites.Recovery
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Verifies the per-mission lock in <see cref="MergeRecoveryHandler"/>: when two
    /// merge-entry failures arrive concurrently for the same mission, only one
    /// RecoveryAttempts increment is applied (no double-redispatch).
    /// </summary>
    public class ConcurrentFailureLockTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Concurrent Failure Lock";

        /// <summary>Run the concurrent-lock cases.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("OnMergeFailed_TwoConcurrentInvocations_OnlyOneIncrement", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.MaxRecoveryAttempts = 2;

                    Fleet fleet = new Fleet("Concurrent Fleet");
                    await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("Concurrent Vessel", "https://github.com/test/concurrent");
                    vessel.FleetId = fleet.Id;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Voyage voyage = new Voyage("Concurrent Voyage", "test concurrent recovery");
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission mission = new Mission("Concurrent Mission", "two failures racing");
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vessel.Id;
                    mission.Status = MissionStatusEnum.Failed;
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    MergeEntry entryA = new MergeEntry("captain-a-" + Guid.NewGuid().ToString("N").Substring(0, 6), "main");
                    entryA.MissionId = mission.Id;
                    entryA.VesselId = vessel.Id;
                    entryA.Status = MergeStatusEnum.Failed;
                    entryA.MergeFailureClass = MergeFailureClassEnum.StaleBase;
                    entryA.MergeFailureSummary = "stale base race A";
                    await testDb.Driver.MergeEntries.CreateAsync(entryA).ConfigureAwait(false);

                    MergeEntry entryB = new MergeEntry("captain-b-" + Guid.NewGuid().ToString("N").Substring(0, 6), "main");
                    entryB.MissionId = mission.Id;
                    entryB.VesselId = vessel.Id;
                    entryB.Status = MergeStatusEnum.Failed;
                    entryB.MergeFailureClass = MergeFailureClassEnum.StaleBase;
                    entryB.MergeFailureSummary = "stale base race B";
                    await testDb.Driver.MergeEntries.CreateAsync(entryB).ConfigureAwait(false);

                    // Router that returns Redispatch on first call, then Surface on subsequent
                    // calls. Combined with the per-mission lock the handler must:
                    //  - First invocation: see RecoveryAttempts=0, increment to 1, dispatch.
                    //  - Second invocation: see RecoveryAttempts=1 (the fresh re-read inside
                    //    the lock), call Route again; since this stub returns Surface on the
                    //    second call, no further increment happens.
                    GatedRouter router = new GatedRouter();
                    GatedAdmiralService admiral = new GatedAdmiralService();

                    MergeRecoveryHandler handler = new MergeRecoveryHandler(logging, testDb.Driver, router, admiral, settings);

                    Task taskA = Task.Run(() => handler.OnMergeFailedAsync(entryA.Id));
                    Task taskB = Task.Run(() => handler.OnMergeFailedAsync(entryB.Id));
                    await Task.WhenAll(taskA, taskB).ConfigureAwait(false);

                    Mission? finalMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(finalMission, "mission should still exist after concurrent failures");
                    AssertEqual(1, finalMission!.RecoveryAttempts,
                        "exactly one increment is allowed even with two concurrent fail-events for the same mission");
                    AssertEqual(1, admiral.RestartCalls.Count,
                        "admiral.RestartMissionAsync must only be called once across the two concurrent invocations");
                }
            });
        }

        /// <summary>
        /// Hand-rolled router whose first Route call returns Redispatch, every subsequent
        /// call returns Surface(recovery_exhausted). Mirrors the natural progression of
        /// recovery-attempts under the lock.
        /// </summary>
        private sealed class GatedRouter : IRecoveryRouter
        {
            private int _CallCount = 0;

            public RecoveryAction Route(MergeFailureClassEnum failureClass, bool conflictTrivial, int recoveryAttempts)
            {
                int call = Interlocked.Increment(ref _CallCount);
                if (call == 1) return new RecoveryAction.Redispatch();
                return new RecoveryAction.Surface("recovery_exhausted");
            }
        }

        /// <summary>
        /// Hand-rolled admiral that records RestartMissionAsync calls. Throws on every
        /// other interface method since they are not reachable under the test path.
        /// </summary>
        private sealed class GatedAdmiralService : IAdmiralService
        {
            public List<string> RestartCalls { get; } = new List<string>();

            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public Task<Mission> RestartMissionAsync(string missionId, CancellationToken token = default)
            {
                lock (RestartCalls) RestartCalls.Add(missionId);
                return Task.FromResult(new Mission { Id = missionId, Status = MissionStatusEnum.Pending });
            }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default) => throw new NotImplementedException();
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default) => throw new NotImplementedException();
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default) => throw new NotImplementedException();
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default) => throw new NotImplementedException();
            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default) => throw new NotImplementedException();
            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default) => Task.FromResult<Pipeline?>(null);
            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task RecallCaptainAsync(string captainId, CancellationToken token = default) => throw new NotImplementedException();
            public Task RecallAllAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task HealthCheckAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task CleanupStaleCaptainsAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default) => throw new NotImplementedException();
        }
    }
}
