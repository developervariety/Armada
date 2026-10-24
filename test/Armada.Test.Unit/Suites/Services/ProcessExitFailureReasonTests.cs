namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Guards the process-exit failure-reason extractor. It must not scrape an agent's own SUCCESS report
    /// line (e.g. a "0 Errors" build-summary table row) as the failure reason, which produced false-Failed
    /// missions that cascade-cancelled good work. It must also select a provider SAFEGUARD, quota, credit or
    /// credential signal ahead of a later generic result-error record, so the captain is benched on a
    /// provider rejection instead of released and re-offered a mission it cannot run.
    /// </summary>
    public class ProcessExitFailureReasonTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Process Exit Failure Reason";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("SuccessReportLines_AreNotGenuineErrors", () =>
            {
                // The exact evidence line.
                AssertTrue(!AdmiralService.IsGenuineErrorSignal("| `dotnet build -c Release` | **0 Warnings, 0 Errors** |"), "a 0-Errors build-summary row must not be a failure signal");
                AssertTrue(!AdmiralService.IsGenuineErrorSignal("Build succeeded -- 0 Errors, 0 Warnings"), "0 Errors is success");
                AssertTrue(!AdmiralService.IsGenuineErrorSignal("no errors found"), "no errors is success");
                AssertTrue(!AdmiralService.IsGenuineErrorSignal("zero errors"), "zero errors is success");
                AssertTrue(!AdmiralService.IsGenuineErrorSignal("Completed without errors"), "without errors is success");
            });

            await RunTest("GenuineErrorLines_AreDetected", () =>
            {
                AssertTrue(AdmiralService.IsGenuineErrorSignal("error CS0507: cannot change access modifiers"), "compiler error is a failure");
                AssertTrue(AdmiralService.IsGenuineErrorSignal("Build FAILED. 3 Errors"), "3 Errors is a failure");
                AssertTrue(AdmiralService.IsGenuineErrorSignal("1 error(s), 0 warning(s)"), "1 error is a failure");
                AssertTrue(AdmiralService.IsGenuineErrorSignal("System.InvalidOperationException: boom"), "an exception is a failure");
                AssertTrue(AdmiralService.IsGenuineErrorSignal("API Error: overloaded"), "API Error is a failure");
                AssertTrue(AdmiralService.IsGenuineErrorSignal("[stderr] fatal: not a git repository"), "a stderr line is a failure");
            });

            await RunTest("ExplicitSuccessVerdicts_AreHonored", () =>
            {
                AssertTrue(AdmiralService.HasExplicitSuccessVerdict(new[] { "doing work", "[ARMADA:RESULT] COMPLETE", "cleanup" }), "a Worker [ARMADA:RESULT] COMPLETE must be honored");
                AssertTrue(AdmiralService.HasExplicitSuccessVerdict(new[] { "  [armada:result]   complete  " }), "case/whitespace-insensitive");
                AssertTrue(AdmiralService.HasExplicitSuccessVerdict(new[] { "review done", "[ARMADA:VERDICT] PASS" }), "a reviewer [ARMADA:VERDICT] PASS must be honored (the evidence was a reviewer)");
            });

            await RunTest("NoOrNonSuccessVerdict_IsNotHonored", () =>
            {
                AssertTrue(!AdmiralService.HasExplicitSuccessVerdict(new[] { "build succeeded", "0 Errors", "done" }), "prose success without the marker is not an explicit verdict");
                AssertTrue(!AdmiralService.HasExplicitSuccessVerdict(new[] { "[ARMADA:RESULT] FAILED" }), "an explicit FAILED result is not success");
                AssertTrue(!AdmiralService.HasExplicitSuccessVerdict(new[] { "[ARMADA:VERDICT] NEEDS_REVISION" }), "NEEDS_REVISION is not a success (handled by reviewer-chain logic)");
                AssertTrue(!AdmiralService.HasExplicitSuccessVerdict(new[] { "[ARMADA:STATUS] Complete" }), "a STATUS marker is not a RESULT/VERDICT verdict (status is handled during the run)");
                AssertTrue(!AdmiralService.HasExplicitSuccessVerdict(new string[0]), "empty output is not success");
            });

            await RunTest("CredentialRejection_IsSelectedOverALaterGenericError", () =>
            {
                // A credential rejection the provider returns, followed by a generic runtime result-error
                // record on the way down. The rejection must be the recorded failure reason so the captain is
                // benched, not released and re-offered the same mission it cannot run. "opencode" is the
                // captain's own runtime, so its provider telemetry speaks for the mission.
                string[] lines = new[]
                {
                    "[ARMADA:ACTIVITY] opencode error Upstream request failed: Invalid credential (status 401)",
                    "[ARMADA:ACTIVITY] opencode error session processing aborted",
                    "Agent exited with code 1",
                };

                string? selected = AdmiralService.SelectProcessExitFailureReason(lines, "opencode");
                AssertTrue(selected != null, "a credential rejection in the tail must be selected");
                AssertTrue(ProviderQuotaLimitDetector.IsCreditAuthBenchSignal(selected),
                    "the selected reason must carry the credential-rejection signal that benches the captain, " +
                    "not the later generic error line");
                // The later generic line, scanned first newest-first, is itself a genuine error but NOT a bench
                // signal; without the credit/auth priority pass it would be selected and the captain released.
                AssertTrue(AdmiralService.IsGenuineErrorSignal("opencode error session processing aborted"),
                    "the trailing generic line is a genuine error");
                AssertTrue(!ProviderQuotaLimitDetector.IsCreditAuthBenchSignal("opencode error session processing aborted"),
                    "the trailing generic line is not a bench signal");
            });

            await RunTest("OrdinaryGenericError_IsNotBenched", () =>
            {
                // Negative control: an ordinary transient captain failure with no credential, credit or quota
                // signal must select the generic error line and must NOT read as a bench signal, so an ordinary
                // failure never quarantines the captain.
                string[] lines = new[]
                {
                    "[ARMADA:ACTIVITY] opencode error session processing aborted",
                    "Agent exited with code 1",
                };

                string? selected = AdmiralService.SelectProcessExitFailureReason(lines, "opencode");
                AssertTrue(selected != null, "a genuine error in the tail must be selected");
                AssertTrue(AdmiralService.IsGenuineErrorSignal(selected), "the selected reason is a genuine error");
                AssertTrue(!ProviderQuotaLimitDetector.IsCreditAuthBenchSignal(selected),
                    "an ordinary failure must not read as a credential/credit bench signal");
                AssertTrue(!ProviderQuotaLimitDetector.IsQuotaLimitSignal(selected),
                    "an ordinary failure must not read as a quota bench signal");
            });

            await RunTest("ForeignRuntimeCredentialLine_IsNotSelected", () =>
            {
                // A credential rejection a SUB-TOOL emitted (an opencode call made by a Cursor captain) is the
                // sub-tool's, not the mission's: it must never be scraped as the failure reason or bench the
                // captain. Only a genuine error on the captain's own runtime is selected.
                string[] lines = new[]
                {
                    "[ARMADA:ACTIVITY] opencode error Upstream request failed: Invalid credential (status 401)",
                    "[ARMADA:ACTIVITY] cursor error build step failed",
                    "Agent exited with code 1",
                };

                string? selected = AdmiralService.SelectProcessExitFailureReason(lines, "cursor");
                AssertTrue(selected != null, "the captain's own-runtime error must be selected");
                AssertTrue(!ProviderQuotaLimitDetector.IsCreditAuthBenchSignal(selected),
                    "a sub-tool's credential rejection must not bench the captain");
            });
        }
    }
}
