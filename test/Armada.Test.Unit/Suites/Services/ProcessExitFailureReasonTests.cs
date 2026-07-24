namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Guards obj_mrwvb10w: the process-exit failure-reason extractor must not scrape an agent's own
    /// SUCCESS report line (e.g. a "0 Errors" build-summary table row) as the failure reason, which
    /// previously produced false-Failed missions that cascade-cancelled good work.
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
                // The exact obj_mrwvb10w evidence line.
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
                AssertTrue(AdmiralService.HasExplicitSuccessVerdict(new[] { "review done", "[ARMADA:VERDICT] PASS" }), "a reviewer [ARMADA:VERDICT] PASS must be honored (the obj_mrwvb10w evidence was a reviewer)");
            });

            await RunTest("NoOrNonSuccessVerdict_IsNotHonored", () =>
            {
                AssertTrue(!AdmiralService.HasExplicitSuccessVerdict(new[] { "build succeeded", "0 Errors", "done" }), "prose success without the marker is not an explicit verdict");
                AssertTrue(!AdmiralService.HasExplicitSuccessVerdict(new[] { "[ARMADA:RESULT] FAILED" }), "an explicit FAILED result is not success");
                AssertTrue(!AdmiralService.HasExplicitSuccessVerdict(new[] { "[ARMADA:VERDICT] NEEDS_REVISION" }), "NEEDS_REVISION is not a success (handled by reviewer-chain logic)");
                AssertTrue(!AdmiralService.HasExplicitSuccessVerdict(new[] { "[ARMADA:STATUS] Complete" }), "a STATUS marker is not a RESULT/VERDICT verdict (status is handled during the run)");
                AssertTrue(!AdmiralService.HasExplicitSuccessVerdict(new string[0]), "empty output is not success");
            });
        }
    }
}
