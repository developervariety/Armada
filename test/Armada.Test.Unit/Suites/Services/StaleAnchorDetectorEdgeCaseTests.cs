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
    using Armada.Core.Memory;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Edge-case and negative-path coverage for <see cref="StaleAnchorDetector"/> and the
    /// <c>armada_check_stale_memory</c> MCP tool. Complements <see cref="StaleAnchorDetectorTests"/>
    /// which covers the happy paths.
    /// </summary>
    public class StaleAnchorDetectorEdgeCaseTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Stale Anchor Detector Edge Cases";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Detect_NullDatabase_ThrowsArgumentNullException", async () =>
            {
                await AssertThrowsAsync<ArgumentNullException>(async () =>
                {
                    await StaleAnchorDetector.DetectAsync(null!, "vsl_any").ConfigureAwait(false);
                }).ConfigureAwait(false);
            });

            await RunTest("Detect_NullVesselId_ThrowsArgumentNullException", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DatabaseDriver db = testDb.Driver;
                    await AssertThrowsAsync<ArgumentNullException>(async () =>
                    {
                        await StaleAnchorDetector.DetectAsync(db, null!).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
            });

            await RunTest("Detect_WhitespaceVesselId_ThrowsArgumentNullException", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DatabaseDriver db = testDb.Driver;
                    await AssertThrowsAsync<ArgumentNullException>(async () =>
                    {
                        await StaleAnchorDetector.DetectAsync(db, "   ").ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
            });

            await RunTest("Detect_VesselNotInDatabase_FileChecksSkippedGracefully", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StaleAnchorDetectionResult result = await StaleAnchorDetector.DetectAsync(
                        testDb.Driver, "vsl_doesnotexist123").ConfigureAwait(false);

                    AssertFalse(result.FileChecksAvailable, "no file checks when vessel missing");
                    AssertEqual("no_local_path", result.SkipReason, "skip reason set when vessel is missing");
                    AssertEqual(0, result.CheckedEventCount, "no events for missing vessel");
                    AssertEqual(0, result.Warnings.Count, "no warnings produced");
                }
            });

            await RunTest("Detect_OtherVesselEvent_NotIncludedInScan", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel target = await CreateVesselAsync(testDb.Driver, "stale-scope-target", localPath: null).ConfigureAwait(false);
                    Vessel other = await CreateVesselAsync(testDb.Driver, "stale-scope-other", localPath: null).ConfigureAwait(false);
                    Mission targetMission = await CreateMissionAsync(testDb.Driver, target.Id).ConfigureAwait(false);
                    Mission otherMission = await CreateMissionAsync(testDb.Driver, other.Id).ConfigureAwait(false);

                    await CreateAcceptedEventAsync(
                        testDb.Driver,
                        other.Id,
                        otherMission.Id,
                        filePaths: new List<string>(),
                        sourceMissionIds: new List<string> { "msn_scope_phantom_other" }).ConfigureAwait(false);

                    await CreateAcceptedEventAsync(
                        testDb.Driver,
                        target.Id,
                        targetMission.Id,
                        filePaths: new List<string>(),
                        sourceMissionIds: new List<string> { targetMission.Id }).ConfigureAwait(false);

                    StaleAnchorDetectionResult result = await StaleAnchorDetector.DetectAsync(
                        testDb.Driver, target.Id).ConfigureAwait(false);

                    AssertEqual(1, result.CheckedEventCount, "only target vessel events scanned");
                    AssertEqual(0, result.Warnings.Count, "phantom mission from other vessel must not leak");
                    AssertFalse(
                        result.Warnings.Exists(w => w.AffectedMissionId == "msn_scope_phantom_other"),
                        "other vessel phantom anchor must not appear in target scan");
                }
            });

            await RunTest("Detect_MalformedJsonPayload_SilentlySkipped", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "stale-malformed", localPath: null).ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel.Id).ConfigureAwait(false);

                    ArmadaEvent ev = new ArmadaEvent("reflection.accepted", "malformed payload");
                    ev.VesselId = vessel.Id;
                    ev.MissionId = mission.Id;
                    ev.Payload = "{ this is not valid JSON @!#";
                    await testDb.Driver.Events.CreateAsync(ev).ConfigureAwait(false);

                    StaleAnchorDetectionResult result = await StaleAnchorDetector.DetectAsync(
                        testDb.Driver, vessel.Id).ConfigureAwait(false);

                    AssertEqual(1, result.CheckedEventCount, "malformed event still counted (parse attempted)");
                    AssertEqual(0, result.Warnings.Count, "no warnings emitted for malformed payload");
                }
            });

            await RunTest("Detect_PayloadWithoutAnchors_NoWarnings", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "stale-noanchors", localPath: null).ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel.Id).ConfigureAwait(false);

                    ArmadaEvent ev = new ArmadaEvent("reflection.accepted", "legacy payload");
                    ev.VesselId = vessel.Id;
                    ev.MissionId = mission.Id;
                    ev.Payload = JsonSerializer.Serialize(new
                    {
                        playbookId = "art_legacy",
                        missionId = mission.Id,
                        appliedContentLength = 42
                    });
                    await testDb.Driver.Events.CreateAsync(ev).ConfigureAwait(false);

                    StaleAnchorDetectionResult result = await StaleAnchorDetector.DetectAsync(
                        testDb.Driver, vessel.Id).ConfigureAwait(false);

                    AssertEqual(1, result.CheckedEventCount, "legacy payload counted but anchors absent");
                    AssertEqual(0, result.Warnings.Count, "legacy payload yields no warnings");
                }
            });

            await RunTest("Detect_MultipleMissingFiles_AllFlaggedIndividually", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada-stale-multi-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);
                    try
                    {
                        Vessel vessel = await CreateVesselAsync(testDb.Driver, "stale-multi-missing", localPath: tempDir).ConfigureAwait(false);
                        Mission mission = await CreateMissionAsync(testDb.Driver, vessel.Id).ConfigureAwait(false);
                        await CreateAcceptedEventAsync(
                            testDb.Driver,
                            vessel.Id,
                            mission.Id,
                            filePaths: new List<string>
                            {
                                "src/Gone/One.cs",
                                "src/Gone/Two.cs",
                                "src/Gone/Three.cs"
                            },
                            sourceMissionIds: new List<string> { mission.Id }).ConfigureAwait(false);

                        StaleAnchorDetectionResult result = await StaleAnchorDetector.DetectAsync(
                            testDb.Driver, vessel.Id).ConfigureAwait(false);

                        int missingFileWarnings = 0;
                        foreach (StaleAnchorWarning w in result.Warnings)
                            if (w.WarnKind == "missing_file") missingFileWarnings++;

                        AssertEqual(3, missingFileWarnings, "one warning emitted per missing path");
                        AssertTrue(result.Warnings.Exists(w => w.AffectedPath == "src/Gone/One.cs"), "first path flagged");
                        AssertTrue(result.Warnings.Exists(w => w.AffectedPath == "src/Gone/Two.cs"), "second path flagged");
                        AssertTrue(result.Warnings.Exists(w => w.AffectedPath == "src/Gone/Three.cs"), "third path flagged");
                    }
                    finally
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                }
            });

            await RunTest("Detect_MixedPresentAndMissingMissions_OnlyMissingFlagged", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "stale-mixed-missions", localPath: null).ConfigureAwait(false);
                    Mission missionA = await CreateMissionAsync(testDb.Driver, vessel.Id).ConfigureAwait(false);
                    Mission missionB = await CreateMissionAsync(testDb.Driver, vessel.Id).ConfigureAwait(false);
                    string phantomId = "msn_mixed_phantom_99";

                    await CreateAcceptedEventAsync(
                        testDb.Driver,
                        vessel.Id,
                        missionA.Id,
                        filePaths: new List<string>(),
                        sourceMissionIds: new List<string> { missionA.Id, missionB.Id, phantomId }).ConfigureAwait(false);

                    StaleAnchorDetectionResult result = await StaleAnchorDetector.DetectAsync(
                        testDb.Driver, vessel.Id).ConfigureAwait(false);

                    int missingMissionWarnings = 0;
                    foreach (StaleAnchorWarning w in result.Warnings)
                        if (w.WarnKind == "missing_mission") missingMissionWarnings++;

                    AssertEqual(1, missingMissionWarnings, "exactly one missing-mission warning for the phantom");
                    AssertTrue(
                        result.Warnings.Exists(w => w.AffectedMissionId == phantomId),
                        "phantom ID flagged");
                    AssertFalse(
                        result.Warnings.Exists(w => w.AffectedMissionId == missionA.Id),
                        "present mission A not flagged");
                    AssertFalse(
                        result.Warnings.Exists(w => w.AffectedMissionId == missionB.Id),
                        "present mission B not flagged");
                }
            });

            await RunTest("Detect_Warning_CarriesEventAndPlaybookContext", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "stale-warn-context", localPath: null).ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel.Id).ConfigureAwait(false);
                    string phantomId = "msn_context_phantom";

                    ArmadaEvent ev = new ArmadaEvent("reflection.accepted", "context test");
                    ev.VesselId = vessel.Id;
                    ev.MissionId = mission.Id;
                    ev.Payload = JsonSerializer.Serialize(new
                    {
                        playbookId = "art_specific_playbook",
                        missionId = mission.Id,
                        anchors = new
                        {
                            sourceMissionIds = new List<string> { phantomId },
                            filePaths = new List<string>(),
                            confidence = "high",
                            evidenceKind = "verbatim"
                        }
                    });
                    await testDb.Driver.Events.CreateAsync(ev).ConfigureAwait(false);

                    StaleAnchorDetectionResult result = await StaleAnchorDetector.DetectAsync(
                        testDb.Driver, vessel.Id).ConfigureAwait(false);

                    StaleAnchorWarning? warning = null;
                    foreach (StaleAnchorWarning w in result.Warnings)
                    {
                        if (w.AffectedMissionId == phantomId)
                        {
                            warning = w;
                            break;
                        }
                    }

                    AssertNotNull(warning, "warning emitted for phantom mission");
                    AssertEqual(ev.Id, warning!.EventId, "warning carries originating event ID");
                    AssertEqual("art_specific_playbook", warning.PlaybookId, "warning carries playbook ID from payload");
                    AssertEqual(mission.Id, warning.SourceMissionId, "warning carries source mission ID from payload");
                    AssertEqual("missing_mission", warning.WarnKind, "warn kind is missing_mission");
                    AssertContains(phantomId, warning.Detail, "detail mentions the affected mission ID");
                }
            });

            await RunTest("Detect_ReadOnly_DoesNotMutateEventsOrPlaybooks", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "stale-readonly", localPath: null).ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel.Id).ConfigureAwait(false);
                    await CreateAcceptedEventAsync(
                        testDb.Driver,
                        vessel.Id,
                        mission.Id,
                        filePaths: new List<string>(),
                        sourceMissionIds: new List<string> { "msn_readonly_phantom" }).ConfigureAwait(false);

                    List<ArmadaEvent> eventsBefore = await testDb.Driver.Events.EnumerateByVesselAsync(vessel.Id, 200).ConfigureAwait(false);
                    int eventCountBefore = eventsBefore.Count;
                    string? payloadBefore = eventsBefore.Count > 0 ? eventsBefore[0].Payload : null;

                    StaleAnchorDetectionResult result = await StaleAnchorDetector.DetectAsync(
                        testDb.Driver, vessel.Id).ConfigureAwait(false);

                    AssertTrue(result.Warnings.Count > 0, "warning produced so we have a real scan");

                    List<ArmadaEvent> eventsAfter = await testDb.Driver.Events.EnumerateByVesselAsync(vessel.Id, 200).ConfigureAwait(false);
                    AssertEqual(eventCountBefore, eventsAfter.Count, "event count unchanged after detection");
                    string? payloadAfter = eventsAfter.Count > 0 ? eventsAfter[0].Payload : null;
                    AssertEqual(payloadBefore, payloadAfter, "event payload unchanged after detection");

                    Vessel? rereadVessel = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(rereadVessel, "vessel still exists");
                    AssertEqual(vessel.Id, rereadVessel!.Id, "vessel ID unchanged");
                }
            });

            await RunTest("Detect_LimitCapsScannedEvents", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "stale-limit", localPath: null).ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel.Id).ConfigureAwait(false);

                    for (int i = 0; i < 5; i++)
                    {
                        await CreateAcceptedEventAsync(
                            testDb.Driver,
                            vessel.Id,
                            mission.Id,
                            filePaths: new List<string>(),
                            sourceMissionIds: new List<string> { "msn_limit_phantom_" + i }).ConfigureAwait(false);
                    }

                    StaleAnchorDetectionResult capped = await StaleAnchorDetector.DetectAsync(
                        testDb.Driver, vessel.Id, limit: 2).ConfigureAwait(false);

                    AssertTrue(capped.CheckedEventCount <= 2, "limit caps inspected events");

                    StaleAnchorDetectionResult full = await StaleAnchorDetector.DetectAsync(
                        testDb.Driver, vessel.Id, limit: 50).ConfigureAwait(false);
                    AssertEqual(5, full.CheckedEventCount, "higher limit returns all events");
                }
            });

            // MCP tool: armada_check_stale_memory coverage

            await RunTest("McpCheckStaleMemory_MissingVesselId_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> handler = CaptureCheckStaleHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new { vesselId = "" });
                    object result = await handler(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("vessel_id_required", json, "empty vessel ID returns vessel_id_required");
                }
            });

            await RunTest("McpCheckStaleMemory_WhitespaceVesselId_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> handler = CaptureCheckStaleHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new { vesselId = "   " });
                    object result = await handler(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("vessel_id_required", json, "whitespace vessel ID also rejected");
                }
            });

            await RunTest("McpCheckStaleMemory_ReturnsExpectedShape", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "mcp-shape", localPath: null).ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel.Id).ConfigureAwait(false);
                    string phantom = "msn_mcp_phantom_shape";
                    await CreateAcceptedEventAsync(
                        testDb.Driver,
                        vessel.Id,
                        mission.Id,
                        filePaths: new List<string>(),
                        sourceMissionIds: new List<string> { phantom }).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>> handler = CaptureCheckStaleHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new { vesselId = vessel.Id });
                    object result = await handler(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("\"vesselId\"", json, "vesselId field present");
                    AssertContains("\"checkedEvents\"", json, "checkedEvents field present");
                    AssertContains("\"fileChecksAvailable\"", json, "fileChecksAvailable field present");
                    AssertContains("\"fileCheckSkipReason\"", json, "fileCheckSkipReason field present");
                    AssertContains("\"warningCount\"", json, "warningCount field present");
                    AssertContains("\"warnings\"", json, "warnings field present");
                    AssertContains(vessel.Id, json, "vessel ID echoed");
                    AssertContains("no_local_path", json, "skip reason surfaces in result");
                    AssertContains(phantom, json, "phantom mission ID surfaces in warnings");
                }
            });

            await RunTest("McpCheckStaleMemory_LimitZeroOrNegative_FallsBackToDefault", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "mcp-limit-zero", localPath: null).ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel.Id).ConfigureAwait(false);
                    for (int i = 0; i < 3; i++)
                    {
                        await CreateAcceptedEventAsync(
                            testDb.Driver,
                            vessel.Id,
                            mission.Id,
                            filePaths: new List<string>(),
                            sourceMissionIds: new List<string> { mission.Id }).ConfigureAwait(false);
                    }

                    Func<JsonElement?, Task<object>> handler = CaptureCheckStaleHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new { vesselId = vessel.Id, limit = 0 });
                    object result = await handler(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("\"checkedEvents\":3", json, "limit<=0 falls back to default; all 3 events scanned");

                    JsonElement neg = JsonSerializer.SerializeToElement(new { vesselId = vessel.Id, limit = -5 });
                    object negResult = await handler(neg).ConfigureAwait(false);
                    string negJson = JsonSerializer.Serialize(negResult);
                    AssertContains("\"checkedEvents\":3", negJson, "negative limit also falls back to default");
                }
            });

            await RunTest("McpCheckStaleMemory_ReadOnly_DoesNotEmitNewEvents", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "mcp-readonly", localPath: null).ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel.Id).ConfigureAwait(false);
                    await CreateAcceptedEventAsync(
                        testDb.Driver,
                        vessel.Id,
                        mission.Id,
                        filePaths: new List<string>(),
                        sourceMissionIds: new List<string> { "msn_mcp_phantom_readonly" }).ConfigureAwait(false);

                    int eventsBefore = (await testDb.Driver.Events.EnumerateByVesselAsync(vessel.Id, 200).ConfigureAwait(false)).Count;

                    Func<JsonElement?, Task<object>> handler = CaptureCheckStaleHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new { vesselId = vessel.Id });
                    await handler(args).ConfigureAwait(false);

                    int eventsAfter = (await testDb.Driver.Events.EnumerateByVesselAsync(vessel.Id, 200).ConfigureAwait(false)).Count;
                    AssertEqual(eventsBefore, eventsAfter, "MCP tool emits no new events (read-only)");
                }
            });
        }

        private static async Task<Vessel> CreateVesselAsync(DatabaseDriver database, string name, string? localPath)
        {
            Vessel vessel = new Vessel(name, "https://github.com/test/" + name + ".git");
            vessel.TenantId = Constants.DefaultTenantId;
            vessel.LocalPath = localPath;
            return await database.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateMissionAsync(DatabaseDriver database, string vesselId)
        {
            Mission mission = new Mission("stale-edge-test", "stale anchor edge-case test");
            mission.VesselId = vesselId;
            mission.Persona = "MemoryConsolidator";
            mission.Status = MissionStatusEnum.WorkProduced;
            return await database.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task CreateAcceptedEventAsync(
            DatabaseDriver database,
            string vesselId,
            string missionId,
            List<string> filePaths,
            List<string> sourceMissionIds)
        {
            ArmadaEvent ev = new ArmadaEvent("reflection.accepted", "stale edge test event");
            ev.VesselId = vesselId;
            ev.MissionId = missionId;
            ev.Payload = JsonSerializer.Serialize(new
            {
                playbookId = "art_edge_test",
                missionId = missionId,
                vesselId = vesselId,
                anchors = new
                {
                    sourceMissionIds = sourceMissionIds,
                    filePaths = filePaths,
                    confidence = "high",
                    evidenceKind = "verbatim"
                }
            });
            await database.Events.CreateAsync(ev).ConfigureAwait(false);
        }

        private static Func<JsonElement?, Task<object>> CaptureCheckStaleHandler(DatabaseDriver database)
        {
            Func<JsonElement?, Task<object>>? handler = null;
            StubAdmiralService admiral = new StubAdmiralService();
            ArmadaSettings settings = new ArmadaSettings();
            ReflectionMemoryService memSvc = new ReflectionMemoryService(database);
            ReflectionDispatcher dispatcher = new ReflectionDispatcher(database, admiral, settings, memSvc);
            McpReflectionTools.Register(
                (name, _, _, h) => { if (name == "armada_check_stale_memory") handler = h; },
                database,
                dispatcher,
                settings);
            if (handler == null) throw new InvalidOperationException("armada_check_stale_memory handler missing");
            return handler;
        }

        private sealed class StubAdmiralService : IAdmiralService
        {
            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();

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

            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
                => throw new NotImplementedException();
        }
    }
}
