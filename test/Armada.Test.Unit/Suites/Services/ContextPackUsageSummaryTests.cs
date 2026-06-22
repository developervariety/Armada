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
    /// Tests for ContextPackUsageSummary.FromEventPayload and armada_mission_status
    /// context-pack-usage projection.
    /// </summary>
    public class ContextPackUsageSummaryTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Context Pack Usage Summary";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            // FromEventPayload: ReadBeforeSearch compliance + read offset -> PackReadVerified true
            await RunTest("FromEventPayload_ReadBeforeSearch_PackReadVerifiedTrue", async () =>
            {
                await Task.CompletedTask.ConfigureAwait(false);
                string payload = JsonSerializer.Serialize(new
                {
                    MissionId = "msn_test01",
                    LogAvailable = true,
                    ContextPackStaged = true,
                    ContextPackCompliance = "ReadBeforeSearch",
                    FirstContextPackReadOffset = (int?)42,
                    FirstSearchToolOffset = (int?)200,
                    SearchToolCallCount = 3,
                    FilesReadFromPack = new List<string> { "_briefing/context-pack.md" },
                    FilesIgnoredFromPack = new List<string>(),
                    FilesGrepDiscovered = new List<string>(),
                    FilesEdited = new List<string> { "src/Foo.cs" }
                });
                ContextPackUsageSummary? summary = ContextPackUsageSummary.FromEventPayload(payload);
                AssertNotNull(summary, "summary must not be null for valid payload");
                AssertTrue(summary!.PackReadVerified, "PackReadVerified must be true when FirstContextPackReadOffset is set");
                AssertEqual("ReadBeforeSearch", summary.ContextPackCompliance);
                AssertEqual(42, summary.FirstContextPackReadOffset);
            });

            // FromEventPayload: no read offset but compliance claims read -> PackReadVerified false (anti-fabrication)
            await RunTest("FromEventPayload_NoReadOffset_ClaimsRead_PackReadVerifiedFalse", async () =>
            {
                await Task.CompletedTask.ConfigureAwait(false);
                // Self-reported compliance claims a read, but FirstContextPackReadOffset is null.
                string payload = JsonSerializer.Serialize(new
                {
                    MissionId = "msn_test02",
                    LogAvailable = true,
                    ContextPackStaged = true,
                    ContextPackCompliance = "ReadBeforeSearch",
                    FirstContextPackReadOffset = (int?)null,
                    FirstSearchToolOffset = (int?)100,
                    SearchToolCallCount = 1,
                    FilesReadFromPack = new List<string>(),
                    FilesIgnoredFromPack = new List<string>(),
                    FilesGrepDiscovered = new List<string>(),
                    FilesEdited = new List<string>()
                });
                ContextPackUsageSummary? summary = ContextPackUsageSummary.FromEventPayload(payload);
                AssertNotNull(summary);
                AssertFalse(summary!.PackReadVerified,
                    "PackReadVerified must be false when FirstContextPackReadOffset is null, even if compliance string claims a read");
            });

            // FromEventPayload: malformed JSON -> null, no throw
            await RunTest("FromEventPayload_MalformedJson_ReturnsNull", async () =>
            {
                await Task.CompletedTask.ConfigureAwait(false);
                ContextPackUsageSummary? summary = ContextPackUsageSummary.FromEventPayload("{ not valid json %%");
                AssertNull(summary, "malformed JSON must return null without throwing");
            });

            // FromEventPayload: empty string -> null
            await RunTest("FromEventPayload_EmptyString_ReturnsNull", async () =>
            {
                await Task.CompletedTask.ConfigureAwait(false);
                ContextPackUsageSummary? result = ContextPackUsageSummary.FromEventPayload("");
                AssertNull(result, "empty string must return null");
            });

            // FromEventPayload: null -> null
            await RunTest("FromEventPayload_NullInput_ReturnsNull", async () =>
            {
                await Task.CompletedTask.ConfigureAwait(false);
                ContextPackUsageSummary? result = ContextPackUsageSummary.FromEventPayload(null);
                AssertNull(result, "null input must return null");
            });

            // FromEventPayload: whitespace-only -> null
            await RunTest("FromEventPayload_WhitespaceOnly_ReturnsNull", async () =>
            {
                await Task.CompletedTask.ConfigureAwait(false);
                ContextPackUsageSummary? result = ContextPackUsageSummary.FromEventPayload("   \t\n");
                AssertNull(result, "whitespace-only input must return null");
            });

            // FromEventPayload: JSON null literal -> null
            await RunTest("FromEventPayload_JsonNullLiteral_ReturnsNull", async () =>
            {
                await Task.CompletedTask.ConfigureAwait(false);
                ContextPackUsageSummary? result = ContextPackUsageSummary.FromEventPayload("null");
                AssertNull(result, "JSON null literal must return null");
            });

            // FromEventPayload: unknown fields are ignored (forward-compatible payloads)
            await RunTest("FromEventPayload_UnknownFields_Ignored", async () =>
            {
                await Task.CompletedTask.ConfigureAwait(false);
                string payload = JsonSerializer.Serialize(new
                {
                    MissionId = "msn_unknown01",
                    LogAvailable = true,
                    ContextPackStaged = true,
                    ContextPackCompliance = "ReadBeforeSearch",
                    FirstContextPackReadOffset = (int?)15,
                    FirstSearchToolOffset = (int?)50,
                    SearchToolCallCount = 1,
                    FilesReadFromPack = new List<string>(),
                    FilesIgnoredFromPack = new List<string>(),
                    FilesGrepDiscovered = new List<string>(),
                    FilesEdited = new List<string>(),
                    FutureTelemetryField = "should be ignored",
                    AnotherUnknown = 42
                });
                ContextPackUsageSummary? summary = ContextPackUsageSummary.FromEventPayload(payload);
                AssertNotNull(summary, "unknown fields must not break deserialization");
                AssertEqual("msn_unknown01", summary!.MissionId);
                AssertTrue(summary.PackReadVerified, "known fields must still deserialize when unknown fields are present");
            });

            // FromEventPayload: omitted/null list fields default to empty lists
            await RunTest("FromEventPayload_NullListFields_DefaultToEmptyLists", async () =>
            {
                await Task.CompletedTask.ConfigureAwait(false);
                string payload = "{\"missionId\":\"msn_lists01\",\"logAvailable\":true,\"contextPackStaged\":true," +
                    "\"contextPackCompliance\":\"SearchBeforeRead\",\"firstContextPackReadOffset\":5," +
                    "\"searchToolCallCount\":0," +
                    "\"filesReadFromPack\":null,\"filesIgnoredFromPack\":null," +
                    "\"filesGrepDiscovered\":null,\"filesEdited\":null}";
                ContextPackUsageSummary? summary = ContextPackUsageSummary.FromEventPayload(payload);
                AssertNotNull(summary);
                AssertEqual(0, summary!.FilesReadFromPack.Count, "null FilesReadFromPack must default to empty list");
                AssertEqual(0, summary.FilesIgnoredFromPack.Count);
                AssertEqual(0, summary.FilesGrepDiscovered.Count);
                AssertEqual(0, summary.FilesEdited.Count);
                AssertTrue(summary.PackReadVerified, "read offset alone drives PackReadVerified regardless of compliance string");
            });

            // Round-trip: serialize payload using EmitContextPackUsageTelemetryAsync shape, deserialize via FromEventPayload
            await RunTest("FromEventPayload_RoundTrip_FieldsMatchPayloadShape", async () =>
            {
                await Task.CompletedTask.ConfigureAwait(false);
                string missionId = "msn_rt01";
                int readOffset = 77;
                int searchOffset = 300;
                int searchCount = 5;
                List<string> readFromPack = new List<string> { "_briefing/context-pack.md" };
                List<string> ignored = new List<string> { "src/Other.cs" };
                List<string> grep = new List<string> { "src/Discovered.cs" };
                List<string> edited = new List<string> { "src/Changed.cs" };

                // Build payload matching anonymous type emitted by EmitContextPackUsageTelemetryAsync.
                string payload = JsonSerializer.Serialize(new
                {
                    MissionId = missionId,
                    LogAvailable = true,
                    ContextPackStaged = true,
                    ContextPackCompliance = "ReadBeforeSearch",
                    FirstContextPackReadOffset = (int?)readOffset,
                    FirstSearchToolOffset = (int?)searchOffset,
                    SearchToolCallCount = searchCount,
                    FilesReadFromPack = readFromPack,
                    FilesIgnoredFromPack = ignored,
                    FilesGrepDiscovered = grep,
                    FilesEdited = edited
                });

                ContextPackUsageSummary? summary = ContextPackUsageSummary.FromEventPayload(payload);
                AssertNotNull(summary);
                AssertEqual(missionId, summary!.MissionId);
                AssertTrue(summary.LogAvailable, "LogAvailable must round-trip");
                AssertTrue(summary.ContextPackStaged, "ContextPackStaged must round-trip");
                AssertEqual("ReadBeforeSearch", summary.ContextPackCompliance);
                AssertEqual(readOffset, summary.FirstContextPackReadOffset);
                AssertEqual(searchOffset, summary.FirstSearchToolOffset);
                AssertEqual(searchCount, summary.SearchToolCallCount);
                AssertTrue(summary.PackReadVerified, "PackReadVerified derived from offset");
                AssertEqual(1, summary.FilesReadFromPack.Count);
                AssertEqual(1, summary.FilesIgnoredFromPack.Count);
                AssertEqual(1, summary.FilesGrepDiscovered.Count);
                AssertEqual(1, summary.FilesEdited.Count);
            });

            // armada_mission_status: event exists -> ContextPackUsage populated
            await RunTest("MissionStatus_WithContextPackEvent_PopulatesContextPackUsage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("cps-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    Mission mission = new Mission("cps-test-mission");
                    mission.VesselId = vessel.Id;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    string payload = JsonSerializer.Serialize(new
                    {
                        MissionId = mission.Id,
                        LogAvailable = true,
                        ContextPackStaged = true,
                        ContextPackCompliance = "ReadBeforeSearch",
                        FirstContextPackReadOffset = (int?)10,
                        FirstSearchToolOffset = (int?)100,
                        SearchToolCallCount = 2,
                        FilesReadFromPack = new List<string>(),
                        FilesIgnoredFromPack = new List<string>(),
                        FilesGrepDiscovered = new List<string>(),
                        FilesEdited = new List<string>()
                    });

                    ArmadaEvent packEvent = new ArmadaEvent("mission.context_pack_usage", "pack usage test");
                    packEvent.MissionId = mission.Id;
                    packEvent.EntityType = "mission";
                    packEvent.EntityId = mission.Id;
                    packEvent.Payload = payload;
                    await testDb.Driver.Events.CreateAsync(packEvent).ConfigureAwait(false);

                    NullAdmiralDouble admiralDouble = new NullAdmiralDouble();
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
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);
                    AssertTrue(resultJson.Contains("contextPackUsage") || resultJson.Contains("ContextPackUsage"),
                        "result must include contextPackUsage field");
                    AssertTrue(resultJson.Contains("packReadVerified") || resultJson.Contains("PackReadVerified"),
                        "result must include packReadVerified field");

                    Mission? resultMission = result as Mission;
                    AssertNotNull(resultMission, "handler must return a Mission instance");
                    AssertNotNull(resultMission!.ContextPackUsage, "ContextPackUsage must be populated when event exists");
                    AssertTrue(resultMission.ContextPackUsage!.PackReadVerified,
                        "PackReadVerified must be true when event payload has a read offset");
                    AssertEqual(10, resultMission.ContextPackUsage.FirstContextPackReadOffset);
                    AssertEqual("ReadBeforeSearch", resultMission.ContextPackUsage.ContextPackCompliance);
                }
            });

            // armada_mission_status: multiple events -> uses most recent by CreatedUtc
            await RunTest("MissionStatus_MultipleContextPackEvents_UsesMostRecent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("cps-multi-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    Mission mission = new Mission("cps-multi-mission");
                    mission.VesselId = vessel.Id;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    DateTime anchorUtc = DateTime.UtcNow;

                    string olderPayload = JsonSerializer.Serialize(new
                    {
                        MissionId = mission.Id,
                        LogAvailable = true,
                        ContextPackStaged = true,
                        ContextPackCompliance = "SearchBeforeRead",
                        FirstContextPackReadOffset = (int?)null,
                        FirstSearchToolOffset = (int?)50,
                        SearchToolCallCount = 1,
                        FilesReadFromPack = new List<string>(),
                        FilesIgnoredFromPack = new List<string>(),
                        FilesGrepDiscovered = new List<string>(),
                        FilesEdited = new List<string>()
                    });
                    ArmadaEvent olderEvent = new ArmadaEvent("mission.context_pack_usage", "older pack usage");
                    olderEvent.MissionId = mission.Id;
                    olderEvent.EntityType = "mission";
                    olderEvent.EntityId = mission.Id;
                    olderEvent.Payload = olderPayload;
                    olderEvent.CreatedUtc = anchorUtc.AddMinutes(-10);
                    await testDb.Driver.Events.CreateAsync(olderEvent).ConfigureAwait(false);

                    string newerPayload = JsonSerializer.Serialize(new
                    {
                        MissionId = mission.Id,
                        LogAvailable = true,
                        ContextPackStaged = true,
                        ContextPackCompliance = "ReadBeforeSearch",
                        FirstContextPackReadOffset = (int?)99,
                        FirstSearchToolOffset = (int?)200,
                        SearchToolCallCount = 2,
                        FilesReadFromPack = new List<string> { "_briefing/context-pack.md" },
                        FilesIgnoredFromPack = new List<string>(),
                        FilesGrepDiscovered = new List<string>(),
                        FilesEdited = new List<string>()
                    });
                    ArmadaEvent newerEvent = new ArmadaEvent("mission.context_pack_usage", "newer pack usage");
                    newerEvent.MissionId = mission.Id;
                    newerEvent.EntityType = "mission";
                    newerEvent.EntityId = mission.Id;
                    newerEvent.Payload = newerPayload;
                    newerEvent.CreatedUtc = anchorUtc;
                    await testDb.Driver.Events.CreateAsync(newerEvent).ConfigureAwait(false);

                    NullAdmiralDouble admiralDouble = new NullAdmiralDouble();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_mission_status") statusHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null);

                    AssertNotNull(statusHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await statusHandler!(args).ConfigureAwait(false);
                    Mission? resultMission = result as Mission;
                    AssertNotNull(resultMission);
                    AssertNotNull(resultMission!.ContextPackUsage,
                        "most-recent mission.context_pack_usage event must be projected");
                    AssertTrue(resultMission.ContextPackUsage!.PackReadVerified,
                        "newer event with read offset must win over older unverified event");
                    AssertEqual(99, resultMission.ContextPackUsage.FirstContextPackReadOffset);
                    AssertEqual("ReadBeforeSearch", resultMission.ContextPackUsage.ContextPackCompliance);
                }
            });

            // armada_mission_status: malformed event payload -> ContextPackUsage null, no throw
            await RunTest("MissionStatus_MalformedEventPayload_ContextPackUsageNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("cps-bad-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    Mission mission = new Mission("cps-bad-mission");
                    mission.VesselId = vessel.Id;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    ArmadaEvent badEvent = new ArmadaEvent("mission.context_pack_usage", "malformed payload");
                    badEvent.MissionId = mission.Id;
                    badEvent.EntityType = "mission";
                    badEvent.EntityId = mission.Id;
                    badEvent.Payload = "{ not valid json %%";
                    await testDb.Driver.Events.CreateAsync(badEvent).ConfigureAwait(false);

                    NullAdmiralDouble admiralDouble = new NullAdmiralDouble();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_mission_status") statusHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null);

                    AssertNotNull(statusHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await statusHandler!(args).ConfigureAwait(false);
                    Mission? resultMission = result as Mission;
                    AssertNotNull(resultMission, "malformed payload must not prevent mission status from returning");
                    AssertNull(resultMission!.ContextPackUsage,
                        "malformed event payload must leave ContextPackUsage null");
                }
            });

            // armada_mission_status: no event -> ContextPackUsage null
            await RunTest("MissionStatus_NoContextPackEvent_ContextPackUsageNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("cps-null-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    Mission mission = new Mission("cps-null-mission");
                    mission.VesselId = vessel.Id;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    NullAdmiralDouble admiralDouble = new NullAdmiralDouble();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_mission_status") statusHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null);

                    AssertNotNull(statusHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await statusHandler!(args).ConfigureAwait(false);

                    // ContextPackUsage should be null (not present or null in JSON).
                    Mission? resultMission = result as Mission;
                    if (resultMission != null)
                    {
                        AssertNull(resultMission.ContextPackUsage, "ContextPackUsage must be null when no event exists");
                    }
                    else
                    {
                        // The handler returned a Mission serialized as object; check the JSON doesn't claim a value.
                        string resultJson = JsonSerializer.Serialize(result);
                        AssertFalse(resultJson.Contains("\"packReadVerified\":true") ||
                                    resultJson.Contains("\"PackReadVerified\":true"),
                            "ContextPackUsage must not claim a verified read when no event exists");
                    }
                }
            });
        }

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
