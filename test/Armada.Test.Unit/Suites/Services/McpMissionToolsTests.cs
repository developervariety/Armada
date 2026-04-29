namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for McpMissionTools: verifies DefaultPlaybooks merge in armada_create_mission.
    /// </summary>
    public class McpMissionToolsTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "MCP Mission Tools";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("CreateMission_VesselWithDefaultPlaybooks_MergesIntoMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("msn-dp-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    string defaultPlaybooksJson = "[{\"playbookId\":\"pbk_msn1\",\"deliveryMode\":\"InlineFullContent\"},{\"playbookId\":\"pbk_msn2\",\"deliveryMode\":\"AttachIntoWorktree\"}]";
                    vessel.DefaultPlaybooks = defaultPlaybooksJson;
                    vessel = await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                    Func<JsonElement?, Task<object>>? createHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_create_mission") createHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null);

                    AssertNotNull(createHandler, "armada_create_mission handler must be registered");

                    // Caller passes no selectedPlaybooks -- vessel defaults should be applied.
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "test mission",
                        description = "test desc",
                        vesselId = vessel.Id
                    });
                    object result = await createHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);
                    AssertNotNull(admiralDouble.LastDispatched, "DispatchMissionAsync should have been called");
                    AssertEqual(2, admiralDouble.LastDispatched!.SelectedPlaybooks.Count, "Both vessel defaults should be in SelectedPlaybooks");
                    AssertEqual("pbk_msn1", admiralDouble.LastDispatched.SelectedPlaybooks[0].PlaybookId);
                    AssertEqual("pbk_msn2", admiralDouble.LastDispatched.SelectedPlaybooks[1].PlaybookId);
                }
            });

            await RunTest("CreateMission_CallerPlaybooksOverrideDefaultOnCollision", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("msn-coll-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    string defaultPlaybooksJson = "[{\"playbookId\":\"pbk_shared\",\"deliveryMode\":\"InlineFullContent\"}]";
                    vessel.DefaultPlaybooks = defaultPlaybooksJson;
                    vessel = await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                    Func<JsonElement?, Task<object>>? createHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_create_mission") createHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null);

                    AssertNotNull(createHandler);

                    // Caller supplies pbk_shared with different deliveryMode -- should override default.
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "collision test",
                        description = "collision desc",
                        vesselId = vessel.Id,
                        selectedPlaybooks = new[]
                        {
                            new { playbookId = "pbk_shared", deliveryMode = "AttachIntoWorktree" }
                        }
                    });
                    object result = await createHandler!(args).ConfigureAwait(false);

                    AssertNotNull(admiralDouble.LastDispatched);
                    AssertEqual(1, admiralDouble.LastDispatched!.SelectedPlaybooks.Count, "Collision merges to one entry");
                    AssertEqual("pbk_shared", admiralDouble.LastDispatched.SelectedPlaybooks[0].PlaybookId);
                    AssertEqual(PlaybookDeliveryModeEnum.AttachIntoWorktree, admiralDouble.LastDispatched.SelectedPlaybooks[0].DeliveryMode,
                        "Caller deliveryMode should override default on collision");
                }
            });
        }

        private sealed class RecordingAdmiralDouble : IAdmiralService
        {
            /// <summary>The last mission passed to DispatchMissionAsync.</summary>
            public Mission? LastDispatched { get; private set; }

            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
            {
                LastDispatched = mission;
                mission.Status = MissionStatusEnum.InProgress;
                return Task.FromResult(mission);
            }

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
