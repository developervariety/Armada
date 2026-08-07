namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for <see cref="PromptBudgetSummary.FromEventPayloads"/> and the
    /// armada_mission_status prompt-budget projection (obj_ms6vukad: mission status
    /// exposes the measured prompt-component sizes).
    /// </summary>
    public class PromptBudgetSummaryTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Prompt Budget Summary";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("FromEventPayloads_ParsesBudgetAndLaunchBytes", () =>
            {
                string payload = JsonSerializer.Serialize(new
                {
                    MissionId = "msn_budget01",
                    Runtime = "ClaudeCode",
                    InstructionsRelativePath = ".armada/instructions/CLAUDE.md",
                    InstructionFileBytes = 12000,
                    TrackedModuleBytes = 11800,
                    ModuleCount = 9,
                    ByteBudget = 32768,
                    OverBudget = false,
                    Modules = new Dictionary<string, int>
                    {
                        { "mission.rules", 774 },
                        { "mission.ai_memory", 900 }
                    }
                });
                string launchPayload = JsonSerializer.Serialize(new
                {
                    MissionId = "msn_budget01",
                    Runtime = "ClaudeCode",
                    LaunchPromptBytes = 1278
                });

                PromptBudgetSummary? summary = PromptBudgetSummary.FromEventPayloads(payload, launchPayload);
                AssertNotNull(summary, "Budget payload must parse");
                AssertEqual("msn_budget01", summary!.MissionId);
                AssertEqual(12000, summary.InstructionFileBytes);
                AssertEqual(11800, summary.TrackedModuleBytes);
                AssertEqual(9, summary.ModuleCount);
                AssertEqual(32768, summary.ByteBudget);
                AssertFalse(summary.OverBudget);
                AssertEqual(2, summary.Modules.Count);
                AssertEqual(774, summary.Modules["mission.rules"]);
                AssertEqual(900, summary.Modules["mission.ai_memory"]);
                AssertEqual(1278, summary.LaunchPromptBytes);
                return Task.CompletedTask;
            });

            await RunTest("FromEventPayloads_OverBudgetFlagPreserved", () =>
            {
                string payload = JsonSerializer.Serialize(new
                {
                    MissionId = "msn_budget02",
                    InstructionFileBytes = 106750,
                    TrackedModuleBytes = 106000,
                    ModuleCount = 12,
                    ByteBudget = 32768,
                    OverBudget = true,
                    Modules = new Dictionary<string, int>()
                });

                PromptBudgetSummary? summary = PromptBudgetSummary.FromEventPayloads(payload, null);
                AssertNotNull(summary, "Over-budget payload must parse");
                AssertTrue(summary!.OverBudget, "OverBudget must be preserved");
                AssertNull(summary.LaunchPromptBytes, "LaunchPromptBytes must be null without a launch event");
                return Task.CompletedTask;
            });

            await RunTest("FromEventPayloads_RejectsEmptyMalformedAndNull", () =>
            {
                AssertNull(PromptBudgetSummary.FromEventPayloads(null, null), "Null payload must yield null");
                AssertNull(PromptBudgetSummary.FromEventPayloads("", null), "Empty payload must yield null");
                AssertNull(PromptBudgetSummary.FromEventPayloads("   \t\n", null), "Whitespace payload must yield null");
                AssertNull(PromptBudgetSummary.FromEventPayloads("{ not valid json %%", null), "Malformed payload must yield null");
                AssertNull(PromptBudgetSummary.FromEventPayloads("null", null), "Literal null payload must yield null");
                return Task.CompletedTask;
            });

            await RunTest("MissionStatus_WithBudgetEvents_PopulatesPromptBudget", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("budget-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    Mission mission = new Mission("budget-test-mission");
                    mission.VesselId = vessel.Id;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    string budgetPayload = JsonSerializer.Serialize(new
                    {
                        MissionId = mission.Id,
                        Runtime = "ClaudeCode",
                        InstructionsRelativePath = ".armada/instructions/CLAUDE.md",
                        InstructionFileBytes = 12090,
                        TrackedModuleBytes = 11900,
                        ModuleCount = 10,
                        ByteBudget = 32768,
                        OverBudget = false,
                        Modules = new Dictionary<string, int> { { "mission.rules", 774 } }
                    });
                    string launchPayload = JsonSerializer.Serialize(new
                    {
                        MissionId = mission.Id,
                        Runtime = "ClaudeCode",
                        LaunchPromptBytes = 1278
                    });

                    ArmadaEvent budgetEvent = new ArmadaEvent("mission.prompt_budget", "budget test");
                    budgetEvent.MissionId = mission.Id;
                    budgetEvent.EntityType = "mission";
                    budgetEvent.EntityId = mission.Id;
                    budgetEvent.Payload = budgetPayload;
                    await testDb.Driver.Events.CreateAsync(budgetEvent).ConfigureAwait(false);

                    ArmadaEvent launchEvent = new ArmadaEvent("mission.launch_prompt_budget", "launch budget test");
                    launchEvent.MissionId = mission.Id;
                    launchEvent.EntityType = "mission";
                    launchEvent.EntityId = mission.Id;
                    launchEvent.Payload = launchPayload;
                    await testDb.Driver.Events.CreateAsync(launchEvent).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_mission_status") statusHandler = handler; },
                        testDb.Driver,
                        new NullAdmiralDouble(),
                        null,
                        null);

                    AssertNotNull(statusHandler, "armada_mission_status handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await statusHandler!(args).ConfigureAwait(false);

                    AssertFalse(JsonSerializer.Serialize(result).Contains("\"Error\""), "Should not return error");

                    Mission? resultMission = result as Mission;
                    AssertNotNull(resultMission, "handler must return a Mission instance");
                    AssertNotNull(resultMission!.PromptBudget, "PromptBudget must be populated when events exist");
                    AssertEqual(12090, resultMission.PromptBudget!.InstructionFileBytes);
                    AssertEqual(1278, resultMission.PromptBudget.LaunchPromptBytes);
                    AssertEqual(1, resultMission.PromptBudget.Modules.Count);
                }
            });

            await RunTest("MissionStatus_NoBudgetEvents_PromptBudgetNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("budget-none-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    Mission mission = new Mission("budget-none-mission");
                    mission.VesselId = vessel.Id;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_mission_status") statusHandler = handler; },
                        testDb.Driver,
                        new NullAdmiralDouble(),
                        null,
                        null);

                    AssertNotNull(statusHandler, "armada_mission_status handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await statusHandler!(args).ConfigureAwait(false);

                    Mission? resultMission = result as Mission;
                    AssertNotNull(resultMission, "handler must return a Mission instance");
                    AssertNull(resultMission!.PromptBudget, "PromptBudget must be null when no budget event exists");
                }
            });
        }

        /// <summary>Stub admiral that throws on any use; the status handler never invokes it.</summary>
        private sealed class NullAdmiralDouble : IAdmiralService
        {
            /// <summary>Not used in status tests.</summary>
            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }

            /// <summary>Not used in status tests.</summary>
            public Func<Captain, Task>? OnStopAgent { get; set; }

            /// <summary>Not used in status tests.</summary>
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }

            /// <summary>Not used in status tests.</summary>
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }

            /// <summary>Not used in status tests.</summary>
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }

            /// <summary>Not used in status tests.</summary>
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }

            /// <summary>Not used in status tests.</summary>
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }

            /// <summary>Not used in status tests.</summary>
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            /// <summary>Not used in status tests.</summary>
            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not used in status tests.</summary>
            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not used in status tests.</summary>
            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not used in status tests.</summary>
            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not used in status tests.</summary>
            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not used in status tests.</summary>
            public Task<Pipeline?> ResolvePipelineAsync(
                string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => Task.FromResult<Pipeline?>(null);

            /// <summary>Not used in status tests.</summary>
            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not used in status tests.</summary>
            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not used in status tests.</summary>
            public Task RecallAllAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not used in status tests.</summary>
            public Task HealthCheckAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not used in status tests.</summary>
            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not used in status tests.</summary>
            public Task HandleProcessExitAsync(
                int processId, int? exitCode, string captainId, string missionId,
                CancellationToken token = default)
                => throw new NotImplementedException();
        }
    }
}
