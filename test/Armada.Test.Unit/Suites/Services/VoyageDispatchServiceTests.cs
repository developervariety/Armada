namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Server.Routes;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for the shared voyage dispatch service used by REST and MCP dispatch paths.
    /// </summary>
    public class VoyageDispatchServiceTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Voyage Dispatch Service";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("RestMapping_WithPerMissionFields_DispatchesCreatedMissionsWithFields", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("rest-dispatch-vessel", "https://github.com/test/repo.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);

                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService();
                    string cachedPackPath = Path.Combine(Path.GetTempPath(), "rest-context-pack.md");
                    codeIndex.CachedResponse = new ContextPackResponse();
                    codeIndex.CachedResponse.PrestagedFiles.Add(new PrestagedFile(cachedPackPath, "_briefing/context-pack.md"));

                    VoyageRequest restRequest = new VoyageRequest
                    {
                        Title = "REST parity voyage",
                        Description = "verify REST maps every dispatch field",
                        VesselId = vessel.Id,
                        CodeContextMode = "force",
                        CodeContextTokenBudget = 1400,
                        CodeContextMaxResults = 4,
                        Missions = new List<MissionRequest>
                        {
                            new MissionRequest
                            {
                                Title = "REST worker",
                                Description = "carry fields",
                                PreferredModel = "high",
                                DependsOnMissionId = "msn_existing_0001",
                                CodeContextQuery = "custom REST query",
                                PrestagedFiles = new List<PrestagedFile>
                                {
                                    new PrestagedFile(Path.Combine(Path.GetTempPath(), "rest-input.txt"), "notes/input.txt")
                                },
                                SelectedPlaybooks = new List<SelectedPlaybook>
                                {
                                    new SelectedPlaybook
                                    {
                                        PlaybookId = "pbk_rest",
                                        DeliveryMode = PlaybookDeliveryModeEnum.AttachIntoWorktree
                                    }
                                }
                            }
                        },
                        SelectedPlaybooks = new List<SelectedPlaybook>
                        {
                            new SelectedPlaybook
                            {
                                PlaybookId = "pbk_voyage",
                                DeliveryMode = PlaybookDeliveryModeEnum.InstructionWithReference
                            }
                        }
                    };

                    SharedVoyageDispatchRequest dispatchRequest = VoyageRoutes.CreateDispatchRequest(restRequest);
                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver,
                        admiral,
                        null,
                        codeIndex,
                        null,
                        null);

                    VoyageDispatchResult result = await service.DispatchAsync(dispatchRequest).ConfigureAwait(false);

                    AssertTrue(result.Succeeded, "REST dispatch service result should succeed");
                    AssertEqual(1, codeIndex.CacheRequests.Count, "REST dispatch should use code context orchestration");
                    AssertEqual("custom REST query", codeIndex.CacheRequests[0].Goal);
                    AssertEqual(1400, codeIndex.CacheRequests[0].TokenBudget);
                    AssertEqual(4, codeIndex.CacheRequests[0].MaxResults!.Value);

                    List<Mission> missions = await testDb.Driver.Missions.EnumerateByVoyageAsync(result.Voyage!.Id).ConfigureAwait(false);
                    AssertEqual(1, missions.Count, "One mission should be created");
                    Mission created = missions[0];
                    AssertEqual("high", created.PreferredModel);
                    AssertEqual("msn_existing_0001", created.DependsOnMissionId);
                    AssertNotNull(created.PrestagedFiles, "Prestaged files should survive REST mapping and context merge");
                    AssertEqual(2, created.PrestagedFiles!.Count);
                    AssertEqual("notes/input.txt", created.PrestagedFiles[0].DestPath);
                    AssertEqual("_briefing/context-pack.md", created.PrestagedFiles[1].DestPath);
                    AssertEqual(1, admiral.CreatedMissions[0].SelectedPlaybooks.Count);
                    AssertEqual("pbk_rest", admiral.CreatedMissions[0].SelectedPlaybooks[0].PlaybookId);
                }
            });

            await RunTest("RestMapping_NoMissions_UsesBareVoyagePath", () =>
            {
                VoyageRequest request = new VoyageRequest
                {
                    Title = "Bare",
                    Description = "No missions",
                    VesselId = "vsl_any",
                    Missions = new List<MissionRequest>()
                };

                AssertTrue(VoyageRoutes.ShouldCreateBareVoyage(request),
                    "REST requests without missions must keep the existing bare-voyage path");
                return Task.CompletedTask;
            });

            await RunTest("McpDispatch_StandardRequest_StillCreatesVoyageThroughSharedService", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("mcp-dispatch-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiral);

                    AssertNotNull(dispatchHandler, "armada_dispatch handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "MCP regression voyage",
                        description = "delegate regression",
                        vesselId = vessel.Id,
                        codeContextMode = "off",
                        missions = new object[]
                        {
                            new { title = "Task A", description = "Do A" }
                        }
                    });

                    object response = await dispatchHandler!(args).ConfigureAwait(false);
                    string responseJson = JsonSerializer.Serialize(response);

                    AssertFalse(responseJson.Contains("\"Error\""), "MCP dispatch should not return an error: " + responseJson);
                    AssertTrue(admiral.DispatchVoyageCalled, "MCP standard dispatch should still call voyage dispatch");
                    AssertEqual(1, admiral.CreatedMissions.Count, "MCP standard dispatch should create one mission through the shared path");
                }
            });
        }

        private sealed class RecordingAdmiralService : IAdmiralService
        {
            private readonly DatabaseDriver _Database;

            public RecordingAdmiralService(DatabaseDriver database)
            {
                _Database = database;
            }

            public bool DispatchVoyageCalled { get; private set; }

            public List<Mission> CreatedMissions { get; } = new List<Mission>();

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
            {
                return DispatchVoyageAsync(title, description, vesselId, missionDescriptions, null, null, token);
            }

            public Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
            {
                return DispatchVoyageAsync(title, description, vesselId, missionDescriptions, null, selectedPlaybooks, token);
            }

            public Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                CancellationToken token = default)
            {
                return DispatchVoyageAsync(title, description, vesselId, missionDescriptions, pipelineId, null, token);
            }

            public async Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
            {
                DispatchVoyageCalled = true;
                Voyage voyage = await _Database.Voyages.CreateAsync(new Voyage(title, description)
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId
                }, token).ConfigureAwait(false);

                foreach (MissionDescription md in missionDescriptions)
                {
                    Mission mission = new Mission(md.Title, md.Description);
                    mission.TenantId = Constants.DefaultTenantId;
                    mission.UserId = Constants.DefaultUserId;
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vesselId;
                    mission.PreferredModel = md.PreferredModel;
                    mission.DependsOnMissionId = md.DependsOnMissionId;
                    mission.PrestagedFiles = md.PrestagedFiles;
                    mission.SelectedPlaybooks = md.SelectedPlaybooks ?? new List<SelectedPlaybook>();
                    mission = await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                    CreatedMissions.Add(mission);
                }

                return voyage;
            }

            public async Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
            {
                mission = await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                CreatedMissions.Add(mission);
                return mission;
            }

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
            {
                return Task.FromResult<Pipeline?>(null);
            }

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

            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
                => throw new NotImplementedException();
        }

        private sealed class RecordingCodeIndexService : ICodeIndexService
        {
            public List<ContextPackRequest> CacheRequests { get; } = new List<ContextPackRequest>();

            public ContextPackResponse? CachedResponse { get; set; }

            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStatus { VesselId = vesselId });

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task WarmBaselineCacheAsync(string vesselId, CancellationToken token = default)
                => Task.CompletedTask;

            public Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default)
            {
                CacheRequests.Add(request);
                return Task.FromResult(CachedResponse);
            }
        }
    }
}
