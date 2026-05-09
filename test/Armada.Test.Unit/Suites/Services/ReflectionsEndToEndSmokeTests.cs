namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
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
    /// End-to-end smoke: consolidate reflection mission, accept proposal, confirm learned playbook
    /// and a subsequent voyage snapshot carry the same accepted markdown (no live captain/git/network).
    /// </summary>
    public class ReflectionsEndToEndSmokeTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Reflections End-To-End Smoke";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("ReflectionWorkflow_AcceptedProposal_UpdatesLearnedPlaybookAndFutureSnapshots", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string vesselName = "reflection smoke e2e vessel";
                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        vesselName).ConfigureAwait(false);

                    string learnedFileName = ReflectionTestHelpers.ReflectionLearnedMarkdownFileName(vessel);
                    Playbook? learnedBeforeAccept = await testDb.Driver.Playbooks.ReadByFileNameAsync(
                        Constants.DefaultTenantId,
                        learnedFileName).ConfigureAwait(false);
                    AssertNotNull(learnedBeforeAccept, "Bootstrap must create learned playbook row");
                    AssertContains(
                        "No accepted reflection facts yet",
                        learnedBeforeAccept!.Content,
                        "Initial bootstrap template");

                    DateTime baseUtc = DateTime.UtcNow;
                    await ReflectionTestHelpers.CreateReflectionEvidenceMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        "alpha",
                        baseUtc.AddMinutes(-25)).ConfigureAwait(false);
                    await ReflectionTestHelpers.CreateReflectionEvidenceMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        "beta",
                        baseUtc.AddMinutes(-15)).ConfigureAwait(false);
                    await ReflectionTestHelpers.CreateReflectionEvidenceMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        "gamma",
                        baseUtc.AddMinutes(-5)).ConfigureAwait(false);

                    ArmadaSettings reflectionSettings = new ArmadaSettings();
                    SmokeRecordingAdmiralService reflectionAdmiral = new SmokeRecordingAdmiralService(testDb.Driver);
                    ReflectionDispatcher dispatcher = new ReflectionDispatcher(
                        testDb.Driver,
                        reflectionAdmiral,
                        reflectionSettings,
                        new ReflectionMemoryService(testDb.Driver));

                    Func<JsonElement?, Task<object>>? consolidateHandler = null;
                    Func<JsonElement?, Task<object>>? acceptHandler = null;
                    McpReflectionTools.Register(
                        (name, _, _, handler) =>
                        {
                            if (name == "armada_consolidate_memory")
                            {
                                consolidateHandler = handler;
                            }

                            if (name == "armada_accept_memory_proposal")
                            {
                                acceptHandler = handler;
                            }
                        },
                        testDb.Driver,
                        dispatcher,
                        reflectionSettings);

                    if (consolidateHandler == null || acceptHandler == null)
                    {
                        throw new InvalidOperationException("Reflection MCP handlers must register");
                    }

                    JsonElement consolidateArgs = JsonSerializer.SerializeToElement(new { vesselId = vessel.Id });
                    object consolidateResult = await consolidateHandler!(consolidateArgs).ConfigureAwait(false);
                    string consolidateJson = JsonSerializer.Serialize(consolidateResult);
                    AssertFalse(consolidateJson.Contains("\"Error\""), consolidateJson);

                    using (JsonDocument doc = JsonDocument.Parse(consolidateJson))
                    {
                        JsonElement root = doc.RootElement;
                        string reflectionMissionId = root.GetProperty("missionId").GetString() ?? "";
                        AssertTrue(reflectionMissionId.StartsWith("msn_", StringComparison.Ordinal), reflectionMissionId);

                        Mission? reflectionMission = await testDb.Driver.Missions.ReadAsync(reflectionMissionId).ConfigureAwait(false);
                        AssertNotNull(reflectionMission, "Reflection mission row");

                        string candidateMarkdown = "# Learned facts\n\n" + ReflectionTestHelpers.SmokeLearnedContentMarker
                            + "\n\nRun dotnet build before committing.";
                        reflectionMission!.AgentOutput = ReflectionTestHelpers.BuildReflectionProposalAgentOutput(candidateMarkdown);
                        reflectionMission.Status = MissionStatusEnum.Complete;
                        reflectionMission.CompletedUtc = DateTime.UtcNow;
                        await testDb.Driver.Missions.UpdateAsync(reflectionMission).ConfigureAwait(false);

                        Vessel? vesselBeforePointer = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                        AssertNotNull(vesselBeforePointer);
                        AssertTrue(
                            String.IsNullOrEmpty(vesselBeforePointer!.LastReflectionMissionId),
                            "LastReflectionMissionId should be empty before accept");

                        JsonElement acceptArgs = JsonSerializer.SerializeToElement(new { missionId = reflectionMissionId });
                        object acceptResult = await acceptHandler!(acceptArgs).ConfigureAwait(false);
                        string acceptJson = JsonSerializer.Serialize(acceptResult);
                        AssertFalse(acceptJson.Contains("\"Error\""), acceptJson);

                        Vessel? vesselAfterPointer = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                        AssertNotNull(vesselAfterPointer);
                        AssertEqual(
                            reflectionMissionId,
                            vesselAfterPointer!.LastReflectionMissionId,
                            "Accept advances LastReflectionMissionId");

                        Playbook? learnedAfterAccept = await testDb.Driver.Playbooks.ReadByFileNameAsync(
                            Constants.DefaultTenantId,
                            learnedFileName).ConfigureAwait(false);
                        AssertNotNull(learnedAfterAccept);
                        AssertContains(ReflectionTestHelpers.SmokeLearnedContentMarker, learnedAfterAccept!.Content, "Learned row updated");

                        LoggingModule admiralLogging = new LoggingModule();
                        admiralLogging.Settings.EnableConsole = false;
                        ArmadaSettings admiralSettings = new ArmadaSettings();
                        admiralSettings.DocksDirectory = Path.Combine(
                            Path.GetTempPath(),
                            "armada_smoke_docks_" + Guid.NewGuid().ToString("N"));
                        admiralSettings.ReposDirectory = Path.Combine(
                            Path.GetTempPath(),
                            "armada_smoke_repos_" + Guid.NewGuid().ToString("N"));

                        StubGitService git = new StubGitService();
                        IDockService dockService = new DockService(admiralLogging, testDb.Driver, admiralSettings, git);
                        ICaptainService captainService = new CaptainService(admiralLogging, testDb.Driver, admiralSettings, git, dockService);
                        IMissionService missionService = new MissionService(admiralLogging, testDb.Driver, admiralSettings, dockService, captainService);
                        IVoyageService voyageService = new VoyageService(admiralLogging, testDb.Driver);
                        AdmiralService admiral = new AdmiralService(
                            admiralLogging,
                            testDb.Driver,
                            admiralSettings,
                            captainService,
                            missionService,
                            voyageService,
                            dockService);

                        Vessel vesselFresh = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                        AssertNotNull(vesselFresh);
                        List<SelectedPlaybook> voyagePlaybooks = PlaybookMerge.MergeWithVesselDefaults(
                            vesselFresh!.GetDefaultPlaybooks(),
                            new List<SelectedPlaybook>());

                        List<MissionDescription> followOn = new List<MissionDescription>
                        {
                            new MissionDescription("Smoke follow-on worker", "Post-accept voyage to verify playbook snapshots.")
                        };

                        Voyage followVoyage = await admiral.DispatchVoyageAsync(
                            "Smoke post-reflection voyage",
                            "Ensure learned playbook snapshots include accepted markdown.",
                            vesselFresh.Id,
                            followOn,
                            voyagePlaybooks).ConfigureAwait(false);

                        List<Mission> followMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(followVoyage.Id).ConfigureAwait(false);
                        AssertEqual(1, followMissions.Count, "Single follow-on mission");

                        List<MissionPlaybookSnapshot> snapshots = await testDb.Driver.Playbooks
                            .GetMissionSnapshotsAsync(followMissions[0].Id).ConfigureAwait(false);
                        AssertTrue(snapshots.Count >= 1, "Mission must materialize playbook snapshots");

                        MissionPlaybookSnapshot? learnedSnap = snapshots.Find(
                            s => String.Equals(s.PlaybookId, learnedAfterAccept.Id, StringComparison.Ordinal));
                        AssertNotNull(learnedSnap, "Learned playbook snapshot missing from dispatch");
                        AssertContains(
                            ReflectionTestHelpers.SmokeLearnedContentMarker,
                            learnedSnap!.Content,
                            "Accepted markdown must appear in mission playbook snapshot content");
                    }
                }
            });
        }

        private sealed class SmokeRecordingAdmiralService : IAdmiralService
        {
            private readonly DatabaseDriver _Database;

            public SmokeRecordingAdmiralService(DatabaseDriver database)
            {
                _Database = database;
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
            {
                return DispatchVoyageAsync(title, description, vesselId, missionDescriptions, (string?)null, token);
            }

            public Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
            {
                return DispatchVoyageAsync(title, description, vesselId, missionDescriptions, (string?)null, token);
            }

            public async Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                CancellationToken token = default)
            {
                Voyage voyage = await _Database.Voyages.CreateAsync(new Voyage(title, description), token).ConfigureAwait(false);
                foreach (MissionDescription md in missionDescriptions)
                {
                    Mission mission = new Mission(md.Title, md.Description);
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vesselId;
                    mission.Persona = pipelineId == "Reflections" ? "MemoryConsolidator" : "Worker";
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
            {
                return DispatchVoyageAsync(title, description, vesselId, missionDescriptions, pipelineId, token);
            }

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
