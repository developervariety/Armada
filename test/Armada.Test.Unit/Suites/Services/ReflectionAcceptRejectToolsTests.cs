namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Memory;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Services;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>Tests for armada_accept_memory_proposal.</summary>
    public class ReflectionAcceptRejectToolsTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Reflection Accept Reject Tools";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("AcceptMemoryProposal_VerbatimAgentOutput_UpdatesLearnedPlaybook", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "accept-verbatim").ConfigureAwait(false);
                    Mission mission = await CreateReflectionMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        WorkProducedAgentOutput("# From candidate\nLine A\n")).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = CaptureAcceptHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await handler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertFalse(json.Contains("Error"), json);
                    AssertContains("pbk_", json, "playbook id");
                    AssertContains("From candidate", json, "applied content");

                    Playbook? pb = await FindLearnedPlaybookAsync(testDb.Driver, vessel).ConfigureAwait(false);
                    AssertNotNull(pb, "Learned playbook row should exist");
                    AssertContains("From candidate", pb!.Content, "DB content");
                }
            });

            await RunTest("AcceptMemoryProposal_WithEdits_UsesEditsNotCandidate", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "accept-edits").ConfigureAwait(false);
                    Mission mission = await CreateReflectionMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        WorkProducedAgentOutput("# Candidate only\n")).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = CaptureAcceptHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id, editsMarkdown = "# Override\nEdited body\n" });
                    object result = await handler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("Override", json, "override wins");
                    AssertFalse(json.Contains("Candidate only"), "candidate should not appear");

                    Playbook? pb = await FindLearnedPlaybookAsync(testDb.Driver, vessel).ConfigureAwait(false);
                    AssertNotNull(pb);
                    AssertContains("Edited body", pb!.Content);
                }
            });

            await RunTest("AcceptMemoryProposal_EditsBypassMalformedAgentOutput", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "accept-edits-malformed").ConfigureAwait(false);
                    Mission mission = await CreateReflectionMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        "not valid fenced output at all").ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = CaptureAcceptHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        missionId = mission.Id,
                        editsMarkdown = "# Fixed by operator\nOK\n"
                    });
                    object result = await handler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertFalse(json.Contains("output_contract_violation"), json);
                    AssertContains("Fixed by operator", json);
                }
            });

            await RunTest("AcceptMemoryProposal_MalformedAgentOutputWithoutEdits_ReturnsViolation", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "accept-bad-out").ConfigureAwait(false);
                    Mission mission = await CreateReflectionMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        "no fences here").ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = CaptureAcceptHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await handler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("output_contract_violation", json);
                }
            });

            await RunTest("AcceptMemoryProposal_WrongPersona_ReturnsNotReflection", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "accept-worker").ConfigureAwait(false);
                    Mission mission = new Mission("worker", "d");
                    mission.VesselId = vessel.Id;
                    mission.Persona = "Worker";
                    mission.Status = MissionStatusEnum.WorkProduced;
                    mission.AgentOutput = WorkProducedAgentOutput("# X\n");
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = CaptureAcceptHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await handler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("mission_not_a_reflection", json);
                }
            });

            await RunTest("AcceptMemoryProposal_IncompleteMission_ReturnsNotComplete", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "accept-incomplete").ConfigureAwait(false);
                    Mission mission = new Mission("inflight", "d");
                    mission.VesselId = vessel.Id;
                    mission.Persona = "MemoryConsolidator";
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.AgentOutput = WorkProducedAgentOutput("# X\n");
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = CaptureAcceptHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await handler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("mission_not_complete", json);
                }
            });

            await RunTest("AcceptMemoryProposal_SecondAccept_ReturnsAlreadyProcessed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "accept-twice").ConfigureAwait(false);
                    Mission mission = await CreateReflectionMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        WorkProducedAgentOutput("# Once\n")).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = CaptureAcceptHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    await handler!(args).ConfigureAwait(false);

                    object second = await handler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(second);
                    AssertContains("proposal_already_processed", json);
                }
            });

            await RunTest("AcceptMemoryProposal_OnFailure_DoesNotAdvanceLastReflectionMissionId", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "accept-no-advance").ConfigureAwait(false);
                    vessel.LastReflectionMissionId = "msn_prior_reflection_id";
                    await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    Mission mission = await CreateReflectionMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        "bad output").ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = CaptureAcceptHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    await handler!(args).ConfigureAwait(false);

                    Vessel? reread = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertEqual("msn_prior_reflection_id", reread!.LastReflectionMissionId, "Pointer unchanged on failure");
                }
            });

            await RunTest("AcceptMemoryProposal_OnSuccess_UpdatesLastReflectionMissionId", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "accept-pointer").ConfigureAwait(false);
                    vessel.LastReflectionMissionId = "msn_old";
                    await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    Mission mission = await CreateReflectionMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        WorkProducedAgentOutput("# OK\n")).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = CaptureAcceptHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    await handler!(args).ConfigureAwait(false);

                    Vessel? reread = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertEqual(mission.Id, reread!.LastReflectionMissionId, "Pointer advances to accepted mission");
                }
            });

            await RunTest("AcceptMemoryProposal_ExistingLearnedPlaybook_UpdatesSamePlaybookAndRecordsEvent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "accept-existing-playbook").ConfigureAwait(false);
                    Playbook existing = new Playbook(LearnedPlaybookFileName(vessel), "# Old content");
                    existing.TenantId = Constants.DefaultTenantId;
                    existing.UserId = Constants.DefaultUserId;
                    existing = await testDb.Driver.Playbooks.CreateAsync(existing).ConfigureAwait(false);

                    Mission mission = await CreateReflectionMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        WorkProducedAgentOutput("# Replaced content\n")).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = CaptureAcceptHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await handler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains(existing.Id, json, "existing playbook id returned");
                    AssertContains("playbookVersion", json, "version timestamp returned");

                    Playbook? rereadPlaybook = await FindLearnedPlaybookAsync(testDb.Driver, vessel).ConfigureAwait(false);
                    AssertNotNull(rereadPlaybook, "Learned playbook row should still exist");
                    AssertEqual(existing.Id, rereadPlaybook!.Id, "Accept updates rather than creating a second learned playbook");
                    AssertContains("Replaced content", rereadPlaybook.Content, "Updated content");
                    AssertFalse(rereadPlaybook.Content.Contains("Old content"), "Old content replaced");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id).ConfigureAwait(false);
                    ArmadaEvent? accepted = null;
                    foreach (ArmadaEvent armadaEvent in events)
                    {
                        if (armadaEvent.EventType == "reflection.accepted")
                        {
                            accepted = armadaEvent;
                        }
                    }

                    AssertNotNull(accepted, "Accepted event should be recorded");
                    AssertEqual(mission.Id, accepted!.MissionId, "Accepted event mission id");
                    AssertEqual(vessel.Id, accepted.VesselId, "Accepted event vessel id");
                    AssertContains(existing.Id, accepted.Payload ?? "", "Payload playbook id");
                    AssertContains(mission.Id, accepted.Payload ?? "", "Payload mission id");
                }
            });

            await RunTest("AcceptMemoryProposal_PreexistingRejectedEvent_ReturnsAlreadyProcessed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "accept-rejected").ConfigureAwait(false);
                    Playbook existing = new Playbook(LearnedPlaybookFileName(vessel), "# Keep this content");
                    existing.TenantId = Constants.DefaultTenantId;
                    existing.UserId = Constants.DefaultUserId;
                    await testDb.Driver.Playbooks.CreateAsync(existing).ConfigureAwait(false);

                    Mission mission = await CreateReflectionMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        WorkProducedAgentOutput("# Should not apply\n")).ConfigureAwait(false);

                    ArmadaEvent rejected = new ArmadaEvent("reflection.rejected", "Reflection proposal rejected.");
                    rejected.TenantId = Constants.DefaultTenantId;
                    rejected.EntityType = "mission";
                    rejected.EntityId = mission.Id;
                    rejected.MissionId = mission.Id;
                    rejected.VesselId = vessel.Id;
                    await testDb.Driver.Events.CreateAsync(rejected).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = CaptureAcceptHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await handler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("proposal_already_processed", json, "Rejected proposal cannot later be accepted");

                    Playbook? rereadPlaybook = await FindLearnedPlaybookAsync(testDb.Driver, vessel).ConfigureAwait(false);
                    AssertNotNull(rereadPlaybook, "Existing learned playbook should remain");
                    AssertContains("Keep this content", rereadPlaybook!.Content, "Existing content preserved");
                    AssertFalse(rereadPlaybook.Content.Contains("Should not apply"), "Candidate was not applied");

                    Vessel? rereadVessel = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNull(rereadVessel!.LastReflectionMissionId, "Rejected proposal does not advance pointer");
                }
            });
        }

        private static string ValidDiffJson()
        {
            return "{\n  \"added\": [],\n  \"removed\": [],\n  \"merged\": [],\n  \"unchangedCount\": 1,\n  \"evidenceConfidence\": \"high\",\n  \"notes\": \"ok\"\n}";
        }

        private static string WorkProducedAgentOutput(string candidateBody)
        {
            return "```reflections-candidate\n" + candidateBody.TrimEnd() + "\n```\n```reflections-diff\n" + ValidDiffJson() + "\n```\n";
        }

        private static Func<JsonElement?, Task<object>>? CaptureAcceptHandler(DatabaseDriver database)
        {
            Func<JsonElement?, Task<object>>? handler = null;
            RecordingAdmiralService admiral = new RecordingAdmiralService(database);
            ArmadaSettings settings = new ArmadaSettings();
            ReflectionDispatcher dispatcher = new ReflectionDispatcher(
                database,
                admiral,
                settings,
                new ReflectionMemoryService(database));
            McpReflectionTools.Register(
                (name, _, _, h) => { if (name == "armada_accept_memory_proposal") handler = h; },
                database,
                dispatcher,
                settings);
            if (handler == null) throw new InvalidOperationException("armada_accept_memory_proposal handler missing");
            return handler;
        }

        private static async Task<Vessel> CreateVesselAsync(DatabaseDriver database, string name)
        {
            Vessel vessel = new Vessel(name, "https://github.com/test/" + name + ".git");
            vessel.TenantId = Constants.DefaultTenantId;
            return await database.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateReflectionMissionAsync(
            DatabaseDriver database,
            string vesselId,
            string agentOutput)
        {
            Mission mission = new Mission("reflection", "d");
            mission.VesselId = vesselId;
            mission.Persona = "MemoryConsolidator";
            mission.Status = MissionStatusEnum.WorkProduced;
            mission.AgentOutput = agentOutput;
            return await database.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task<Playbook?> FindLearnedPlaybookAsync(DatabaseDriver database, Vessel vessel)
        {
            string fileName = LearnedPlaybookFileName(vessel);
            return await database.Playbooks.ReadByFileNameAsync(Constants.DefaultTenantId, fileName).ConfigureAwait(false);
        }

        private static string LearnedPlaybookFileName(Vessel vessel)
        {
            string lower = vessel.Name.ToLowerInvariant();
            string sanitized = System.Text.RegularExpressions.Regex.Replace(lower, "[^a-z0-9]+", "-").Trim('-');
            return "vessel-" + sanitized + "-learned.md";
        }

        private sealed class RecordingAdmiralService : IAdmiralService
        {
            public RecordingAdmiralService(DatabaseDriver _)
            {
            }

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
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(
               string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
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

            public Task HandleProcessExitAsync(
                int processId,
                int? exitCode,
                string captainId,
                string missionId,
                CancellationToken token = default)
                => throw new NotImplementedException();
        }
    }
}
