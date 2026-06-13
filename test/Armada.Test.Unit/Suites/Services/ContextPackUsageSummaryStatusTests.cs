namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests that armada_mission_status surfaces <see cref="Mission.ContextPackUsage"/>.
    /// </summary>
    public class ContextPackUsageSummaryStatusTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Context Pack Usage Status Surface";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("MissionStatus_WithContextPackUsageEvent_PopulatesContextPackUsage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("ctx-pack-status-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Mission mission = await testDb.Driver.Missions.CreateAsync(
                        new Mission("ctx pack status", "desc")).ConfigureAwait(false);
                    mission.VesselId = vessel.Id;
                    mission = await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);

                    string payload = JsonSerializer.Serialize(new
                    {
                        MissionId = mission.Id,
                        LogAvailable = true,
                        ContextPackStaged = true,
                        ContextPackCompliance = "ReadBeforeSearch",
                        FirstContextPackReadOffset = 50,
                        FirstSearchToolOffset = 200,
                        SearchToolCallCount = 1,
                        FilesReadFromPack = new[] { "src/A.cs", "src/B.cs" },
                        FilesIgnoredFromPack = new[] { "src/C.cs" },
                        FilesGrepDiscovered = Array.Empty<string>(),
                        FilesEdited = new[] { "src/A.cs" }
                    });

                    ArmadaEvent usageEvent = new ArmadaEvent(
                        ContextPackUsageSummary.EventType,
                        "Context pack usage: ReadBeforeSearch");
                    usageEvent.MissionId = mission.Id;
                    usageEvent.VesselId = vessel.Id;
                    usageEvent.Payload = payload;
                    await testDb.Driver.Events.CreateAsync(usageEvent).ConfigureAwait(false);

                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_mission_status") statusHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null);

                    AssertNotNull(statusHandler, "armada_mission_status handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await statusHandler!(args).ConfigureAwait(false);

                    Mission? statusMission = result as Mission;
                    AssertNotNull(statusMission, "result should be Mission");
                    AssertNotNull(statusMission!.ContextPackUsage, "ContextPackUsage should be populated");
                    AssertEqual("ReadBeforeSearch", statusMission.ContextPackUsage!.Compliance);
                    AssertEqual(2, statusMission.ContextPackUsage.FilesReadFromPackCount);
                    AssertEqual(1, statusMission.ContextPackUsage.FilesIgnoredFromPackCount);
                    AssertEqual(0, statusMission.ContextPackUsage.FilesGrepDiscoveredCount);
                    AssertEqual(1, statusMission.ContextPackUsage.FilesEditedCount);
                }
            });

            await RunTest("MissionStatus_WithoutContextPackUsageEvent_LeavesContextPackUsageNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await testDb.Driver.Missions.CreateAsync(
                        new Mission("no ctx pack event", "desc")).ConfigureAwait(false);

                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_mission_status") statusHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null);

                    AssertNotNull(statusHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await statusHandler!(args).ConfigureAwait(false);

                    Mission? statusMission = result as Mission;
                    AssertNotNull(statusMission, "result should be Mission");
                    AssertNull(statusMission!.ContextPackUsage, "ContextPackUsage should be null");
                }
            });
        }

        private sealed class MinimalAdmiralDouble : IAdmiralService
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
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => Task.FromResult<Pipeline?>(null);

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
                int processId, int? exitCode, string captainId, string missionId,
                CancellationToken token = default)
                => throw new NotImplementedException();
        }
    }
}
