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
    using Armada.Core.Memory;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>Tests for structured anchor extraction on accepted reflection memory proposals.</summary>
    public class ReflectionAnchorTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Reflection Anchor Extraction";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            // MemoryAnchorExtractor unit tests

            await RunTest("Extract_SourceMissionId_AlwaysIncluded", async () =>
            {
                MemoryAnchor anchor = MemoryAnchorExtractor.Extract(
                    "# Notes\nSome note with no mission refs.",
                    null,
                    false,
                    "msn_abc123_test",
                    "consolidate");

                AssertTrue(anchor.SourceMissionIds.Contains("msn_abc123_test"), "source mission ID must always appear");
                await Task.CompletedTask.ConfigureAwait(false);
            });

            await RunTest("Extract_MissionIdEmbeddedInContent_IncludedInAnchors", async () =>
            {
                string content = "# Notes\nSee also msn_xyzfoo_bar for details on this pattern.";
                MemoryAnchor anchor = MemoryAnchorExtractor.Extract(
                    content,
                    null,
                    false,
                    "msn_source_mission",
                    "consolidate");

                AssertTrue(anchor.SourceMissionIds.Contains("msn_xyzfoo_bar"), "embedded mission ID extracted from content");
                await Task.CompletedTask.ConfigureAwait(false);
            });

            await RunTest("Extract_FilePathInContent_IncludedInFilePaths", async () =>
            {
                string content = "## Pattern\nSee src/Armada.Core/Services/ReflectionMemoryService.cs for context.";
                MemoryAnchor anchor = MemoryAnchorExtractor.Extract(
                    content,
                    null,
                    false,
                    "msn_p_path",
                    "consolidate");

                AssertTrue(anchor.FilePaths.Count > 0, "at least one file path extracted");
                AssertTrue(anchor.FilePaths.Contains("src/Armada.Core/Services/ReflectionMemoryService.cs"), "exact path extracted");
                await Task.CompletedTask.ConfigureAwait(false);
            });

            await RunTest("Extract_NonCodeFileExtension_NotIncludedInFilePaths", async () =>
            {
                string content = "See foo/bar.xyz for context.";
                MemoryAnchor anchor = MemoryAnchorExtractor.Extract(
                    content,
                    null,
                    false,
                    "msn_ext_test",
                    "consolidate");

                AssertFalse(anchor.FilePaths.Contains("foo/bar.xyz"), "non-code extension filtered out");
                await Task.CompletedTask.ConfigureAwait(false);
            });

            await RunTest("Extract_DiffWithMixedConfidence_ConfidenceSetToMixed", async () =>
            {
                string diffJson = "{\"added\":[],\"removed\":[],\"merged\":[],\"unchangedCount\":1,\"evidenceConfidence\":\"mixed\",\"notes\":\"ok\"}";
                MemoryAnchor anchor = MemoryAnchorExtractor.Extract(
                    "# Notes",
                    diffJson,
                    false,
                    "msn_conf_test",
                    "consolidate");

                AssertEqual("mixed", anchor.Confidence, "confidence from diff JSON");
                await Task.CompletedTask.ConfigureAwait(false);
            });

            await RunTest("Extract_EditsOverride_ConfidenceHighAndKindEdits", async () =>
            {
                MemoryAnchor anchor = MemoryAnchorExtractor.Extract(
                    "# Override content",
                    null,
                    true,
                    "msn_edits_test",
                    "consolidate");

                AssertEqual("high", anchor.Confidence, "edits override defaults confidence to high");
                AssertEqual("edits", anchor.EvidenceKind, "edits override sets evidenceKind to edits");
                await Task.CompletedTask.ConfigureAwait(false);
            });

            await RunTest("Extract_PackCurateMode_KindIsPackCurate", async () =>
            {
                MemoryAnchor anchor = MemoryAnchorExtractor.Extract(
                    "# Pack curate content",
                    null,
                    false,
                    "msn_pack_test",
                    "pack-curate");

                AssertEqual("pack_curate", anchor.EvidenceKind, "pack-curate mode maps to pack_curate evidence kind");
                await Task.CompletedTask.ConfigureAwait(false);
            });

            await RunTest("Extract_VerbatimMode_KindIsVerbatim", async () =>
            {
                MemoryAnchor anchor = MemoryAnchorExtractor.Extract(
                    "# Normal consolidation output",
                    null,
                    false,
                    "msn_verb_test",
                    "consolidate");

                AssertEqual("verbatim", anchor.EvidenceKind, "consolidate mode maps to verbatim evidence kind");
                await Task.CompletedTask.ConfigureAwait(false);
            });

            // Integration tests via AcceptMemoryProposalAsync

            await RunTest("AcceptMemoryProposal_Verbatim_ResultContainsAnchors", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateAnchorVesselAsync(testDb.Driver, "anchor-verbatim").ConfigureAwait(false);
                    string content = "# Learning\nSee src/Armada.Core/Models/Vessel.cs and msn_ref123_abc.";
                    Mission mission = await CreateReflectionMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        ReflectionTestHelpers.BuildReflectionProposalAgentOutput(content)).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = CaptureAcceptHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await handler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("anchors", json, "anchors field in result JSON");
                    AssertContains("sourceMissionIds", json, "sourceMissionIds in anchors");
                    AssertContains(mission.Id.ToLowerInvariant(), json, "source mission ID in anchors");
                }
            });

            await RunTest("AcceptMemoryProposal_EditsOverride_AnchorEvidenceKindIsEdits", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateAnchorVesselAsync(testDb.Driver, "anchor-edits").ConfigureAwait(false);
                    Mission mission = await CreateReflectionMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        "irrelevant output").ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = CaptureAcceptHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id, editsMarkdown = "# Operator edit\nFinal content." });
                    object result = await handler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("edits", json, "evidenceKind=edits in result anchors");
                }
            });

            await RunTest("AcceptMemoryProposal_ContentWithFilePaths_AnchorContainsPaths", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateAnchorVesselAsync(testDb.Driver, "anchor-paths").ConfigureAwait(false);
                    string content = "# Learnings\nChanged test/Armada.Test.Unit/Program.cs during this mission.";
                    Mission mission = await CreateReflectionMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        ReflectionTestHelpers.BuildReflectionProposalAgentOutput(content)).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = CaptureAcceptHandler(testDb.Driver);
                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await handler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("filePaths", json, "filePaths present in anchors");
                    AssertContains("Program.cs", json, "extracted file path appears in result");
                }
            });

            await RunTest("AcceptMemoryProposal_EventPayloadContainsAnchors", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateAnchorVesselAsync(testDb.Driver, "anchor-event").ConfigureAwait(false);
                    Mission mission = await CreateReflectionMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        ReflectionTestHelpers.BuildReflectionProposalAgentOutput("# Event anchor test")).ConfigureAwait(false);

                    ReflectionMemoryService memSvc = new ReflectionMemoryService(testDb.Driver);
                    ReflectionOutputParser parser = new ReflectionOutputParser();
                    ReflectionAcceptProposalResult outcome = await memSvc.AcceptMemoryProposalAsync(
                        mission.Id,
                        null,
                        parser).ConfigureAwait(false);

                    AssertNull(outcome.Error, "no error on accept");
                    AssertNotNull(outcome.Anchors, "Anchors property populated on result");
                    AssertTrue(
                        outcome.Anchors!.SourceMissionIds.Contains(mission.Id.ToLowerInvariant())
                        || outcome.Anchors.SourceMissionIds.Contains(mission.Id),
                        "mission ID in source anchors");
                    AssertEqual("verbatim", outcome.Anchors.EvidenceKind, "verbatim evidenceKind");
                    AssertEqual("high", outcome.Anchors.Confidence, "high confidence from diff");
                }
            });

            await RunTest("AcceptMemoryProposal_OldProposals_StillAcceptWithoutAnchors", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateAnchorVesselAsync(testDb.Driver, "anchor-compat").ConfigureAwait(false);
                    Mission mission = await CreateReflectionMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        ReflectionTestHelpers.BuildReflectionProposalAgentOutput("# Minimal note\nNo paths or refs.")).ConfigureAwait(false);

                    ReflectionMemoryService memSvc = new ReflectionMemoryService(testDb.Driver);
                    ReflectionOutputParser parser = new ReflectionOutputParser();
                    ReflectionAcceptProposalResult outcome = await memSvc.AcceptMemoryProposalAsync(
                        mission.Id,
                        null,
                        parser).ConfigureAwait(false);

                    AssertNull(outcome.Error, "backward-compatible accept still succeeds");
                    AssertNotNull(outcome.PlaybookId, "playbook created");
                    AssertNotNull(outcome.Anchors, "Anchors always populated even when content is minimal");
                }
            });
        }

        private static async Task<Vessel> CreateAnchorVesselAsync(DatabaseDriver database, string name)
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
            Mission mission = new Mission("anchor-reflection", "anchor test");
            mission.VesselId = vesselId;
            mission.Persona = "MemoryConsolidator";
            mission.Status = MissionStatusEnum.WorkProduced;
            mission.AgentOutput = agentOutput;
            return await database.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static Func<JsonElement?, Task<object>>? CaptureAcceptHandler(DatabaseDriver database)
        {
            Func<JsonElement?, Task<object>>? handler = null;
            StubAdmiralService admiral = new StubAdmiralService();
            ArmadaSettings settings = new ArmadaSettings();
            ReflectionMemoryService memSvc = new ReflectionMemoryService(database);
            ReflectionDispatcher dispatcher = new ReflectionDispatcher(database, admiral, settings, memSvc);
            McpReflectionTools.Register(
                (name, _, _, h) => { if (name == "armada_accept_memory_proposal") handler = h; },
                database,
                dispatcher,
                settings);
            if (handler == null) throw new InvalidOperationException("armada_accept_memory_proposal handler missing");
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
