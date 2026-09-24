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
            await RunTest("TransitionMissionStatus_CallerOutsideMissionOwnerIsRefusedWithoutChange", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("owned transition")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        Status = MissionStatusEnum.Pending
                    }).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? transitionHandler = null;
                    // No transition service is supplied, so a caller who passes the scope check
                    // receives the unavailable refusal; only the scope check yields "Mission not found".
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_transition_mission_status") transitionHandler = handler; },
                        testDb.Driver,
                        new RecordingAdmiralDouble(),
                        null,
                        null);
                    AssertNotNull(transitionHandler, "armada_transition_mission_status handler must be registered");
                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id, status = "Assigned" });

                    AuthContext otherTenantUser = AuthContext.Authenticated("ten_other", "usr_other", false, false, "Test");
                    string foreignJson;
                    using (McpCallerContext.Begin(otherTenantUser))
                    {
                        foreignJson = JsonSerializer.Serialize(await transitionHandler!(args).ConfigureAwait(false));
                    }
                    AssertContains("Mission not found", foreignJson, "another tenant's caller cannot transition or discover the mission");

                    AuthContext otherTenantAdmin = AuthContext.Authenticated("ten_other", "usr_other_admin", false, true, "Test");
                    string foreignAdminJson;
                    using (McpCallerContext.Begin(otherTenantAdmin))
                    {
                        foreignAdminJson = JsonSerializer.Serialize(await transitionHandler!(args).ConfigureAwait(false));
                    }
                    AssertContains("Mission not found", foreignAdminJson, "a tenant administrator cannot transition another tenant's mission");

                    AuthContext ownerTenantAdmin = AuthContext.Authenticated(Armada.Core.Constants.DefaultTenantId, "usr_owner_admin", false, true, "Test");
                    string ownerJson;
                    using (McpCallerContext.Begin(ownerTenantAdmin))
                    {
                        ownerJson = JsonSerializer.Serialize(await transitionHandler!(args).ConfigureAwait(false));
                    }
                    AssertFalse(ownerJson.Contains("Mission not found", StringComparison.Ordinal),
                        "the owning tenant's administrator passes the scope check: " + ownerJson);

                    Mission? unchanged = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, unchanged!.Status, "a refused transition leaves the mission unchanged");
                }
            });

            await RunTest("CreateMission_IsOwnedByTheCaller", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    TenantMetadata tenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("mission-owner-tenant")).ConfigureAwait(false);
                    UserMaster user = await testDb.Driver.Users.CreateAsync(new UserMaster(tenant.Id, "mission-owner@example.com", "password")).ConfigureAwait(false);
                    AuthContext caller = AuthContext.Authenticated(tenant.Id, user.Id, false, false, "Test");
                    Vessel vessel = new Vessel("mission-owner-vessel", "https://github.com/test/mission-owner.git");
                    vessel.TenantId = tenant.Id;
                    vessel.UserId = user.Id;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                    Func<JsonElement?, Task<object>>? createHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_create_mission") createHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null);
                    AssertNotNull(createHandler, "armada_create_mission handler must be registered");

                    using (McpCallerContext.Begin(caller))
                    {
                        await createHandler!(JsonSerializer.SerializeToElement(new { title = "owned", description = "owned mission", vesselId = vessel.Id })).ConfigureAwait(false);
                    }

                    AssertNotNull(admiralDouble.LastDispatched, "the mission reaches dispatch");
                    AssertEqual(tenant.Id, admiralDouble.LastDispatched!.TenantId, "the mission belongs to the caller's tenant");
                    AssertEqual(user.Id, admiralDouble.LastDispatched.UserId, "the mission belongs to the calling user");
                }
            });

            await RunTest("RestartMission_ProgressSignalIsOwnedLikeTheMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    TenantMetadata tenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("restart-owner-tenant")).ConfigureAwait(false);
                    UserMaster user = await testDb.Driver.Users.CreateAsync(new UserMaster(tenant.Id, "restart-owner@example.com", "password")).ConfigureAwait(false);
                    Vessel vessel = new Vessel("restart-vessel", "https://github.com/test/restart.git");
                    vessel.TenantId = tenant.Id;
                    vessel.UserId = user.Id;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("restart owned")
                    {
                        TenantId = tenant.Id,
                        UserId = user.Id,
                        VesselId = vessel.Id,
                        Status = MissionStatusEnum.Failed
                    }).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? restartHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_restart_mission") restartHandler = handler; },
                        testDb.Driver,
                        new RecordingAdmiralDouble(),
                        null,
                        null);
                    AssertNotNull(restartHandler, "armada_restart_mission handler must be registered");

                    string json;
                    using (McpCallerContext.Begin(McpTestCaller.Operator))
                    {
                        json = JsonSerializer.Serialize(await restartHandler!(JsonSerializer.SerializeToElement(new { missionId = mission.Id })).ConfigureAwait(false));
                    }

                    List<Signal> signals = await testDb.Driver.Signals.EnumerateRecentAsync(100).ConfigureAwait(false);
                    Signal? restarted = signals.Find(s => (s.Payload ?? String.Empty).Contains(mission.Id + " restarted", StringComparison.Ordinal));
                    AssertNotNull(restarted, "the restart writes a progress signal: " + json);
                    AssertEqual(tenant.Id, restarted!.TenantId, "the restart signal belongs to the mission's tenant");
                    AssertEqual(user.Id, restarted.UserId, "the restart signal belongs to the mission's user");
                }
            });

            await RunTest("UpdateMission_RefusesVesselOrVoyageRebinding_AndWritesNothing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("bound-vessel", "https://github.com/test/bound.git")).ConfigureAwait(false);
                    Vessel otherVessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("other-vessel", "https://github.com/test/other.git")).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("bound voyage")).ConfigureAwait(false);
                    Voyage otherVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("other voyage")).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("bound mission")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        Status = MissionStatusEnum.Pending
                    }).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? updateHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_update_mission") updateHandler = handler; },
                        testDb.Driver,
                        new RecordingAdmiralDouble(),
                        null,
                        null);
                    AssertNotNull(updateHandler, "armada_update_mission handler must be registered");

                    foreach (object args in new object[]
                    {
                        new { missionId = mission.Id, title = "moved", vesselId = otherVessel.Id },
                        new { missionId = mission.Id, title = "moved", voyageId = otherVoyage.Id }
                    })
                    {
                        string json;
                        using (McpCallerContext.Begin(McpTestCaller.Operator))
                        {
                            json = JsonSerializer.Serialize(await updateHandler!(JsonSerializer.SerializeToElement(args)).ConfigureAwait(false));
                        }
                        AssertContains("cannot be changed", json, "a rebinding update is refused like REST and WebSocket: " + json);
                        Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(vessel.Id, stored!.VesselId, "the refused update keeps the vessel");
                        AssertEqual(voyage.Id, stored.VoyageId, "the refused update keeps the voyage");
                        AssertEqual("bound mission", stored.Title, "the refused update writes no other field");
                    }

                    string sameBinding;
                    using (McpCallerContext.Begin(McpTestCaller.Operator))
                    {
                        sameBinding = JsonSerializer.Serialize(await updateHandler!(JsonSerializer.SerializeToElement(new { missionId = mission.Id, title = "renamed", vesselId = vessel.Id, voyageId = voyage.Id })).ConfigureAwait(false));
                    }
                    Mission? renamed = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual("renamed", renamed!.Title, "an update naming the current bindings is accepted: " + sameBinding);
                }
            });

            await RunTest("MissionOutput_ReturnsPersistedDigestBackedPage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = new Mission("persisted output")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        AgentOutput = "page one 🙂 page two",
                        Status = MissionStatusEnum.Complete
                    };
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? outputHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_mission_output") outputHandler = handler; },
                        testDb.Driver,
                        new RecordingAdmiralDouble(),
                        null,
                        null);

                    AssertNotNull(outputHandler, "armada_mission_output handler must be registered");
                    object result = await outputHandler!(JsonSerializer.SerializeToElement(new
                    {
                        missionId = mission.Id,
                        offset = 0,
                        length = 9
                    })).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);
                    AssertContains("mission-output:" + mission.Id, json);
                    AssertContains("\"HasMore\":true", json);
                    AssertContains("\"Complete\":true", json);
                    AssertContains("\"Sha256\":", json);
                }
            });

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
                        (name, _, _, handler) => { if (name == "armada_create_mission") createHandler = McpTestCaller.Wrap(handler); },
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
                        (name, _, _, handler) => { if (name == "armada_create_mission") createHandler = McpTestCaller.Wrap(handler); },
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
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
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

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => Task.FromResult<Pipeline?>(null);

            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task RecallAllAsync(CancellationToken token = default)
                => throw new NotImplementedException();
            public Task StopAllAgentProcessesAsync(CancellationToken token = default) => Task.CompletedTask;

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
