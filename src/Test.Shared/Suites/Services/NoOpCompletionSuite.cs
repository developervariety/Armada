namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="NoOpCompletionDetector"/>. Positive cases confirm a fast, empty-diff,
    /// trivial-output completion is flagged; negative cases confirm real work, slow runs, unknown
    /// duration, and verbose output are NOT flagged (so a genuine "nothing to change" result survives).
    /// </summary>
    public sealed class NoOpCompletionSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the no-op completion suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("noop_empty_diff_fast_trivial_is_flagged", "Empty diff + fast + trivial output is flagged", TestTags.Positive, () =>
            {
                AssertTrue(NoOpCompletionDetector.IsNoOp("", "Done.", 1200));
                AssertTrue(NoOpCompletionDetector.IsNoOp(null, "   ", 500));
                AssertTrue(NoOpCompletionDetector.IsNoOp("   \n  ", "[ARMADA:PROGRESS] 100", 3000));
            }));

            cases.Add(Case("noop_nonempty_diff_not_flagged", "Non-empty diff is never flagged", TestTags.Negative, () =>
            {
                AssertFalse(NoOpCompletionDetector.IsNoOp("diff --git a/x b/x\n+change", "Done.", 1000));
            }));

            cases.Add(Case("noop_slow_run_not_flagged", "A slow run is not a fast false-complete", TestTags.Negative, () =>
            {
                // Empty diff but the captain spent real time investigating -> legitimate "nothing to do".
                AssertFalse(NoOpCompletionDetector.IsNoOp("", "Investigated; no change needed.", 120000));
            }));

            cases.Add(Case("noop_unknown_runtime_not_flagged", "Unknown runtime is not flagged (conservative)", TestTags.Negative, () =>
            {
                AssertFalse(NoOpCompletionDetector.IsNoOp("", "Done.", null));
            }));

            cases.Add(Case("noop_verbose_output_not_flagged", "Verbose output is not trivial", TestTags.Negative, () =>
            {
                string verbose = new string('x', 1000);
                AssertFalse(NoOpCompletionDetector.IsNoOp("", verbose, 2000));
            }));

            cases.Add(Case("noop_custom_thresholds_respected", "Custom thresholds are honored", TestTags.Positive, () =>
            {
                // With a 10s cap, a 5s run is fast; with a 2s cap, the same run is not.
                AssertTrue(NoOpCompletionDetector.IsNoOp("", "ok", 5000, maxRuntimeMs: 10000));
                AssertFalse(NoOpCompletionDetector.IsNoOp("", "ok", 5000, maxRuntimeMs: 2000));
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.NoOpCompletion",
                displayName: "No-Op Completion Detector",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.NoOpCompletion",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        #endregion
    }
}
