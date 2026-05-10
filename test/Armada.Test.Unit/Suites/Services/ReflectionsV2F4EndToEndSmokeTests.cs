namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Nodes;
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
    /// Reflections v2-F4 end-to-end smoke coverage: reorganize accept, dual-Judge
    /// success, dual-Judge failure, and cross-vessel fan-out. Aligned to spec
    /// acceptance criteria sections 12-15.
    /// </summary>
    public class ReflectionsV2F4EndToEndSmokeTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Reflections v2-F4 End-To-End Smoke";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("F4_SingleVesselReorganize_AcceptUpdatesPlaybookAndEmitsMetrics", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "f4-single-reorg").ConfigureAwait(false);

                    string seedContent = "# Vessel Learned Facts\n\n"
                        + "## Build\n"
                        + "- Always run dotnet build before committing changes.\n"
                        + "- Always run dotnet test before pushing to origin.\n"
                        + "- Tests live next to production under src/.\n";
                    await SeedLearnedPlaybookAsync(testDb.Driver, vessel, seedContent).ConfigureAwait(false);

                    ArmadaSettings settings = new ArmadaSettings
                    {
                        ReorganizePlaybookMinCharacters = 50
                    };
                    F4RecordingAdmiralService admiral = new F4RecordingAdmiralService(testDb.Driver);
                    ReflectionDispatcher dispatcher = new ReflectionDispatcher(
                        testDb.Driver,
                        admiral,
                        settings,
                        new ReflectionMemoryService(testDb.Driver));

                    Func<JsonElement?, Task<object>>? consolidateHandler = null;
                    Func<JsonElement?, Task<object>>? acceptHandler = null;
                    McpReflectionTools.Register(
                        (name, _, _, h) =>
                        {
                            if (name == "armada_consolidate_memory") consolidateHandler = h;
                            if (name == "armada_accept_memory_proposal") acceptHandler = h;
                        },
                        testDb.Driver,
                        dispatcher,
                        settings);
                    AssertNotNull(consolidateHandler);
                    AssertNotNull(acceptHandler);

                    JsonElement consolidateArgs = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        mode = "reorganize"
                    });
                    object dispatchResult = await consolidateHandler!(consolidateArgs).ConfigureAwait(false);
                    string dispatchJson = JsonSerializer.Serialize(dispatchResult);
                    AssertFalse(dispatchJson.Contains("\"Error\""), dispatchJson);
                    AssertContains("\"mode\":\"reorganize\"", dispatchJson, "echo mode");
                    AssertContains("\"dualJudge\":false", dispatchJson, "dual judge default");

                    using JsonDocument dispatchDoc = JsonDocument.Parse(dispatchJson);
                    string reflectionMissionId = dispatchDoc.RootElement.GetProperty("missionId").GetString() ?? "";
                    AssertTrue(reflectionMissionId.StartsWith("msn_", StringComparison.Ordinal), reflectionMissionId);

                    Mission? reflectionMission = await testDb.Driver.Missions.ReadAsync(reflectionMissionId).ConfigureAwait(false);
                    AssertNotNull(reflectionMission);

                    string reorganizedCandidate = "# Vessel Learned Facts\n\n"
                        + "## Build\n"
                        + "- Always run dotnet build before committing changes.\n"
                        + "- Always run dotnet test before pushing to origin.\n\n"
                        + "## Repository layout\n"
                        + "- Tests live next to production under src/.\n";
                    string diff = "{\n  \"added\": [\"## Repository layout\"],\n  \"removed\": [],\n  \"merged\": [],\n  \"unchangedCount\": 3,\n  \"evidenceConfidence\": \"high\",\n  \"notes\": \"reorganize-only\"\n}";
                    reflectionMission!.AgentOutput = "```reflections-candidate\n" + reorganizedCandidate + "\n```\n```reflections-diff\n" + diff + "\n```\n";
                    reflectionMission.Status = MissionStatusEnum.Complete;
                    reflectionMission.CompletedUtc = DateTime.UtcNow;
                    await testDb.Driver.Missions.UpdateAsync(reflectionMission).ConfigureAwait(false);

                    JsonElement acceptArgs = JsonSerializer.SerializeToElement(new { missionId = reflectionMissionId });
                    object acceptResult = await acceptHandler!(acceptArgs).ConfigureAwait(false);
                    string acceptJson = JsonSerializer.Serialize(acceptResult);
                    AssertFalse(acceptJson.Contains("\"Error\""), acceptJson);
                    AssertContains("\"mode\":\"reorganize\"", acceptJson, "accept echoes reorganize mode");

                    Playbook? after = await testDb.Driver.Playbooks
                        .ReadByFileNameAsync(Constants.DefaultTenantId, ReflectionTestHelpers.ReflectionLearnedMarkdownFileName(vessel))
                        .ConfigureAwait(false);
                    AssertNotNull(after);
                    AssertContains("Repository layout", after!.Content, "playbook updated");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(reflectionMissionId).ConfigureAwait(false);
                    ArmadaEvent? acceptedEvt = null;
                    foreach (ArmadaEvent evt in events)
                    {
                        if (evt.EventType == "reflection.accepted") acceptedEvt = evt;
                    }
                    AssertNotNull(acceptedEvt);
                    AssertContains("\"mode\":\"reorganize\"", acceptedEvt!.Payload ?? "", "accepted payload mode");
                    AssertContains("\"entriesBefore\"", acceptedEvt.Payload ?? "", "entriesBefore metric");
                    AssertContains("\"entriesAfter\"", acceptedEvt.Payload ?? "", "entriesAfter metric");
                    AssertContains("\"tokensBefore\"", acceptedEvt.Payload ?? "", "tokensBefore metric");
                    AssertContains("\"tokensAfter\"", acceptedEvt.Payload ?? "", "tokensAfter metric");
                }
            });

            await RunTest("F4_DualJudgeSuccess_TwoPassVerdicts_AcceptSucceedsWithVerdicts", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "f4-dual-pass").ConfigureAwait(false);

                    string seedContent = "# Vessel Learned Facts\n\n## Conventions\n- Use _PascalCase for private fields.\n- Tests live next to production under src/.\n";
                    await SeedLearnedPlaybookAsync(testDb.Driver, vessel, seedContent).ConfigureAwait(false);

                    ArmadaSettings settings = new ArmadaSettings { ReorganizePlaybookMinCharacters = 20 };
                    F4RecordingAdmiralService admiral = new F4RecordingAdmiralService(testDb.Driver);
                    ReflectionDispatcher dispatcher = new ReflectionDispatcher(
                        testDb.Driver,
                        admiral,
                        settings,
                        new ReflectionMemoryService(testDb.Driver));

                    Func<JsonElement?, Task<object>>? consolidateHandler = null;
                    Func<JsonElement?, Task<object>>? acceptHandler = null;
                    McpReflectionTools.Register(
                        (name, _, _, h) =>
                        {
                            if (name == "armada_consolidate_memory") consolidateHandler = h;
                            if (name == "armada_accept_memory_proposal") acceptHandler = h;
                        },
                        testDb.Driver,
                        dispatcher,
                        settings);

                    JsonElement dispatchArgs = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        mode = "reorganize",
                        dualJudge = true
                    });
                    object dispatchResult = await consolidateHandler!(dispatchArgs).ConfigureAwait(false);
                    string dispatchJson = JsonSerializer.Serialize(dispatchResult);
                    AssertContains("\"dualJudge\":true", dispatchJson);
                    AssertContains("ReflectionsDualJudge", admiral.LastPipelineId ?? "", "dual judge pipeline selected");

                    using JsonDocument dispatchDoc = JsonDocument.Parse(dispatchJson);
                    string workerMissionId = dispatchDoc.RootElement.GetProperty("missionId").GetString() ?? "";
                    string voyageId = dispatchDoc.RootElement.GetProperty("voyageId").GetString() ?? "";

                    string candidateBody = "# Vessel Learned Facts\n\n## Conventions\n- Use _PascalCase for private fields.\n- Tests live next to production under src/.\n";
                    await CompleteWorkerMissionAsync(testDb.Driver, workerMissionId, candidateBody).ConfigureAwait(false);
                    await SeedTwoJudgeSiblingsAsync(testDb.Driver, voyageId, vessel.Id, workerMissionId, "PASS", "PASS").ConfigureAwait(false);

                    JsonElement acceptArgs = JsonSerializer.SerializeToElement(new { missionId = workerMissionId });
                    object acceptResult = await acceptHandler!(acceptArgs).ConfigureAwait(false);
                    string acceptJson = JsonSerializer.Serialize(acceptResult);
                    AssertFalse(acceptJson.Contains("\"Error\""), acceptJson);
                    AssertContains("judgeVerdicts", acceptJson, "judgeVerdicts surfaced on success");
                    AssertContains("PASS", acceptJson, "PASS verdict in payload");
                }
            });

            await RunTest("F4_DualJudgeFailure_OneConcernVerdict_AcceptReturnsDualJudgeNotPassed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "f4-dual-fail").ConfigureAwait(false);

                    string seed = "# Vessel Learned Facts\n\n## Conventions\n- One useful fact about logging.\n";
                    await SeedLearnedPlaybookAsync(testDb.Driver, vessel, seed).ConfigureAwait(false);

                    ArmadaSettings settings = new ArmadaSettings { ReorganizePlaybookMinCharacters = 20 };
                    F4RecordingAdmiralService admiral = new F4RecordingAdmiralService(testDb.Driver);
                    ReflectionDispatcher dispatcher = new ReflectionDispatcher(
                        testDb.Driver,
                        admiral,
                        settings,
                        new ReflectionMemoryService(testDb.Driver));

                    Func<JsonElement?, Task<object>>? consolidateHandler = null;
                    Func<JsonElement?, Task<object>>? acceptHandler = null;
                    McpReflectionTools.Register(
                        (name, _, _, h) =>
                        {
                            if (name == "armada_consolidate_memory") consolidateHandler = h;
                            if (name == "armada_accept_memory_proposal") acceptHandler = h;
                        },
                        testDb.Driver,
                        dispatcher,
                        settings);

                    JsonElement dispatchArgs = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        mode = "reorganize",
                        dualJudge = true
                    });
                    object dispatchResult = await consolidateHandler!(dispatchArgs).ConfigureAwait(false);
                    using JsonDocument dispatchDoc = JsonDocument.Parse(JsonSerializer.Serialize(dispatchResult));
                    string workerMissionId = dispatchDoc.RootElement.GetProperty("missionId").GetString() ?? "";
                    string voyageId = dispatchDoc.RootElement.GetProperty("voyageId").GetString() ?? "";

                    string candidate = "# Vessel Learned Facts\n\n## Conventions\n- One useful fact about logging.\n";
                    await CompleteWorkerMissionAsync(testDb.Driver, workerMissionId, candidate).ConfigureAwait(false);
                    await SeedTwoJudgeSiblingsAsync(testDb.Driver, voyageId, vessel.Id, workerMissionId, "PASS", "NEEDS_REVISION").ConfigureAwait(false);

                    JsonElement acceptArgs = JsonSerializer.SerializeToElement(new { missionId = workerMissionId });
                    object acceptResult = await acceptHandler!(acceptArgs).ConfigureAwait(false);
                    string acceptJson = JsonSerializer.Serialize(acceptResult);
                    AssertContains("dual_judge_not_passed", acceptJson, "non-PASS Judge blocks accept");
                }
            });

            await RunTest("F4_FanOut_ThreeVessels_OneDispatchTwoSkipped", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel populated = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "f4-fanout-populated").ConfigureAwait(false);
                    string populatedContent = "# Vessel Learned Facts\n\n## Conventions\n- Always run dotnet test before pushing.\n- Tests live next to production under src/.\n- Use _PascalCase for private fields.\n";
                    await SeedLearnedPlaybookAsync(testDb.Driver, populated, populatedContent).ConfigureAwait(false);

                    Vessel emptyVessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "f4-fanout-empty").ConfigureAwait(false);

                    Vessel inflight = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "f4-fanout-inflight").ConfigureAwait(false);
                    string inflightContent = "# Vessel Learned Facts\n\n## Conventions\n- Use _PascalCase for private fields.\n- Always run dotnet build before committing changes.\n- Tests live next to production under src/.\n";
                    await SeedLearnedPlaybookAsync(testDb.Driver, inflight, inflightContent).ConfigureAwait(false);
                    Mission inflightMission = new Mission("ongoing reflection", "desc");
                    inflightMission.VesselId = inflight.Id;
                    inflightMission.Persona = "MemoryConsolidator";
                    inflightMission.Status = MissionStatusEnum.InProgress;
                    await testDb.Driver.Missions.CreateAsync(inflightMission).ConfigureAwait(false);

                    ArmadaSettings settings = new ArmadaSettings { ReorganizePlaybookMinCharacters = 50 };
                    F4RecordingAdmiralService admiral = new F4RecordingAdmiralService(testDb.Driver);
                    ReflectionDispatcher dispatcher = new ReflectionDispatcher(
                        testDb.Driver,
                        admiral,
                        settings,
                        new ReflectionMemoryService(testDb.Driver));

                    Func<JsonElement?, Task<object>>? consolidateHandler = null;
                    McpReflectionTools.Register(
                        (name, _, _, h) =>
                        {
                            if (name == "armada_consolidate_memory") consolidateHandler = h;
                        },
                        testDb.Driver,
                        dispatcher,
                        settings);

                    JsonElement dispatchArgs = JsonSerializer.SerializeToElement(new
                    {
                        mode = "reorganize"
                    });
                    object result = await consolidateHandler!(dispatchArgs).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    JsonNode? root = JsonNode.Parse(json);
                    JsonArray? dispatched = root?["dispatchedMissions"]?.AsArray();
                    JsonArray? skipped = root?["skipped"]?.AsArray();
                    AssertNotNull(dispatched);
                    AssertNotNull(skipped);
                    AssertEqual(1, dispatched!.Count, "exactly one vessel dispatched");
                    AssertEqual(populated.Id, dispatched[0]?["vesselId"]?.GetValue<string>(), "populated vessel dispatched");
                    AssertEqual(2, skipped!.Count, "two vessels skipped");

                    HashSet<string> skipReasons = new HashSet<string>();
                    foreach (JsonNode? entry in skipped)
                    {
                        string? reason = entry?["reason"]?.GetValue<string>();
                        if (reason != null) skipReasons.Add(reason);
                    }
                    AssertTrue(skipReasons.Contains("in_flight"), "in_flight reason present");
                    AssertTrue(skipReasons.Contains("no_playbook"), "no_playbook reason present");
                }
            });
        }

        private static async Task SeedLearnedPlaybookAsync(DatabaseDriver database, Vessel vessel, string content)
        {
            string fileName = ReflectionTestHelpers.ReflectionLearnedMarkdownFileName(vessel);
            Playbook? existing = await database.Playbooks.ReadByFileNameAsync(Constants.DefaultTenantId, fileName).ConfigureAwait(false);
            if (existing != null)
            {
                existing.Content = content;
                await database.Playbooks.UpdateAsync(existing).ConfigureAwait(false);
                return;
            }

            Playbook playbook = new Playbook(fileName, content);
            playbook.TenantId = Constants.DefaultTenantId;
            playbook.UserId = Constants.DefaultUserId;
            await database.Playbooks.CreateAsync(playbook).ConfigureAwait(false);
        }

        private static async Task CompleteWorkerMissionAsync(DatabaseDriver database, string missionId, string candidateBody)
        {
            Mission? mission = await database.Missions.ReadAsync(missionId).ConfigureAwait(false);
            if (mission == null) throw new InvalidOperationException("worker mission not found: " + missionId);
            mission.AgentOutput = ReflectionTestHelpers.BuildReflectionProposalAgentOutput(candidateBody);
            mission.Status = MissionStatusEnum.Complete;
            mission.CompletedUtc = DateTime.UtcNow;
            await database.Missions.UpdateAsync(mission).ConfigureAwait(false);
        }

        private static async Task SeedTwoJudgeSiblingsAsync(
            DatabaseDriver database,
            string voyageId,
            string vesselId,
            string workerMissionId,
            string firstVerdict,
            string secondVerdict)
        {
            await CreateJudgeSiblingAsync(database, voyageId, vesselId, workerMissionId, firstVerdict).ConfigureAwait(false);
            await CreateJudgeSiblingAsync(database, voyageId, vesselId, workerMissionId, secondVerdict).ConfigureAwait(false);
        }

        private static async Task CreateJudgeSiblingAsync(
            DatabaseDriver database,
            string voyageId,
            string vesselId,
            string workerMissionId,
            string verdict)
        {
            Mission judge = new Mission("judge sibling", "review");
            judge.VoyageId = voyageId;
            judge.VesselId = vesselId;
            judge.Persona = "Judge";
            judge.DependsOnMissionId = workerMissionId;
            judge.Status = verdict == "PASS" ? MissionStatusEnum.Complete : MissionStatusEnum.Failed;
            judge.AgentOutput = "## Findings\n## Changes Made\n## Validation\n## Verdict\n[ARMADA:VERDICT] " + verdict + "\n";
            judge.CompletedUtc = DateTime.UtcNow;
            await database.Missions.CreateAsync(judge).ConfigureAwait(false);
        }

        private sealed class F4RecordingAdmiralService : IAdmiralService
        {
            private readonly DatabaseDriver _Database;

            public F4RecordingAdmiralService(DatabaseDriver database) { _Database = database; }

            public string? LastPipelineId { get; private set; }

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
                => DispatchVoyageAsync(title, description, vesselId, missionDescriptions, (string?)null, token);

            public Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
                => DispatchVoyageAsync(title, description, vesselId, missionDescriptions, (string?)null, token);

            public async Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                CancellationToken token = default)
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

            public Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
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
