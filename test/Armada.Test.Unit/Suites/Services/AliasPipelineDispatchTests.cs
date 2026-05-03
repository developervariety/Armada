namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Verifies that the alias-aware dispatch path in McpVoyageTools honours
    /// pipelines just like the standard non-alias path. Each MissionDescription
    /// must expand into a chain of stage missions, the alias must map to the
    /// LAST stage so downstream missions wait for the full chain, and per-stage
    /// PreferredModel must override the per-mission pin.
    /// </summary>
    public class AliasPipelineDispatchTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Alias + Pipeline Dispatch";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("AliasDispatch_WithReviewedPipeline_ExpandsEachMdIntoStageChain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("alias-pipeline-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Pipeline reviewed = new Pipeline("Reviewed");
                    reviewed.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge") { PreferredModel = "claude-opus-4-7" }
                    };
                    reviewed = await testDb.Driver.Pipelines.CreateAsync(reviewed).ConfigureAwait(false);

                    PersistingAdmiralDouble admiralDouble = new PersistingAdmiralDouble(testDb.Driver, reviewed);

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);
                    AssertNotNull(dispatchHandler, "armada_dispatch handler must be registered");

                    // Two MDs, M1 -> M2, dispatched with the Reviewed pipeline.
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "alias+pipeline voyage",
                        description = "verifies pipeline expansion under alias dispatch",
                        vesselId = vessel.Id,
                        pipeline = "Reviewed",
                        missions = new object[]
                        {
                            new { title = "first feature", description = "d1", alias = "M1", preferredModel = "claude-sonnet-4-6" },
                            new { title = "second feature", description = "d2", alias = "M2", dependsOnMissionAlias = "M1", preferredModel = "claude-sonnet-4-6" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);

                    // The voyage id is in the serialized result; pull all missions for it.
                    Voyage voyage = (Voyage)result;
                    List<Mission> all = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(4, all.Count, "Two MDs * two pipeline stages = 4 missions");

                    // Group by which MD each belongs to via the [Persona] prefix in the title.
                    List<Mission> m1Stages = all.Where(m => m.Title.Contains("first feature")).OrderBy(m => m.Persona).ToList();
                    List<Mission> m2Stages = all.Where(m => m.Title.Contains("second feature")).OrderBy(m => m.Persona).ToList();
                    AssertEqual(2, m1Stages.Count, "M1 should produce two stage missions");
                    AssertEqual(2, m2Stages.Count, "M2 should produce two stage missions");

                    Mission m1Worker = m1Stages.First(m => m.Persona == "Worker");
                    Mission m1Judge = m1Stages.First(m => m.Persona == "Judge");
                    Mission m2Worker = m2Stages.First(m => m.Persona == "Worker");
                    Mission m2Judge = m2Stages.First(m => m.Persona == "Judge");

                    // Stage chains within each MD: Worker -> Judge.
                    AssertTrue(String.IsNullOrEmpty(m1Worker.DependsOnMissionId),
                        "M1.Worker should have no upstream dependency (first MD, no alias dep)");
                    AssertEqual(m1Worker.Id, m1Judge.DependsOnMissionId,
                        "M1.Judge should depend on M1.Worker");
                    AssertEqual(m2Worker.Id, m2Judge.DependsOnMissionId,
                        "M2.Judge should depend on M2.Worker");

                    // Cross-MD alias dep: M2.Worker waits for the LAST stage of M1, not M1.Worker.
                    AssertEqual(m1Judge.Id, m2Worker.DependsOnMissionId,
                        "M2.Worker should depend on M1's LAST stage (Judge), not Worker");

                    // PreferredModel: Worker stages inherit the per-mission Sonnet pin;
                    // Judge stages take the stage-level Opus override.
                    AssertEqual("claude-sonnet-4-6", m1Worker.PreferredModel,
                        "M1.Worker should inherit per-mission PreferredModel");
                    AssertEqual("claude-opus-4-7", m1Judge.PreferredModel,
                        "M1.Judge should pick up stage-level Opus override");
                    AssertEqual("claude-sonnet-4-6", m2Worker.PreferredModel,
                        "M2.Worker should inherit per-mission PreferredModel");
                    AssertEqual("claude-opus-4-7", m2Judge.PreferredModel,
                        "M2.Judge should pick up stage-level Opus override");

                    // Persona must be set on every stage mission (was null pre-fix).
                    foreach (Mission m in all)
                    {
                        AssertFalse(String.IsNullOrEmpty(m.Persona),
                            "Pipeline-expanded stage missions must carry a Persona; got null/empty for " + m.Title);
                    }
                }
            });

            await RunTest("AliasDispatch_WithoutPipeline_PreservesLegacySingleMissionShape", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("alias-no-pipeline-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    PersistingAdmiralDouble admiralDouble = new PersistingAdmiralDouble(testDb.Driver, null);

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null);
                    AssertNotNull(dispatchHandler, "armada_dispatch handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "alias-only voyage",
                        description = "no pipeline -> legacy alias path",
                        vesselId = vessel.Id,
                        missions = new object[]
                        {
                            new { title = "feature", description = "d", alias = "A" },
                            new { title = "follow-up", description = "d", alias = "B", dependsOnMissionAlias = "A" }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);

                    Voyage voyage = (Voyage)result;
                    List<Mission> all = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(2, all.Count, "Legacy alias path should produce one mission per MD");

                    // No pipeline -> no Persona required.
                    foreach (Mission m in all)
                    {
                        AssertTrue(String.IsNullOrEmpty(m.Persona) || m.Persona == "Worker",
                            "Legacy alias dispatch should not auto-stamp a non-Worker persona, got: " + m.Persona);
                    }

                    Mission a = all.First(m => m.Title == "feature");
                    Mission b = all.First(m => m.Title == "follow-up");
                    AssertTrue(String.IsNullOrEmpty(a.DependsOnMissionId), "A should have no dep");
                    AssertEqual(a.Id, b.DependsOnMissionId, "B should depend on A's id");
                }
            });

            await RunTest("AliasDispatch_WithReviewedPipelineAndPlaybooks_PersistsSnapshotsForAllStages", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    // Vessel needs TenantId so snapshot creation is not skipped.
                    Vessel vessel = new Vessel("alias-pb-snap-vessel", "https://github.com/test/repo.git");
                    vessel.TenantId = Constants.DefaultTenantId;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    // Two playbooks for the default tenant.
                    Playbook pb1 = new Playbook("guide-a.md", "# guide a");
                    pb1.TenantId = Constants.DefaultTenantId;
                    pb1 = await testDb.Driver.Playbooks.CreateAsync(pb1).ConfigureAwait(false);

                    Playbook pb2 = new Playbook("guide-b.md", "# guide b");
                    pb2.TenantId = Constants.DefaultTenantId;
                    pb2 = await testDb.Driver.Playbooks.CreateAsync(pb2).ConfigureAwait(false);

                    // Reviewed pipeline (Worker -> Judge).
                    Pipeline reviewed = new Pipeline("SnapshotReviewed");
                    reviewed.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge") { PreferredModel = "claude-opus-4-7" }
                    };
                    reviewed = await testDb.Driver.Pipelines.CreateAsync(reviewed).ConfigureAwait(false);

                    // Admiral double that also persists snapshots for first-stage missions.
                    PersistingAdmiralDouble admiralDouble = new PersistingAdmiralDouble(testDb.Driver, reviewed, logging);

                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null,
                        logging);
                    AssertNotNull(dispatchHandler, "armada_dispatch handler must be registered");

                    // Voyage-level: pb1=InlineFullContent, pb2=InlineFullContent.
                    // M2 per-mission override: pb1=AttachIntoWorktree (most-specific wins).
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "pb snapshot voyage",
                        description = "alias pipeline snapshot regression",
                        vesselId = vessel.Id,
                        pipeline = "SnapshotReviewed",
                        selectedPlaybooks = new object[]
                        {
                            new { playbookId = pb1.Id, deliveryMode = "InlineFullContent" },
                            new { playbookId = pb2.Id, deliveryMode = "InlineFullContent" }
                        },
                        missions = new object[]
                        {
                            new { title = "M1 feature", description = "m1", alias = "M1" },
                            new
                            {
                                title = "M2 feature",
                                description = "m2",
                                alias = "M2",
                                dependsOnMissionAlias = "M1",
                                selectedPlaybooks = new object[]
                                {
                                    new { playbookId = pb1.Id, deliveryMode = "AttachIntoWorktree" }
                                }
                            }
                        }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);

                    Voyage voyage = (Voyage)result;
                    List<Mission> all = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(4, all.Count, "Two MDs * two pipeline stages = 4 missions");

                    // Every stage mission (Worker and Judge, first and downstream) must have snapshots.
                    foreach (Mission m in all)
                    {
                        List<MissionPlaybookSnapshot> snaps = await testDb.Driver.Playbooks
                            .GetMissionSnapshotsAsync(m.Id).ConfigureAwait(false);
                        AssertEqual(2, snaps.Count, "Every stage must have 2 playbook snapshots, got " + snaps.Count + " for " + m.Title);

                        // No duplicate playbookIds within a single mission's snapshots.
                        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                        foreach (MissionPlaybookSnapshot snap in snaps)
                        {
                            AssertTrue(seen.Add(snap.PlaybookId ?? ""), "Duplicate playbookId in snapshots for " + m.Title + ": " + snap.PlaybookId);
                        }
                    }

                    // M1 missions: pb1 should be InlineFullContent (voyage-level, no override).
                    List<Mission> m1Missions = all.Where(m => m.Title.Contains("M1 feature")).ToList();
                    foreach (Mission m in m1Missions)
                    {
                        List<MissionPlaybookSnapshot> snaps = await testDb.Driver.Playbooks
                            .GetMissionSnapshotsAsync(m.Id).ConfigureAwait(false);
                        MissionPlaybookSnapshot? pb1Snap = snaps.Find(s => s.PlaybookId == pb1.Id);
                        AssertNotNull(pb1Snap, "M1 missions must have pb1 snapshot on " + m.Title);
                        AssertEqual(PlaybookDeliveryModeEnum.InlineFullContent, pb1Snap!.DeliveryMode,
                            "M1 pb1 must be InlineFullContent (voyage default) on " + m.Title);
                    }

                    // M2 missions: pb1 should be AttachIntoWorktree (per-mission override wins).
                    List<Mission> m2Missions = all.Where(m => m.Title.Contains("M2 feature")).ToList();
                    foreach (Mission m in m2Missions)
                    {
                        List<MissionPlaybookSnapshot> snaps = await testDb.Driver.Playbooks
                            .GetMissionSnapshotsAsync(m.Id).ConfigureAwait(false);
                        MissionPlaybookSnapshot? pb1Snap = snaps.Find(s => s.PlaybookId == pb1.Id);
                        AssertNotNull(pb1Snap, "M2 missions must have pb1 snapshot on " + m.Title);
                        AssertEqual(PlaybookDeliveryModeEnum.AttachIntoWorktree, pb1Snap!.DeliveryMode,
                            "M2 pb1 must be AttachIntoWorktree (per-mission override) on " + m.Title);
                    }
                }
            });
        }

        /// <summary>
        /// Stub admiral that persists the dispatched first-stage mission into the
        /// real test database (so the McpVoyageTools alias path can build a chain
        /// that round-trips through EnumerateByVoyageAsync) and returns a configured
        /// pipeline for ResolvePipelineAsync calls.
        /// </summary>
        private sealed class PersistingAdmiralDouble : IAdmiralService
        {
            private readonly DatabaseDriver _Database;
            private readonly Pipeline? _Pipeline;
            private readonly LoggingModule? _Logging;

            public PersistingAdmiralDouble(DatabaseDriver database, Pipeline? pipeline, LoggingModule? logging = null)
            {
                _Database = database;
                _Pipeline = pipeline;
                _Logging = logging;
            }

            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public async Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
            {
                mission.Status = MissionStatusEnum.Pending;
                mission = await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                if (_Logging != null && mission.SelectedPlaybooks != null
                    && mission.SelectedPlaybooks.Count > 0
                    && !String.IsNullOrEmpty(mission.TenantId))
                {
                    IPlaybookService playbooks = new PlaybookService(_Database, _Logging);
                    List<MissionPlaybookSnapshot> snapshots = await playbooks.CreateSnapshotsAsync(
                        mission.TenantId,
                        mission.SelectedPlaybooks,
                        token).ConfigureAwait(false);
                    await _Database.Playbooks.SetMissionSnapshotsAsync(mission.Id, snapshots, token).ConfigureAwait(false);
                }
                return mission;
            }

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
            {
                return Task.FromResult<Pipeline?>(_Pipeline);
            }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
                => throw new NotImplementedException();
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
                => throw new NotImplementedException();
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();
            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task RecallCaptainAsync(string captainId, CancellationToken token = default) => throw new NotImplementedException();
            public Task RecallAllAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task HealthCheckAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task CleanupStaleCaptainsAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
                => Task.CompletedTask;
        }
    }
}
