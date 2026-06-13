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
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for McpVoyageTools armada_dispatch: alias-based dependency resolution
    /// and backward-compatible literal-ID dispatch.
    /// </summary>
    public class McpVoyageToolsTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "MCP Voyage Tools";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Dispatch_AliasChain_CreatesInTopoOrderWithResolvedIds", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("alias-chain-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler, "armada_dispatch handler must be registered");

                    // M1 <- M2 <- M3 chain supplied in reverse declaration order.
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "alias chain voyage",
                        description = "tests alias resolution",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { title = "M3", description = "d3", alias = "M3", dependsOnMissionAlias = "M2" },
                            new { title = "M2", description = "d2", alias = "M2", dependsOnMissionAlias = "M1" },
                            new { title = "M1", description = "d1", alias = "M1" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);

                    // Three missions should have been dispatched in topological order.
                    AssertEqual(3, admiralDouble.DispatchedMissions.Count, "All three missions must be dispatched");

                    Mission dispatched0 = admiralDouble.DispatchedMissions[0];
                    Mission dispatched1 = admiralDouble.DispatchedMissions[1];
                    Mission dispatched2 = admiralDouble.DispatchedMissions[2];

                    AssertEqual("M1", dispatched0.Title, "First dispatched must be M1 (no deps)");
                    AssertEqual("M2", dispatched1.Title, "Second dispatched must be M2 (depends on M1)");
                    AssertEqual("M3", dispatched2.Title, "Third dispatched must be M3 (depends on M2)");

                    // M1 has no dependency.
                    AssertTrue(String.IsNullOrEmpty(dispatched0.DependsOnMissionId),
                        "M1 must have no DependsOnMissionId");

                    // M2 must reference the ID assigned to M1.
                    AssertEqual(dispatched0.Id, dispatched1.DependsOnMissionId,
                        "M2.DependsOnMissionId must equal the msn_* ID assigned to M1");

                    // M3 must reference the ID assigned to M2.
                    AssertEqual(dispatched1.Id, dispatched2.DependsOnMissionId,
                        "M3.DependsOnMissionId must equal the msn_* ID assigned to M2");
                }
            });

            await RunTest("Dispatch_LiteralIdsOnly_DelegatesToDispatchVoyageAsync", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("literal-ids-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler);

                    // No aliases: should use the standard DispatchVoyageAsync path.
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "literal voyage",
                        description = "no aliases",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { title = "Task A", description = "da" },
                            new { title = "Task B", description = "db" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);
                    AssertTrue(admiralDouble.DispatchVoyageCalled, "Standard voyage dispatch must be used when no aliases present");
                    // DispatchMissionAsync should NOT be called for the literal-ID path.
                    AssertEqual(0, admiralDouble.DispatchedMissions.Count,
                        "DispatchMissionAsync must not be called for legacy literal-ID dispatch");
                }
            });

            await RunTest("Dispatch_ObjectiveId_LinksVoyageAndMissionsToObjective", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("objective-linked-vessel", "https://github.com/test/repo.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);

                    ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    Objective objective = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "Use structured delivery records",
                        VesselIds = new List<string> { vessel.Id }
                    }).ConfigureAwait(false);

                    ReflectionDrainRecordingAdmiral admiralDouble = new ReflectionDrainRecordingAdmiral(testDb.Driver);

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        null,
                        objectives);

                    AssertNotNull(dispatchHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "objective linked voyage",
                        description = "structured-first dispatch",
                        vesselId = vessel.Id,
                        objectiveId = objective.Id,
                        missions = new object[]
                        {
                            new { title = "Task A", description = "Implement a narrow scoped change" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);

                    Objective? linked = await objectives.ReadAsync(auth, objective.Id).ConfigureAwait(false);
                    AssertNotNull(linked);
                    AssertEqual(ObjectiveStatusEnum.InProgress, linked.Status);
                    AssertEqual(ObjectiveBacklogStateEnum.Dispatched, linked.BacklogState);
                    AssertEqual(1, linked.VoyageIds.Count);
                    AssertEqual(1, linked.MissionIds.Count);
                    AssertTrue(linked.VesselIds.Contains(vessel.Id), "Expected linked objective to retain vessel lineage.");
                }
            });

            await RunTest("Dispatch_CodeContextForce_AttachesContextPackAndPreservesPrestagedFiles", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("code-context-force-attach-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService();
                    codeIndex.ContextPackResponse.PrestagedFiles.Add(new PrestagedFile(
                        Path.Combine(Path.GetTempPath(), "context-pack-force.md"),
                        "_briefing/context-pack.md"));

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        codeIndex);

                    AssertNotNull(dispatchHandler);

                    string existingSource = Path.Combine(Path.GetTempPath(), "existing-prestage.txt");
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "context voyage",
                        vesselId = vessel.Id,
                        codeContextMode = "force",
                        codeContextTokenBudget = 1200,
                        codeContextMaxResults = 3,
                        missions = new object[]
                        {
                            new
                            {
                                title = "Task A",
                                description = "Fix dispatch parsing",
                                prestagedFiles = new[]
                                {
                                    new { sourcePath = existingSource, destPath = "notes/input.md" }
                                }
                            }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);
                    AssertEqual(1, codeIndex.ContextPackRequests.Count, "Force mode should request one context pack");
                    AssertContains("Task A", codeIndex.ContextPackRequests[0].Goal);
                    AssertContains("Fix dispatch parsing", codeIndex.ContextPackRequests[0].Goal);
                    AssertEqual(1200, codeIndex.ContextPackRequests[0].TokenBudget);
                    AssertEqual(3, codeIndex.ContextPackRequests[0].MaxResults!.Value);

                    AssertEqual(1, admiralDouble.LastMissionDescriptions.Count, "Standard dispatch should receive one mission description");
                    List<PrestagedFile>? prestaged = admiralDouble.LastMissionDescriptions[0].PrestagedFiles;
                    AssertNotNull(prestaged, "Prestaged files should be preserved and extended");
                    AssertEqual(2, prestaged!.Count);
                    AssertEqual("notes/input.md", prestaged[0].DestPath);
                    AssertEqual("_briefing/context-pack.md", prestaged[1].DestPath);
                }
            });

            await RunTest("Dispatch_BlocksWhenCodeIndexUpdateInProgress", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("index-running-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService();
                    codeIndex.Status.UpdateInProgress = true;
                    codeIndex.Status.UpdateStartedUtc = DateTime.UtcNow.AddMinutes(-2);

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        codeIndex);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "blocked voyage",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { title = "Task A", description = "Should wait for fresh index" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("\"Error\"", resultJson);
                    AssertContains("code_index_update_in_progress", resultJson);
                    AssertContains("generated context packs and search results include the most recently landed code", resultJson);
                    AssertEqual(0, codeIndex.ContextPackRequests.Count, "Dispatch should block before context pack generation");
                    AssertFalse(admiralDouble.DispatchVoyageCalled, "Dispatch must not persist a voyage while the vessel index is refreshing");
                }
            });

            await RunTest("Dispatch_BlocksWhenCodeIndexStale", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("index-stale-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService();
                    codeIndex.Status.Freshness = "Stale";
                    codeIndex.Status.IndexedCommitSha = "old";
                    codeIndex.Status.CurrentCommitSha = "new";

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        codeIndex);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "stale index voyage",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { title = "Task A", description = "Should wait for fresh index" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("\"Error\"", resultJson);
                    AssertContains("code_index_stale", resultJson);
                    AssertContains("Run armada_index_update", resultJson);
                    AssertEqual(0, codeIndex.ContextPackRequests.Count, "Dispatch should block before stale context pack generation");
                    AssertFalse(admiralDouble.DispatchVoyageCalled, "Dispatch must not persist a voyage while the vessel index is stale");
                }
            });

            await RunTest("Dispatch_CodeContextOff_SkipsContextPack", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("code-context-off-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService();

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        codeIndex);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "off voyage",
                        vesselId = vessel.Id,
                        codeContextMode = "off",
                        missions = new object[]
                        {
                            new { title = "Task A", description = "No context" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);
                    AssertEqual(0, codeIndex.ContextPackRequests.Count, "Off mode should not request context packs");
                    AssertNull(admiralDouble.LastMissionDescriptions[0].PrestagedFiles, "Off mode should not add prestaged files");
                }
            });

            await RunTest("Dispatch_CodeContextAuto_CacheMiss_DeferredNoBuildAttempted", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("code-context-auto-defer-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService();

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        codeIndex);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "auto defer voyage",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { title = "Task A", description = "Auto mode defers on cache miss" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Auto mode should not return error on cache miss: " + resultJson);
                    AssertEqual(0, codeIndex.ContextPackRequests.Count, "Auto mode must NOT call build on cache miss");
                    AssertEqual(0, codeIndex.WarmBaselineCacheVesselIds.Count, "Auto mode must NOT warm on cache miss");
                    AssertTrue(admiralDouble.DispatchVoyageCalled, "Dispatch must proceed after auto deferral");
                    AssertEqual(1, admiralDouble.LastMissionDescriptions.Count, "Dispatch should receive the mission");
                    AssertNull(admiralDouble.LastMissionDescriptions[0].PrestagedFiles, "Auto deferred missions have no inline prestaged context file");
                    AssertEqual("auto", admiralDouble.LastMissionDescriptions[0].CodeContextMode, "Deferred intent must be set on mission description");
                    AssertNotNull(admiralDouble.LastMissionDescriptions[0].CodeContextQuery, "Deferred query must be set so the stager knows what to generate");
                    AssertContains("Task A", admiralDouble.LastMissionDescriptions[0].CodeContextQuery!);
                }
            });

            await RunTest("Dispatch_CodeContextAuto_CacheMiss_ReturnsPromptlyWithoutBlocking", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("code-context-auto-nonblocking-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                    // NeverCompleteBuild would block indefinitely if called; its presence
                    // verifies that auto mode never reaches BuildContextPackAsync on a cache miss.
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService
                    {
                        NeverCompleteBuild = true
                    };

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        codeIndex);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "auto nonblocking voyage",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { title = "Task A", description = "Auto mode must not block on cache miss" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Auto mode must not return error on cache miss: " + resultJson);
                    AssertEqual(0, codeIndex.ContextPackRequests.Count, "Auto mode must NOT call build (would block)");
                    AssertEqual(0, codeIndex.WarmBaselineCacheVesselIds.Count, "Auto mode must NOT warm on cache miss");
                    AssertTrue(admiralDouble.DispatchVoyageCalled, "Dispatch must proceed after auto deferral");
                    AssertEqual("auto", admiralDouble.LastMissionDescriptions[0].CodeContextMode, "Deferred intent must be set");
                }
            });

            await RunTest("Dispatch_CodeContextForce_ReturnsErrorWhenUnavailable", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("code-context-force-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "force voyage",
                        vesselId = vessel.Id,
                        codeContextMode = "force",
                        missions = new object[]
                        {
                            new { title = "Task A", description = "Must have context" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("\"Error\"", resultJson);
                    AssertContains("code index service is unavailable", resultJson);
                    AssertFalse(admiralDouble.DispatchVoyageCalled, "Force failure should happen before dispatch persistence");
                }
            });

            await RunTest("Dispatch_CodeContextForce_ReturnsErrorWhenGenerationFails", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("code-context-force-failure-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService();
                    codeIndex.BuildException = new InvalidOperationException("index crashed");

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        codeIndex);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "force failure voyage",
                        vesselId = vessel.Id,
                        codeContextMode = "force",
                        missions = new object[]
                        {
                            new { title = "Task A", description = "Must fail clearly" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("\"Error\"", resultJson);
                    AssertContains("code context generation failed", resultJson);
                    AssertContains("index crashed", resultJson);
                    AssertEqual(1, codeIndex.ContextPackRequests.Count, "Force mode should attempt context generation once");
                    AssertFalse(admiralDouble.DispatchVoyageCalled, "Force generation failure should happen before dispatch persistence");
                }
            });

            await RunTest("Dispatch_CodeContextForce_ReturnsErrorWhenGenerationTimesOut", async () =>
            {
                string? priorTimeout = Environment.GetEnvironmentVariable("ARMADA_CODE_CONTEXT_TIMEOUT_MS");
                Environment.SetEnvironmentVariable("ARMADA_CODE_CONTEXT_TIMEOUT_MS", "100");
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                            new Vessel("code-context-force-timeout-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                        RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                        RecordingCodeIndexService codeIndex = new RecordingCodeIndexService
                        {
                            NeverCompleteBuild = true
                        };

                        Func<JsonElement?, Task<object>>? dispatchHandler = null;
                        McpVoyageTools.Register(
                            (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                            testDb.Driver,
                            admiralDouble,
                            null,
                            null,
                            null,
                            codeIndex);

                        JsonElement args = JsonSerializer.SerializeToElement(new
                        {
                            title = "force timeout voyage",
                            vesselId = vessel.Id,
                            codeContextMode = "force",
                            missions = new object[]
                            {
                                new { title = "Task A", description = "Force mode should fail clearly after a slow context pack" }
                            }
                        });

                        object result = await dispatchHandler!(args).ConfigureAwait(false);
                        string resultJson = JsonSerializer.Serialize(result);

                        AssertContains("\"Error\"", resultJson);
                        AssertContains("code context generation failed", resultJson);
                        AssertContains("exceeded", resultJson);
                        AssertFalse(admiralDouble.DispatchVoyageCalled, "Force timeout should happen before dispatch persistence");
                    }
                }
                finally
                {
                    Environment.SetEnvironmentVariable("ARMADA_CODE_CONTEXT_TIMEOUT_MS", priorTimeout);
                }
            });

            await RunTest("Dispatch_MissionCodeContextOverride_OverridesTopLevelOff", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("code-context-override-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService();
                    codeIndex.ContextPackResponse.PrestagedFiles.Add(new PrestagedFile(
                        Path.Combine(Path.GetTempPath(), "context-pack-override.md"),
                        "_briefing/context-pack.md"));

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        codeIndex);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "override voyage",
                        vesselId = vessel.Id,
                        codeContextMode = "off",
                        missions = new object[]
                        {
                            new { title = "Task A", description = "Skipped by top-level off" },
                            new { title = "Task B", description = "Forced by mission", codeContextMode = "force", codeContextQuery = "custom query for task b" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);
                    AssertEqual(1, codeIndex.ContextPackRequests.Count, "Mission-level force should override top-level off");
                    AssertEqual("custom query for task b", codeIndex.ContextPackRequests[0].Goal);
                    AssertNull(admiralDouble.LastMissionDescriptions[0].PrestagedFiles, "Top-level off mission should not receive context");
                    AssertNotNull(admiralDouble.LastMissionDescriptions[1].PrestagedFiles, "Forced mission should receive context");
                }
            });

            await RunTest("Dispatch_InvalidMissionCodeContextMode_ReturnsErrorBeforeDispatch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("code-context-invalid-mode-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService();

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        codeIndex);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "invalid context mode voyage",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { title = "Task A", description = "Invalid mode", codeContextMode = "required" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("\"Error\"", resultJson);
                    AssertContains("invalid codeContextMode for mission", resultJson);
                    AssertContains("required", resultJson);
                    AssertEqual(0, codeIndex.ContextPackRequests.Count, "Invalid mode should fail before context generation");
                    AssertFalse(admiralDouble.DispatchVoyageCalled, "Invalid mode should fail before dispatch persistence");
                }
            });

            await RunTest("RegisterAll_PassesCodeIndexServiceToVoyageTools", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("registrar-code-context-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService();
                    codeIndex.ContextPackResponse.PrestagedFiles.Add(new PrestagedFile(
                        Path.Combine(Path.GetTempPath(), "context-pack-registrar.md"),
                        "_briefing/context-pack.md"));

                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                    McpToolRegistrar.RegisterAll(
                        (name, _, _, handler) => { handlers[name] = handler; },
                        testDb.Driver,
                        admiralDouble,
                        codeIndexService: codeIndex);

                    AssertTrue(handlers.ContainsKey("armada_fleet_code_search"), "Registrar should include fleet code-search tool");
                    AssertTrue(handlers.ContainsKey("armada_fleet_context_pack"), "Registrar should include fleet context-pack tool");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "registrar voyage",
                        vesselId = vessel.Id,
                        codeContextMode = "force",
                        missions = new object[]
                        {
                            new { title = "Task A", description = "Registrar should pass code index" }
                        }
                    });

                    object result = await handlers["armada_dispatch"](args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Registrar-wired dispatch should not fail: " + resultJson);
                    AssertEqual(1, codeIndex.ContextPackRequests.Count, "Registrar must pass ICodeIndexService to voyage tools");
                }
            });

            await RunTest("RegisterAll_NullReflectionDispatcher_AuditDrainStillAutoDispatchesReflections", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings { DefaultReflectionThreshold = 5 };
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("registrar-reflection-drain-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    vessel.TenantId = Constants.DefaultTenantId;
                    vessel.ReflectionThreshold = 5;
                    vessel = await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    for (int i = 0; i < 5; i++)
                    {
                        Mission mission = new Mission("terminal " + i, "desc " + i);
                        mission.VesselId = vessel.Id;
                        mission.Persona = "Worker";
                        mission.Status = MissionStatusEnum.Complete;
                        mission.CompletedUtc = DateTime.UtcNow.AddMinutes(-10 + i);
                        mission.DiffSnapshot = "diff " + i;
                        mission.AgentOutput = "output " + i;
                        await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    }

                    ReflectionDrainRecordingAdmiral admiral = new ReflectionDrainRecordingAdmiral(testDb.Driver);
                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                    McpToolRegistrar.RegisterAll(
                        (name, _, _, handler) => { handlers[name] = handler; },
                        testDb.Driver,
                        admiral,
                        settings: settings,
                        reflectionDispatcher: null);

                    JsonElement drainArgs = JsonSerializer.SerializeToElement(new { vesselId = vessel.Id, limit = 10 });
                    object drainResult = await handlers["armada_drain_audit_queue"](drainArgs).ConfigureAwait(false);
                    JsonDocument drainDoc = JsonDocument.Parse(JsonSerializer.Serialize(drainResult));
                    JsonElement reflectionsEl = drainDoc.RootElement.GetProperty("reflectionsDispatched");
                    AssertEqual(JsonValueKind.Array, reflectionsEl.ValueKind);
                    AssertEqual(1, reflectionsEl.GetArrayLength());
                    AssertEqual(1, admiral.DispatchCount);
                }
            });

            await RunTest("Dispatch_InvalidAliasCycle_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("cycle-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "cycle voyage",
                        description = "will fail",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { title = "M1", description = "d1", alias = "M1", dependsOnMissionAlias = "M2" },
                            new { title = "M2", description = "d2", alias = "M2", dependsOnMissionAlias = "M1" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertTrue(resultJson.Contains("\"Error\""), "Cycle must produce an error response: " + resultJson);
                    AssertTrue(resultJson.ToLowerInvariant().Contains("cycle"),
                        "Error message must mention cycle: " + resultJson);
                }
            });

            await RunTest("Dispatch_MixedAliasAndLiteralDep_ResolvesAliasCorrectly", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("mixed-dep-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler);

                    // M1 has no deps; M2 uses alias dep on M1; M3 uses a literal external ID.
                    // Because M2 has an alias dep, the alias path is activated for all missions.
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "mixed dep voyage",
                        description = "alias + literal",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { title = "M1", description = "d1", alias = "M1" },
                            new { title = "M2", description = "d2", alias = "M2", dependsOnMissionAlias = "M1" },
                            new { title = "M3", description = "d3" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);
                    AssertEqual(3, admiralDouble.DispatchedMissions.Count, "All three missions must be dispatched");

                    // Find M1 and M2 by title to verify the alias dep was resolved correctly.
                    Mission? m1 = null;
                    Mission? m2 = null;
                    foreach (Mission m in admiralDouble.DispatchedMissions)
                    {
                        if (m.Title == "M1") m1 = m;
                        if (m.Title == "M2") m2 = m;
                    }
                    AssertNotNull(m1, "M1 must be dispatched");
                    AssertNotNull(m2, "M2 must be dispatched");
                    AssertTrue(String.IsNullOrEmpty(m1!.DependsOnMissionId), "M1 has no dep");
                    AssertEqual(m1.Id, m2!.DependsOnMissionId,
                        "M2's alias dep on M1 must resolve to M1's assigned ID");
                }
            });

            await RunTest("Dispatch_UsesCachedContextPack_SkipsGeneration", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("cache-hit-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    string cachedPackPath = Path.Combine(Path.GetTempPath(), "cache-hit-context-pack.md");
                    ContextPackResponse cachedPack = new ContextPackResponse
                    {
                        Goal = "cached goal",
                        MaterializedPath = cachedPackPath,
                        Metrics = new ContextPackMetrics { CacheHit = true, CacheKey = "abc123" }
                    };
                    cachedPack.PrestagedFiles.Add(new PrestagedFile(cachedPackPath, "_briefing/context-pack.md"));

                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService
                    {
                        CachedResponse = cachedPack
                    };
                    codeIndex.ContextPackResponse.PrestagedFiles.Add(new PrestagedFile(
                        Path.Combine(Path.GetTempPath(), "generated.md"), "_briefing/context-pack.md"));

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        codeIndex);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "cache hit voyage",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { title = "Task A", description = "should use cached pack" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);
                    AssertEqual(1, codeIndex.CacheRequests.Count, "Cache lookup must have been attempted");
                    AssertEqual(0, codeIndex.ContextPackRequests.Count, "Synchronous generation must NOT be called on cache hit");
                    AssertTrue(admiralDouble.DispatchVoyageCalled, "Dispatch must proceed");
                    AssertNotNull(admiralDouble.LastMissionDescriptions[0].PrestagedFiles, "Cached pack must be staged");
                    AssertEqual(cachedPackPath, admiralDouble.LastMissionDescriptions[0].PrestagedFiles![0].SourcePath);
                }
            });

            await RunTest("Dispatch_Force_CacheMiss_FallsBackToGeneration", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("cache-miss-force-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    string generatedPackPath = Path.Combine(Path.GetTempPath(), "generated-context-pack.md");
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService();
                    codeIndex.ContextPackResponse.PrestagedFiles.Add(new PrestagedFile(
                        generatedPackPath, "_briefing/context-pack.md"));

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        codeIndex);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "cache miss force voyage",
                        vesselId = vessel.Id,
                        codeContextMode = "force",
                        missions = new object[]
                        {
                            new { title = "Task B", description = "force must fall back to generation on cache miss" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);
                    AssertEqual(2, codeIndex.CacheRequests.Count, "Cache lookup must be attempted before and after warm");
                    AssertEqual(1, codeIndex.WarmBaselineCacheVesselIds.Count, "Baseline warm must run on cache miss");
                    AssertEqual(vessel.Id, codeIndex.WarmBaselineCacheVesselIds[0]);
                    AssertEqual(1, codeIndex.ContextPackRequests.Count, "Synchronous generation must be called when warm does not populate cache");
                    AssertTrue(admiralDouble.DispatchVoyageCalled, "Dispatch must proceed");
                    AssertNotNull(admiralDouble.LastMissionDescriptions[0].PrestagedFiles, "Generated pack must be staged");
                    AssertEqual(generatedPackPath, admiralDouble.LastMissionDescriptions[0].PrestagedFiles![0].SourcePath);
                }
            });

            await RunTest("Dispatch_Force_CacheMiss_LazyWarmHitsCache_SkipsGeneration", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("lazy-warm-force-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    string warmedPackPath = Path.Combine(Path.GetTempPath(), "lazy-warm-context-pack.md");
                    ContextPackResponse warmedPack = new ContextPackResponse
                    {
                        Goal = "warmed goal",
                        MaterializedPath = warmedPackPath,
                        Metrics = new ContextPackMetrics { CacheHit = true, CacheKey = "warm123" }
                    };
                    warmedPack.PrestagedFiles.Add(new PrestagedFile(warmedPackPath, "_briefing/context-pack.md"));

                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService
                    {
                        SecondCacheLookupResponse = warmedPack
                    };
                    codeIndex.ContextPackResponse.PrestagedFiles.Add(new PrestagedFile(
                        Path.Combine(Path.GetTempPath(), "should-not-generate.md"), "_briefing/context-pack.md"));

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        codeIndex);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "lazy warm force voyage",
                        vesselId = vessel.Id,
                        codeContextMode = "force",
                        missions = new object[]
                        {
                            new { title = "Task D", description = "force must warm then use cached pack" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);
                    AssertEqual(2, codeIndex.CacheRequests.Count, "Cache must be checked before and after warm");
                    AssertEqual(1, codeIndex.WarmBaselineCacheVesselIds.Count, "Baseline warm must run on cache miss");
                    AssertEqual(vessel.Id, codeIndex.WarmBaselineCacheVesselIds[0]);
                    AssertEqual(0, codeIndex.ContextPackRequests.Count, "Generation must be skipped when warm produces cache hit");
                    AssertTrue(admiralDouble.DispatchVoyageCalled, "Dispatch must proceed");
                    AssertNotNull(admiralDouble.LastMissionDescriptions[0].PrestagedFiles, "Warmed pack must be staged");
                    AssertEqual(warmedPackPath, admiralDouble.LastMissionDescriptions[0].PrestagedFiles![0].SourcePath);
                }
            });

            await RunTest("Dispatch_CodeContextAuto_CacheMiss_NeverCallsWarm", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("warm-never-called-auto-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    // WarmBaselineCacheException would surface if WarmBaselineCacheAsync were
                    // called; its presence proves auto mode never reaches warm on a cache miss.
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService
                    {
                        WarmBaselineCacheException = new InvalidOperationException("warm must not be called")
                    };

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        codeIndex);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "auto no warm voyage",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { title = "Task E", description = "auto must not call warm on cache miss" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Auto mode must not error: " + resultJson);
                    AssertEqual(1, codeIndex.CacheRequests.Count, "Only one cache lookup runs for auto miss");
                    AssertEqual(0, codeIndex.WarmBaselineCacheVesselIds.Count, "Auto mode must NOT call warm on cache miss");
                    AssertEqual(0, codeIndex.ContextPackRequests.Count, "Auto mode must NOT call build on cache miss");
                    AssertTrue(admiralDouble.DispatchVoyageCalled, "Dispatch must proceed");
                    AssertEqual("auto", admiralDouble.LastMissionDescriptions[0].CodeContextMode, "Deferred intent must be set");
                    AssertNull(admiralDouble.LastMissionDescriptions[0].PrestagedFiles, "No pack should be staged for deferred auto");
                }
            });

            await RunTest("Dispatch_WarmThrows_ForceMode_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("warm-throw-force-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService
                    {
                        WarmBaselineCacheException = new InvalidOperationException("warm boom")
                    };

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        codeIndex);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "warm throw force voyage",
                        vesselId = vessel.Id,
                        codeContextMode = "force",
                        missions = new object[]
                        {
                            new { title = "Task F", description = "warm throws and force must surface error" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertTrue(resultJson.Contains("\"Error\""), "Force mode must surface warm failure as an error: " + resultJson);
                    AssertTrue(resultJson.Contains("warm boom"), "Error must carry the underlying failure message: " + resultJson);
                    AssertEqual(1, codeIndex.WarmBaselineCacheVesselIds.Count, "Baseline warm must have been attempted");
                    AssertEqual(0, codeIndex.ContextPackRequests.Count, "Generation must NOT run after warm throws");
                    AssertFalse(admiralDouble.DispatchVoyageCalled, "Dispatch must NOT proceed when force code context fails");
                }
            });

            await RunTest("Dispatch_ForceMode_LazyWarmHitsCache_Succeeds", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("force-warm-hit-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    string warmedPackPath = Path.Combine(Path.GetTempPath(), "force-warm-context-pack.md");
                    ContextPackResponse warmedPack = new ContextPackResponse
                    {
                        Goal = "force warmed goal",
                        MaterializedPath = warmedPackPath,
                        Metrics = new ContextPackMetrics { CacheHit = true, CacheKey = "forcewarm" }
                    };
                    warmedPack.PrestagedFiles.Add(new PrestagedFile(warmedPackPath, "_briefing/context-pack.md"));

                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService
                    {
                        SecondCacheLookupResponse = warmedPack
                    };
                    codeIndex.ContextPackResponse.PrestagedFiles.Add(new PrestagedFile(
                        Path.Combine(Path.GetTempPath(), "force-should-not-generate.md"), "_briefing/context-pack.md"));

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        codeIndex);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "force warm voyage",
                        vesselId = vessel.Id,
                        codeContextMode = "force",
                        missions = new object[]
                        {
                            new { title = "Task G", description = "force mode warms then uses cached pack" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Force mode must succeed when warm produces a cache hit: " + resultJson);
                    AssertEqual(2, codeIndex.CacheRequests.Count, "Cache must be checked before and after warm");
                    AssertEqual(1, codeIndex.WarmBaselineCacheVesselIds.Count, "Baseline warm must run on cold force dispatch");
                    AssertEqual(0, codeIndex.ContextPackRequests.Count, "Generation must be skipped when warm satisfies force mode");
                    AssertTrue(admiralDouble.DispatchVoyageCalled, "Dispatch must proceed");
                    AssertNotNull(admiralDouble.LastMissionDescriptions[0].PrestagedFiles, "Warmed pack must be staged");
                    AssertEqual(warmedPackPath, admiralDouble.LastMissionDescriptions[0].PrestagedFiles![0].SourcePath);
                }
            });

            await RunTest("Dispatch_DefaultDispatchTimeout_IsAtLeast60Seconds", () =>
            {
                string? priorTimeout = Environment.GetEnvironmentVariable("ARMADA_CODE_CONTEXT_TIMEOUT_MS");
                Environment.SetEnvironmentVariable("ARMADA_CODE_CONTEXT_TIMEOUT_MS", null);
                try
                {
                    TimeSpan defaultTimeout = CodeContextTimeouts.Resolve(CodeContextTimeouts.DefaultDispatchTimeoutMs);
                    AssertTrue(defaultTimeout.TotalSeconds >= 60,
                        "Default dispatch timeout must be at least 60 seconds; got " + defaultTimeout.TotalSeconds + " seconds");
                    AssertTrue(defaultTimeout.TotalSeconds <= 90,
                        "Default dispatch timeout must be at most 90 seconds; got " + defaultTimeout.TotalSeconds + " seconds");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("ARMADA_CODE_CONTEXT_TIMEOUT_MS", priorTimeout);
                }
                return Task.CompletedTask;
            });

            await RunTest("Dispatch_Force_CachedPackWithEmptyPrestagedFiles_FallsBackToGeneration", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("cache-empty-force-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    // Cache returns a non-null response, but with NO prestaged files. The dispatch
                    // guard (PrestagedFiles.Count > 0) must treat this as a miss and generate (force).
                    ContextPackResponse emptyCachedPack = new ContextPackResponse
                    {
                        Goal = "empty cached pack",
                        MaterializedPath = Path.Combine(Path.GetTempPath(), "empty-cache.md"),
                        Metrics = new ContextPackMetrics { CacheHit = true, CacheKey = "empty" }
                    };

                    string generatedPackPath = Path.Combine(Path.GetTempPath(), "fallback-after-empty.md");
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService
                    {
                        CachedResponse = emptyCachedPack
                    };
                    codeIndex.ContextPackResponse.PrestagedFiles.Add(new PrestagedFile(
                        generatedPackPath, "_briefing/context-pack.md"));

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        codeIndex);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "empty cache force voyage",
                        vesselId = vessel.Id,
                        codeContextMode = "force",
                        missions = new object[]
                        {
                            new { title = "Task C", description = "force must not short-circuit on empty cached pack" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);
                    AssertEqual(2, codeIndex.CacheRequests.Count, "Cache lookup must be attempted before and after warm");
                    AssertEqual(1, codeIndex.WarmBaselineCacheVesselIds.Count, "Baseline warm must run when first cache entry is empty");
                    AssertEqual(1, codeIndex.ContextPackRequests.Count,
                        "Empty cached prestaged files must NOT short-circuit; generation must run");
                    AssertTrue(admiralDouble.DispatchVoyageCalled, "Dispatch must proceed");
                    AssertNotNull(admiralDouble.LastMissionDescriptions[0].PrestagedFiles, "Generated pack must be staged");
                    AssertEqual(generatedPackPath, admiralDouble.LastMissionDescriptions[0].PrestagedFiles![0].SourcePath);
                }
            });

            await RunTest("Dispatch_Force_CachedPackWithNullPrestagedFiles_FallsBackToGeneration", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("cache-null-force-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    // Cache returns a non-null response whose PrestagedFiles is explicitly null.
                    // The dispatch guard's null check must treat this as a miss and generate (force).
                    ContextPackResponse nullCachedPack = new ContextPackResponse
                    {
                        Goal = "null cached pack",
                        MaterializedPath = Path.Combine(Path.GetTempPath(), "null-cache.md"),
                        Metrics = new ContextPackMetrics { CacheHit = true, CacheKey = "null" },
                        PrestagedFiles = null!
                    };

                    string generatedPackPath = Path.Combine(Path.GetTempPath(), "fallback-after-null.md");
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService
                    {
                        CachedResponse = nullCachedPack
                    };
                    codeIndex.ContextPackResponse.PrestagedFiles.Add(new PrestagedFile(
                        generatedPackPath, "_briefing/context-pack.md"));

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        null,
                        codeIndex);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "null cache force voyage",
                        vesselId = vessel.Id,
                        codeContextMode = "force",
                        missions = new object[]
                        {
                            new { title = "Task D", description = "force must not short-circuit on null cached prestaged files" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);
                    AssertEqual(2, codeIndex.CacheRequests.Count, "Cache lookup must be attempted before and after warm");
                    AssertEqual(1, codeIndex.WarmBaselineCacheVesselIds.Count, "Baseline warm must run when first cache entry has null prestaged files");
                    AssertEqual(1, codeIndex.ContextPackRequests.Count,
                        "Null cached prestaged files must NOT short-circuit; generation must run");
                    AssertTrue(admiralDouble.DispatchVoyageCalled, "Dispatch must proceed");
                    AssertNotNull(admiralDouble.LastMissionDescriptions[0].PrestagedFiles, "Generated pack must be staged");
                    AssertEqual(generatedPackPath, admiralDouble.LastMissionDescriptions[0].PrestagedFiles![0].SourcePath);
                }
            });

            await RunTest("Dispatch_EmptyMissions_ReturnsStructuredError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("empty-missions-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "empty missions voyage",
                        description = "no missions supplied",
                        vesselId = vessel.Id,
                        missions = new object[] { }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("\"Error\"", resultJson);
                    AssertContains("missions", resultJson);
                    AssertContains("missing_missions", resultJson);
                    AssertFalse(resultJson.Contains("-32603"), "Validation must not surface a bare JSON-RPC -32603: " + resultJson);
                    AssertFalse(resultJson.Contains("vessel_not_found"), "Missions validation must fire, not vessel_not_found: " + resultJson);
                    AssertFalse(admiralDouble.DispatchVoyageCalled, "Admiral must not be reached on empty missions");
                }
            });

            await RunTest("Dispatch_MissionMissingTitle_ReturnsStructuredError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("mission-no-title-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "missing mission title voyage",
                        description = "one mission lacks a title",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { description = "has description but no title" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("\"Error\"", resultJson);
                    AssertContains("title", resultJson);
                    AssertContains("missing_mission_title", resultJson);
                    AssertFalse(resultJson.Contains("-32603"), "Validation must not surface a bare JSON-RPC -32603: " + resultJson);
                    AssertFalse(resultJson.Contains("vessel_not_found"), "Mission title validation must fire, not vessel_not_found: " + resultJson);
                    AssertFalse(admiralDouble.DispatchVoyageCalled, "Admiral must not be reached when a mission lacks a title");
                }
            });

            await RunTest("Dispatch_MissionMissingDescription_ReturnsStructuredError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("mission-no-desc-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "missing mission description voyage",
                        description = "one mission lacks a description",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { title = "has title but no description" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("\"Error\"", resultJson);
                    AssertContains("description", resultJson);
                    AssertContains("missing_mission_description", resultJson);
                    AssertFalse(resultJson.Contains("-32603"), "Validation must not surface a bare JSON-RPC -32603: " + resultJson);
                    AssertFalse(resultJson.Contains("vessel_not_found"), "Mission description validation must fire, not vessel_not_found: " + resultJson);
                    AssertFalse(admiralDouble.DispatchVoyageCalled, "Admiral must not be reached when a mission lacks a description");
                }
            });

            await RunTest("Dispatch_MissingTitle_ReturnsStructuredError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("missing-title-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler);

                    // Title is whitespace-only: exercises the IsNullOrWhiteSpace branch
                    // (not merely an absent field). A well-formed mission is supplied so
                    // the title guard -- not the missions guard -- is the failing check.
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "   ",
                        description = "voyage with a blank title",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { title = "valid mission", description = "valid description" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("\"Error\"", resultJson);
                    AssertContains("title", resultJson);
                    AssertContains("missing_title", resultJson);
                    AssertFalse(resultJson.Contains("missing_missions"), "Title guard must fire before missions guard: " + resultJson);
                    AssertFalse(resultJson.Contains("-32603"), "Validation must not surface a bare JSON-RPC -32603: " + resultJson);
                    AssertFalse(resultJson.Contains("vessel_not_found"), "Title validation must fire before the vessel read: " + resultJson);
                    AssertFalse(admiralDouble.DispatchVoyageCalled, "Admiral must not be reached when the voyage title is blank");
                }
            });

            await RunTest("Dispatch_SecondMissionMissingDescription_NamesMissionIndex", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("second-mission-bad-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler);

                    // First mission is valid; the SECOND mission lacks a description.
                    // This proves the 1-based index is actionable and points at the
                    // offending mission (mission 2), not always the first one.
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "second mission bad voyage",
                        description = "first mission ok, second lacks a description",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { title = "first valid mission", description = "first description" },
                            new { title = "second mission has a title only" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("\"Error\"", resultJson);
                    AssertContains("missing_mission_description", resultJson);
                    AssertContains("mission 2", resultJson);
                    AssertFalse(resultJson.Contains("mission 1"), "Error must name the offending mission (2), not the valid first mission: " + resultJson);
                    AssertFalse(resultJson.Contains("-32603"), "Validation must not surface a bare JSON-RPC -32603: " + resultJson);
                    AssertFalse(resultJson.Contains("vessel_not_found"), "Mission validation must fire, not vessel_not_found: " + resultJson);
                    AssertFalse(admiralDouble.DispatchVoyageCalled, "Admiral must not be reached when a later mission is malformed");
                }
            });

            await RunTest("Dispatch_WhitespaceMissionTitle_ReturnsStructuredError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("whitespace-mission-title-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler);

                    // Mission title is present but whitespace-only: a JSON-schema
                    // "required" check would pass this, so the runtime IsNullOrWhiteSpace
                    // guard is the only thing that catches it.
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "whitespace mission title voyage",
                        description = "mission title is blank",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { title = "   ", description = "has a description but a blank title" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("\"Error\"", resultJson);
                    AssertContains("missing_mission_title", resultJson);
                    AssertContains("mission 1", resultJson);
                    AssertFalse(resultJson.Contains("-32603"), "Validation must not surface a bare JSON-RPC -32603: " + resultJson);
                    AssertFalse(resultJson.Contains("vessel_not_found"), "Mission title validation must fire, not vessel_not_found: " + resultJson);
                    AssertFalse(admiralDouble.DispatchVoyageCalled, "Admiral must not be reached when a mission title is whitespace");
                }
            });

            await RunTest("Dispatch_AbsentMissions_ReturnsStructuredError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("absent-missions-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler);

                    // The missions field is omitted entirely. VoyageDispatchArgs.Missions
                    // initializes to an empty list, so the absent case lands on the same
                    // missing_missions guard as an explicit empty array. The acceptance
                    // names "empty or absent missions input"; this proves the absent path.
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "absent missions voyage",
                        description = "missions field omitted",
                        vesselId = vessel.Id
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("\"Error\"", resultJson);
                    AssertContains("missions", resultJson);
                    AssertContains("missing_missions", resultJson);
                    // Structured error must carry the actionable Reason/Action fields, not
                    // just an Error string -- the whole point of the structured contract.
                    AssertContains("\"Reason\"", resultJson);
                    AssertContains("\"Action\"", resultJson);
                    AssertFalse(resultJson.Contains("-32603"), "Validation must not surface a bare JSON-RPC -32603: " + resultJson);
                    AssertFalse(resultJson.Contains("vessel_not_found"), "Missions validation must fire, not vessel_not_found: " + resultJson);
                    AssertFalse(admiralDouble.DispatchVoyageCalled, "Admiral must not be reached when missions is absent");
                }
            });

            await RunTest("Dispatch_NullMissions_ReturnsStructuredError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("null-missions-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler);

                    // An explicit null missions value deserializes the property to null,
                    // exercising the "missions == null" half of the guard distinctly from
                    // the empty-array (Count == 0) tests above.
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "null missions voyage",
                        description = "missions field is explicitly null",
                        vesselId = vessel.Id,
                        missions = (object?)null
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("\"Error\"", resultJson);
                    AssertContains("missing_missions", resultJson);
                    AssertFalse(resultJson.Contains("-32603"), "Validation must not surface a bare JSON-RPC -32603: " + resultJson);
                    AssertFalse(resultJson.Contains("vessel_not_found"), "Missions validation must fire before the vessel read: " + resultJson);
                    AssertFalse(admiralDouble.DispatchVoyageCalled, "Admiral must not be reached when missions is null");
                }
            });

            await RunTest("Dispatch_WhitespaceMissionDescription_ReturnsStructuredError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("whitespace-mission-desc-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler);

                    // Description is present but whitespace-only: a JSON-schema "required"
                    // check would pass this, so the runtime IsNullOrWhiteSpace guard is the
                    // only thing that rejects it. The prior description test used an absent
                    // field; this proves the whitespace branch too.
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "whitespace mission description voyage",
                        description = "mission description is blank",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { title = "valid mission title", description = "   " }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("\"Error\"", resultJson);
                    AssertContains("missing_mission_description", resultJson);
                    AssertContains("mission 1", resultJson);
                    AssertFalse(resultJson.Contains("-32603"), "Validation must not surface a bare JSON-RPC -32603: " + resultJson);
                    AssertFalse(resultJson.Contains("vessel_not_found"), "Mission description validation must fire, not vessel_not_found: " + resultJson);
                    AssertFalse(admiralDouble.DispatchVoyageCalled, "Admiral must not be reached when a mission description is whitespace");
                }
            });
        }

        /// <summary>
        /// Recording test double for IAdmiralService. Captures DispatchMissionAsync
        /// calls (alias path) and records whether DispatchVoyageAsync was called
        /// (legacy path). Assigns synthetic but unique mission IDs to each dispatched
        /// mission so alias resolution can be verified by the caller.
        /// </summary>
        private sealed class RecordingAdmiralDouble : IAdmiralService
        {
            private int _NextMsnSeq = 1;

            /// <summary>Missions dispatched via DispatchMissionAsync (alias path).</summary>
            public List<Mission> DispatchedMissions { get; } = new List<Mission>();

            /// <summary>True when DispatchVoyageAsync was invoked (legacy path).</summary>
            public bool DispatchVoyageCalled { get; private set; }

            /// <summary>Mission descriptions passed to the most recent voyage dispatch call.</summary>
            public List<MissionDescription> LastMissionDescriptions { get; private set; } = new List<MissionDescription>();

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
                mission.Id = "msn_test_" + _NextMsnSeq.ToString("D4");
                _NextMsnSeq++;
                mission.Status = MissionStatusEnum.Pending;
                DispatchedMissions.Add(mission);
                return Task.FromResult(mission);
            }

            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                CancellationToken token = default)
            {
                DispatchVoyageCalled = true;
                CaptureMissionDescriptions(missionDescriptions);
                Voyage voyage = new Voyage(title, description);
                voyage.Id = "vyg_test_0001";
                voyage.Status = VoyageStatusEnum.Open;
                return Task.FromResult(voyage);
            }

            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
            {
                DispatchVoyageCalled = true;
                CaptureMissionDescriptions(missionDescriptions);
                Voyage voyage = new Voyage(title, description);
                voyage.Id = "vyg_test_0001";
                voyage.Status = VoyageStatusEnum.Open;
                return Task.FromResult(voyage);
            }

            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                CancellationToken token = default)
            {
                DispatchVoyageCalled = true;
                CaptureMissionDescriptions(missionDescriptions);
                Voyage voyage = new Voyage(title, description);
                voyage.Id = "vyg_test_0001";
                voyage.Status = VoyageStatusEnum.Open;
                return Task.FromResult(voyage);
            }

            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
            {
                DispatchVoyageCalled = true;
                CaptureMissionDescriptions(missionDescriptions);
                Voyage voyage = new Voyage(title, description);
                voyage.Id = "vyg_test_0001";
                voyage.Status = VoyageStatusEnum.Open;
                return Task.FromResult(voyage);
            }

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => Task.FromResult<Pipeline?>(null);

            private void CaptureMissionDescriptions(List<MissionDescription> missionDescriptions)
            {
                LastMissionDescriptions = new List<MissionDescription>();
                if (missionDescriptions != null)
                    LastMissionDescriptions.AddRange(missionDescriptions);
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

            public Task HandleProcessExitAsync(
                int processId, int? exitCode, string captainId, string missionId,
                CancellationToken token = default)
                => throw new NotImplementedException();
        }

        /// <summary>
        /// Persists voyages and missions from DispatchVoyageAsync for reflection drain coverage
        /// (matches the RecordingAdmiralService pattern used in reflection audit drain tests).
        /// </summary>
        private sealed class ReflectionDrainRecordingAdmiral : IAdmiralService
        {
            private readonly DatabaseDriver _Database;

            public ReflectionDrainRecordingAdmiral(DatabaseDriver database)
            {
                _Database = database;
            }

            public int DispatchCount { get; private set; }

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
                return DispatchVoyageAsync(title, description, vesselId, missionDescriptions, (string?)null, token);
            }

            public Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
            {
                return DispatchVoyageAsync(title, description, vesselId, missionDescriptions, (string?)null, token);
            }

            public async Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                CancellationToken token = default)
            {
                DispatchCount++;
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
                    mission.Persona = pipelineId == "Reflections" ? "MemoryConsolidator" : "Worker";
                    mission.PreferredModel = md.PreferredModel;
                    await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                }

                return voyage;
            }

            public Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
            {
                return DispatchVoyageAsync(title, description, vesselId, missionDescriptions, pipelineId, token);
            }

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
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
                int processId,
                int? exitCode,
                string captainId,
                string missionId,
                CancellationToken token = default)
                => throw new NotImplementedException();
        }

        private sealed class RecordingCodeIndexService : ICodeIndexService
        {
            private int _CacheLookupCount;

            public List<ContextPackRequest> ContextPackRequests { get; } = new List<ContextPackRequest>();

            public List<ContextPackRequest> CacheRequests { get; } = new List<ContextPackRequest>();

            public List<string> WarmBaselineCacheVesselIds { get; } = new List<string>();

            public ContextPackResponse ContextPackResponse { get; } = new ContextPackResponse();

            public ContextPackResponse? CachedResponse { get; set; }

            public ContextPackResponse? SecondCacheLookupResponse { get; set; }

            public CodeIndexStatus Status { get; } = new CodeIndexStatus();

            public Exception? BuildException { get; set; }

            public Exception? WarmBaselineCacheException { get; set; }

            public bool NeverCompleteBuild { get; set; }

            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
            {
                Status.VesselId = vesselId;
                if (String.IsNullOrWhiteSpace(Status.VesselName))
                    Status.VesselName = vesselId;
                return Task.FromResult(Status);
            }

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public async Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
            {
                ContextPackRequests.Add(request);
                if (BuildException != null) throw BuildException;
                if (NeverCompleteBuild)
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);

                return ContextPackResponse;
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
            {
                WarmBaselineCacheVesselIds.Add(vesselId ?? "");
                if (WarmBaselineCacheException != null) throw WarmBaselineCacheException;
                return Task.CompletedTask;
            }

            public Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default)
            {
                CacheRequests.Add(request);
                _CacheLookupCount++;
                if (_CacheLookupCount == 1)
                    return Task.FromResult<ContextPackResponse?>(CachedResponse);

                return Task.FromResult<ContextPackResponse?>(SecondCacheLookupResponse);
            }
        }
    }
}
