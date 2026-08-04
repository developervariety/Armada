namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Coverage for CheckRunParsingService test-summary parsing, focused on the multi-project
    /// dotnet case. <c>dotnet test</c> over a solution emits one Passed!/Failed! line per test
    /// project, so no single line describes the run. Reading only one line reports that project's
    /// counts as the whole suite: a failing check then renders a Summary such as
    /// "failed. 83 passed, 0 failed, 83 total" while a later project actually failed, and anything
    /// reading Summary instead of Output concludes the suite was clean. These tests pin the
    /// aggregate so that cannot recur, and pin the single-project and non-dotnet formats that must
    /// keep working unchanged.
    /// </summary>
    public class CheckRunParsingServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Check Run Parsing Service";

        // Shape taken from a real OtrBuddy UnitTest check: the first project listed passes and a
        // later, much larger project fails. This ordering is the whole point -- first-match parsing
        // reports the clean 83 and never sees the failure.
        private const string _MultiProjectOutputFirstPassesLaterFails =
            "Passed!  - Failed:     0, Passed:    83, Skipped:     0, Total:    83, Duration: 99 ms - FleetBase.VinDecoding.Tests.dll (net10.0)\n" +
            "Passed!  - Failed:     0, Passed:    26, Skipped:     0, Total:    26, Duration: 239 ms - OtrBuddy.BundlePublisher.Tests.dll (net10.0)\n" +
            "Passed!  - Failed:     0, Passed:    50, Skipped:     3, Total:    53, Duration: 7 s - FleetPortal.Worker.Tests.dll (net10.0)\n" +
            "Failed!  - Failed:     1, Passed:  2653, Skipped:     0, Total:  2654, Duration: 21 s - FleetDevice.Agent.Tests.dll (net10.0)\n";

        private const string _SingleProjectPassOutput =
            "Passed!  - Failed:     0, Passed:    12, Skipped:     1, Total:    13, Duration: 340 ms - Armada.Test.Unit.dll (net10.0)\n";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            // The regression this suite exists for. Against first-match parsing this reports
            // Failed=0 and the assertion below fails, which is the required proof that the test
            // covers the reported symptom rather than merely exercising the code.
            await RunTest("Multi-project dotnet output reports a later project's failure", () =>
            {
                CheckRunTestSummary? summary = CheckRunParsingService.ParseTestSummary(_MultiProjectOutputFirstPassesLaterFails);

                AssertNotNull(summary, "summary");
                AssertEqual("dotnet", summary!.Format, "Format");
                AssertEqual(1, summary.Failed, "Failed must count the failing project, not the first project");
            }).ConfigureAwait(false);

            await RunTest("Multi-project dotnet counts aggregate across every project", () =>
            {
                CheckRunTestSummary? summary = CheckRunParsingService.ParseTestSummary(_MultiProjectOutputFirstPassesLaterFails);

                AssertNotNull(summary, "summary");

                // 83 + 26 + 50 + 2653
                AssertEqual(2812, summary!.Passed, "Passed");

                // 0 + 0 + 3 + 0
                AssertEqual(3, summary.Skipped, "Skipped");

                // 83 + 26 + 53 + 2654
                AssertEqual(2816, summary.Total, "Total");
            }).ConfigureAwait(false);

            // Test projects run concurrently, so elapsed time tracks the longest project. Summing
            // would overstate it and taking the first would report 99 ms for a 21 s run.
            await RunTest("Multi-project dotnet duration is the longest project", () =>
            {
                CheckRunTestSummary? summary = CheckRunParsingService.ParseTestSummary(_MultiProjectOutputFirstPassesLaterFails);

                AssertNotNull(summary, "summary");
                AssertEqual(21000L, summary!.DurationMs, "DurationMs");
            }).ConfigureAwait(false);

            // Aggregation must not change the single-project result, which is the common case.
            await RunTest("Single-project dotnet output parses unchanged", () =>
            {
                CheckRunTestSummary? summary = CheckRunParsingService.ParseTestSummary(_SingleProjectPassOutput);

                AssertNotNull(summary, "summary");
                AssertEqual("dotnet", summary!.Format, "Format");
                AssertEqual(0, summary.Failed, "Failed");
                AssertEqual(12, summary.Passed, "Passed");
                AssertEqual(1, summary.Skipped, "Skipped");
                AssertEqual(13, summary.Total, "Total");
                AssertEqual(340L, summary.DurationMs, "DurationMs");
            }).ConfigureAwait(false);

            await RunTest("Output with no recognizable summary returns null", () =>
            {
                AssertNull(CheckRunParsingService.ParseTestSummary("Build succeeded.\n    0 Warning(s)\n"), "summary");
            }).ConfigureAwait(false);

            // The other parsers read one final aggregate line and correctly take the LAST match, so
            // they are not affected by the dotnet defect. Pinned here so a future change that
            // switches them to first-match is caught, and so the claim is verified rather than
            // asserted in prose.
            await RunTest("Pytest output takes the final summary line", () =>
            {
                string output =
                    "===== 1 failed, 2 passed in 0.10s =====\n" +
                    "===== 5 passed in 1.50s =====\n";

                CheckRunTestSummary? summary = CheckRunParsingService.ParseTestSummary(output);

                AssertNotNull(summary, "summary");
                AssertEqual("pytest", summary!.Format, "Format");
                AssertEqual(5, summary.Passed, "Passed");
                AssertEqual(0, summary.Failed, "Failed");
            }).ConfigureAwait(false);
        }
    }
}
