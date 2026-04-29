namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
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
            });
        }
    }
}
