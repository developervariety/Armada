namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
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

            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
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
                Voyage voyage = new Voyage(title, description);
                voyage.Id = "vyg_test_0001";
                voyage.Status = VoyageStatusEnum.Open;
                return Task.FromResult(voyage);
            }

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
