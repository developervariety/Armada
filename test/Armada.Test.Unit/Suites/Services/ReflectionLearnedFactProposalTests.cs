namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
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
    /// Tests that mission-discovered facts flow into reflection evidence instead of raw ModelContext.
    /// </summary>
    public class ReflectionLearnedFactProposalTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Reflection Learned-Fact Proposals";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("BuildEvidenceBundle_EnableModelContextTrue_RoutesProposalToEvidence", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "proposal-enabled", true).ConfigureAwait(false);
                    vessel.ModelContext = "Legacy context remains readable but should not grow.";
                    await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    await CreateMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        "[LEARNED-FACT-PROPOSAL] [high] Reflection evidence should carry durable discoveries.").ConfigureAwait(false);

                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver);
                    ReflectionDispatcher.EvidenceBundleResult bundle = await dispatcher.BuildEvidenceBundleAsync(
                        vessel,
                        null,
                        8000).ConfigureAwait(false);

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertContains("Learned-fact proposals:", bundle.Brief, "Evidence should include extracted proposal section");
                    AssertContains("[high] Reflection evidence should carry durable discoveries.", bundle.Brief, "Evidence should include proposal body");
                    AssertEqual("Legacy context remains readable but should not grow.", updated!.ModelContext, "ModelContext should not be appended during evidence routing");
                }
            });

            await RunTest("BuildEvidenceBundle_EnableModelContextFalse_DoesNotAddProposalSection", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "proposal-disabled", false).ConfigureAwait(false);
                    await CreateMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        "[LEARNED-FACT-PROPOSAL] [medium] This should stay only in raw output when disabled.").ConfigureAwait(false);

                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver);
                    ReflectionDispatcher.EvidenceBundleResult bundle = await dispatcher.BuildEvidenceBundleAsync(
                        vessel,
                        null,
                        8000).ConfigureAwait(false);

                    AssertFalse(bundle.Brief.Contains("Learned-fact proposals:"), "Disabled ModelContext should not route proposals into the curated evidence section");
                }
            });
        }

        private static ReflectionDispatcher CreateDispatcher(DatabaseDriver database)
        {
            return new ReflectionDispatcher(
                database,
                new NoOpAdmiralService(),
                new ArmadaSettings { InitialReflectionWindow = 10 },
                new ReflectionMemoryService(database));
        }

        private static async Task<Vessel> CreateVesselAsync(DatabaseDriver database, string name, bool enableModelContext)
        {
            Vessel vessel = new Vessel(name, "https://github.com/test/" + name + ".git");
            vessel.TenantId = Constants.DefaultTenantId;
            vessel.EnableModelContext = enableModelContext;
            return await database.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateMissionAsync(DatabaseDriver database, string vesselId, string agentOutput)
        {
            Mission mission = new Mission("proposal mission", "desc");
            mission.VesselId = vesselId;
            mission.Persona = "Worker";
            mission.Status = MissionStatusEnum.Complete;
            mission.CompletedUtc = DateTime.UtcNow;
            mission.AgentOutput = agentOutput;
            mission.DiffSnapshot = "diff";
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

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
            {
                return Task.FromResult<Pipeline?>(null);
            }

            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
            {
                return Task.FromResult(new ArmadaStatus());
            }

            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
            {
                return Task.CompletedTask;
            }

            public Task RecallAllAsync(CancellationToken token = default)
            {
                return Task.CompletedTask;
            }

            public Task HealthCheckAsync(CancellationToken token = default)
            {
                return Task.CompletedTask;
            }

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
            {
                return Task.CompletedTask;
            }

            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
            {
                return Task.CompletedTask;
            }
        }
    }
}
