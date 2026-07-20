namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
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

            await RunTest("Parity_RestMappingAndMcpHandler_CreateFieldIdenticalMissions", async () =>
            {
                // Drive the SAME logical voyage through both real entry points -- the REST
                // mapping (VoyageRoutes.CreateDispatchRequest -> VoyageDispatchService) and the
                // registered MCP armada_dispatch handler -- against isolated databases, then
                // assert every per-mission dispatch field lands identically. This is the core
                // parity guarantee: REST and MCP must produce the same missions.
                using (TestDatabase restDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (TestDatabase mcpDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel restVessel = await restDb.Driver.Vessels.CreateAsync(
                        new Vessel("parity-rest-vessel", "https://github.com/test/repo.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);
                    Vessel mcpVessel = await mcpDb.Driver.Vessels.CreateAsync(
                        new Vessel("parity-mcp-vessel", "https://github.com/test/repo.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);

                    string prestagedSource = Path.Combine(Path.GetTempPath(), "parity-input.txt");

                    // --- REST entry point ---
                    VoyageRequest restRequest = new VoyageRequest
                    {
                        Title = "Parity voyage",
                        Description = "same logical request through both paths",
                        VesselId = restVessel.Id,
                        CodeContextMode = "off",
                        SelectedPlaybooks = new List<SelectedPlaybook>
                        {
                            new SelectedPlaybook { PlaybookId = "pbk_voyage", DeliveryMode = PlaybookDeliveryModeEnum.InstructionWithReference }
                        },
                        Missions = new List<MissionRequest>
                        {
                            new MissionRequest
                            {
                                Title = "alpha",
                                Description = "first task",
                                PreferredModel = "high",
                                PrestagedFiles = new List<PrestagedFile> { new PrestagedFile(prestagedSource, "notes/alpha.txt") },
                                SelectedPlaybooks = new List<SelectedPlaybook>
                                {
                                    new SelectedPlaybook { PlaybookId = "pbk_alpha", DeliveryMode = PlaybookDeliveryModeEnum.AttachIntoWorktree }
                                }
                            },
                            new MissionRequest
                            {
                                Title = "beta",
                                Description = "second task",
                                PreferredModel = "low",
                                DependsOnMissionId = "msn_existing_0001"
                            }
                        }
                    };

                    RecordingAdmiralService restAdmiral = new RecordingAdmiralService(restDb.Driver);
                    VoyageDispatchService restService = new VoyageDispatchService(restDb.Driver, restAdmiral, null, null, null, null);
                    VoyageDispatchResult restResult = await restService
                        .DispatchAsync(VoyageRoutes.CreateDispatchRequest(restRequest)).ConfigureAwait(false);
                    AssertTrue(restResult.Succeeded, "REST parity dispatch should succeed");

                    // --- MCP entry point ---
                    RecordingAdmiralService mcpAdmiral = new RecordingAdmiralService(mcpDb.Driver);
                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        mcpDb.Driver,
                        mcpAdmiral);
                    AssertNotNull(dispatchHandler, "armada_dispatch handler must be registered");

                    JsonElement mcpArgs = JsonSerializer.SerializeToElement(new
                    {
                        title = "Parity voyage",
                        description = "same logical request through both paths",
                        vesselId = mcpVessel.Id,
                        codeContextMode = "off",
                        selectedPlaybooks = new object[]
                        {
                            new { playbookId = "pbk_voyage", deliveryMode = "InstructionWithReference" }
                        },
                        missions = new object[]
                        {
                            new
                            {
                                title = "alpha",
                                description = "first task",
                                preferredModel = "high",
                                prestagedFiles = new object[]
                                {
                                    new { sourcePath = prestagedSource, destPath = "notes/alpha.txt" }
                                },
                                selectedPlaybooks = new object[]
                                {
                                    new { playbookId = "pbk_alpha", deliveryMode = "AttachIntoWorktree" }
                                }
                            },
                            new
                            {
                                title = "beta",
                                description = "second task",
                                preferredModel = "low",
                                dependsOnMissionId = "msn_existing_0001"
                            }
                        }
                    });

                    object mcpResponse = await dispatchHandler!(mcpArgs).ConfigureAwait(false);
                    AssertFalse(JsonSerializer.Serialize(mcpResponse).Contains("\"Error\""), "MCP parity dispatch should not error");

                    AssertMissionParity(restAdmiral.CreatedMissions, mcpAdmiral.CreatedMissions);
                }
            });

            await RunTest("Parity_InvalidVessel_RestMappingAndMcpHandler_ReturnIdenticalErrorPayload", async () =>
            {
                // A request both entry points reject (missing vessel) must yield the byte-identical
                // error payload, since both serialize the same shared-service result value.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    VoyageRequest restRequest = new VoyageRequest
                    {
                        Title = "missing vessel voyage",
                        Description = "no such vessel",
                        VesselId = "vsl_does_not_exist",
                        Missions = new List<MissionRequest> { new MissionRequest { Title = "t", Description = "d" } }
                    };

                    RecordingAdmiralService restAdmiral = new RecordingAdmiralService(testDb.Driver);
                    VoyageDispatchService restService = new VoyageDispatchService(testDb.Driver, restAdmiral, null, null, null, null);
                    VoyageDispatchResult restResult = await restService
                        .DispatchAsync(VoyageRoutes.CreateDispatchRequest(restRequest)).ConfigureAwait(false);
                    AssertFalse(restResult.Succeeded, "REST dispatch to a missing vessel should fail");
                    AssertEqual(404, restResult.StatusCode, "missing vessel should map to 404");

                    RecordingAdmiralService mcpAdmiral = new RecordingAdmiralService(testDb.Driver);
                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        mcpAdmiral);
                    AssertNotNull(dispatchHandler, "armada_dispatch handler must be registered");

                    JsonElement mcpArgs = JsonSerializer.SerializeToElement(new
                    {
                        title = "missing vessel voyage",
                        description = "no such vessel",
                        vesselId = "vsl_does_not_exist",
                        missions = new object[] { new { title = "t", description = "d" } }
                    });
                    object mcpResponse = await dispatchHandler!(mcpArgs).ConfigureAwait(false);

                    AssertEqual(
                        JsonSerializer.Serialize(restResult.Value),
                        JsonSerializer.Serialize(mcpResponse),
                        "REST and MCP must return identical error payloads for a missing vessel");
                    AssertEqual(0, mcpAdmiral.CreatedMissions.Count, "no missions should be created on a rejected dispatch");
                }
            });

            await RunTest("Validation_MissingTitle_Returns400BeforeTouchingDatabase", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "   ",
                        VesselId = "vsl_unread",
                        Missions = new List<MissionDescription> { new MissionDescription("t", "d") }
                    };
                    VoyageDispatchResult result = await NewService(testDb).DispatchAsync(request).ConfigureAwait(false);

                    AssertFalse(result.Succeeded, "whitespace title must be rejected");
                    AssertEqual(400, result.StatusCode);
                    AssertContains("missing_title", JsonSerializer.Serialize(result.Value));
                }
            });

            await RunTest("Validation_MissionMissingDescription_Returns400", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "valid",
                        VesselId = "vsl_unread",
                        Missions = new List<MissionDescription> { new MissionDescription("has title", "  ") }
                    };
                    VoyageDispatchResult result = await NewService(testDb).DispatchAsync(request).ConfigureAwait(false);

                    AssertFalse(result.Succeeded, "mission without a description must be rejected");
                    AssertEqual(400, result.StatusCode);
                    AssertContains("missing_mission_description", JsonSerializer.Serialize(result.Value));
                }
            });

            await RunTest("Validation_VesselNotFound_Returns404", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "valid",
                        VesselId = "vsl_ghost",
                        Missions = new List<MissionDescription> { new MissionDescription("t", "d") }
                    };
                    VoyageDispatchResult result = await NewService(testDb).DispatchAsync(request).ConfigureAwait(false);

                    AssertEqual(404, result.StatusCode);
                    AssertContains("vessel_not_found", JsonSerializer.Serialize(result.Value));
                }
            });

            await RunTest("Validation_InvalidCodeContextMode_Returns400", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("ctx-mode-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "valid",
                        VesselId = vessel.Id,
                        CodeContextMode = "banana",
                        Missions = new List<MissionDescription> { new MissionDescription("t", "d") }
                    };
                    VoyageDispatchResult result = await NewService(testDb).DispatchAsync(request).ConfigureAwait(false);

                    AssertEqual(400, result.StatusCode);
                    AssertContains("invalid codeContextMode", JsonSerializer.Serialize(result.Value));
                }
            });

            await RunTest("Validation_ForceCodeContext_WithoutIndexService_Returns400", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("force-ctx-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "valid",
                        VesselId = vessel.Id,
                        CodeContextMode = "force",
                        Missions = new List<MissionDescription> { new MissionDescription("t", "d") }
                    };
                    // No code index service supplied -> force cannot be satisfied.
                    VoyageDispatchResult result = await NewService(testDb).DispatchAsync(request).ConfigureAwait(false);

                    AssertEqual(400, result.StatusCode);
                    AssertContains("code index service is unavailable", JsonSerializer.Serialize(result.Value));
                }
            });

            await RunTest("Validation_PipelineNameNotFound_Returns400", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("pipeline-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "valid",
                        VesselId = vessel.Id,
                        CodeContextMode = "off",
                        Pipeline = "no-such-pipeline",
                        Missions = new List<MissionDescription> { new MissionDescription("t", "d") }
                    };
                    VoyageDispatchResult result = await NewService(testDb).DispatchAsync(request).ConfigureAwait(false);

                    AssertEqual(400, result.StatusCode);
                    AssertContains("pipeline_not_found", JsonSerializer.Serialize(result.Value));
                }
            });

            await RunTest("Validation_ObjectiveIdWithoutObjectiveService_Returns400", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("objective-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "valid",
                        VesselId = vessel.Id,
                        CodeContextMode = "off",
                        ObjectiveId = "obj_orphan",
                        Missions = new List<MissionDescription> { new MissionDescription("t", "d") }
                    };
                    // Service constructed without an ObjectiveService -> link cannot be honored.
                    VoyageDispatchResult result = await NewService(testDb).DispatchAsync(request).ConfigureAwait(false);

                    AssertEqual(400, result.StatusCode);
                    AssertContains("Objective service unavailable", JsonSerializer.Serialize(result.Value));
                }
            });

            await RunTest("AliasDependency_ResolvesToConcreteMissionId_ThroughSharedPath", async () =>
            {
                // The alias-aware branch is shared by REST and MCP. A dependsOnMissionAlias must be
                // rewritten to the concrete msn_* id of the dependency once it is created.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("alias-vessel", "https://github.com/test/repo.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);

                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "alias voyage",
                        VesselId = vessel.Id,
                        CodeContextMode = "off",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("dependency", "runs first") { Alias = "dep" },
                            new MissionDescription("dependent", "waits on dep") { DependsOnMissionAlias = "dep" }
                        }
                    };
                    VoyageDispatchService service = new VoyageDispatchService(testDb.Driver, admiral, null, null, null, null);
                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                    AssertTrue(result.Succeeded, "alias dispatch should succeed");
                    AssertEqual(2, admiral.CreatedMissions.Count, "both missions should be created");

                    Mission dependency = admiral.CreatedMissions.Single(m => m.Title == "dependency");
                    Mission dependent = admiral.CreatedMissions.Single(m => m.Title == "dependent");
                    AssertNull(dependency.DependsOnMissionId, "the dependency mission has no upstream dependency");
                    AssertEqual(dependency.Id, dependent.DependsOnMissionId,
                        "dependsOnMissionAlias must resolve to the concrete dependency mission id");
                }
            });

            await RunTest("AutoMode_CacheMiss_BuildsAndAttachesContextPack", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("auto-ctx-vessel", "https://github.com/test/repo.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);

                    string packSource = Path.Combine(Path.GetTempPath(), "auto-ctx-pack-" + Guid.NewGuid().ToString("N") + ".md");
                    File.WriteAllText(packSource, "# context pack");

                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService
                    {
                        CachedResponse = null,
                        BuildResponse = new ContextPackResponse()
                    };
                    codeIndex.BuildResponse.PrestagedFiles.Add(new PrestagedFile(packSource, "_briefing/context-pack.md"));

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "auto context voyage",
                        VesselId = vessel.Id,
                        CodeContextMode = "auto",
                        Missions = new List<MissionDescription> { new MissionDescription("auto worker", "fix something") }
                    };

                    VoyageDispatchService service = new VoyageDispatchService(testDb.Driver, admiral, null, codeIndex, null, null);
                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                    AssertTrue(result.Succeeded, "auto dispatch should succeed");
                    AssertEqual(1, admiral.CreatedMissions.Count, "one mission should be created");

                    Mission mission = await WaitForMissionPrestagedAsync(testDb.Driver, admiral.CreatedMissions[0].Id).ConfigureAwait(false);
                    AssertEqual(1, codeIndex.BuildRequests.Count, "auto mode with cache miss must build a context pack");
                    AssertNotNull(mission.PrestagedFiles, "mission should carry the generated context pack");
                    AssertEqual(1, mission.PrestagedFiles!.Count);
                    AssertEqual("_briefing/context-pack.md", mission.PrestagedFiles[0].DestPath);
                    AssertEqual(packSource, mission.PrestagedFiles[0].SourcePath);
                }
            });

            await RunTest("AutoMode_BuildReturnsEmpty_LogsWarning", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("auto-ctx-warn-vessel", "https://github.com/test/repo.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);

                    string logPath = Path.Combine(Path.GetTempPath(), "auto-ctx-warn-" + Guid.NewGuid().ToString("N") + ".log");
                    using (LoggingModule logging = new LoggingModule(logPath, FileLoggingMode.SingleLogFile, false))
                    {
                        RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                        RecordingCodeIndexService codeIndex = new RecordingCodeIndexService
                        {
                            CachedResponse = null,
                            BuildResponse = new ContextPackResponse()
                        };

                        SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                        {
                            Title = "auto context warning voyage",
                            VesselId = vessel.Id,
                            CodeContextMode = "auto",
                            Missions = new List<MissionDescription> { new MissionDescription("auto worker", "fix something") }
                        };

                        // Legacy best-effort warn-and-continue only applies when the pack is not required.
                        ArmadaSettings legacySettings = new ArmadaSettings();
                        legacySettings.CodeIndex.RequireContextPackWhenEnabled = false;

                        VoyageDispatchService service = new VoyageDispatchService(testDb.Driver, admiral, logging, codeIndex, null, legacySettings);
                        VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                        AssertTrue(result.Succeeded, "auto dispatch with empty pack should still succeed");
                        await Task.Delay(200).ConfigureAwait(false);
                        await logging.FlushAsync().ConfigureAwait(false);
                    }

                    string logContent = File.ReadAllText(logPath);
                    AssertTrue(logContent.Contains("yielded no pack"), "expected structured warning when auto context pack is empty; log was: " + logContent);
                }
            });

            await RunTest("AutoMode_PipelineWorkerStage_AttachesContextPack", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("auto-pipeline-vessel", "https://github.com/test/repo.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);

                    Pipeline pipeline = new Pipeline("Reviewed");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge")
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    string packSource = Path.Combine(Path.GetTempPath(), "auto-pipeline-pack-" + Guid.NewGuid().ToString("N") + ".md");
                    File.WriteAllText(packSource, "# context pack");

                    PipelinePersistingAdmiralService admiral = new PipelinePersistingAdmiralService(testDb.Driver, pipeline);
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService
                    {
                        CachedResponse = null,
                        BuildResponse = new ContextPackResponse()
                    };
                    codeIndex.BuildResponse.PrestagedFiles.Add(new PrestagedFile(packSource, "_briefing/context-pack.md"));

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "auto pipeline context voyage",
                        VesselId = vessel.Id,
                        PipelineId = pipeline.Id,
                        CodeContextMode = "auto",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("pipeline feature", "fix it") { Alias = "M1" }
                        }
                    };

                    VoyageDispatchService service = new VoyageDispatchService(testDb.Driver, admiral, null, codeIndex, null, null);
                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                    AssertTrue(result.Succeeded, "alias+pipeline dispatch should succeed");

                    List<Mission> all = await WaitForVoyageMissionsAsync(testDb.Driver, result.Voyage!.Id, 2).ConfigureAwait(false);
                    Mission worker = all.Single(m => m.Persona == "Worker");
                    Mission judge = all.Single(m => m.Persona == "Judge");

                    worker = await WaitForMissionPrestagedAsync(testDb.Driver, worker.Id).ConfigureAwait(false);
                    AssertEqual(1, codeIndex.BuildRequests.Count, "auto mode with cache miss must build a context pack");
                    AssertNotNull(worker.PrestagedFiles, "Worker stage should carry the context pack");
                    AssertTrue(worker.PrestagedFiles!.Any(p => p.DestPath == "_briefing/context-pack.md"), "Worker stage should have the context pack staged");
                    AssertNull(judge.PrestagedFiles, "Judge stage should not inherit the full prestaged set");
                }
            });

            await RunTest("AutoDispatch_SlowPackBuild_CreatesVoyageBeforePackCompletes", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("slow-pack-vessel", "https://github.com/test/repo.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);

                    SlowCodeIndexService codeIndex = new SlowCodeIndexService();
                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);

                    // Deferred (non-blocking) pack build is the legacy path, gated off the required-pack flag.
                    ArmadaSettings legacySettings = new ArmadaSettings();
                    legacySettings.CodeIndex.RequireContextPackWhenEnabled = false;

                    VoyageDispatchService service = new VoyageDispatchService(testDb.Driver, admiral, null, codeIndex, null, legacySettings);

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "slow pack voyage",
                        Description = "auto dispatch should return before pack build finishes",
                        VesselId = vessel.Id,
                        CodeContextMode = "auto",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("slow mission", "build me a pack")
                        }
                    };

                    Task<VoyageDispatchResult> dispatchTask = service.DispatchAsync(request);
                    await codeIndex.BuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                    VoyageDispatchResult result = await dispatchTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    AssertTrue(result.Succeeded, "dispatch should succeed while pack build is still running: " + JsonSerializer.Serialize(result.Value));

                    Voyage? voyage = await testDb.Driver.Voyages.ReadAsync(result.Voyage!.Id).ConfigureAwait(false);
                    AssertNotNull(voyage, "voyage should be persisted before the pack build completes");

                    codeIndex.ReleaseBuild.SetResult(true);
                    await codeIndex.BuildCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                    Mission mission = await WaitForMissionPrestagedAsync(testDb.Driver, admiral.CreatedMissions[0].Id).ConfigureAwait(false);
                    AssertNotNull(mission.PrestagedFiles, "deferred pack should be attached to the pending mission");
                    AssertEqual(1, mission.PrestagedFiles!.Count);
                    AssertEqual("_briefing/context-pack.md", mission.PrestagedFiles[0].DestPath);
                }
            });

            await RunTest("ValidatePreconditions_RejectsBadRequestsSoBackgroundDispatchStillFailsFast", async () =>
            {
                // armada_dispatch hands the expensive tail to a background job. That is only safe if a
                // bad request is still rejected SYNCHRONOUSLY with its specific code -- otherwise a
                // typo'd vesselId would be accepted as a job the caller must poll to discover the
                // mistake. These assertions pin that contract.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    VoyageDispatchService service = NewService(testDb);

                    VoyageDispatchResult? missingTitle = await service.ValidatePreconditionsAsync(new SharedVoyageDispatchRequest
                    {
                        Title = "",
                        VesselId = "vsl_whatever",
                        CodeContextMode = "off",
                        Missions = new List<MissionDescription> { new MissionDescription("t", "d") }
                    }).ConfigureAwait(false);
                    AssertNotNull(missingTitle, "an empty title must be rejected before any work is scheduled");
                    AssertEqual(400, missingTitle!.StatusCode);

                    VoyageDispatchResult? missingVessel = await service.ValidatePreconditionsAsync(new SharedVoyageDispatchRequest
                    {
                        Title = "valid",
                        VesselId = "vsl_does_not_exist",
                        CodeContextMode = "off",
                        Missions = new List<MissionDescription> { new MissionDescription("t", "d") }
                    }).ConfigureAwait(false);
                    AssertNotNull(missingVessel, "an unknown vessel must be rejected before any work is scheduled");
                    AssertEqual(404, missingVessel!.StatusCode);
                    AssertContains("vessel_not_found", JsonSerializer.Serialize(missingVessel.Value),
                        "the rejection must keep its specific code rather than degrade to a generic error");

                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("preflight-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    VoyageDispatchResult? dispatchable = await service.ValidatePreconditionsAsync(new SharedVoyageDispatchRequest
                    {
                        Title = "valid",
                        VesselId = vessel.Id,
                        CodeContextMode = "off",
                        Missions = new List<MissionDescription> { new MissionDescription("t", "d") }
                    }).ConfigureAwait(false);
                    AssertNull(dispatchable, "a dispatchable request must pass preconditions so it can be backgrounded");
                }
            });
        }

        private static VoyageDispatchService NewService(TestDatabase testDb)
        {
            return new VoyageDispatchService(
                testDb.Driver,
                new RecordingAdmiralService(testDb.Driver),
                null,
                null,
                null,
                null);
        }

        private static async Task<Mission> WaitForMissionPrestagedAsync(DatabaseDriver database, string missionId)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                Mission? mission = await database.Missions.ReadAsync(missionId).ConfigureAwait(false);
                if (mission != null && mission.PrestagedFiles != null && mission.PrestagedFiles.Count > 0)
                    return mission;

                await Task.Delay(50).ConfigureAwait(false);
            }

            Mission? final = await database.Missions.ReadAsync(missionId).ConfigureAwait(false);
            return final ?? throw new TimeoutException("Mission " + missionId + " was not prestaged in time");
        }

        private static async Task<List<Mission>> WaitForVoyageMissionsAsync(DatabaseDriver database, string voyageId, int expectedCount)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                List<Mission> missions = await database.Missions.EnumerateByVoyageAsync(voyageId).ConfigureAwait(false);
                if (missions.Count >= expectedCount)
                    return missions;

                await Task.Delay(50).ConfigureAwait(false);
            }

            return await database.Missions.EnumerateByVoyageAsync(voyageId).ConfigureAwait(false);
        }

        private void AssertMissionParity(List<Mission> rest, List<Mission> mcp)
        {
            AssertEqual(rest.Count, mcp.Count, "mission count parity");
            for (int i = 0; i < rest.Count; i++)
            {
                Mission r = rest[i];
                Mission m = mcp[i];
                AssertEqual(r.Title, m.Title, "title parity #" + i);
                AssertEqual(r.Description, m.Description, "description parity #" + i);
                AssertEqual(r.PreferredModel, m.PreferredModel, "preferredModel parity #" + i);
                AssertEqual(r.DependsOnMissionId, m.DependsOnMissionId, "dependsOnMissionId parity #" + i);

                int restPrestaged = r.PrestagedFiles?.Count ?? 0;
                int mcpPrestaged = m.PrestagedFiles?.Count ?? 0;
                AssertEqual(restPrestaged, mcpPrestaged, "prestaged count parity #" + i);
                for (int j = 0; j < restPrestaged; j++)
                {
                    AssertEqual(r.PrestagedFiles![j].DestPath, m.PrestagedFiles![j].DestPath, "prestaged destPath parity #" + i + "." + j);
                    AssertEqual(r.PrestagedFiles![j].SourcePath, m.PrestagedFiles![j].SourcePath, "prestaged sourcePath parity #" + i + "." + j);
                }

                int restPlaybooks = r.SelectedPlaybooks?.Count ?? 0;
                int mcpPlaybooks = m.SelectedPlaybooks?.Count ?? 0;
                AssertEqual(restPlaybooks, mcpPlaybooks, "playbook count parity #" + i);
                for (int j = 0; j < restPlaybooks; j++)
                {
                    AssertEqual(r.SelectedPlaybooks![j].PlaybookId, m.SelectedPlaybooks![j].PlaybookId, "playbook id parity #" + i + "." + j);
                    AssertEqual(r.SelectedPlaybooks![j].DeliveryMode, m.SelectedPlaybooks![j].DeliveryMode, "playbook deliveryMode parity #" + i + "." + j);
                }
            }
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

        private sealed class PipelinePersistingAdmiralService : IAdmiralService
        {
            private readonly DatabaseDriver _Database;
            private readonly Pipeline? _Pipeline;

            public PipelinePersistingAdmiralService(DatabaseDriver database, Pipeline? pipeline)
            {
                _Database = database;
                _Pipeline = pipeline;
            }

            public List<Mission> CreatedMissions { get; } = new List<Mission>();

            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public async Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
            {
                mission = await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                CreatedMissions.Add(mission);
                return mission;
            }

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
            {
                return Task.FromResult<Pipeline?>(_Pipeline);
            }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
                => throw new NotImplementedException();
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
                => throw new NotImplementedException();
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();
            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task RecallCaptainAsync(string captainId, CancellationToken token = default) => throw new NotImplementedException();
            public Task RecallAllAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task HealthCheckAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task CleanupStaleCaptainsAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
                => throw new NotImplementedException();
        }

        private sealed class RecordingCodeIndexService : ICodeIndexService
        {
            public List<ContextPackRequest> CacheRequests { get; } = new List<ContextPackRequest>();
            public List<ContextPackRequest> BuildRequests { get; } = new List<ContextPackRequest>();

            public ContextPackResponse? CachedResponse { get; set; }
            public ContextPackResponse BuildResponse { get; set; } = new ContextPackResponse();

            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStatus { VesselId = vesselId });

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
            {
                BuildRequests.Add(request);
                return Task.FromResult(BuildResponse);
            }

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

        private sealed class SlowCodeIndexService : ICodeIndexService
        {
            public TaskCompletionSource<bool> BuildStarted { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> ReleaseBuild { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> BuildCompleted { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public List<ContextPackRequest> BuildRequests { get; } = new List<ContextPackRequest>();

            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStatus { VesselId = vesselId });

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public async Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
            {
                BuildRequests.Add(request);
                BuildStarted.TrySetResult(true);
                await ReleaseBuild.Task.ConfigureAwait(false);
                BuildCompleted.TrySetResult(true);
                return new ContextPackResponse
                {
                    PrestagedFiles = new List<PrestagedFile>
                    {
                        new PrestagedFile(Path.Combine(Path.GetTempPath(), "deferred-context-pack.md"), "_briefing/context-pack.md")
                    }
                };
            }

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
                => Task.FromResult<ContextPackResponse?>(null);
        }
    }
}
