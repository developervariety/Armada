namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Reflection;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>
    /// Pins the verdict-emission robustness fix for Judge missions whose test run is
    /// backgrounded: a verdict reached mid-review (or echoed as a progress signal) is still
    /// extracted, a run that exits with no verdict at all is retried in place (bounded) rather
    /// than hard-failed, and an explicit FAIL / NEEDS_REVISION verdict stays terminal.
    /// Invokes the private static MissionService helpers via reflection so the contract is
    /// pinned without forcing it onto a public surface (mirrors JudgeHybridFollowUpsTests).
    /// </summary>
    public class JudgeVerdictRobustnessTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Judge Verdict Robustness";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            // Extraction fallback: a Judge that ran a slow/backgrounded test phase and emitted its
            // verdict before trailing test chatter still yields PASS even though the verdict line is
            // not the last line of output.
            await RunTest("ParseJudgeVerdict_VerdictBeforeBackgroundedTestChatter_StillExtractsPass", () =>
            {
                string output =
                    "## Completeness\nAll required pieces present.\n" +
                    "## Correctness\nLogic is sound.\n" +
                    "## Tests\nAdequate negative-path coverage.\n" +
                    "## Failure Modes\nNone material.\n" +
                    "## Verdict\nPASS\n" +
                    "[ARMADA:VERDICT] PASS\n" +
                    "tests are still running in the background... scheduled a wakeup to re-check\n";
                AssertEqual("Pass", InvokeParseVerdict(output));
                return Task.CompletedTask;
            });

            // Extraction fallback: the canonical standalone line was dropped, but the in-flight
            // verdict was surfaced as a progress signal ([verdict] PASS). Honor it.
            await RunTest("ParseJudgeVerdict_ProgressSignalForm_ExtractsVerdict", () =>
            {
                string output =
                    "## Completeness\nCovered.\n## Correctness\nGood.\n## Tests\nGood.\n## Failure Modes\nNone.\n" +
                    "[verdict] PASS\n" +
                    "now kicking off the full suite in the background and scheduling a wakeup\n";
                AssertEqual("Pass", InvokeParseVerdict(output));
                return Task.CompletedTask;
            });

            await RunTest("ParseJudgeVerdict_ProgressSignalForm_NeedsRevision", () =>
            {
                AssertEqual("NeedsRevision", InvokeParseVerdict("[verdict] NEEDS_REVISION\n"));
                return Task.CompletedTask;
            });

            // No verdict at all (the dropped-verdict bug): parser reports None so the caller can
            // classify it as a retryable operational miss.
            await RunTest("ParseJudgeVerdict_NoVerdictEmitted_ReturnsNone", () =>
            {
                string output =
                    "## Tests\nLaunched the suite as a background task.\n" +
                    "tests are still running... scheduled a wakeup\n";
                AssertEqual("None", InvokeParseVerdict(output));
                return Task.CompletedTask;
            });

            // Retry classification: a missing verdict under budget is retryable in place.
            await RunTest("ShouldRetryMissingVerdict_NoVerdictFirstMiss_ReturnsTrue", () =>
            {
                AssertTrue(InvokeShouldRetry(false, 0), "First missing-verdict miss must be retried in place, not hard-failed.");
                AssertTrue(InvokeShouldRetry(false, 1), "Second missing-verdict miss is still within the bounded budget.");
                return Task.CompletedTask;
            });

            // Bounded: once the retry budget is exhausted, stop retrying so it settles terminal.
            await RunTest("ShouldRetryMissingVerdict_BudgetExhausted_ReturnsFalse", () =>
            {
                AssertFalse(InvokeShouldRetry(false, 2), "Retries must be bounded so a persistently silent Judge is not retried forever.");
                AssertFalse(InvokeShouldRetry(false, 5), "Any count at or beyond the cap must stop retrying.");
                return Task.CompletedTask;
            });

            // Terminal: a real verdict (PASS / FAIL / NEEDS_REVISION) is never retried, regardless
            // of the retry count, so a genuine rejection does not loop.
            await RunTest("ShouldRetryMissingVerdict_RealVerdict_ReturnsFalse", () =>
            {
                AssertFalse(InvokeShouldRetry(true, 0), "A parseable verdict is terminal and must not trigger an in-place retry.");
                AssertFalse(InvokeShouldRetry(true, 1), "A parseable verdict stays terminal even with retries remaining.");
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Invoke the private static MissionService.ParseJudgeVerdict via reflection and return the
        /// enum name (None / Pass / Fail / NeedsRevision) so the private enum need not be referenced.
        /// </summary>
        private static string InvokeParseVerdict(string? agentOutput)
        {
            Assembly asm = typeof(Mission).Assembly;
            Type t = asm.GetType("Armada.Core.Services.MissionService")!;
            MethodInfo mi = t.GetMethod("ParseJudgeVerdict", BindingFlags.NonPublic | BindingFlags.Static)!;
            object? result = mi.Invoke(null, new object?[] { agentOutput });
            return result!.ToString()!;
        }

        /// <summary>
        /// Invoke the private static MissionService.ShouldRetryMissingJudgeVerdict via reflection.
        /// </summary>
        private static bool InvokeShouldRetry(bool hasParseableVerdict, int priorRetryCount)
        {
            Assembly asm = typeof(Mission).Assembly;
            Type t = asm.GetType("Armada.Core.Services.MissionService")!;
            MethodInfo mi = t.GetMethod("ShouldRetryMissingJudgeVerdict", BindingFlags.NonPublic | BindingFlags.Static)!;
            return (bool)mi.Invoke(null, new object?[] { hasParseableVerdict, priorRetryCount })!;
        }
    }
}
