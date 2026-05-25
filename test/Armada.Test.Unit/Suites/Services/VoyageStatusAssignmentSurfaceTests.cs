namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests that AssignmentState is surfaced via armada_voyage_status and armada_mission_status,
    /// and that armada_dispatch returns structured Code/Reason/Action errors for bad vessel/pipeline.
    /// </summary>
    public class VoyageStatusAssignmentSurfaceTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Voyage status assignment surface";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("VoyageStatusSummary_IncludesMissionCountsByAssignmentState_WithCorrectGrouping", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("assignment-state-summary-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(
                        new Voyage("assignment state voyage", "")).ConfigureAwait(false);

                    // Seed 4 missions: 2 WaitingForVesselMutex, 1 WaitingForIdleCaptain, 1 Provisioning.
                    for (int i = 0; i < 4; i++)
                    {
                        Mission m = new Mission("M" + i, "d" + i);
                        m.VoyageId = voyage.Id;
                        m.VesselId = vessel.Id;
                        m = await testDb.Driver.Missions.CreateAsync(m).ConfigureAwait(false);

                        if (i < 2)
                            m.AssignmentState = MissionAssignmentStateEnum.WaitingForVesselMutex;
                        else if (i == 2)
                            m.AssignmentState = MissionAssignmentStateEnum.WaitingForIdleCaptain;
                        else
                            m.AssignmentState = MissionAssignmentStateEnum.Provisioning;

                        await testDb.Driver.Missions.UpdateAsync(m).ConfigureAwait(false);
                    }

                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_voyage_status") statusHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(statusHandler, "armada_voyage_status handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { voyageId = voyage.Id });
                    object result = await statusHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("MissionCountsByAssignmentState", json);
                    AssertContains("WaitingForVesselMutex", json);
                    AssertContains("WaitingForIdleCaptain", json);
                    AssertContains("Provisioning", json);

                    // Verify the actual counts by deserializing the relevant sub-object.
                    JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement countsEl = doc.RootElement.GetProperty("MissionCountsByAssignmentState");
                    AssertEqual(2, countsEl.GetProperty("WaitingForVesselMutex").GetInt32());
                    AssertEqual(1, countsEl.GetProperty("WaitingForIdleCaptain").GetInt32());
                    AssertEqual(1, countsEl.GetProperty("Provisioning").GetInt32());
                }
            });

            await RunTest("VoyageStatusFull_MissionsIncludeAssignmentStateField", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("assignment-state-full-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(
                        new Voyage("full mode voyage", "")).ConfigureAwait(false);

                    Mission m1 = new Mission("M1", "d1");
                    m1.VoyageId = voyage.Id;
                    m1.VesselId = vessel.Id;
                    m1 = await testDb.Driver.Missions.CreateAsync(m1).ConfigureAwait(false);
                    m1.AssignmentState = MissionAssignmentStateEnum.WaitingForIdleCaptain;
                    await testDb.Driver.Missions.UpdateAsync(m1).ConfigureAwait(false);

                    Mission m2 = new Mission("M2", "d2");
                    m2.VoyageId = voyage.Id;
                    m2.VesselId = vessel.Id;
                    m2 = await testDb.Driver.Missions.CreateAsync(m2).ConfigureAwait(false);
                    m2.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    await testDb.Driver.Missions.UpdateAsync(m2).ConfigureAwait(false);

                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_voyage_status") statusHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(statusHandler, "armada_voyage_status handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { voyageId = voyage.Id, summary = false, includeMissions = true });
                    object result = await statusHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("AssignmentState", json);
                    AssertContains("WaitingForIdleCaptain", json);
                    AssertContains("Assigned", json);
                }
            });

            await RunTest("Dispatch_BadVesselId_ReturnsStructuredCodeReasonAction", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler, "armada_dispatch handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "bad vessel voyage",
                        vesselId = "vsl_does_not_exist",
                        missions = new object[] { new { title = "M1", description = "d1" } }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("Error", json);
                    AssertContains("vessel_not_found", json);
                    AssertContains("Reason", json);
                    AssertContains("Action", json);
                    AssertContains("vsl_does_not_exist", json);
                }
            });

            await RunTest("Dispatch_BadPipelineName_ReturnsStructuredCodeReasonAction", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("bad-pipeline-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler, "armada_dispatch handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "bad pipeline voyage",
                        vesselId = vessel.Id,
                        pipeline = "NonExistentPipeline",
                        missions = new object[] { new { title = "M1", description = "d1" } }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("Error", json);
                    AssertContains("pipeline_not_found", json);
                    AssertContains("Reason", json);
                    AssertContains("Action", json);
                    AssertContains("NonExistentPipeline", json);
                }
            });

            await RunTest("MissionStatus_ReturnsAssignmentState", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("mission-status-assignment-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Mission mission = new Mission("Assignment State Mission", "desc");
                    mission.VesselId = vessel.Id;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    mission.AssignmentState = MissionAssignmentStateEnum.WaitingForIdleCaptain;
                    await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);

                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_mission_status") statusHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null);

                    AssertNotNull(statusHandler, "armada_mission_status handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await statusHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("AssignmentState", json);
                    AssertContains("WaitingForIdleCaptain", json);
                }
            });

            // Edge case: voyage with zero missions. The summary handler runs
            // EnumerateSummariesAsync + GroupBy; a non-trivial guard is needed so
            // the empty-input GroupBy still produces a serializable (empty) dict
            // rather than throwing or omitting MissionCountsByAssignmentState.
            await RunTest("VoyageStatusSummary_EmptyVoyage_ReturnsEmptyMissionCountsByAssignmentState", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(
                        new Voyage("empty voyage", "")).ConfigureAwait(false);

                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_voyage_status") statusHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(statusHandler, "armada_voyage_status handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { voyageId = voyage.Id });
                    object result = await statusHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("MissionCountsByAssignmentState", json);

                    JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement countsEl = doc.RootElement.GetProperty("MissionCountsByAssignmentState");
                    AssertEqual(JsonValueKind.Object, countsEl.ValueKind, "MissionCountsByAssignmentState must be an object, not null");
                    AssertEqual(0, countsEl.EnumerateObject().Count(),
                        "MissionCountsByAssignmentState must be an empty object for an empty voyage");
                    AssertEqual(0, doc.RootElement.GetProperty("TotalMissions").GetInt32(),
                        "TotalMissions must be zero for an empty voyage");
                }
            });

            // Sanity: counts under MissionCountsByAssignmentState must sum to TotalMissions.
            // Catches future drift if someone adds filtering or a where-clause that drops missions.
            await RunTest("VoyageStatusSummary_AssignmentCountsSum_EqualsTotalMissions", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("sum-check-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(
                        new Voyage("sum check voyage", "")).ConfigureAwait(false);

                    // Seed 5 missions across 3 distinct AssignmentState values plus default Pending.
                    MissionAssignmentStateEnum[] states = new[]
                    {
                        MissionAssignmentStateEnum.Pending,
                        MissionAssignmentStateEnum.WaitingForDependency,
                        MissionAssignmentStateEnum.WaitingForIdleCaptain,
                        MissionAssignmentStateEnum.Failed,
                        MissionAssignmentStateEnum.Assigned
                    };

                    for (int i = 0; i < states.Length; i++)
                    {
                        Mission m = new Mission("M" + i, "d" + i);
                        m.VoyageId = voyage.Id;
                        m.VesselId = vessel.Id;
                        m = await testDb.Driver.Missions.CreateAsync(m).ConfigureAwait(false);
                        m.AssignmentState = states[i];
                        await testDb.Driver.Missions.UpdateAsync(m).ConfigureAwait(false);
                    }

                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_voyage_status") statusHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(statusHandler, "armada_voyage_status handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { voyageId = voyage.Id });
                    object result = await statusHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    JsonDocument doc = JsonDocument.Parse(json);
                    int totalMissions = doc.RootElement.GetProperty("TotalMissions").GetInt32();
                    int assignmentSum = 0;
                    foreach (JsonProperty entry in doc.RootElement.GetProperty("MissionCountsByAssignmentState").EnumerateObject())
                    {
                        assignmentSum += entry.Value.GetInt32();
                    }

                    AssertEqual(states.Length, totalMissions, "TotalMissions must include every seeded mission");
                    AssertEqual(totalMissions, assignmentSum,
                        "Sum of MissionCountsByAssignmentState values must equal TotalMissions (no missions lost in GroupBy)");
                }
            });

            // Validation order: vessel-not-found must short-circuit before the pipeline lookup.
            // If both vessel and pipeline are bad, the response must carry vessel_not_found so
            // operators fix the vessel reference first; otherwise they would chase a phantom
            // pipeline error and never learn the vessel id is wrong.
            await RunTest("Dispatch_BadVesselAndBadPipeline_VesselNotFoundWins", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler, "armada_dispatch handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "bad both voyage",
                        vesselId = "vsl_does_not_exist",
                        pipeline = "AlsoDoesNotExist",
                        missions = new object[] { new { title = "M1", description = "d1" } }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("vessel_not_found", json);
                    AssertFalse(json.Contains("pipeline_not_found"),
                        "Vessel check must short-circuit before pipeline lookup; pipeline_not_found must not appear");
                }
            });

            // Different enum value than the existing happy-path WaitingForIdleCaptain test.
            // Failed is the most operationally interesting state: dock provisioning or agent
            // launch threw and reverted the mission. Verifies the JsonStringEnumConverter
            // is in effect (Failed appears as a string, not the numeric enum value).
            await RunTest("MissionStatus_WithFailedAssignmentState_AppearsAsStringInResponse", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("mission-status-failed-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Mission mission = new Mission("Failed assignment mission", "desc");
                    mission.VesselId = vessel.Id;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    mission.AssignmentState = MissionAssignmentStateEnum.Failed;
                    await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);

                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_mission_status") statusHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null);

                    AssertNotNull(statusHandler, "armada_mission_status handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await statusHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("AssignmentState", json);
                    AssertContains("\"Failed\"", json);
                }
            });
        }

        /// <summary>
        /// Minimal admiral double that satisfies the IAdmiralService interface.
        /// Voyage status and mission status handlers use the database directly, not the admiral.
        /// Dispatch tests that hit vessel/pipeline validation return before admiral is called.
        /// </summary>
        private sealed class MinimalAdmiralDouble : IAdmiralService
        {
            /// <summary>Not used by the tested handlers.</summary>
            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            /// <summary>Not used by the tested handlers.</summary>
            public Func<Captain, Task>? OnStopAgent { get; set; }
            /// <summary>Not used by the tested handlers.</summary>
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            /// <summary>Not used by the tested handlers.</summary>
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            /// <summary>Not used by the tested handlers.</summary>
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            /// <summary>Not used by the tested handlers.</summary>
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            /// <summary>Not used by the tested handlers.</summary>
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            /// <summary>Not used by the tested handlers.</summary>
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            /// <summary>Not invoked by the pre-persistence error paths under test.</summary>
            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the pre-persistence error paths under test.</summary>
            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the pre-persistence error paths under test.</summary>
            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the pre-persistence error paths under test.</summary>
            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the status handlers under test.</summary>
            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the handlers under test.</summary>
            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => Task.FromResult<Pipeline?>(null);

            /// <summary>Not invoked by the handlers under test.</summary>
            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the handlers under test.</summary>
            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the handlers under test.</summary>
            public Task RecallAllAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the handlers under test.</summary>
            public Task HealthCheckAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the handlers under test.</summary>
            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the handlers under test.</summary>
            public Task HandleProcessExitAsync(
                int processId, int? exitCode, string captainId, string missionId,
                CancellationToken token = default)
                => throw new NotImplementedException();
        }
    }
}
