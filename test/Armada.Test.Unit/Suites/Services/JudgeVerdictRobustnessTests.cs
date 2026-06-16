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

            // -----------------------------------------------------------------------------------
            // TestEngineer hardening: pin the rest of the changed parse surface and the composite
            // retry decision the completion handler actually makes
            // (ShouldRetryMissingJudgeVerdict(verdict != None, RecoveryAttempts)).
            // -----------------------------------------------------------------------------------

            // Terminal-at-the-parse-layer: a genuine canonical FAIL verdict is extracted as Fail so
            // the completion handler classifies it as a real rejection (terminal), never a retry.
            await RunTest("ParseJudgeVerdict_CanonicalFailVerdict_ReturnsFail", () =>
            {
                string output =
                    "## Completeness\nReviewed.\n## Correctness\nA regression slipped through.\n" +
                    "## Tests\nMissing negative-path coverage.\n## Failure Modes\nData loss on conflict.\n" +
                    "## Verdict\nFAIL\n" +
                    "[ARMADA:VERDICT] FAIL\n";
                AssertEqual("Fail", InvokeParseVerdict(output));
                return Task.CompletedTask;
            });

            // Terminal-at-the-parse-layer: canonical NEEDS_REVISION extracted as NeedsRevision.
            await RunTest("ParseJudgeVerdict_CanonicalNeedsRevisionVerdict_ReturnsNeedsRevision", () =>
            {
                AssertEqual("NeedsRevision", InvokeParseVerdict("[ARMADA:VERDICT] NEEDS_REVISION\n"));
                return Task.CompletedTask;
            });

            // Completes the progress-signal matrix (PASS / NEEDS_REVISION already pinned): a dropped
            // final line whose in-flight progress signal was FAIL is salvaged as Fail, not None --
            // so a genuine rejection surfaced only as a progress signal still settles terminal.
            await RunTest("ParseJudgeVerdict_ProgressSignalForm_Fail", () =>
            {
                AssertEqual("Fail", InvokeParseVerdict("[verdict] FAIL\n"));
                return Task.CompletedTask;
            });

            // The fix leans on the regex IgnoreCase flag for both the tag (ARMADA: optional) and the
            // verdict token. Pin that the runtime's casing variations are still honored.
            await RunTest("ParseJudgeVerdict_CaseInsensitiveTagAndToken_StillExtracts", () =>
            {
                AssertEqual("Pass", InvokeParseVerdict("[Verdict] pass\n"));
                AssertEqual("NeedsRevision", InvokeParseVerdict("[armada:verdict] needs_revision\n"));
                return Task.CompletedTask;
            });

            // Last-verdict-wins (reverse scan): an in-flight progress signal emitted mid-review must
            // NOT override the real final verdict. This is exactly the backgrounded scenario -- the
            // review echoed [verdict] NEEDS_REVISION while iterating, then concluded PASS. The final
            // canonical line is the authoritative verdict.
            await RunTest("ParseJudgeVerdict_FinalCanonicalVerdictWinsOverEarlierProgressSignal", () =>
            {
                string output =
                    "## Completeness\nCovered.\n" +
                    "[verdict] NEEDS_REVISION\n" +
                    "re-ran the suite in the foreground; the gap is now addressed\n" +
                    "## Correctness\nSound.\n## Tests\nGood.\n## Failure Modes\nNone.\n" +
                    "## Verdict\nPASS\n" +
                    "[ARMADA:VERDICT] PASS\n";
                AssertEqual("Pass", InvokeParseVerdict(output));
                return Task.CompletedTask;
            });

            // Windows runtime output uses CRLF line endings; the parser normalizes \r\n before
            // splitting. Pin that a verdict on a CRLF line is still extracted.
            await RunTest("ParseJudgeVerdict_CrlfLineEndings_StillExtractsPass", () =>
            {
                string output =
                    "## Completeness\r\nCovered.\r\n## Correctness\r\nGood.\r\n" +
                    "## Tests\r\nGood.\r\n## Failure Modes\r\nNone.\r\n" +
                    "[ARMADA:VERDICT] PASS\r\n";
                AssertEqual("Pass", InvokeParseVerdict(output));
                return Task.CompletedTask;
            });

            // Boundary inputs that drive the retry classifier: null and whitespace-only output both
            // parse to None (the operational-miss signal), so a Judge that produced no usable output
            // is classified as retryable rather than throwing on the null AgentOutput.
            await RunTest("ParseJudgeVerdict_NullAndWhitespaceOutput_ReturnsNone", () =>
            {
                AssertEqual("None", InvokeParseVerdict(null));
                AssertEqual("None", InvokeParseVerdict("   \n  \t \n"));
                return Task.CompletedTask;
            });

            // Composite over the EXACT decision the completion handler makes: a no-verdict run is
            // re-run in place across the whole bounded budget, then settles terminal. Mirrors
            // ShouldRetryMissingJudgeVerdict(verdict != None, RecoveryAttempts) with parse == None.
            await RunTest("MissingVerdictDecision_NoVerdict_RetriesAcrossBudgetThenSettlesTerminal", () =>
            {
                string noVerdict =
                    "## Tests\nLaunched the suite as a background task.\n" +
                    "tests are still running... scheduled a wakeup\n";
                bool hasVerdict = !String.Equals("None", InvokeParseVerdict(noVerdict), StringComparison.Ordinal);
                AssertFalse(hasVerdict, "A backgrounded run with no verdict line must parse to None.");
                AssertTrue(InvokeShouldRetry(hasVerdict, 0), "First miss is retried in place.");
                AssertTrue(InvokeShouldRetry(hasVerdict, 1), "Second miss is still within budget.");
                AssertFalse(InvokeShouldRetry(hasVerdict, 2), "At the cap the mission settles terminal.");
                return Task.CompletedTask;
            });

            // Composite: a genuine verdict salvaged only from the progress-signal fallback (final
            // standalone line dropped) is terminal -- it must NOT be retried even with budget left.
            // Pins that the salvage path does not turn a real FAIL into a spurious retry loop.
            await RunTest("SalvagedRealVerdictDecision_FromProgressSignal_IsTerminalNotRetried", () =>
            {
                string salvagedFail =
                    "## Completeness\nReviewed.\n## Correctness\nBug found.\n## Tests\nGap.\n## Failure Modes\nCrash.\n" +
                    "[verdict] FAIL\n" +
                    "the standalone verdict line never flushed before exit\n";
                bool hasVerdict = !String.Equals("None", InvokeParseVerdict(salvagedFail), StringComparison.Ordinal);
                AssertTrue(hasVerdict, "A FAIL surfaced as a progress signal must be salvaged, not treated as missing.");
                AssertEqual("Fail", InvokeParseVerdict(salvagedFail));
                AssertFalse(InvokeShouldRetry(hasVerdict, 0), "A salvaged real verdict is terminal and must not be retried.");
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
