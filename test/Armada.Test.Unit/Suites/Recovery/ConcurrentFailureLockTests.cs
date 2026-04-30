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
    /// merge-entry failures arrive concurrently for the same mission, exactly one
    /// admiral redispatch fires and the RecoveryAttempts counter increments exactly
    /// once. The router used here gates by the persisted RecoveryAttempts (cap 1) so
    /// the SECOND invocation -- which can only see a fresh attempts value if the lock
    /// serialised it after the first invocation's persist -- routes to Surface.
    /// Without the lock both invocations would read RecoveryAttempts==0 concurrently,
    /// both route to Redispatch, both call admiral, and the test fails.
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
                    // Cap of 1 so a router driven by attempts will return Redispatch when
                    // attempts==0 and Surface when attempts>=1. The lock is what makes the
                    // second invocation observe attempts==1 instead of the stale 0.
                    settings.MaxRecoveryAttempts = 1;

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

                    AttemptsAwareRouter router = new AttemptsAwareRouter(maxAttempts: 1);
                    GatedAdmiralService admiral = new GatedAdmiralService();

                    MergeRecoveryHandler handler = new MergeRecoveryHandler(logging, testDb.Driver, router, admiral, settings);

                    Task taskA = Task.Run(() => handler.OnMergeFailedAsync(entryA.Id));
                    Task taskB = Task.Run(() => handler.OnMergeFailedAsync(entryB.Id));
                    await Task.WhenAll(taskA, taskB).ConfigureAwait(false);

                    Mission? finalMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(finalMission, "mission should still exist after concurrent failures");
                    AssertEqual(1, finalMission!.RecoveryAttempts,
                        "exactly one increment is allowed even with two concurrent fail-events for the same mission; without the per-mission lock the second invocation would read attempts==0 and double-increment");
                    AssertEqual(1, admiral.RestartCalls.Count,
                        "admiral.RestartMissionAsync must be called once: the second invocation, serialised behind the lock, re-reads attempts==1 and routes to Surface");
                    AssertEqual(2, router.RouteCallCount,
                        "router must be consulted by both invocations (no fake-greening via call-count gating)");
                }
            });
        }

        /// <summary>
        /// Hand-rolled router that gates SOLELY on the recoveryAttempts argument, NOT on
        /// internal call counts. Returns <see cref="RecoveryAction.Redispatch"/> while
        /// attempts &lt; cap and <see cref="RecoveryAction.Surface"/> otherwise. This makes
        /// the per-mission lock load-bearing: the second concurrent invocation can only
        /// route to Surface if the lock forced it to re-read the persisted (incremented)
        /// attempts value. A previous version of this test used a router that returned
        /// Redispatch on its first call and Surface afterward, which masked a missing
        /// lock because the second router call always returned Surface regardless of
        /// concurrency.
        /// </summary>
        private sealed class AttemptsAwareRouter : IRecoveryRouter
        {
            private readonly int _MaxAttempts;
            private int _RouteCallCount;

            public AttemptsAwareRouter(int maxAttempts)
            {
                _MaxAttempts = maxAttempts;
            }

            public int RouteCallCount => _RouteCallCount;

            public RecoveryAction Route(MergeFailureClassEnum failureClass, bool conflictTrivial, int recoveryAttempts)
            {
                Interlocked.Increment(ref _RouteCallCount);
                if (recoveryAttempts < _MaxAttempts) return new RecoveryAction.Redispatch();
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
