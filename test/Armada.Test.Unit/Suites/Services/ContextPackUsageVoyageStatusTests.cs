namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
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
    /// Tests that armada_voyage_status detailed mode surfaces per-mission ContextPackUsage
    /// from mission.context_pack_usage events, and that summary mode shape is unchanged.
    /// </summary>
    public class ContextPackUsageVoyageStatusTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Context pack usage in armada_voyage_status";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("DetailedMode_MissionWithUsageEvent_PopulatesContextPackUsage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("ctx-pack-vessel-1", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(
                        new Voyage("ctx-pack-voyage-1", "")).ConfigureAwait(false);

                    Mission missionWithEvent = new Mission("M1", "mission with context pack event");
                    missionWithEvent.VoyageId = voyage.Id;
                    missionWithEvent.VesselId = vessel.Id;
                    missionWithEvent = await testDb.Driver.Missions.CreateAsync(missionWithEvent).ConfigureAwait(false);

                    Mission missionWithoutEvent = new Mission("M2", "mission without context pack event");
                    missionWithoutEvent.VoyageId = voyage.Id;
                    missionWithoutEvent.VesselId = vessel.Id;
                    missionWithoutEvent = await testDb.Driver.Missions.CreateAsync(missionWithoutEvent).ConfigureAwait(false);

                    // Seed a mission.context_pack_usage event for missionWithEvent only.
                    string payloadJson = JsonSerializer.Serialize(new
                    {
                        MissionId = missionWithEvent.Id,
                        LogAvailable = true,
                        ContextPackStaged = true,
                        ContextPackCompliance = "ReadBeforeSearch",
                        FirstContextPackReadOffset = 5,
                        FirstSearchToolOffset = 12,
                        SearchToolCallCount = 2,
                        FilesReadFromPack = new[] { "src/Armada.Core/Models/Mission.cs" },
                        FilesIgnoredFromPack = Array.Empty<string>(),
                        FilesGrepDiscovered = Array.Empty<string>(),
                        FilesEdited = new[] { "src/Armada.Core/Models/Mission.cs" }
                    });

                    ArmadaEvent usageEvent = new ArmadaEvent(ContextPackUsageSummary.EventType, "Context pack usage: ReadBeforeSearch");
                    usageEvent.EntityType = "mission";
                    usageEvent.EntityId = missionWithEvent.Id;
                    usageEvent.MissionId = missionWithEvent.Id;
                    usageEvent.VesselId = vessel.Id;
                    usageEvent.VoyageId = voyage.Id;
                    usageEvent.Payload = payloadJson;
                    await testDb.Driver.Events.CreateAsync(usageEvent).ConfigureAwait(false);

                    MinimalAdmiralStub admiralStub = new MinimalAdmiralStub();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_voyage_status") statusHandler = handler; },
                        testDb.Driver,
                        admiralStub,
                        null);

                    AssertNotNull(statusHandler, "armada_voyage_status handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        voyageId = voyage.Id,
                        summary = false,
                        includeMissions = true
                    });

                    object result = await statusHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    // Response must contain Missions array.
                    AssertContains("\"Missions\"", json);

                    JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement missionsEl = doc.RootElement.GetProperty("Missions");

                    AssertEqual(2, missionsEl.GetArrayLength(), "Both missions must appear");

                    // Find the mission with ContextPackUsage populated.
                    bool foundWithUsage = false;
                    bool foundWithoutUsage = false;
                    foreach (JsonElement missionEl in missionsEl.EnumerateArray())
                    {
                        string missionId = missionEl.GetProperty("Id").GetString() ?? "";
                        JsonElement cpuEl;
                        bool hasCpu = missionEl.TryGetProperty("ContextPackUsage", out cpuEl);
                        if (missionId == missionWithEvent.Id)
                        {
                            AssertTrue(hasCpu && cpuEl.ValueKind != JsonValueKind.Null,
                                "Mission with usage event must have non-null ContextPackUsage");
                            string compliance = cpuEl.GetProperty("ContextPackCompliance").GetString() ?? "";
                            AssertEqual("ReadBeforeSearch", compliance, "Compliance must be deserialized correctly");
                            int searchCount = cpuEl.GetProperty("SearchToolCallCount").GetInt32();
                            AssertEqual(2, searchCount, "SearchToolCallCount must match event payload");
                            foundWithUsage = true;
                        }
                        else if (missionId == missionWithoutEvent.Id)
                        {
                            bool isNull = !hasCpu || cpuEl.ValueKind == JsonValueKind.Null;
                            AssertTrue(isNull, "Mission without usage event must have null ContextPackUsage");
                            foundWithoutUsage = true;
                        }
                    }

                    AssertTrue(foundWithUsage, "Must find mission with usage event in response");
                    AssertTrue(foundWithoutUsage, "Must find mission without usage event in response");
                }
            });

            await RunTest("SummaryMode_ResponseShape_Unchanged_NoContextPackUsage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("ctx-pack-vessel-2", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(
                        new Voyage("ctx-pack-voyage-2", "")).ConfigureAwait(false);

                    Mission m1 = new Mission("M1", "summary mode mission");
                    m1.VoyageId = voyage.Id;
                    m1.VesselId = vessel.Id;
                    m1.Status = MissionStatusEnum.Complete;
                    m1 = await testDb.Driver.Missions.CreateAsync(m1).ConfigureAwait(false);
                    m1.Status = MissionStatusEnum.Complete;
                    await testDb.Driver.Missions.UpdateAsync(m1).ConfigureAwait(false);

                    // Seed a usage event for the mission (should NOT appear in summary mode).
                    ArmadaEvent usageEvent = new ArmadaEvent(ContextPackUsageSummary.EventType, "Context pack usage: ReadBeforeSearch");
                    usageEvent.MissionId = m1.Id;
                    usageEvent.VoyageId = voyage.Id;
                    usageEvent.VesselId = vessel.Id;
                    usageEvent.Payload = JsonSerializer.Serialize(new
                    {
                        MissionId = m1.Id,
                        ContextPackCompliance = "ReadBeforeSearch",
                        SearchToolCallCount = 1
                    });
                    await testDb.Driver.Events.CreateAsync(usageEvent).ConfigureAwait(false);

                    MinimalAdmiralStub admiralStub = new MinimalAdmiralStub();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_voyage_status") statusHandler = handler; },
                        testDb.Driver,
                        admiralStub,
                        null);

                    AssertNotNull(statusHandler, "armada_voyage_status handler must be registered");

                    // Default args -- no explicit summary flag, defaults to summary=true.
                    JsonElement args = JsonSerializer.SerializeToElement(new { voyageId = voyage.Id });
                    object result = await statusHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    // Summary mode must include counts but no per-mission ContextPackUsage.
                    AssertContains("TotalMissions", json);
                    AssertContains("MissionCountsByStatus", json);
                    AssertFalse(json.Contains("ContextPackUsage"), "Summary mode must not include ContextPackUsage");
                    AssertFalse(json.Contains("\"Missions\""), "Summary mode must not include Missions array");
                }
            });
        }

        #region Private-Methods

        private sealed class MinimalAdmiralStub : IAdmiralService
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

            /// <summary>Not invoked by voyage status handlers.</summary>
            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by voyage status handlers.</summary>
            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by voyage status handlers.</summary>
            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by voyage status handlers.</summary>
            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by voyage status handlers.</summary>
            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by voyage status handlers.</summary>
            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => Task.FromResult<Pipeline?>(null);

            /// <summary>Not invoked by voyage status handlers.</summary>
            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by voyage status handlers.</summary>
            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by voyage status handlers.</summary>
            public Task RecallAllAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by voyage status handlers.</summary>
            public Task HealthCheckAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by voyage status handlers.</summary>
            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by voyage status handlers.</summary>
            public Task HandleProcessExitAsync(
                int processId, int? exitCode, string captainId, string missionId,
                CancellationToken token = default)
                => throw new NotImplementedException();
        }

        #endregion
    }
}
