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
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Server.Routes;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using FleetRoutingSettings = global::Test.Shared.Infrastructure.FleetRoutingSettings;

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
            await RunTest("ValidatePreconditions_UsesSharedObjectiveDispatchPreview", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                        "preview-gated-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "Preview-gated objective",
                        VesselIds = new List<string> { vessel.Id }
                    }).ConfigureAwait(false);
                    ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                    RecordingObjectiveDispatchPreview preview = new RecordingObjectiveDispatchPreview
                    {
                        Result = new ObjectiveDispatchPreview
                        {
                            ObjectiveId = objective.Id,
                            VesselId = vessel.Id,
                            IsReady = false,
                            Issues = new List<ObjectiveDispatchPreviewIssue>
                            {
                                new ObjectiveDispatchPreviewIssue
                                {
                                    Code = "brief_acceptance_missing",
                                    Area = "brief",
                                    Severity = ReadinessSeverityEnum.Error,
                                    Message = "Acceptance is missing."
                                }
                            }
                        }
                    };
                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver,
                        new RecordingAdmiralService(testDb.Driver),
                        objectiveService: objectives,
                        settings: new ArmadaSettings { CodeIndex = { Enabled = false } },
                        objectiveDispatchPreview: preview);

                    VoyageDispatchResult? invalid = await service.ValidatePreconditionsAsync(new SharedVoyageDispatchRequest
                    {
                        Title = "blocked by preview",
                        VesselId = vessel.Id,
                        ObjectiveId = objective.Id,
                        Pipeline = "Reviewed",
                        CaptainAssignments = new List<CaptainAssignmentOverride>
                        {
                            new CaptainAssignmentOverride("Worker", "cpt_requested", CaptainTierEnum.Standard)
                        },
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("Do not dispatch", "The preview blocks this work.")
                        }
                    }).ConfigureAwait(false);

                    AssertNotNull(invalid, "The shared preview must block an unready objective dispatch.");
                    AssertEqual(400, invalid!.StatusCode);
                    AssertContains("objective_dispatch_not_ready", JsonSerializer.Serialize(invalid.Value));
                    AssertEqual(1, preview.CallCount, "The operator precondition path calls the preview once.");
                    AssertEqual(vessel.Id, preview.RequestedVesselId);
                    AssertEqual("Reviewed", preview.RequestedPipelineId);
                    AssertEqual("cpt_requested", preview.CaptainAssignments!.Single().CaptainId);
                }
            });

            await RunTest("DispatchAsync_UsesObjectiveSuggestedPipelineForPreviewAndExecution", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                        "objective-pipeline-vessel", "https://github.com/test/repo.git")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId
                    }).ConfigureAwait(false);
                    await testDb.Driver.Pipelines.CreateAsync(new Pipeline("Objective pipeline")
                    {
                        Id = "pln_objective",
                        TenantId = Constants.DefaultTenantId
                    }).ConfigureAwait(false);
                    Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Title = "Objective pipeline",
                        VesselIds = new List<string> { vessel.Id },
                        SuggestedPipelineId = "pln_objective"
                    }).ConfigureAwait(false);
                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    RecordingObjectiveDispatchPreview preview = new RecordingObjectiveDispatchPreview();
                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver,
                        admiral,
                        objectiveService: new ObjectiveService(testDb.Driver),
                        settings: new ArmadaSettings { CodeIndex = { Enabled = false } },
                        objectiveDispatchPreview: preview);

                    VoyageDispatchResult result = await service.DispatchAsync(new SharedVoyageDispatchRequest
                    {
                        Title = "Use objective pipeline",
                        VesselId = vessel.Id,
                        ObjectiveId = objective.Id,
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("Implement", "Use the selected pipeline.")
                        }
                    }).ConfigureAwait(false);

                    AssertTrue(result.Succeeded, "Dispatch failed: " + JsonSerializer.Serialize(result.Value));
                    AssertEqual("pln_objective", preview.RequestedPipelineId);
                    AssertEqual("pln_objective", admiral.LastPipelineId);
                }
            });

            await RunTest("ValidatePreconditions_AllEffectiveModesOff_SkipsCodeIndexStatus", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("off-mode-vessel", "https://github.com/test/repo.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);

                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService();
                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver,
                        new RecordingAdmiralService(testDb.Driver),
                        null,
                        codeIndex,
                        null,
                        null);
                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "off-mode dispatch",
                        VesselId = vessel.Id,
                        CodeContextMode = "off",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("inherits off", "must not probe the index"),
                            new MissionDescription("explicit off", "must not probe the index")
                            {
                                CodeContextMode = "OFF"
                            }
                        }
                    };

                    VoyageDispatchResult? invalid = await service.ValidatePreconditionsAsync(request)
                        .WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

                    AssertNull(invalid, "an all-off dispatch should pass preconditions");
                    AssertEqual(0, codeIndex.StatusRequests.Count,
                        "all-off dispatch must not call the code-index status dependency");
                }
            });

            await RunTest("ValidatePreconditions_MissionAutoOverride_StillChecksCodeIndexStatus", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("mixed-mode-vessel", "https://github.com/test/repo.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);

                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService();
                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver,
                        new RecordingAdmiralService(testDb.Driver),
                        null,
                        codeIndex,
                        null,
                        null);
                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "mixed-mode dispatch",
                        VesselId = vessel.Id,
                        CodeContextMode = "off",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("auto override", "must retain the staleness guard")
                            {
                                CodeContextMode = "auto"
                            }
                        }
                    };

                    VoyageDispatchResult? invalid = await service.ValidatePreconditionsAsync(request)
                        .ConfigureAwait(false);

                    AssertNull(invalid, "a healthy mixed-mode dispatch should pass preconditions");
                    AssertEqual(1, codeIndex.StatusRequests.Count,
                        "an auto mission override must retain the code-index status guard");
                }
            });

            await RunTest("Dispatch_CodeIndexDisabled_SkipsAllIndexWorkAndCreatesMissions", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("disabled-index-vessel", "https://github.com/test/repo.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.CodeIndex.Enabled = false;
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService();
                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver,
                        new RecordingAdmiralService(testDb.Driver),
                        null,
                        codeIndex,
                        null,
                        settings);
                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "disabled index dispatch",
                        VesselId = vessel.Id,
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("default auto mission", "must dispatch without index work")
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request)
                        .WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

                    AssertEqual(201, result.StatusCode, "disabled indexing must not block dispatch");
                    AssertEqual(0, codeIndex.StatusRequests.Count, "disabled indexing must not probe status");
                    AssertEqual(0, codeIndex.CacheRequests.Count, "disabled indexing must not probe context caches");
                    AssertEqual(0, codeIndex.BuildRequests.Count, "disabled indexing must not build context packs");
                    List<Mission> created = await testDb.Driver.Missions.EnumerateAsync().ConfigureAwait(false);
                    AssertEqual(1, created.Count, "dispatch should create the requested mission");
                }
            });

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

            await RunTest("Validation_MissionTitleWithStagePersonaPrefix_Returns400", async () =>
            {
                // A dispatch whose mission title already carries a "[<persona>] " tag is a prior
                // run's materialized STAGE mission resubmitted as a task. The pipeline prepends the
                // persona itself, so expanding it multiplies the work by the stage count (3 -> 9 ->
                // 27). Validation must reject it before any voyage row exists.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("persona-prefix-vessel", "https://github.com/test/repo.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);

                    // Every persona spelling the pipeline prepends is rejected as a task title.
                    List<string> taggedTitles = new List<string>
                    {
                        "[Worker] Fix the queue naming",
                        "[Worker] [Worker] Fix the queue naming",
                        "[TestEngineer] Fix the queue naming",
                        "[Test Engineer] Fix the queue naming",
                        "[Judge] Fix the queue naming",
                        "[Architect] Fix the queue naming",
                        "[Product Manager] Fix the queue naming"
                    };
                    foreach (string tagged in taggedTitles)
                    {
                        SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                        {
                            Title = "persona-prefix voyage",
                            VesselId = vessel.Id,
                            CodeContextMode = "off",
                            Missions = new List<MissionDescription>
                            {
                                new MissionDescription(tagged, "a prior run's stage mission resubmitted as a task")
                            }
                        };
                        VoyageDispatchResult result = await NewService(testDb, FleetRoutingSettings.CreateArmadaSettings()).DispatchAsync(request).ConfigureAwait(false);
                        AssertEqual(400, result.StatusCode, "a '" + tagged + "' title must be rejected before any voyage is created");
                        AssertContains("mission_title_carries_stage_persona_prefix", JsonSerializer.Serialize(result.Value),
                            "the rejection must name the stage-persona-prefix code for '" + tagged + "'");
                    }

                    // A normal task title dispatches.
                    SharedVoyageDispatchRequest ok = new SharedVoyageDispatchRequest
                    {
                        Title = "normal voyage",
                        VesselId = vessel.Id,
                        CodeContextMode = "off",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("Fix the queue naming prefix divergence", "the real task")
                        }
                    };
                    VoyageDispatchResult okResult = await NewService(testDb, FleetRoutingSettings.CreateArmadaSettings()).DispatchAsync(ok).ConfigureAwait(false);
                    AssertTrue(okResult.Succeeded, "a normal task title must dispatch");

                    // A bracket tag that is NOT a persona must not trip the guard (no false positive).
                    SharedVoyageDispatchRequest bracket = new SharedVoyageDispatchRequest
                    {
                        Title = "bracket voyage",
                        VesselId = vessel.Id,
                        CodeContextMode = "off",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("[URGENT] Fix the queue naming", "a non-persona bracket tag")
                        }
                    };
                    VoyageDispatchResult bracketResult = await NewService(testDb, FleetRoutingSettings.CreateArmadaSettings()).DispatchAsync(bracket).ConfigureAwait(false);
                    AssertTrue(bracketResult.Succeeded, "a non-persona bracket prefix must NOT be rejected");
                }
            });

            await RunTest("Validation_MissionTitleWithStagePersonaPrefix_DefaultSettingsAllows", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("vanilla-prefix-vessel", "https://github.com/test/repo.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "vanilla prefix voyage",
                        VesselId = vessel.Id,
                        CodeContextMode = "off",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("[Worker] Fix the queue naming", "vanilla defaults must not reject a stage-persona prefix")
                        }
                    };
                    VoyageDispatchResult result = await NewService(testDb).DispatchAsync(request).ConfigureAwait(false);
                    AssertTrue(result.Succeeded, "product defaults leave the stage-persona title guard off");
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
                    AssertTrue(logContent.Contains("generation returned no prestaged files"), "expected warning when auto context pack is empty; log was: " + logContent);
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
                    AssertNotNull(judge.PrestagedFiles, "Every pipeline stage must carry the prestaged context files");
                    AssertTrue(judge.PrestagedFiles!.Any(p => p.DestPath == "_briefing/context-pack.md"),
                        "Judge stage must receive the context pack in its own dock");
                }
            });

            await RunTest("AutoDispatch_SlowPackBuild_CompletesBeforeVoyageCreation", async () =>
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

                    // Context files must be ready before any mission can claim a dock, even when
                    // the legacy setting allows dispatch to continue without a pack.
                    ArmadaSettings legacySettings = new ArmadaSettings();
                    legacySettings.CodeIndex.RequireContextPackWhenEnabled = false;

                    VoyageDispatchService service = new VoyageDispatchService(testDb.Driver, admiral, null, codeIndex, null, legacySettings);

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "slow pack voyage",
                        Description = "auto dispatch must not race pack staging",
                        VesselId = vessel.Id,
                        CodeContextMode = "auto",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("slow mission", "build me a pack")
                        }
                    };

                    Task<VoyageDispatchResult> dispatchTask = service.DispatchAsync(request);
                    await codeIndex.BuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                    await Task.Delay(100).ConfigureAwait(false);
                    AssertFalse(dispatchTask.IsCompleted, "dispatch must wait for the required staging input");

                    codeIndex.ReleaseBuild.SetResult(true);
                    await codeIndex.BuildCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                    VoyageDispatchResult result = await dispatchTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    AssertTrue(result.Succeeded, "dispatch should succeed after pack staging: " + JsonSerializer.Serialize(result.Value));

                    Mission mission = await WaitForMissionPrestagedAsync(testDb.Driver, admiral.CreatedMissions[0].Id).ConfigureAwait(false);
                    AssertNotNull(mission.PrestagedFiles, "pack should be attached before the mission is assigned");
                    AssertEqual(1, mission.PrestagedFiles!.Count);
                    AssertEqual("_briefing/context-pack.md", mission.PrestagedFiles[0].DestPath);
                }
            });

            await RunTest("Inherited high tier on Worker blocks routing until persona aware cap", () =>
            {
                ModelTierSettings fleet = FleetRoutingSettings.CreateModelTier();
                Captain captain = new Captain("mid-tier-worker");
                captain.Model = "gpt-5.6-luna";
                captain.State = CaptainStateEnum.Idle;

                string? enforced = PreferredModelTierSelector.EnforceHighTierForPersona(
                    "high",
                    "Worker",
                    fleet.SpecialistPersonas);
                AssertEqual("high", enforced, "the enforce-only path keeps high on Worker and reproduces the failure mode");
                AssertFalse(
                    MissionService.CaptainSatisfiesPreferredRouting(captain, "Worker", enforced, fleet),
                    "high on Worker leaves an idle mid-tier roster with zero eligible captains");

                string? resolved = PreferredModelTierSelector.ResolveEffectivePreferredModel(
                    null,
                    "high",
                    "Worker",
                    fleet.SpecialistPersonas);
                AssertEqual("mid", resolved, "the persona-aware resolver caps inherited high to mid on Worker");
                AssertTrue(
                    MissionService.CaptainSatisfiesPreferredRouting(captain, "Worker", resolved, fleet),
                    "the cap restores assignable coverage on the same idle roster");
                return Task.CompletedTask;
            });

            await RunTest("Pipeline dispatch caps mission high tier to mid on Worker stages", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("high-tier-cap-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Pipeline pipeline = new Pipeline("ReviewedHighCap");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge")
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    PipelinePersistingAdmiralService admiral = new PipelinePersistingAdmiralService(testDb.Driver, pipeline);
                    ArmadaSettings settings = FleetRoutingSettings.CreateArmadaSettings();
                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver, admiral, null, null, null, settings);

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "High tier cap voyage",
                        VesselId = vessel.Id,
                        PipelineId = pipeline.Id,
                        CodeContextMode = "off",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("Implement feature", "Worker should inherit mid, Judge stays high")
                            {
                                PreferredModel = "high",
                                Alias = "M1"
                            }
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);
                    AssertTrue(result.Succeeded, "pipeline dispatch should succeed");

                    List<Mission> missions = await WaitForVoyageMissionsAsync(testDb.Driver, result.Voyage!.Id, 2).ConfigureAwait(false);
                    Mission worker = missions.Single(m => m.Persona == "Worker");
                    Mission judge = missions.Single(m => m.Persona == "Judge");

                    AssertEqual("mid", worker.PreferredModel,
                        "a mission-level high tier inherited by a Worker stage must cap to mid so assignment can proceed");
                    AssertEqual("high", judge.PreferredModel,
                        "a Judge stage must still persist high when the mission requests high");
                }
            });

            await RunTest("Pipeline dispatch avoids WaitingForIdleCaptain for inherited high on Worker", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArmadaSettings settings = FleetRoutingSettings.CreateArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    StubGitService git = new StubGitService();

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService);
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                    AdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService);
                    admiral.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);

                    Vessel vessel = new Vessel("high-tier-assign-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    for (int index = 0; index < 3; index++)
                    {
                        Captain captain = new Captain("mid-worker-" + index);
                        captain.Model = "gpt-5.6-luna";
                        captain.State = CaptainStateEnum.Idle;
                        await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    }

                    Pipeline pipeline = new Pipeline("ReviewedHighAssign");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge")
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver, admiral, logging, null, null, settings);

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "High tier assign voyage",
                        VesselId = vessel.Id,
                        PipelineId = pipeline.Id,
                        CodeContextMode = "off",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("Implement feature", "Idle mid-tier workers must receive the Worker stage")
                            {
                                PreferredModel = "high"
                            }
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);
                    AssertTrue(result.Succeeded, "pipeline dispatch should succeed");

                    List<Mission> missions = await WaitForVoyageMissionsAsync(testDb.Driver, result.Voyage!.Id, 2).ConfigureAwait(false);
                    Mission worker = missions.Single(m => m.Persona == "Worker");
                    AssertEqual("mid", worker.PreferredModel,
                        "Worker stage must cap inherited high to mid before assignment runs");

                    Mission? refreshedWorker = null;
                    DateTime assignmentDeadline = DateTime.UtcNow.AddSeconds(5);
                    while (DateTime.UtcNow < assignmentDeadline)
                    {
                        refreshedWorker = await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false);
                        if (refreshedWorker?.CaptainId != null
                            && (refreshedWorker.Status == MissionStatusEnum.Assigned
                                || refreshedWorker.Status == MissionStatusEnum.InProgress))
                            break;
                        await Task.Delay(25).ConfigureAwait(false);
                    }
                    AssertNotNull(refreshedWorker, "Worker mission must remain readable after dispatch");
                    AssertNotNull(refreshedWorker!.CaptainId,
                        "an idle mid-tier captain must be assigned after the Worker preference is capped to mid");
                    AssertTrue(
                        refreshedWorker.Status == MissionStatusEnum.Assigned
                            || refreshedWorker.Status == MissionStatusEnum.InProgress,
                        "the Worker must reach Assigned or InProgress instead of only avoiding WaitingForIdleCaptain");
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

            await RunTest("DispatchAsync_ObjectiveAlreadyHasActiveVoyage_ReturnsConflictBeforeCreatingVoyage", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("already-dispatched-vessel", "https://github.com/test/already.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                Voyage winner = await testDb.Driver.Voyages.CreateAsync(new Voyage("Winning voyage")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.InProgress
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Already dispatched",
                    Status = ObjectiveStatusEnum.InProgress,
                    VesselIds = new List<string> { vessel.Id },
                    VoyageIds = new List<string> { winner.Id }
                }).ConfigureAwait(false);

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                VoyageDispatchService service = new VoyageDispatchService(
                    testDb.Driver,
                    admiral,
                    objectiveService: objectives,
                    settings: new ArmadaSettings { CodeIndex = { Enabled = false } });

                VoyageDispatchResult result = await service.DispatchAsync(new SharedVoyageDispatchRequest
                {
                    Title = "Operator duplicate",
                    VesselId = vessel.Id,
                    ObjectiveId = objective.Id,
                    Missions = new List<MissionDescription>
                    {
                        new MissionDescription("Implement", "Duplicate work.")
                    }
                }).ConfigureAwait(false);

                AssertFalse(result.Succeeded, "The duplicate operator dispatch must not report success.");
                AssertEqual(409, result.StatusCode, "The already-dispatched outcome must surface as a conflict.");
                string payload = JsonSerializer.Serialize(result.Value);
                AssertContains("objective_already_dispatched", payload);
                AssertContains(winner.Id, payload, "The response must identify the existing winning voyage.");
                AssertFalse(admiral.DispatchVoyageCalled,
                    "Admission must reject the duplicate before Admiral creates a voyage.");

                Objective stored = (await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false))!;
                AssertEqual(1, stored.VoyageIds.Count, "The duplicate must not be linked to the objective.");

                List<Voyage> allVoyages = await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false);
                AssertEqual(1, allVoyages.Count,
                    "Admission must leave only the existing winner; it must not create a duplicate voyage.");
                AssertEqual(winner.Id, allVoyages[0].Id);
            });

            await RunTest("DispatchAsync_BusyObjectiveAdmission_ReturnsRetryableConflictWithoutCreatingVoyage", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("busy-admission-vessel", "https://github.com/test/busy.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Busy admission",
                    Status = ObjectiveStatusEnum.Scoped,
                    VesselIds = new List<string> { vessel.Id }
                }).ConfigureAwait(false);
                AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                VoyageDispatchService service = new VoyageDispatchService(
                    testDb.Driver,
                    admiral,
                    objectiveService: new ObjectiveService(testDb.Driver, dispatchAdmissionWait: TimeSpan.FromMilliseconds(200)),
                    settings: new ArmadaSettings { CodeIndex = { Enabled = false } });

                await using (ObjectiveDispatchAdmission held = await new ObjectiveService(testDb.Driver)
                    .AcquireDispatchAdmissionAsync(auth, objective.Id).ConfigureAwait(false))
                {
                    using (CancellationTokenSource cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    {
                        VoyageDispatchResult? result = null;
                        Exception? failure = null;
                        try
                        {
                            result = await service.DispatchAsync(new SharedVoyageDispatchRequest
                            {
                                Title = "Busy dispatch",
                                VesselId = vessel.Id,
                                ObjectiveId = objective.Id,
                                Missions = new List<MissionDescription> { new MissionDescription("Implement", "Busy work.") }
                            }, cancel.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                        }

                        AssertNull(failure, "A busy admission must return a result, not wait for cancellation.");
                        AssertEqual(409, result!.StatusCode);
                        string payload = JsonSerializer.Serialize(result.Value);
                        AssertContains("objective_dispatch_busy", payload);
                        AssertContains("\"Retryable\":true", payload);
                        AssertFalse(admiral.DispatchVoyageCalled, "A busy admission must not create a voyage.");
                    }
                }
            });

            await RunTest("DispatchAsync_OppositeOrderMultiObjectiveDispatches_OneWinsWithoutDeadlockOrDuplicate", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("multi-order-vessel", "https://github.com/test/multi-order.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                Objective first = await CreateScopedObjectiveAsync(testDb, "Multi order one", vessel.Id).ConfigureAwait(false);
                Objective second = await CreateScopedObjectiveAsync(testDb, "Multi order two", vessel.Id).ConfigureAwait(false);

                VoyageDispatchService Build(RecordingAdmiralService admiral)
                {
                    return new VoyageDispatchService(
                        testDb.Driver,
                        admiral,
                        objectiveService: new ObjectiveService(testDb.Driver, dispatchAdmissionWait: TimeSpan.FromSeconds(3)),
                        settings: new ArmadaSettings { CodeIndex = { Enabled = false } });
                }

                RecordingAdmiralService leftAdmiral = new RecordingAdmiralService(testDb.Driver)
                {
                    AfterVoyageCreateAsync = () => Task.Delay(150)
                };
                RecordingAdmiralService rightAdmiral = new RecordingAdmiralService(testDb.Driver)
                {
                    AfterVoyageCreateAsync = () => Task.Delay(150)
                };
                Task<VoyageDispatchResult> left = Build(leftAdmiral).DispatchAsync(new SharedVoyageDispatchRequest
                {
                    Title = "Left planning dispatch",
                    VesselId = vessel.Id,
                    ObjectiveId = first.Id,
                    LinkedObjectiveIds = new List<string> { second.Id },
                    Missions = new List<MissionDescription> { new MissionDescription("Implement", "Left work.") }
                });
                Task<VoyageDispatchResult> right = Build(rightAdmiral).DispatchAsync(new SharedVoyageDispatchRequest
                {
                    Title = "Right planning dispatch",
                    VesselId = vessel.Id,
                    ObjectiveId = second.Id,
                    LinkedObjectiveIds = new List<string> { first.Id },
                    Missions = new List<MissionDescription> { new MissionDescription("Implement", "Right work.") }
                });

                Task both = Task.WhenAll(left, right);
                Task finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(false);
                AssertTrue(ReferenceEquals(finished, both), "Opposite-order multi-objective dispatches must not deadlock.");

                VoyageDispatchResult[] results = new[] { left.Result, right.Result };
                AssertEqual(1, results.Count(result => result.Succeeded), "Exactly one overlapping dispatch may win.");
                VoyageDispatchResult loser = results.Single(result => !result.Succeeded);
                AssertEqual(409, loser.StatusCode);
                AssertEqual(1, leftAdmiral.DispatchVoyageCallCount + rightAdmiral.DispatchVoyageCallCount,
                    "The loser must be refused before it creates a voyage.");

                Voyage winner = results.Single(result => result.Succeeded).Voyage!;
                foreach (Objective objective in new[] { first, second })
                {
                    Objective stored = (await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false))!;
                    AssertEqual(1, stored.VoyageIds.Count, "No objective may carry a second nonterminal voyage.");
                    AssertEqual(winner.Id, stored.VoyageIds[0], "Every objective is linked to the one winning voyage.");
                }
            });

            await RunTest("DispatchAsync_MultiObjectiveLaterLinkFailure_RestoresEarlierObjectiveAndCancelsVoyage", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("multi-revert-vessel", "https://github.com/test/multi-revert.git")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId
                }).ConfigureAwait(false);
                Objective x = await CreateScopedObjectiveAsync(testDb, "Revert x", vessel.Id).ConfigureAwait(false);
                Objective y = await CreateScopedObjectiveAsync(testDb, "Revert y", vessel.Id).ConfigureAwait(false);
                List<Objective> ordered = new[] { x, y }
                    .OrderBy(item => ObjectiveService.BuildDispatchAdmissionLeaseName(Constants.DefaultTenantId, item.Id), StringComparer.Ordinal)
                    .ToList();
                Objective earlier = ordered[0];
                Objective later = ordered[1];
                later.SuggestedPipelineId = "ppl_missing";
                await testDb.Driver.Objectives.UpdateAsync(later).ConfigureAwait(false);

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                VoyageDispatchService service = new VoyageDispatchService(
                    testDb.Driver,
                    admiral,
                    objectiveService: new ObjectiveService(testDb.Driver),
                    settings: new ArmadaSettings { CodeIndex = { Enabled = false } });

                VoyageDispatchResult result = await service.DispatchAsync(new SharedVoyageDispatchRequest
                {
                    Title = "Partial link failure",
                    VesselId = vessel.Id,
                    ObjectiveId = earlier.Id,
                    LinkedObjectiveIds = new List<string> { later.Id },
                    Missions = new List<MissionDescription> { new MissionDescription("Implement", "Must not survive a partial link.") }
                }).ConfigureAwait(false);

                AssertFalse(result.Succeeded);
                AssertContains("objective_link_failed", JsonSerializer.Serialize(result.Value));
                List<Voyage> voyages = await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false);
                AssertEqual(1, voyages.Count);
                AssertEqual(VoyageStatusEnum.Cancelled, voyages[0].Status, "The voyage of a partially linked dispatch must be cancelled.");
                Objective restored = (await testDb.Driver.Objectives.ReadAsync(earlier.Id).ConfigureAwait(false))!;
                AssertEqual(0, restored.VoyageIds.Count, "The earlier objective must not keep the cancelled voyage.");
                AssertEqual(ObjectiveStatusEnum.Scoped, restored.Status);
                AssertEqual(ObjectiveBacklogStateEnum.ReadyForDispatch, restored.BacklogState);
                foreach (Objective objective in ordered)
                {
                    AssertNull(await testDb.Driver.CoordinationLeases.ReadAsync(
                            ObjectiveService.BuildDispatchAdmissionLeaseName(Constants.DefaultTenantId, objective.Id)).ConfigureAwait(false),
                        "Every admission is released after the failure.");
                }
            });

            await RunTest("DispatchAsync_ObjectiveLinkFailureCancelsCreatedVoyageAndMissions", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                    "link-failure-vessel", "https://github.com/test/link-failure.git")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Invalid linked objective",
                    Status = ObjectiveStatusEnum.Scoped,
                    VesselIds = new List<string> { vessel.Id },
                    SuggestedPipelineId = "ppl_missing"
                }).ConfigureAwait(false);
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                VoyageDispatchService service = new VoyageDispatchService(
                    testDb.Driver,
                    admiral,
                    objectiveService: new ObjectiveService(testDb.Driver),
                    settings: new ArmadaSettings { CodeIndex = { Enabled = false } });

                VoyageDispatchResult result = await service.DispatchAsync(new SharedVoyageDispatchRequest
                {
                    Title = "Must be cleaned up",
                    VesselId = vessel.Id,
                    ObjectiveId = objective.Id,
                    Missions = new List<MissionDescription>
                    {
                        new MissionDescription("Implement", "This voyage must not survive a link failure.")
                    }
                }).ConfigureAwait(false);

                AssertFalse(result.Succeeded);
                AssertEqual(500, result.StatusCode);
                AssertContains("objective_link_failed", JsonSerializer.Serialize(result.Value));
                List<Voyage> voyages = await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false);
                AssertEqual(1, voyages.Count);
                AssertEqual(VoyageStatusEnum.Cancelled, voyages[0].Status,
                    "A voyage that cannot be linked must be terminal.");
                List<Mission> missions = await testDb.Driver.Missions
                    .EnumerateByVoyageAsync(voyages[0].Id).ConfigureAwait(false);
                AssertTrue(missions.Count > 0, "The fake must prove cleanup after mission creation.");
                AssertTrue(missions.All(mission => mission.Status == MissionStatusEnum.Cancelled),
                    "No mission from an unlinked voyage may remain Pending, Assigned, or InProgress.");
                string leaseName = ObjectiveService.BuildDispatchAdmissionLeaseName(
                    Constants.DefaultTenantId,
                    objective.Id);
                AssertNull(await testDb.Driver.CoordinationLeases.ReadAsync(leaseName).ConfigureAwait(false),
                    "Cleanup must release objective admission so a corrected retry can run.");
            });

            await RunTest("DispatchAsync_LostAdmissionAfterCreateCancelsVoyageBeforeLeaseRelease", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                    "lost-admission-vessel", "https://github.com/test/lost-admission.git")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Lose admission after create",
                    Status = ObjectiveStatusEnum.Scoped,
                    VesselIds = new List<string> { vessel.Id }
                }).ConfigureAwait(false);
                string leaseName = ObjectiveService.BuildDispatchAdmissionLeaseName(
                    Constants.DefaultTenantId,
                    objective.Id);
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver)
                {
                    AfterVoyageCreateAsync = async () =>
                    {
                        CoordinationLease lease = (await testDb.Driver.CoordinationLeases
                            .ReadAsync(leaseName).ConfigureAwait(false))!;
                        await testDb.Driver.CoordinationLeases.ReleaseAsync(
                            leaseName,
                            lease.Holder).ConfigureAwait(false);
                        await Task.Delay(100).ConfigureAwait(false);
                    }
                };
                VoyageDispatchService service = new VoyageDispatchService(
                    testDb.Driver,
                    admiral,
                    objectiveService: new ObjectiveService(
                        testDb.Driver,
                        dispatchAdmissionTtl: TimeSpan.FromMilliseconds(90)),
                    settings: new ArmadaSettings { CodeIndex = { Enabled = false } });

                Exception? failure = null;
                try
                {
                    await service.DispatchAsync(new SharedVoyageDispatchRequest
                    {
                        Title = "Lose the lease",
                        VesselId = vessel.Id,
                        ObjectiveId = objective.Id,
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("Implement", "Create, then lose admission.")
                        }
                    }).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    failure = ex;
                }

                AssertNotNull(failure, "Lost durable ownership must fail the dispatch.");
                AssertContains("admission was lost", failure!.Message);
                List<Voyage> voyages = await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false);
                AssertEqual(1, voyages.Count);
                AssertEqual(VoyageStatusEnum.Cancelled, voyages[0].Status,
                    "The voyage must be terminal before the lost admission is released.");
                List<Mission> missions = await testDb.Driver.Missions
                    .EnumerateByVoyageAsync(voyages[0].Id).ConfigureAwait(false);
                AssertTrue(missions.All(mission => mission.Status == MissionStatusEnum.Cancelled),
                    "No mission may remain active after admission ownership is lost.");
            });

            await RunTest("VoyageCancellation_RecallsRunningCaptainBeforePublishingTerminalState", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Running orphan")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.InProgress
                }).ConfigureAwait(false);
                Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("running-captain")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    State = CaptainStateEnum.Working
                }).ConfigureAwait(false);
                Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Running", "Must stop first.")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = voyage.Id,
                    CaptainId = captain.Id,
                    Status = MissionStatusEnum.InProgress
                }).ConfigureAwait(false);
                captain.CurrentMissionId = mission.Id;
                captain = await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
                bool recalledWhileActive = false;

                await VoyageCancellation.CancelVoyageAsync(
                    testDb.Driver,
                    voyage,
                    "cleanup",
                    recallCaptain: async (captainId, _) =>
                    {
                        Mission active = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                        Captain owner = (await testDb.Driver.Captains.ReadAsync(captainId).ConfigureAwait(false))!;
                        Voyage activeVoyage = (await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false))!;
                        recalledWhileActive = active.Status == MissionStatusEnum.InProgress
                            && owner.State == CaptainStateEnum.Working
                            && activeVoyage.Status == VoyageStatusEnum.InProgress;
                    }).ConfigureAwait(false);

                AssertTrue(recalledWhileActive,
                    "Cleanup must invoke the process-stop seam before it marks the mission terminal or captain idle.");
                Mission storedMission = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                Captain storedCaptain = (await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!;
                AssertEqual(MissionStatusEnum.Cancelled, storedMission.Status);
                AssertEqual(CaptainStateEnum.Idle, storedCaptain.State);
            });

            await RunTest("VoyageCancellation_RecallFailureLeavesVoyageActiveAndEscapes", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Recall failure")
                {
                    Status = VoyageStatusEnum.InProgress
                }).ConfigureAwait(false);
                Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("recall-failure-captain"))
                    .ConfigureAwait(false);
                Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Still writing", "Recall fails.")
                {
                    VoyageId = voyage.Id,
                    CaptainId = captain.Id,
                    Status = MissionStatusEnum.InProgress
                }).ConfigureAwait(false);

                Exception? failure = null;
                try
                {
                    await VoyageCancellation.CancelVoyageAsync(
                        testDb.Driver,
                        voyage,
                        "cleanup",
                        recallCaptain: (_, _) => throw new InvalidOperationException("stop failed"))
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    failure = ex;
                }

                AssertNotNull(failure, "A failed process recall must escape cleanup.");
                AssertContains("stop failed", failure!.Message);
                Voyage storedVoyage = (await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false))!;
                Mission storedMission = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                AssertEqual(VoyageStatusEnum.InProgress, storedVoyage.Status,
                    "Occupancy must remain active while the writer could still run.");
                AssertEqual(MissionStatusEnum.InProgress, storedMission.Status);
            });
        }

        private static VoyageDispatchService NewService(TestDatabase testDb, ArmadaSettings? settings = null)
        {
            return new VoyageDispatchService(
                testDb.Driver,
                new RecordingAdmiralService(testDb.Driver),
                null,
                null,
                null,
                settings);
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

        private static async Task<Objective> CreateScopedObjectiveAsync(TestDatabase testDb, string title, string vesselId)
        {
            return await testDb.Driver.Objectives.CreateAsync(new Objective
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId,
                Title = title,
                Status = ObjectiveStatusEnum.Scoped,
                BacklogState = ObjectiveBacklogStateEnum.ReadyForDispatch,
                VesselIds = new List<string> { vesselId }
            }).ConfigureAwait(false);
        }

        private sealed class RecordingAdmiralService : IAdmiralService
        {
            private readonly DatabaseDriver _Database;

            public RecordingAdmiralService(DatabaseDriver database)
            {
                _Database = database;
            }

            public bool DispatchVoyageCalled { get; private set; }

            public int DispatchVoyageCallCount { get; private set; }

            public string? LastPipelineId { get; private set; }

            public List<Mission> CreatedMissions { get; } = new List<Mission>();

            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }
            public Func<Task>? AfterVoyageCreateAsync { get; set; }

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
                DispatchVoyageCallCount++;
                LastPipelineId = pipelineId;
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

                if (AfterVoyageCreateAsync != null)
                    await AfterVoyageCreateAsync().ConfigureAwait(false);

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
            public Task StopAllAgentProcessesAsync(CancellationToken token = default) => Task.CompletedTask;

            public Task HealthCheckAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
                => throw new NotImplementedException();
        }

        private sealed class RecordingObjectiveDispatchPreview : IObjectiveDispatchPreviewService
        {
            public ObjectiveDispatchPreview Result { get; set; } = new ObjectiveDispatchPreview { IsReady = true };
            public int CallCount { get; private set; }
            public string? RequestedVesselId { get; private set; }
            public string? RequestedPipelineId { get; private set; }
            public IReadOnlyList<CaptainAssignmentOverride>? CaptainAssignments { get; private set; }
            public IReadOnlyList<MissionDescription>? MissionDescriptions { get; private set; }

            public Task<ObjectiveDispatchPreview> PreviewAsync(
                AuthContext auth,
                Objective objective,
                string? requestedVesselId = null,
                string? requestedPipelineId = null,
                IReadOnlyList<CaptainAssignmentOverride>? captainAssignments = null,
                IReadOnlyList<MissionDescription>? missionDescriptions = null,
                CancellationToken token = default)
            {
                CallCount++;
                RequestedVesselId = requestedVesselId;
                RequestedPipelineId = requestedPipelineId;
                CaptainAssignments = captainAssignments;
                MissionDescriptions = missionDescriptions;
                return Task.FromResult(Result);
            }
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
            public Task StopAllAgentProcessesAsync(CancellationToken token = default) => Task.CompletedTask;
            public Task HealthCheckAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task CleanupStaleCaptainsAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
                => throw new NotImplementedException();
        }

        private sealed class RecordingCodeIndexService : ICodeIndexService
        {
            public List<string> StatusRequests { get; } = new List<string>();
            public List<ContextPackRequest> CacheRequests { get; } = new List<ContextPackRequest>();
            public List<ContextPackRequest> BuildRequests { get; } = new List<ContextPackRequest>();

            public ContextPackResponse? CachedResponse { get; set; }
            public ContextPackResponse BuildResponse { get; set; } = new ContextPackResponse();

            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
            {
                StatusRequests.Add(vesselId);
                return Task.FromResult(new CodeIndexStatus { VesselId = vesselId });
            }

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
