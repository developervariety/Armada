namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Verifies armada_cancel_voyage and armada_cancel_mission terminate the running
    /// captain agent process via the onStopCaptain callback. Without this, in-flight
    /// cancels left the captain stuck Working forever and the dispatcher refused to
    /// assign new missions to that captain or single-captain pool.
    /// </summary>
    public class CancelKillsCaptainTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Cancel Kills Captain Process";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("CancelVoyage_InProgressMission_InvokesOnStopCaptainAndIdlesCaptain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("cancel-vessel-1", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Captain captain = new Captain("captain-cancel-1");
                    captain.State = CaptainStateEnum.Working;
                    captain.ProcessId = 9999;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("v1", "")).ConfigureAwait(false);

                    Mission mission = new Mission("running mission", "");
                    mission.VesselId = vessel.Id;
                    mission.VoyageId = voyage.Id;
                    mission.CaptainId = captain.Id;
                    mission.Status = MissionStatusEnum.InProgress;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    captain.CurrentMissionId = mission.Id;
                    captain = await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    HashSet<string> stopped = new HashSet<string>();
                    Func<string, Task> onStopCaptain = async (cid) =>
                    {
                        stopped.Add(cid);
                        await Task.CompletedTask;
                    };

                    RecordingRecallAdmiralDouble admiralDouble = new RecordingRecallAdmiralDouble(testDb.Driver);

                    Func<JsonElement?, Task<object>>? cancelHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_cancel_voyage") cancelHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        onStopCaptain);
                    AssertNotNull(cancelHandler, "armada_cancel_voyage handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { voyageId = voyage.Id });
                    object result = await cancelHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertFalse(resultJson.Contains("\"Error\""), "Cancel should not error: " + resultJson);

                    AssertTrue(stopped.Contains(captain.Id),
                        "onStopCaptain must be invoked for the in-flight captain so the agent process dies");
                    AssertTrue(admiralDouble.RecalledCaptainIds.Contains(captain.Id),
                        "RecallCaptainAsync must be invoked to reset captain DB state");

                    Mission? readMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Cancelled, readMission!.Status, "InProgress mission must be cancelled");
                    AssertNull(readMission.ProcessId, "Mission ProcessId must be cleared");
                }
            });

            await RunTest("CancelVoyage_PendingOnly_DoesNotInvokeStopCaptain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("cancel-vessel-pending", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("v-pending", "")).ConfigureAwait(false);

                    Mission mission = new Mission("pending mission", "");
                    mission.VesselId = vessel.Id;
                    mission.VoyageId = voyage.Id;
                    mission.Status = MissionStatusEnum.Pending;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    HashSet<string> stopped = new HashSet<string>();
                    Func<string, Task> onStopCaptain = async (cid) =>
                    {
                        stopped.Add(cid);
                        await Task.CompletedTask;
                    };

                    RecordingRecallAdmiralDouble admiralDouble = new RecordingRecallAdmiralDouble(testDb.Driver);

                    Func<JsonElement?, Task<object>>? cancelHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_cancel_voyage") cancelHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        onStopCaptain);
                    AssertNotNull(cancelHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new { voyageId = voyage.Id });
                    await cancelHandler!(args).ConfigureAwait(false);

                    AssertEqual(0, stopped.Count,
                        "onStopCaptain must NOT be invoked when no missions are assigned to a captain");
                    AssertEqual(0, admiralDouble.RecalledCaptainIds.Count,
                        "RecallCaptainAsync must NOT be invoked for purely pending missions");

                    Mission? readMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Cancelled, readMission!.Status);
                }
            });

            await RunTest("CancelMission_InProgress_InvokesOnStopCaptain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("cancel-mission-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Captain captain = new Captain("captain-mission-1");
                    captain.State = CaptainStateEnum.Working;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Mission mission = new Mission("standalone running", "");
                    mission.VesselId = vessel.Id;
                    mission.CaptainId = captain.Id;
                    mission.Status = MissionStatusEnum.InProgress;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    captain.CurrentMissionId = mission.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    HashSet<string> stopped = new HashSet<string>();
                    Func<string, Task> onStopCaptain = async (cid) =>
                    {
                        stopped.Add(cid);
                        await Task.CompletedTask;
                    };

                    RecordingRecallAdmiralDouble admiralDouble = new RecordingRecallAdmiralDouble(testDb.Driver);

                    Func<JsonElement?, Task<object>>? cancelHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_cancel_mission") cancelHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        onStopCaptain);
                    AssertNotNull(cancelHandler, "armada_cancel_mission handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    await cancelHandler!(args).ConfigureAwait(false);

                    AssertTrue(stopped.Contains(captain.Id),
                        "onStopCaptain must be invoked for the in-flight captain");
                    AssertTrue(admiralDouble.RecalledCaptainIds.Contains(captain.Id),
                        "RecallCaptainAsync must be invoked to reset captain DB state");
                }
            });
        }

        /// <summary>
        /// Stub IAdmiralService that records RecallCaptainAsync calls and updates the
        /// captain's DB row to Idle (matching real admiral semantics) so downstream
        /// assertions can read post-recall state.
        /// </summary>
        private sealed class RecordingRecallAdmiralDouble : IAdmiralService
        {
            private readonly DatabaseDriver _Database;
            public HashSet<string> RecalledCaptainIds { get; } = new HashSet<string>();

            public RecordingRecallAdmiralDouble(DatabaseDriver database)
            {
                _Database = database;
            }

            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public async Task RecallCaptainAsync(string captainId, CancellationToken token = default)
            {
                RecalledCaptainIds.Add(captainId);
                Captain? captain = await _Database.Captains.ReadAsync(captainId, token).ConfigureAwait(false);
                if (captain != null)
                {
                    captain.State = CaptainStateEnum.Idle;
                    captain.CurrentMissionId = null;
                    captain.CurrentDockId = null;
                    captain.ProcessId = null;
                    captain.RecoveryAttempts = 0;
                    captain.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
                }
            }

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
                => throw new NotImplementedException();
            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => Task.FromResult<Pipeline?>(null);
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
                => throw new NotImplementedException();
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
                => throw new NotImplementedException();
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();
            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task RecallAllAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task HealthCheckAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task CleanupStaleCaptainsAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
                => Task.CompletedTask;
        }
    }
}
