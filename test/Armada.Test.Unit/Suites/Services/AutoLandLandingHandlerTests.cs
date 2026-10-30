namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;
    using StubGitService = global::Test.Shared.Infrastructure.StubGitService;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for the auto-land predicate evaluation and ProcessEntryByIdAsync fire decision.
    /// Exercises the predicate-evaluation and background-fire branch directly via AutoLandEvaluator
    /// and a hand-rolled IMergeQueueService recording double.
    /// </summary>
    public class AutoLandLandingHandlerTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "AutoLand LandingHandler";

        /// <summary>
        /// Hand-rolled IMergeQueueService double that records ProcessEntryByIdAsync calls.
        /// </summary>
        private sealed class RecordingMergeQueueService : IMergeQueueService
        {
            public List<string> ProcessedEntryIds { get; } = new List<string>();

            public Task ProcessEntryByIdAsync(string entryId, CancellationToken token = default)
            {
                ProcessedEntryIds.Add(entryId);
                return Task.CompletedTask;
            }

            public Task<MergeEntry> EnqueueAsync(MergeEntry entry, CancellationToken token = default) => throw new NotImplementedException();
            public Task ProcessQueueAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task CancelAsync(string entryId, string? tenantId = null, CancellationToken token = default) => throw new NotImplementedException();
            public Task<List<MergeEntry>> ListAsync(string? tenantId = null, CancellationToken token = default) => throw new NotImplementedException();
            public Task<MergeEntry?> ProcessSingleAsync(string entryId, string? tenantId = null, CancellationToken token = default) => throw new NotImplementedException();
            public Task<MergeEntry?> GetAsync(string entryId, string? tenantId = null, CancellationToken token = default) => throw new NotImplementedException();
            public Task<bool> DeleteAsync(string entryId, string? tenantId = null, CancellationToken token = default) => throw new NotImplementedException();
            public Task<MergeQueuePurgeResult> DeleteMultipleAsync(List<string> entryIds, string? tenantId = null, CancellationToken token = default) => throw new NotImplementedException();
            public Task<int> PurgeTerminalAsync(string? vesselId = null, MergeStatusEnum? status = null, string? tenantId = null, CancellationToken token = default) => throw new NotImplementedException();
            public Task<int> ReconcilePullRequestEntriesAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<int> ReconcileLandingStateMachineAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<int> RecoverInFlightLandingsAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<bool> TryOpenPullRequestForRecoveryAsync(string mergeEntryId, CancellationToken token = default) => Task.FromResult(false);
            public Task<bool> HasActiveMergeEntryForMissionAsync(string missionId, CancellationToken token = default) => Task.FromResult(false);
            public Task<SafetyNetEnqueueResult> TrySafetyNetEnqueueAsync(Mission mission, Vessel vessel, string? unifiedDiff, IAutoLandEvaluator autoLandEvaluator, IConventionChecker conventionChecker, ICriticalTriggerEvaluator criticalTriggerEvaluator, CancellationToken token = default)
                => Task.FromResult(new SafetyNetEnqueueResult(SafetyNetEnqueueOutcomeEnum.Enqueued, null));
        }

        /// <summary>
        /// Mirrors the MissionLandingHandler auto-land decision block: evaluate predicate,
        /// call ProcessEntryByIdAsync on Pass, do nothing on Fail. Returns "triggered" or
        /// "skipped:reason" so tests can assert the chosen action.
        /// </summary>
        private static async Task<string> EvaluateAndMaybeFireAsync(
            IAutoLandEvaluator evaluator,
            IMergeQueueService mergeQueue,
            AutoLandPredicate predicate,
            string diff,
            string entryId)
        {
            EvaluationResult result = evaluator.Evaluate(diff, predicate);
            if (result is EvaluationResult.Pass)
            {
                await mergeQueue.ProcessEntryByIdAsync(entryId).ConfigureAwait(false);
                return "triggered";
            }
            else if (result is EvaluationResult.Fail fail)
            {
                return "skipped:" + fail.Reason;
            }

            return "no-op";
        }

        private async Task AssertStaleCancellationOutcomeAsync(bool landingResult)
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_handler_cancel_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                HeldLandingService landing = new HeldLandingService();
                Task? handling = null;
                try
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArmadaSettings settings = new ArmadaSettings { DocksDirectory = root, LogDirectory = root };
                    StubGitService git = new StubGitService();
                    git.DiffResult = "diff --git a/src/Foo.cs b/src/Foo.cs\n--- a/src/Foo.cs\n+++ b/src/Foo.cs\n@@ -1 +1 @@\n-old\n+new\n";
                    Vessel vessel = new Vessel("cancelled-handler", "https://example.test/repo.git")
                    {
                        LocalPath = root,
                        WorkingDirectory = root,
                        DefaultBranch = "main",
                        LandingMode = LandingModeEnum.LocalMerge,
                        BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalAndRemote
                    };
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Dock dock = new Dock(vessel.Id) { WorktreePath = root, BranchName = "armada/captain/cancelled-handler", Active = true };
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);
                    git.ExistingBranches.Add(dock.BranchName);
                    Mission mission = new Mission("Cancel during direct landing")
                    {
                        VesselId = vessel.Id,
                        DockId = dock.Id,
                        BranchName = dock.BranchName,
                        Persona = "Judge",
                        Status = MissionStatusEnum.WorkProduced,
                        DiffSnapshot = git.DiffResult
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    MissionLandingHandler handler = new MissionLandingHandler(logging, testDb.Driver, settings, git,
                        new RecordingMergeQueueService(), landing, new AutoLandEvaluator(), new ConventionChecker(),
                        new CriticalTriggerEvaluator(), new MessageTemplateService(logging), null,
                        new DockService(logging, testDb.Driver, settings, git), new NoOpRemoteTriggerService(), null);
                    handling = handler.HandleMissionCompleteAsync(mission, dock);
                    await landing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    AssertFalse(handling.IsCompleted, "the actual handler is held inside its landing operation");
                    Mission stored = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    MissionCancellationResult cancelled = await MissionCancellation.CancelAsync(testDb.Driver, stored,
                        MissionCancellation.OperatorCancelReason, null).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    AssertTrue(cancelled.Succeeded, "supported cancellation completes while landing is held");
                    AssertEqual(MissionStatusEnum.WorkProduced, mission.Status, "the handler still owns the stale pre-cancellation object");
                    landing.Release.TrySetResult(landingResult);
                    await handling.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    Mission after = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    AssertEqual(MissionStatusEnum.Cancelled, after.Status, "neither a late success nor late failure replaces durable cancellation");
                    AssertEqual(MissionCancellation.OperatorCancelReason, after.FailureReason, "the operator cancellation reason is preserved");
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id, 100).ConfigureAwait(false);
                    AssertFalse(events.Any(item => item.EventType == "mission.completed" || item.EventType == "mission.landing_failed"),
                        "the stale handler must emit neither false completion nor landing-failure recovery events");
                    ProductionFactPage<MissionAttemptFact> facts = await testDb.Driver.MissionAttemptFacts.EnumerateAsync(
                        new ProductionFactQuery { FromUtc = DateTime.UtcNow.AddMinutes(-1), ToUtc = DateTime.UtcNow.AddMinutes(1) }).ConfigureAwait(false);
                    AssertFalse(facts.Items.Any(item => item.MissionId == mission.Id
                        && (item.FactType == MissionAttemptFactTypeEnum.Landed || item.FactType == MissionAttemptFactTypeEnum.Failed)),
                        "the stale handler must not manufacture a landed or failed attempt fact for cancelled work");
                    AssertEqual(0, git.DeleteBranchCalls.Count, "cancelled source branches remain available for recovery");
                }
                finally
                {
                    landing.Release.TrySetResult(landingResult);
                    if (handling != null) { try { await handling.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { } }
                    try { Directory.Delete(root, true); } catch { }
                }
            }
        }

        private sealed class HeldLandingService : ILandingService
        {
            public TaskCompletionSource<bool> Entered { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public async Task<bool> MergeInDedicatedWorktreeAsync(Vessel vessel, Mission mission, string targetBranch,
                string? sourceBranch = null, string? commitMessage = null, CancellationToken token = default)
            {
                Entered.TrySetResult(true);
                return await Release.Task.ConfigureAwait(false);
            }
            public Task<bool> RetryLandingAsync(string missionId, string? tenantId = null, CancellationToken token = default) => throw new NotImplementedException();
            public bool IsRetryInFlight(string missionId) => false;
        }

        private sealed class NoOpRemoteTriggerService : IRemoteTriggerService
        {
            public Task FireDrainerAsync(string vesselId, string text, CancellationToken token = default) => Task.CompletedTask;
            public Task FireCriticalAsync(string text, CancellationToken token = default) => Task.CompletedTask;
            public AgentWakeSessionRegistration RegisterAgentWakeSession(AgentWakeSessionRegistration registration) => registration;
            public AgentWakeSessionRegistration? GetAgentWakeSession() => null;
            public AgentWakeStatusSnapshot GetAgentWakeStatus() => new AgentWakeStatusSnapshot();
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("WhenPredicateDisabled_DoesNotInvokeProcessEntry", async () =>
            {
                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                AutoLandEvaluator evaluator = new AutoLandEvaluator();
                AutoLandPredicate predicate = new AutoLandPredicate { Enabled = false };

                string action = await EvaluateAndMaybeFireAsync(evaluator, mergeQueue, predicate, string.Empty, "entry-1").ConfigureAwait(false);

                AssertEqual(0, mergeQueue.ProcessedEntryIds.Count, "Disabled predicate should not trigger ProcessEntryByIdAsync");
                AssertTrue(action.StartsWith("skipped:"), "Action should be skipped when predicate is disabled");
                AssertContains("disabled", action, "Skipped action should contain 'disabled' reason");
            });

            await RunTest("WhenPredicatePasses_InvokesProcessEntryByIdAsync", async () =>
            {
                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                AutoLandEvaluator evaluator = new AutoLandEvaluator();
                AutoLandPredicate predicate = new AutoLandPredicate { Enabled = true };
                string diff = "+++ b/src/Foo.cs\n+added line\n";

                string action = await EvaluateAndMaybeFireAsync(evaluator, mergeQueue, predicate, diff, "entry-abc").ConfigureAwait(false);

                AssertEqual("triggered", action, "Passing predicate should return triggered action");
                AssertEqual(1, mergeQueue.ProcessedEntryIds.Count, "ProcessEntryByIdAsync should have been called once");
                AssertEqual("entry-abc", mergeQueue.ProcessedEntryIds[0], "ProcessEntryByIdAsync should be called with correct entry ID");
                await AssertStaleCancellationOutcomeAsync(true).ConfigureAwait(false);
            });

            await RunTest("WhenPredicateFails_DoesNotInvokeProcessEntry_ReturnsSkipReason", async () =>
            {
                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                AutoLandEvaluator evaluator = new AutoLandEvaluator();
                AutoLandPredicate predicate = new AutoLandPredicate
                {
                    Enabled = true,
                    DenyPaths = new List<string> { "**/CLAUDE.md" }
                };
                string diff = "+++ b/CLAUDE.md\n+some change\n";

                string action = await EvaluateAndMaybeFireAsync(evaluator, mergeQueue, predicate, diff, "entry-xyz").ConfigureAwait(false);

                AssertEqual(0, mergeQueue.ProcessedEntryIds.Count, "Fail predicate should not trigger ProcessEntryByIdAsync");
                AssertTrue(action.StartsWith("skipped:"), "Action should be skipped when predicate fails");
                AssertContains("CLAUDE.md", action, "Skipped reason should identify the denied path");
                await AssertStaleCancellationOutcomeAsync(false).ConfigureAwait(false);
            });
        }
    }
}
