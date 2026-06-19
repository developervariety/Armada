namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
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
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests that MemoryConsolidator missions bypass merge/PR/landing and transition
    /// directly to Complete with a reviewable candidate-diff event.
    /// </summary>
    public class ConsolidatorLandingExclusionTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Consolidator Landing Exclusion";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        /// <summary>MemoryConsolidator mission ends Complete, emits candidate event, and advances anchor.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("MemoryConsolidator_ShortCircuitsLanding_CompleteWithCandidateEvent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "consolidator landing vessel").ConfigureAwait(false);

                    string candidateMarkdown = "# Vessel Learned Facts\n\nNew consolidated fact.";
                    ReflectionOutputParser parser = new ReflectionOutputParser();
                    ReflectionOutputParseResult parsed = parser.Parse(ReflectionTestHelpers.BuildReflectionProposalAgentOutput(candidateMarkdown));
                    int expectedCandidateLength = parsed.CandidateMarkdown!.Length;

                    Mission mission = new Mission("Consolidate learned facts");
                    mission.VesselId = vessel.Id;
                    mission.Persona = "MemoryConsolidator";
                    mission.Status = MissionStatusEnum.WorkProduced;
                    mission.AgentOutput = ReflectionTestHelpers.BuildReflectionProposalAgentOutput(candidateMarkdown);
                    mission.DiffSnapshot = "";
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Captain captain = new Captain("test-captain");
                    captain.State = CaptainStateEnum.Working;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.CaptainId = captain.Id;
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_test_wt_" + Guid.NewGuid().ToString("N"));
                    dock.BranchName = "armada/consolidator/msn_test";
                    dock.Active = true;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                    MissionLandingHandler handler = CreateHandler(logging, testDb.Driver, settings, git, mergeQueue);

                    await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "mission row must exist");
                    AssertEqual(MissionStatusEnum.Complete, updated!.Status, "MemoryConsolidator must transition to Complete");
                    AssertTrue(updated.CompletedUtc.HasValue, "CompletedUtc must be set");

                    AssertEqual(0, mergeQueue.EnqueuedEntryIds.Count, "MemoryConsolidator must never enqueue a merge entry");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id, 10).ConfigureAwait(false);
                    List<ArmadaEvent> candidateEvents = events
                        .Where(e => String.Equals(e.EventType, "reflection.candidate_emitted", StringComparison.Ordinal))
                        .ToList();
                    AssertEqual(1, candidateEvents.Count, "exactly one reflection.candidate_emitted event must be written");

                    ArmadaEvent candidateEvent = candidateEvents[0];
                    AssertEqual("mission", candidateEvent.EntityType, "event entity type");
                    AssertEqual(mission.Id, candidateEvent.EntityId, "event entity id");
                    AssertEqual(vessel.Id, candidateEvent.VesselId, "event vessel id");
                    AssertFalse(String.IsNullOrEmpty(candidateEvent.Payload), "event payload must be present");

                    using (JsonDocument doc = JsonDocument.Parse(candidateEvent.Payload!))
                    {
                        JsonElement root = doc.RootElement;
                        AssertTrue(root.TryGetProperty("proposedContentLength", out JsonElement lengthEl), "payload must contain proposedContentLength");
                        AssertEqual(expectedCandidateLength, lengthEl.GetInt32(), "proposed content length");
                        AssertTrue(root.TryGetProperty("hasDiff", out JsonElement diffEl), "payload must contain hasDiff");
                        AssertTrue(diffEl.GetBoolean(), "hasDiff must be true when candidate differs from canonical");
                        AssertTrue(root.TryGetProperty("diffPreview", out JsonElement previewEl), "payload must contain diffPreview");
                        string preview = previewEl.GetString() ?? "";
                        AssertFalse(String.IsNullOrEmpty(preview), "diff preview must be present");
                        AssertTrue(preview.Contains("-"), "diff preview must show removed canonical content");
                        AssertTrue(preview.Contains("+"), "diff preview must show added candidate content");
                    }

                    Vessel? vesselAfter = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(vesselAfter, "vessel row must exist");
                    AssertEqual(mission.Id, vesselAfter!.LastReflectionMissionId, "anchor must advance to the completed consolidator mission id");
                }
            });

            await RunTest("MemoryConsolidator_UnparsableCandidate_StillCompletesAndEmitsEvent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "consolidator unparsable vessel").ConfigureAwait(false);

                    Mission mission = new Mission("Consolidate learned facts");
                    mission.VesselId = vessel.Id;
                    mission.Persona = "MemoryConsolidator";
                    mission.Status = MissionStatusEnum.WorkProduced;
                    mission.AgentOutput = "This output has no candidate fence.";
                    mission.DiffSnapshot = "";
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Captain captain = new Captain("test-captain");
                    captain.State = CaptainStateEnum.Working;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.CaptainId = captain.Id;
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_test_wt_" + Guid.NewGuid().ToString("N"));
                    dock.BranchName = "armada/consolidator/msn_test";
                    dock.Active = true;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                    MissionLandingHandler handler = CreateHandler(logging, testDb.Driver, settings, git, mergeQueue);

                    await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Complete, updated!.Status, "unparsable consolidator must still complete");
                    AssertEqual(0, mergeQueue.EnqueuedEntryIds.Count, "no merge entry for unparsable consolidator");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id, 10).ConfigureAwait(false);
                    AssertEqual(1, events.Count(e => String.Equals(e.EventType, "reflection.candidate_emitted", StringComparison.Ordinal)), "candidate event emitted for unparsable output");

                    Vessel? vesselAfter = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertEqual(mission.Id, vesselAfter!.LastReflectionMissionId, "anchor advances even for unparsable candidate");
                }
            });

            await RunTest("MemoryConsolidator_PersonaCaseInsensitive_ShortCircuits", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "consolidator lowercase persona vessel").ConfigureAwait(false);

                    Mission mission = new Mission("Consolidate learned facts");
                    mission.VesselId = vessel.Id;
                    // Lowercase persona must still match via OrdinalIgnoreCase.
                    mission.Persona = "memoryconsolidator";
                    mission.Status = MissionStatusEnum.WorkProduced;
                    mission.AgentOutput = ReflectionTestHelpers.BuildReflectionProposalAgentOutput("# Vessel Learned Facts\n\nLowercase persona fact.");
                    mission.DiffSnapshot = "";
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Captain captain = new Captain("test-captain");
                    captain.State = CaptainStateEnum.Working;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.CaptainId = captain.Id;
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_test_wt_" + Guid.NewGuid().ToString("N"));
                    dock.BranchName = "armada/consolidator/msn_test";
                    dock.Active = true;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                    MissionLandingHandler handler = CreateHandler(logging, testDb.Driver, settings, git, mergeQueue);

                    await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Complete, updated!.Status, "lowercase-persona consolidator must still short-circuit to Complete");
                    AssertEqual(0, mergeQueue.EnqueuedEntryIds.Count, "no merge entry for lowercase-persona consolidator");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id, 10).ConfigureAwait(false);
                    AssertEqual(1, events.Count(e => String.Equals(e.EventType, "reflection.candidate_emitted", StringComparison.Ordinal)), "candidate event emitted regardless of persona casing");

                    Vessel? vesselAfter = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertEqual(mission.Id, vesselAfter!.LastReflectionMissionId, "anchor advances for lowercase-persona consolidator");
                }
            });

            await RunTest("MemoryConsolidator_MergeQueueMode_NoDiffCompletesWithoutEnqueue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "consolidator merge queue no diff vessel").ConfigureAwait(false);
                    vessel.LandingMode = LandingModeEnum.MergeQueue;
                    await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    Mission mission = new Mission("Consolidate learned facts");
                    mission.VesselId = vessel.Id;
                    mission.Persona = "MemoryConsolidator";
                    mission.Status = MissionStatusEnum.WorkProduced;
                    mission.AgentOutput = ReflectionTestHelpers.BuildReflectionProposalAgentOutput("# Vessel Learned Facts\n\nDB-applied fact.");
                    mission.DiffSnapshot = "";
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Captain captain = new Captain("test-captain");
                    captain.State = CaptainStateEnum.Working;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.CaptainId = captain.Id;
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_test_wt_" + Guid.NewGuid().ToString("N"));
                    dock.BranchName = "armada/consolidator/msn_test";
                    dock.Active = true;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    git.ExistingBranches.Add(dock.BranchName);
                    git.DiffResult = "";

                    RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                    MissionLandingHandler handler = CreateHandler(logging, testDb.Driver, settings, git, mergeQueue);

                    await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "mission row must exist");
                    AssertEqual(MissionStatusEnum.Complete, updated!.Status, "MergeQueue MemoryConsolidator with no diff must complete");
                    AssertTrue(updated.CompletedUtc.HasValue, "CompletedUtc must be set");

                    AssertEqual(0, mergeQueue.EnqueuedEntryIds.Count, "MergeQueue MemoryConsolidator with no diff must not enqueue");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id, 10).ConfigureAwait(false);
                    AssertEqual(0, events.Count(e => String.Equals(e.EventType, "merge_queue.enqueued", StringComparison.Ordinal)), "no merge queue event must be emitted");
                    AssertEqual(1, events.Count(e => String.Equals(e.EventType, "reflection.candidate_emitted", StringComparison.Ordinal)), "candidate event must still be emitted");
                }
            });

            await RunTest("WorkerMission_NotShortCircuited_NoReflectionCandidateEvent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "non consolidator vessel").ConfigureAwait(false);

                    Mission mission = new Mission("Regular worker mission");
                    mission.VesselId = vessel.Id;
                    mission.Persona = "Worker";
                    mission.Status = MissionStatusEnum.WorkProduced;
                    mission.AgentOutput = ReflectionTestHelpers.BuildReflectionProposalAgentOutput("# Should be ignored");
                    mission.DiffSnapshot = "";
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Captain captain = new Captain("test-captain");
                    captain.State = CaptainStateEnum.Working;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.CaptainId = captain.Id;
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_test_wt_" + Guid.NewGuid().ToString("N"));
                    dock.BranchName = "armada/worker/msn_test";
                    dock.Active = true;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                    MissionLandingHandler handler = CreateHandler(logging, testDb.Driver, settings, git, mergeQueue);

                    await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id, 10).ConfigureAwait(false);
                    AssertEqual(0, events.Count(e => String.Equals(e.EventType, "reflection.candidate_emitted", StringComparison.Ordinal)), "Worker must not emit reflection candidate event");
                }
            });

            await RunTest("WorkerMission_WithDiff_MergeQueueMode_Enqueues", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    Vessel vessel = await ReflectionTestHelpers.CreateBootstrappedReflectionVesselAsync(
                        testDb.Driver,
                        "worker diff merge queue vessel").ConfigureAwait(false);
                    vessel.LandingMode = LandingModeEnum.MergeQueue;
                    await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    Mission mission = new Mission("Regular worker mission with diff");
                    mission.VesselId = vessel.Id;
                    mission.Persona = "Worker";
                    mission.Status = MissionStatusEnum.WorkProduced;
                    mission.AgentOutput = "[ARMADA:RESULT] COMPLETE\nSummary.";
                    mission.DiffSnapshot = "diff --git a/src/Example.cs\n+++ b/src/Example.cs\n@@ -1 +1,2 @@\n+// change\n";
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Captain captain = new Captain("test-captain");
                    captain.State = CaptainStateEnum.Working;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.CaptainId = captain.Id;
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_test_wt_" + Guid.NewGuid().ToString("N"));
                    dock.BranchName = "armada/worker/msn_test";
                    dock.Active = true;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    git.ExistingBranches.Add(dock.BranchName);
                    git.DiffResult = mission.DiffSnapshot;

                    RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                    MissionLandingHandler handler = CreateHandler(logging, testDb.Driver, settings, git, mergeQueue);

                    await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "mission row must exist");
                    AssertEqual(MissionStatusEnum.WorkProduced, updated!.Status, "Worker mission with a diff must stay WorkProduced for merge queue processing");

                    AssertEqual(1, mergeQueue.EnqueuedEntryIds.Count, "Worker mission with a diff must enqueue exactly one merge entry");
                    AssertEqual(dock.BranchName, mergeQueue.EnqueuedBranchNames[0], "enqueued branch name must match dock branch");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id, 10).ConfigureAwait(false);
                    AssertEqual(0, events.Count(e => String.Equals(e.EventType, "reflection.candidate_emitted", StringComparison.Ordinal)), "Worker must not emit reflection candidate event");
                    AssertEqual(1, events.Count(e => String.Equals(e.EventType, "merge_queue.enqueued", StringComparison.Ordinal)), "Worker must emit merge_queue.enqueued event");
                }
            });
        }

        private MissionLandingHandler CreateHandler(
            LoggingModule logging,
            DatabaseDriver database,
            ArmadaSettings settings,
            IGitService git,
            IMergeQueueService mergeQueue)
        {
            IDockService dockService = new DockService(logging, database, settings, git);
            ICaptainService captainService = new CaptainService(logging, database, settings, git, dockService);
            ILandingService landingService = new LandingService(logging, database, settings, git);
            IMessageTemplateService templateService = new MessageTemplateService(logging);

            return new MissionLandingHandler(
                logging,
                database,
                settings,
                git,
                mergeQueue,
                landingService,
                new AutoLandEvaluator(),
                new ConventionChecker(),
                new CriticalTriggerEvaluator(),
                templateService,
                null,
                dockService,
                new NoOpRemoteTriggerService(),
                null);
        }

        private sealed class RecordingMergeQueueService : IMergeQueueService
        {
            public List<string> EnqueuedEntryIds { get; } = new List<string>();
            public List<string> EnqueuedBranchNames { get; } = new List<string>();

            public Task<MergeEntry> EnqueueAsync(MergeEntry entry, CancellationToken token = default)
            {
                EnqueuedEntryIds.Add(entry.Id);
                EnqueuedBranchNames.Add(entry.BranchName ?? string.Empty);
                return Task.FromResult(entry);
            }

            public Task ProcessEntryByIdAsync(string entryId, CancellationToken token = default) => Task.CompletedTask;
            public Task ProcessQueueAsync(CancellationToken token = default) => Task.CompletedTask;
            public Task CancelAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.CompletedTask;
            public Task<List<MergeEntry>> ListAsync(string? tenantId = null, CancellationToken token = default) => Task.FromResult(new List<MergeEntry>());
            public Task<MergeEntry?> ProcessSingleAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult<MergeEntry?>(null);
            public Task<MergeEntry?> GetAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult<MergeEntry?>(null);
            public Task<bool> DeleteAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult(true);
            public Task<MergeQueuePurgeResult> DeleteMultipleAsync(List<string> entryIds, string? tenantId = null, CancellationToken token = default) => Task.FromResult(new MergeQueuePurgeResult());
            public Task<int> PurgeTerminalAsync(string? vesselId = null, MergeStatusEnum? status = null, string? tenantId = null, CancellationToken token = default) => Task.FromResult(0);
            public Task<int> ReconcilePullRequestEntriesAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<int> ReconcileLandingStateMachineAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<int> RecoverInFlightLandingsAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<bool> TryOpenPullRequestForRecoveryAsync(string mergeEntryId, CancellationToken token = default) => Task.FromResult(false);
            public Task<bool> HasActiveMergeEntryForMissionAsync(string missionId, CancellationToken token = default) => Task.FromResult(false);
            public Task<SafetyNetEnqueueResult> TrySafetyNetEnqueueAsync(Mission mission, Vessel vessel, string? unifiedDiff, IAutoLandEvaluator autoLandEvaluator, IConventionChecker conventionChecker, ICriticalTriggerEvaluator criticalTriggerEvaluator, CancellationToken token = default)
                => Task.FromResult(new SafetyNetEnqueueResult(SafetyNetEnqueueOutcomeEnum.Enqueued, null));
        }

        private sealed class NoOpRemoteTriggerService : IRemoteTriggerService
        {
            public Task FireDrainerAsync(string vesselId, string text, CancellationToken token = default) => Task.CompletedTask;
            public Task FireCriticalAsync(string text, CancellationToken token = default) => Task.CompletedTask;
            public AgentWakeSessionRegistration RegisterAgentWakeSession(AgentWakeSessionRegistration registration) => registration;
            public AgentWakeSessionRegistration? GetAgentWakeSession() => null;
        }
    }
}
