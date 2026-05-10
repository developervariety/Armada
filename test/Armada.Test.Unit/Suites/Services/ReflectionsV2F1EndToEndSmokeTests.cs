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
    /// Reflections v2-F1 end-to-end smoke coverage and PackUsageMiner unit tests.
    /// Aligned to spec acceptance criteria sections 4 / 13 / 17.
    /// </summary>
    public class ReflectionsV2F1EndToEndSmokeTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Reflections v2-F1 End-To-End Smoke";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("F1_PackUsageMiner_ParsesPrestagedReadIgnoredEdited", () =>
            {
                Mission mission = new Mission("Smoke", "Smoke mission");
                mission.PrestagedFiles = new List<PrestagedFile>
                {
                    new PrestagedFile("/abs/src/A.cs", "src/A.cs"),
                    new PrestagedFile("/abs/src/B.cs", "src/B.cs"),
                };
                string log = "{\"name\":\"Read\",\"file_path\":\"src/A.cs\"}\n"
                    + "{\"name\":\"Edit\",\"file_path\":\"src/A.cs\"}\n"
                    + "Read(\"src/B.cs\") -- never mentioned again, will count as Read\n";
                PackUsageTriple t = PackUsageMiner.Mine(mission, log);
                AssertTrue(t.LogAvailable, "log available");
                AssertTrue(t.FilesReadFromPack.Contains("src/A.cs"), "A.cs read from pack");
                AssertTrue(t.FilesReadFromPack.Contains("src/B.cs"), "B.cs read from pack");
                AssertEqual(0, t.FilesIgnoredFromPack.Count, "no ignored");
                AssertTrue(t.FilesEdited.Contains("src/A.cs"), "A.cs edited");
                return Task.CompletedTask;
            });

            await RunTest("F1_PackUsageMiner_DetectsGrepDiscovered", () =>
            {
                Mission mission = new Mission("Smoke", "Smoke mission");
                mission.PrestagedFiles = new List<PrestagedFile>
                {
                    new PrestagedFile("/abs/src/A.cs", "src/A.cs"),
                };
                string log = "{\"name\":\"Read\",\"file_path\":\"src/A.cs\"}\n"
                    + "{\"name\":\"Grep\",\"pattern\":\"Foo\"}\n"
                    + "{\"name\":\"Read\",\"file_path\":\"src/Discovered.cs\"}\n";
                PackUsageTriple t = PackUsageMiner.Mine(mission, log);
                AssertTrue(t.FilesReadFromPack.Contains("src/A.cs"), "A.cs in pack");
                AssertTrue(t.FilesGrepDiscovered.Contains("src/Discovered.cs"), "Discovered.cs grep-found");
            });

            await RunTest("F1_PackUsageMiner_BucketsIgnoredPrestaged", () =>
            {
                Mission mission = new Mission("Smoke", "Smoke mission");
                mission.PrestagedFiles = new List<PrestagedFile>
                {
                    new PrestagedFile("/abs/src/Used.cs", "src/Used.cs"),
                    new PrestagedFile("/abs/src/Ignored.cs", "src/Ignored.cs"),
                };
                string log = "{\"name\":\"Read\",\"file_path\":\"src/Used.cs\"}\n";
                PackUsageTriple t = PackUsageMiner.Mine(mission, log);
                AssertTrue(t.FilesReadFromPack.Contains("src/Used.cs"), "Used.cs read");
                AssertTrue(t.FilesIgnoredFromPack.Contains("src/Ignored.cs"), "Ignored.cs ignored");
            });

            await RunTest("F1_DispatchPackCurate_AcceptApplies_AndContextPackUsesHints", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "f1-pack-curate-smoke").ConfigureAwait(false);

                    // Seed evidence: terminal mission with PrestagedFiles
                    Mission evidence = await ReflectionTestHelpers.CreateReflectionEvidenceMissionAsync(
                        testDb.Driver, vessel.Id, "evidence-1", DateTime.UtcNow.AddMinutes(-30)).ConfigureAwait(false);
                    evidence.PrestagedFiles = new List<PrestagedFile>
                    {
                        new PrestagedFile("/abs/src/Auth.cs", "src/Auth.cs"),
                    };
                    await testDb.Driver.Missions.UpdateAsync(evidence).ConfigureAwait(false);

                    ArmadaSettings settings = new ArmadaSettings();
                    F1RecordingAdmiralService admiral = new F1RecordingAdmiralService(testDb.Driver);
                    string tempLogDir = Path.Combine(Path.GetTempPath(), "armada-test-logs-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempLogDir);
                    PackUsageMiner miner = new PackUsageMiner(tempLogDir);
                    ReflectionDispatcher dispatcher = new ReflectionDispatcher(
                        testDb.Driver, admiral, settings, new ReflectionMemoryService(testDb.Driver), miner);

                    Func<JsonElement?, Task<object>>? consolidateHandler = null;
                    Func<JsonElement?, Task<object>>? acceptHandler = null;
                    McpReflectionTools.Register(
                        (name, _, _, h) =>
                        {
                            if (name == "armada_consolidate_memory") consolidateHandler = h;
                            if (name == "armada_accept_memory_proposal") acceptHandler = h;
                        },
                        testDb.Driver, dispatcher, settings);
                    AssertNotNull(consolidateHandler);
                    AssertNotNull(acceptHandler);

                    JsonElement consolidateArgs = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        mode = "pack-curate"
                    });
                    object dispatchResult = await consolidateHandler!(consolidateArgs).ConfigureAwait(false);
                    string dispatchJson = JsonSerializer.Serialize(dispatchResult);
                    AssertFalse(dispatchJson.Contains("\"Error\""), dispatchJson);
                    AssertContains("\"mode\":\"pack-curate\"", dispatchJson, "echo mode");

                    using JsonDocument dispatchDoc = JsonDocument.Parse(dispatchJson);
                    string reflectionMissionId = dispatchDoc.RootElement.GetProperty("missionId").GetString() ?? "";
                    Mission? reflectionMission = await testDb.Driver.Missions.ReadAsync(reflectionMissionId).ConfigureAwait(false);
                    AssertNotNull(reflectionMission);

                    string candidateJson = "{\n"
                        + "  \"addHints\": [\n"
                        + "    {\n"
                        + "      \"goalPattern\": \"(?i)\\\\bauth\\\\b\",\n"
                        + "      \"mustInclude\": [\"src/Auth/**\"],\n"
                        + "      \"mustExclude\": [\"**/*.Generated.cs\"],\n"
                        + "      \"priority\": 100,\n"
                        + "      \"confidence\": \"high\",\n"
                        + "      \"justification\": \"Auth dispatches need Auth/**\",\n"
                        + "      \"sourceMissionIds\": [\"" + evidence.Id + "\"]\n"
                        + "    }\n"
                        + "  ]\n"
                        + "}";
                    string diffJson = "{\"added\":[\"1 new hint\"],\"modified\":[],\"disabled\":[],\"evidenceConfidence\":\"high\",\"missionsExamined\":1,\"notes\":\"smoke\"}";
                    reflectionMission!.AgentOutput = "```reflections-candidate\n" + candidateJson + "\n```\n```reflections-diff\n" + diffJson + "\n```\n";
                    reflectionMission.Status = MissionStatusEnum.Complete;
                    reflectionMission.CompletedUtc = DateTime.UtcNow;
                    await testDb.Driver.Missions.UpdateAsync(reflectionMission).ConfigureAwait(false);

                    JsonElement acceptArgs = JsonSerializer.SerializeToElement(new { missionId = reflectionMissionId });
                    object acceptResult = await acceptHandler!(acceptArgs).ConfigureAwait(false);
                    string acceptJson = JsonSerializer.Serialize(acceptResult);
                    AssertFalse(acceptJson.Contains("\"Error\""), acceptJson);

                    using JsonDocument acceptDoc = JsonDocument.Parse(acceptJson);
                    AssertEqual("pack-curate", acceptDoc.RootElement.GetProperty("mode").GetString() ?? "", "echo accepted mode");

                    List<VesselPackHint> persisted = await testDb.Driver.VesselPackHints
                        .EnumerateActiveByVesselAsync(vessel.Id).ConfigureAwait(false);
                    AssertEqual(1, persisted.Count, "one hint persisted");
                    AssertEqual(100, persisted[0].Priority, "priority captured");
                    AssertTrue(persisted[0].GoalPattern.Contains("auth", StringComparison.OrdinalIgnoreCase), "pattern captured");

                    Directory.Delete(tempLogDir, true);
                }
            });

            await RunTest("F1_AcceptRejectsTooBroadPattern", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver, "f1-too-broad").ConfigureAwait(false);
                    await ReflectionTestHelpers.CreateReflectionEvidenceMissionAsync(
                        testDb.Driver, vessel.Id, "ev1", DateTime.UtcNow.AddMinutes(-10)).ConfigureAwait(false);

                    ArmadaSettings settings = new ArmadaSettings();
                    F1RecordingAdmiralService admiral = new F1RecordingAdmiralService(testDb.Driver);
                    string tempLogDir = Path.Combine(Path.GetTempPath(), "armada-test-logs-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempLogDir);
                    PackUsageMiner miner = new PackUsageMiner(tempLogDir);
                    ReflectionDispatcher dispatcher = new ReflectionDispatcher(
                        testDb.Driver, admiral, settings, new ReflectionMemoryService(testDb.Driver), miner);

                    Func<JsonElement?, Task<object>>? consolidateHandler = null;
                    Func<JsonElement?, Task<object>>? acceptHandler = null;
                    McpReflectionTools.Register(
                        (name, _, _, h) =>
                        {
                            if (name == "armada_consolidate_memory") consolidateHandler = h;
                            if (name == "armada_accept_memory_proposal") acceptHandler = h;
                        },
                        testDb.Driver, dispatcher, settings);

                    JsonElement dispatchArgs = JsonSerializer.SerializeToElement(new { vesselId = vessel.Id, mode = "pack-curate" });
                    object dispatchResult = await consolidateHandler!(dispatchArgs).ConfigureAwait(false);
                    string dispatchJson = JsonSerializer.Serialize(dispatchResult);
                    using JsonDocument dispatchDoc = JsonDocument.Parse(dispatchJson);
                    string missionId = dispatchDoc.RootElement.GetProperty("missionId").GetString() ?? "";

                    Mission? mission = await testDb.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false);
                    AssertNotNull(mission);
                    string candidateJson = "{\"addHints\":[{\"goalPattern\":\".*\",\"mustInclude\":[\"src/**\"],\"priority\":50,\"confidence\":\"low\",\"sourceMissionIds\":[]}]}";
                    string diffJson = "{\"added\":[\"1\"],\"modified\":[],\"disabled\":[],\"evidenceConfidence\":\"low\",\"missionsExamined\":1,\"notes\":\"x\"}";
                    mission!.AgentOutput = "```reflections-candidate\n" + candidateJson + "\n```\n```reflections-diff\n" + diffJson + "\n```\n";
                    mission.Status = MissionStatusEnum.Complete;
                    mission.CompletedUtc = DateTime.UtcNow;
                    await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);

                    JsonElement acceptArgs = JsonSerializer.SerializeToElement(new { missionId });
                    object acceptResult = await acceptHandler!(acceptArgs).ConfigureAwait(false);
                    string acceptJson = JsonSerializer.Serialize(acceptResult);
                    AssertContains("pack_hint_pattern_too_broad", acceptJson, "broad-pattern rejected");

                    Directory.Delete(tempLogDir, true);
                }
            });

            await RunTest("F1_ContextPack_FiltersByMatchingHints", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver, "f1-pack-time").ConfigureAwait(false);

                    VesselPackHint hint = new VesselPackHint
                    {
                        VesselId = vessel.Id,
                        GoalPattern = "(?i)\\bauth\\b",
                        MustIncludeJson = JsonSerializer.Serialize(new[] { "src/Auth/**" }),
                        MustExcludeJson = JsonSerializer.Serialize(new[] { "**/Generated.cs" }),
                        Priority = 100,
                        Confidence = "high",
                        Active = true,
                        SourceMissionIdsJson = "[]"
                    };
                    await testDb.Driver.VesselPackHints.CreateAsync(hint).ConfigureAwait(false);

                    List<VesselPackHint> active = await testDb.Driver.VesselPackHints
                        .EnumerateActiveByVesselAsync(vessel.Id).ConfigureAwait(false);
                    AssertEqual(1, active.Count, "active hint");
                    AssertEqual(100, active[0].Priority, "round-trip priority");

                    // Disable round-trips
                    await testDb.Driver.VesselPackHints.DeactivateAsync(active[0].Id).ConfigureAwait(false);
                    List<VesselPackHint> activeAfter = await testDb.Driver.VesselPackHints
                        .EnumerateActiveByVesselAsync(vessel.Id).ConfigureAwait(false);
                    AssertEqual(0, activeAfter.Count, "deactivated");
                }
            });
        }

        private sealed class F1RecordingAdmiralService : IAdmiralService
        {
            private readonly DatabaseDriver _Database;
            public F1RecordingAdmiralService(DatabaseDriver database) { _Database = database; }
            public string? LastPipelineId { get; private set; }
            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
                => DispatchVoyageAsync(title, description, vesselId, missionDescriptions, (string?)null, token);

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => DispatchVoyageAsync(title, description, vesselId, missionDescriptions, (string?)null, token);

            public async Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
            {
                LastPipelineId = pipelineId;
                Voyage voyage = await _Database.Voyages.CreateAsync(new Voyage(title, description), token).ConfigureAwait(false);
                foreach (MissionDescription md in missionDescriptions)
                {
                    Mission mission = new Mission(md.Title, md.Description);
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vesselId;
                    mission.Persona = (pipelineId == "Reflections" || pipelineId == "ReflectionsDualJudge") ? "MemoryConsolidator" : "Worker";
                    mission.PreferredModel = md.PreferredModel;
                    await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                }
                return voyage;
            }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => DispatchVoyageAsync(title, description, vesselId, missionDescriptions, pipelineId, token);

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default) => throw new NotImplementedException();
            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default) => throw new NotImplementedException();
            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task RecallCaptainAsync(string captainId, CancellationToken token = default) => throw new NotImplementedException();
            public Task RecallAllAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task HealthCheckAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task CleanupStaleCaptainsAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default) => throw new NotImplementedException();
        }
    }
}
