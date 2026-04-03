namespace Armada.Test.Unit.Suites.Services
{
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    public class RemoteControlManagementServiceTests : TestSuite
    {
        public override string Name => "Remote Control Management Service";

        protected override async Task RunTestsAsync()
        {
            await RunTest("FleetAndVesselCrudFlowsOperateThroughTunnel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubAdmiralService admiral = new StubAdmiralService(testDb.Driver);
                    RemoteControlManagementService service = new RemoteControlManagementService(
                        testDb.Driver,
                        admiral,
                        (_, _, _, _, _, _, _, _) => Task.CompletedTask);

                    RemoteTunnelRequestResult createFleet = await service.HandleAsync(
                        RemoteTunnelProtocol.CreateRequest("armada.fleet.create", new
                        {
                            name = "Remote Fleet",
                            description = "Fleet from control plane"
                        }),
                        CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(201, createFleet.StatusCode);
                    string createdFleetJson = JsonSerializer.Serialize(createFleet.Payload, RemoteTunnelProtocol.JsonOptions);
                    AssertContains("Remote Fleet", createdFleetJson);

                    Fleet fleet = await testDb.Driver.Fleets.ReadByNameAsync("Remote Fleet").ConfigureAwait(false) ?? throw new InvalidOperationException("Fleet was not created.");

                    RemoteTunnelRequestResult updateFleet = await service.HandleAsync(
                        RemoteTunnelProtocol.CreateRequest("armada.fleet.update", new
                        {
                            fleetId = fleet.Id,
                            fleet = new
                            {
                                name = "Remote Fleet Updated",
                                description = "Updated description",
                                active = true
                            }
                        }),
                        CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(200, updateFleet.StatusCode);
                    AssertContains("Remote Fleet Updated", JsonSerializer.Serialize(updateFleet.Payload, RemoteTunnelProtocol.JsonOptions));

                    RemoteTunnelRequestResult createVessel = await service.HandleAsync(
                        RemoteTunnelProtocol.CreateRequest("armada.vessel.create", new
                        {
                            fleetId = fleet.Id,
                            name = "Remote Vessel",
                            repoUrl = "https://github.com/example/repo.git",
                            workingDirectory = "C:\\code\\repo",
                            defaultBranch = "main"
                        }),
                        CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(201, createVessel.StatusCode);
                    Vessel vessel = await testDb.Driver.Vessels.ReadByNameAsync("Remote Vessel").ConfigureAwait(false) ?? throw new InvalidOperationException("Vessel was not created.");

                    RemoteTunnelRequestResult listVessels = await service.HandleAsync(
                        RemoteTunnelProtocol.CreateRequest("armada.vessels.list", new RemoteTunnelQueryRequest
                        {
                            FleetId = fleet.Id,
                            Limit = 10
                        }),
                        CancellationToken.None).ConfigureAwait(false);

                    string listVesselsJson = JsonSerializer.Serialize(listVessels.Payload, RemoteTunnelProtocol.JsonOptions);
                    AssertContains("Remote Vessel", listVesselsJson);

                    RemoteTunnelRequestResult detail = await service.HandleAsync(
                        RemoteTunnelProtocol.CreateRequest("armada.vessel.detail", new RemoteTunnelQueryRequest
                        {
                            VesselId = vessel.Id
                        }),
                        CancellationToken.None).ConfigureAwait(false);

                    AssertContains("Remote Vessel", JsonSerializer.Serialize(detail.Payload, RemoteTunnelProtocol.JsonOptions));
                }
            });

            await RunTest("VoyageDispatchAndCancellationOperateThroughTunnel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubAdmiralService admiral = new StubAdmiralService(testDb.Driver);
                    RemoteControlManagementService service = new RemoteControlManagementService(
                        testDb.Driver,
                        admiral,
                        (_, _, _, _, _, _, _, _) => Task.CompletedTask);

                    Vessel vessel = new Vessel("Dispatch Vessel", "https://github.com/example/dispatch.git");
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("voyage-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    RemoteTunnelRequestResult dispatch = await service.HandleAsync(
                        RemoteTunnelProtocol.CreateRequest("armada.voyage.dispatch", new
                        {
                            title = "Remote Voyage",
                            description = "Voyage dispatched remotely",
                            vesselId = vessel.Id,
                            missions = new[]
                            {
                                new { title = "Slice One", description = "First slice" },
                                new { title = "Slice Two", description = "Second slice" }
                            }
                        }),
                        CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(201, dispatch.StatusCode);
                    Voyage voyage = await testDb.Driver.Voyages.ReadAsync(
                        JsonDocument.Parse(JsonSerializer.Serialize(dispatch.Payload, RemoteTunnelProtocol.JsonOptions)).RootElement.GetProperty("id").GetString()!)
                        .ConfigureAwait(false) ?? throw new InvalidOperationException("Voyage was not created.");

                    List<Mission> missions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(2, missions.Count);
                    missions[0].CaptainId = captain.Id;
                    missions[0].Status = MissionStatusEnum.InProgress;
                    await testDb.Driver.Missions.UpdateAsync(missions[0]).ConfigureAwait(false);

                    RemoteTunnelRequestResult cancel = await service.HandleAsync(
                        RemoteTunnelProtocol.CreateRequest("armada.voyage.cancel", new RemoteTunnelQueryRequest
                        {
                            VoyageId = voyage.Id
                        }),
                        CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(200, cancel.StatusCode);
                    Voyage cancelled = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false) ?? throw new InvalidOperationException("Voyage missing after cancellation.");
                    AssertEqual(VoyageStatusEnum.Cancelled, cancelled.Status);
                    AssertTrue(admiral.RecalledCaptains.Contains(captain.Id), "Cancelling a voyage should recall active captains");
                }
            });

            await RunTest("MissionCreateUpdateCancelAndRestartOperateThroughTunnel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubAdmiralService admiral = new StubAdmiralService(testDb.Driver);
                    RemoteControlManagementService service = new RemoteControlManagementService(
                        testDb.Driver,
                        admiral,
                        (_, _, _, _, _, _, _, _) => Task.CompletedTask);

                    Vessel vessel = new Vessel("Mission Vessel", "https://github.com/example/mission.git");
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    RemoteTunnelRequestResult createMission = await service.HandleAsync(
                        RemoteTunnelProtocol.CreateRequest("armada.mission.create", new
                        {
                            title = "Remote Mission",
                            description = "Create mission from control plane",
                            vesselId = vessel.Id,
                            persona = "Worker",
                            priority = 25
                        }),
                        CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(201, createMission.StatusCode);
                    Mission mission = await testDb.Driver.Missions.ReadAsync(
                        JsonDocument.Parse(JsonSerializer.Serialize(createMission.Payload, RemoteTunnelProtocol.JsonOptions)).RootElement.GetProperty("id").GetString()!)
                        .ConfigureAwait(false) ?? throw new InvalidOperationException("Mission was not created.");

                    RemoteTunnelRequestResult updateMission = await service.HandleAsync(
                        RemoteTunnelProtocol.CreateRequest("armada.mission.update", new
                        {
                            missionId = mission.Id,
                            mission = new
                            {
                                title = "Remote Mission Updated",
                                description = "Updated instructions",
                                vesselId = vessel.Id,
                                persona = "Judge",
                                priority = 5
                            }
                        }),
                        CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(200, updateMission.StatusCode);
                    AssertContains("Remote Mission Updated", JsonSerializer.Serialize(updateMission.Payload, RemoteTunnelProtocol.JsonOptions));

                    Captain captain = new Captain("mission-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    mission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false) ?? throw new InvalidOperationException("Mission disappeared.");
                    mission.CaptainId = captain.Id;
                    mission.Status = MissionStatusEnum.InProgress;
                    await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);

                    RemoteTunnelRequestResult cancelMission = await service.HandleAsync(
                        RemoteTunnelProtocol.CreateRequest("armada.mission.cancel", new RemoteTunnelQueryRequest
                        {
                            MissionId = mission.Id
                        }),
                        CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(200, cancelMission.StatusCode);
                    Mission cancelled = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false) ?? throw new InvalidOperationException("Mission missing after cancel.");
                    AssertEqual(MissionStatusEnum.Cancelled, cancelled.Status);
                    AssertTrue(admiral.RecalledCaptains.Contains(captain.Id), "Cancelling a mission should recall its captain");

                    RemoteTunnelRequestResult restartMission = await service.HandleAsync(
                        RemoteTunnelProtocol.CreateRequest("armada.mission.restart", new
                        {
                            missionId = mission.Id,
                            title = "Remote Mission Restarted",
                            description = "Retried mission"
                        }),
                        CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(200, restartMission.StatusCode);
                    Mission restarted = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false) ?? throw new InvalidOperationException("Mission missing after restart.");
                    AssertEqual(MissionStatusEnum.Pending, restarted.Status);
                    AssertEqual("Remote Mission Restarted", restarted.Title);
                }
            });
        }

        private sealed class StubAdmiralService : IAdmiralService
        {
            private readonly DatabaseDriver _database;

            public StubAdmiralService(DatabaseDriver database)
            {
                _database = database;
            }

            public HashSet<string> RecalledCaptains { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public async Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
            {
                return await DispatchVoyageAsync(title, description, vesselId, missionDescriptions, null, token).ConfigureAwait(false);
            }

            public async Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
            {
                Vessel? vessel = await _database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
                Voyage voyage = new Voyage(title, description);
                voyage.TenantId = vessel?.TenantId;
                voyage.UserId = vessel?.UserId;
                voyage.Status = VoyageStatusEnum.Open;
                voyage = await _database.Voyages.CreateAsync(voyage, token).ConfigureAwait(false);

                foreach (MissionDescription descriptionItem in missionDescriptions)
                {
                    Mission mission = new Mission(descriptionItem.Title, descriptionItem.Description);
                    mission.VesselId = vesselId;
                    mission.VoyageId = voyage.Id;
                    mission.TenantId = vessel?.TenantId;
                    mission.UserId = vessel?.UserId;
                    await _database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                }

                return voyage;
            }

            public async Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
            {
                mission.Status = MissionStatusEnum.Pending;
                return await _database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
            }

            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public async Task RecallCaptainAsync(string captainId, CancellationToken token = default)
            {
                RecalledCaptains.Add(captainId);
                Captain? captain = await _database.Captains.ReadAsync(captainId, token).ConfigureAwait(false);
                if (captain != null)
                {
                    captain.State = CaptainStateEnum.Idle;
                    captain.CurrentMissionId = null;
                    captain.CurrentDockId = null;
                    captain.ProcessId = null;
                    captain.LastUpdateUtc = DateTime.UtcNow;
                    await _database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
                }
            }

            public Task RecallAllAsync(CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task HealthCheckAsync(CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
            {
                return Task.CompletedTask;
            }
        }
    }
}
