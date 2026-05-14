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

            await RunTest("Dispatch_CodeContextAuto_AttachesContextPackAndPreservesPrestagedFiles", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("code-context-auto-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService();
                    codeIndex.ContextPackResponse.PrestagedFiles.Add(new PrestagedFile(
                        Path.Combine(Path.GetTempPath(), "context-pack-auto.md"),
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
                    AssertEqual(1, codeIndex.ContextPackRequests.Count, "Auto mode should request one context pack");
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

            await RunTest("Dispatch_CodeContextAuto_ContinuesWhenGenerationFails", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("code-context-auto-failure-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                    RecordingCodeIndexService codeIndex = new RecordingCodeIndexService();
                    codeIndex.BuildException = new InvalidOperationException("index offline");

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
                        title = "auto failure voyage",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { title = "Task A", description = "Auto mode should continue" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Auto mode should continue after context generation failure: " + resultJson);
                    AssertEqual(1, codeIndex.ContextPackRequests.Count, "Auto mode should attempt context generation");
                    AssertTrue(admiralDouble.DispatchVoyageCalled, "Auto generation failure should not block dispatch persistence");
                    AssertEqual(1, admiralDouble.LastMissionDescriptions.Count, "Dispatch should still receive the mission");
                    AssertNull(admiralDouble.LastMissionDescriptions[0].PrestagedFiles, "Failed auto generation should not add prestaged files");
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
                Voyage voyage = await _Database.Voyages.CreateAsync(new Voyage(title, description), token).ConfigureAwait(false);
                foreach (MissionDescription md in missionDescriptions)
                {
                    Mission mission = new Mission(md.Title, md.Description);
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
            public List<ContextPackRequest> ContextPackRequests { get; } = new List<ContextPackRequest>();

            public ContextPackResponse ContextPackResponse { get; } = new ContextPackResponse();

            public Exception? BuildException { get; set; }

            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
            {
                ContextPackRequests.Add(request);
                if (BuildException != null) throw BuildException;
                return Task.FromResult(ContextPackResponse);
            }

            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default)
                => throw new NotImplementedException();
        }
    }
}
