namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
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

        // Shape taken from a real fleet UnitTest check: the first project listed passes and a
        // later, much larger project fails. This ordering is the whole point -- first-match parsing
        // reports the clean 83 and never sees the failure.
        private const string _MultiProjectOutputFirstPassesLaterFails =
            "Passed!  - Failed:     0, Passed:    83, Skipped:     0, Total:    83, Duration: 99 ms - ExampleService.VinDecoding.Tests.dll (net10.0)\n" +
            "Passed!  - Failed:     0, Passed:    26, Skipped:     0, Total:    26, Duration: 239 ms - ExampleVessel.BundlePublisher.Tests.dll (net10.0)\n" +
            "Passed!  - Failed:     0, Passed:    50, Skipped:     3, Total:    53, Duration: 7 s - ExamplePortal.Worker.Tests.dll (net10.0)\n" +
            "Failed!  - Failed:     1, Passed:  2653, Skipped:     0, Total:  2654, Duration: 21 s - ExampleDevice.Agent.Tests.dll (net10.0)\n";

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

            await RunTest("CRLF dotnet and pytest output keeps aggregate counts and duration", () =>
            {
                string dotnet =
                    "Passed!  - Failed:     0, Passed:    12, Skipped:     1, Total:    13, Duration: 700 ms - First.Tests.dll (net10.0)\r\n" +
                    "Failed!  - Failed:     2, Passed:    20, Skipped:     3, Total:    25, Duration: 4 s - Second.Tests.dll (net10.0)\r\n";
                CheckRunTestSummary? dotnetSummary = CheckRunParsingService.ParseTestSummary(dotnet);
                AssertNotNull(dotnetSummary, "dotnet summary");
                AssertEqual(32, dotnetSummary!.Passed, "All dotnet projects contribute passed tests.");
                AssertEqual(2, dotnetSummary.Failed, "All dotnet projects contribute failed tests.");
                AssertEqual(4, dotnetSummary.Skipped, "All dotnet projects contribute skipped tests.");
                AssertEqual(38, dotnetSummary.Total, "All dotnet projects contribute total tests.");
                AssertEqual(4000L, dotnetSummary.DurationMs, "Parallel project duration is the longest project.");

                string pytest =
                    "===== 1 failed, 2 passed in 0.10s =====\r\n" +
                    "===== 5 passed, 2 skipped in 1.50s =====\r\n";
                CheckRunTestSummary? pytestSummary = CheckRunParsingService.ParseTestSummary(pytest);
                AssertNotNull(pytestSummary, "pytest summary");
                AssertEqual(5, pytestSummary!.Passed, "Pytest uses its final summary line.");
                AssertEqual(0, pytestSummary.Failed, "Pytest uses the final failure count.");
                AssertEqual(2, pytestSummary.Skipped, "Pytest uses the final skipped count.");
                AssertEqual(7, pytestSummary.Total, "Pytest total includes skipped tests.");
                AssertEqual(1500L, pytestSummary.DurationMs, "Pytest uses the final duration.");

                string table = "unit-shard-1 Total: 1147 Passed: 1146 Failed: 0 Skipped: 1 Runtime: 116691ms\n"
                    + "unit-shard-2 Total: 2242 Passed: 2241 Failed: 0 Skipped: 1 Runtime: 40623ms\n"
                    + "unit-shard-3 Total: 447 Passed: 447 Failed: 0 Skipped: 0 Runtime: 27712ms\n"
                    + "unit-shard-4 Total: 485 Passed: 485 Failed: 0 Skipped: 0 Runtime: 48965ms\n"
                    + "unit-shard-5 Total: 758 Passed: 757 Failed: 0 Skipped: 1 Runtime: 68418ms\n"
                    + "unit-shard-6 Total: 601 Passed: 601 Failed: 0 Skipped: 0 Runtime: 55834ms\n"
                    + "unit Total: 5680 Passed: 5677 Failed: 0 Skipped: 3 (6 of 6 shards summarised)\n";
                foreach (string newline in new[] { "\n", "\r\n" })
                {
                    CheckRunTestSummary? console = CheckRunParsingService.ParseTestSummary(table.Replace("\n", newline));
                    AssertNotNull(console, "the actual sharded console summary is recognized");
                    AssertEqual(5680, console!.Total);
                    AssertEqual(5677, console.Passed);
                    AssertEqual(0, console.Failed);
                    AssertEqual(3, console.Skipped, "the aggregate replaces its shard rows");
                    AssertNull(console.DurationMs, "concurrent shard runtimes are not summed");
                }
                string details = "unit-shard-1 Total: 2 Passed: 1 Failed: 1 Skipped: 0\nunit-shard-2 Total: 2 Passed: 1 Failed: 0 Skipped: 1\n";
                string aggregate = "unit Total: 4 Passed: 2 Failed: 1 Skipped: 1 (2 of 2 shards summarised)\n";
                foreach (string body in new[] { details, details + aggregate })
                {
                    CheckRunTestSummary? failed = CheckRunParsingService.ParseTestSummary(body + "automated Total: 3 Passed: 2 Failed: 0 Skipped: 1\n");
                    AssertNotNull(failed, "shard fallback and independent suites remain visible");
                    AssertEqual(7, failed!.Total); AssertEqual(4, failed.Passed); AssertEqual(1, failed.Failed); AssertEqual(2, failed.Skipped);
                }
                AssertNull(CheckRunParsingService.ParseTestSummary(details + "unit Total: 4 Passed: 3 Failed: 0 Skipped: 1 (2 of 2 shards summarised)\n"), "an inconsistent aggregate cannot hide a failed shard");
                AssertNull(CheckRunParsingService.ParseTestSummary(details + details), "duplicate shard rows are ambiguous");
                CheckRunTestSummary? mixed = CheckRunParsingService.ParseTestSummary(details + aggregate
                    + "Failed!  - Failed: 1, Passed: 1, Skipped: 0, Total: 2, Duration: 10 ms - Other.Tests.dll (net10.0)\n");
                AssertNotNull(mixed, "an independent non-table summary remains visible");
                AssertEqual(6, mixed!.Total); AssertEqual(3, mixed.Passed); AssertEqual(2, mixed.Failed); AssertEqual(1, mixed.Skipped);
                AssertNull(CheckRunParsingService.ParseTestSummary(details + aggregate
                    + "Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 10 ms - Other.Tests.dll (net10.0)\n"
                    + "===== 1 failed, 2 passed in 0.10s =====\n"), "ambiguous outside formats cannot hide a failure through parser precedence");
                AssertNull(CheckRunParsingService.ParseTestSummary(details + aggregate.Replace("2 of 2", "1 of 2")), "an incomplete aggregate is not a verified count");
                AssertNull(CheckRunParsingService.ParseTestSummary(details + "unit Total: malformed\n"), "a malformed table stays unknown");
            }).ConfigureAwait(false);

            await RunTest("Jest and Vitest summaries parse counts, watch output, and duration", () =>
            {
                (string Name, string Output, int Passed, int Failed, int Skipped, int Total, long DurationMs)[] cases =
                {
                    (
                        "Jest final watch summary",
                        "Test Suites: 1 failed, 2 passed, 3 total\r\n" +
                        "Tests:       3 failed, 4 passed, 1 skipped, 2 todo, 10 total\r\n" +
                        "Tests:       1 failed, 7 passed, 2 skipped, 1 todo, 11 total\r\n" +
                        "Time:        2.5 s\r\n" +
                        "Ran all test suites.\r\n",
                        7, 1, 3, 11, 2500L
                    ),
                    (
                        "Repeated counts keep the last valid watch summary",
                        "Tests:       4 failed, 4 passed, 8 total\r\n" +
                        "Tests:       1 failed, 2 failed, 2 passed, 3 passed, 1 skipped, 2 todo, 11 total\r\n" +
                        "Tests:       no test results available\r\n" +
                        "Time:        9.0 s\r\n" +
                        "Duration:    3.25 s\r\n",
                        5, 3, 3, 11, 3250L
                    ),
                    (
                        "Indented Vitest summary",
                        " ❯ src/example.test.ts (10 tests | 1 failed | 2 skipped | 1 todo)\r\n" +
                        " Test Files  1 failed | 2 passed (3)\r\n" +
                        "      Tests  1 failed | 5 passed | 2 skipped | 1 todo (9)\r\n" +
                        "   Duration  1.75s (transform 400ms, setup 100ms, collect 200ms, tests 50ms)\r\n",
                        5, 1, 3, 9, 1750L
                    )
                };

                foreach ((string name, string output, int passed, int failed, int skipped, int total, long durationMs) in cases)
                {
                    CheckRunTestSummary? summary = CheckRunParsingService.ParseTestSummary(output);
                    AssertNotNull(summary, name + " summary");
                    AssertEqual("javascript", summary!.Format, name + " format");
                    AssertEqual(passed, summary.Passed, name + " passed");
                    AssertEqual(failed, summary.Failed, name + " failed");
                    AssertEqual(skipped, summary.Skipped, name + " skipped and todo");
                    AssertEqual(total, summary.Total, name + " total");
                    AssertEqual(durationMs, summary.DurationMs, name + " duration");
                }
            }).ConfigureAwait(false);

            await RunTest("Test suite and test file counts alone are not test totals", () =>
            {
                string output =
                    "Test Suites: 1 failed, 2 passed, 3 total\n" +
                    "Test Files  1 failed | 2 passed (3)\n";
                AssertNull(CheckRunParsingService.ParseTestSummary(output), "suite and file counts do not describe individual tests");
            }).ConfigureAwait(false);

            await RunTest("Fresh JUnit artifact overrides conflicting console counts", () =>
            {
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada_junit_precedence_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);
                try
                {
                    File.WriteAllText(Path.Combine(workingDirectory, "results.xml"),
                        "<testsuites tests=\"12\" failures=\"2\" errors=\"1\" skipped=\"3\"><testsuite tests=\"12\" failures=\"2\" errors=\"1\" skipped=\"3\" time=\"1.25\" /></testsuites>");
                    List<CheckRunArtifact> artifacts = new List<CheckRunArtifact>
                    {
                        new CheckRunArtifact { Path = "results.xml", LastWriteUtc = DateTime.UtcNow }
                    };
                    CheckRunTestSummary? summary = CheckRunParsingService.ParseTestSummary(_SingleProjectPassOutput, workingDirectory, artifacts);
                    AssertNotNull(summary, "JUnit summary");
                    AssertEqual("junit", summary!.Format, "Format");
                    AssertEqual(6, summary.Passed, "Passed");
                    AssertEqual(3, summary.Failed, "Failures and errors");
                    AssertEqual(3, summary.Skipped, "Skipped");
                    AssertEqual(12, summary.Total, "Total");
                    AssertEqual(1250L, summary.DurationMs, "DurationMs");
                }
                finally
                {
                    Directory.Delete(workingDirectory, recursive: true);
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Malformed and outside JUnit artifacts fall back to console counts", () =>
            {
                string baseDirectory = Path.Combine(Path.GetTempPath(), "armada_junit_fallback_" + Guid.NewGuid().ToString("N"));
                string workingDirectory = Path.Combine(baseDirectory, "checkout");
                string outsideDirectory = Path.Combine(baseDirectory, "outside");
                Directory.CreateDirectory(workingDirectory);
                Directory.CreateDirectory(outsideDirectory);
                try
                {
                    File.WriteAllText(Path.Combine(workingDirectory, "malformed.xml"), "<testsuite tests=\"8\">");
                    File.WriteAllText(Path.Combine(outsideDirectory, "results.xml"),
                        "<testsuite tests=\"99\" failures=\"0\" errors=\"0\" skipped=\"0\" />");
                    List<CheckRunArtifact> artifacts = new List<CheckRunArtifact>
                    {
                        new CheckRunArtifact { Path = "malformed.xml", LastWriteUtc = DateTime.UtcNow },
                        new CheckRunArtifact { Path = "../outside/results.xml", LastWriteUtc = DateTime.UtcNow }
                    };
                    CheckRunTestSummary? summary = CheckRunParsingService.ParseTestSummary(_SingleProjectPassOutput, workingDirectory, artifacts);
                    AssertNotNull(summary, "console summary");
                    AssertEqual("dotnet", summary!.Format, "Malformed or outside artifacts do not replace console output.");
                    AssertEqual(12, summary.Passed, "Passed");
                    AssertEqual(13, summary.Total, "Total");
                }
                finally
                {
                    Directory.Delete(baseDirectory, recursive: true);
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);

            // An artifact path is relative to the check's working directory. A path that climbs into a
            // sibling directory whose name starts with the working directory's name is outside it and
            // must not be read, while the same file inside the working directory is.
            await RunTest("Artifact summaries refuse a sibling directory that shares the working directory's name prefix", () =>
            {
                string baseDirectory = Path.Combine(Path.GetTempPath(), "armada_parse_containment_" + Guid.NewGuid().ToString("N"));
                string workingDirectory = Path.Combine(baseDirectory, "checkout");
                string sibling = Path.Combine(baseDirectory, "checkout-backup");
                Directory.CreateDirectory(workingDirectory);
                Directory.CreateDirectory(sibling);
                const string coverage = "<coverage line-rate=\"0.75\" lines-covered=\"15\" lines-valid=\"20\"></coverage>";
                const string junit = "<testsuite tests=\"4\" failures=\"1\" errors=\"0\" skipped=\"0\"></testsuite>";
                File.WriteAllText(Path.Combine(sibling, "coverage.cobertura.xml"), coverage);
                File.WriteAllText(Path.Combine(sibling, "results.xml"), junit);
                File.WriteAllText(Path.Combine(workingDirectory, "coverage.cobertura.xml"), coverage);
                File.WriteAllText(Path.Combine(workingDirectory, "results.xml"), junit);

                try
                {
                    List<CheckRunArtifact> outside = new List<CheckRunArtifact>
                    {
                        new CheckRunArtifact { Path = "../checkout-backup/coverage.cobertura.xml" },
                        new CheckRunArtifact { Path = "../checkout-backup/results.xml" }
                    };
                    AssertNull(CheckRunParsingService.ParseCoverageSummary(workingDirectory, outside), "a sibling-prefix coverage artifact is not read");
                    AssertNull(CheckRunParsingService.ParseTestSummary(null, workingDirectory, outside), "a sibling-prefix test artifact is not read");

                    List<CheckRunArtifact> inside = new List<CheckRunArtifact>
                    {
                        new CheckRunArtifact { Path = "coverage.cobertura.xml" },
                        new CheckRunArtifact { Path = "results.xml" }
                    };
                    AssertNotNull(CheckRunParsingService.ParseCoverageSummary(workingDirectory, inside), "the same coverage artifact inside the working directory is read");
                    AssertNotNull(CheckRunParsingService.ParseTestSummary(null, workingDirectory, inside), "the same test artifact inside the working directory is read");
                }
                finally
                {
                    Directory.Delete(baseDirectory, recursive: true);
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Test timing names per-assembly totals and the ten slowest tests from fresh TRX artifacts", () =>
            {
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada_trx_timing_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);
                try
                {
                    System.Text.StringBuilder results = new System.Text.StringBuilder();
                    System.Text.StringBuilder definitions = new System.Text.StringBuilder();
                    for (int i = 1; i <= 12; i++)
                    {
                        string storage = i <= 3 ? "/build/Slow.Tests.dll" : "/build/Fast.Tests.dll";
                        results.Append("<UnitTestResult testId=\"t" + i + "\" testName=\"Case" + i.ToString("00") + "\" duration=\"00:00:0" + (i <= 3 ? "4" : "0") + "." + i.ToString("00") + "00000\" outcome=\"Passed\" />");
                        definitions.Append("<UnitTest id=\"t" + i + "\" storage=\"" + storage + "\" />");
                    }

                    File.WriteAllText(Path.Combine(workingDirectory, "unit.trx"),
                        "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>" + results
                        + "</Results><TestDefinitions>" + definitions + "</TestDefinitions>"
                        + "<ResultSummary outcome=\"Completed\"><Counters total=\"12\" passed=\"12\" failed=\"0\" notExecuted=\"0\" /></ResultSummary></TestRun>");
                    List<CheckRunArtifact> artifacts = new List<CheckRunArtifact> { new CheckRunArtifact { Path = "unit.trx", LastWriteUtc = DateTime.UtcNow } };

                    string timing = CheckRunParsingService.DescribeTestTiming(workingDirectory, artifacts);
                    AssertContains("tests=12", timing);
                    AssertContains("assemblies=Slow.Tests.dll=12060ms, Fast.Tests.dll=720ms", timing, "assemblies are listed slowest first with their summed time");
                    AssertContains("slowest=Case03=4030ms, Case02=4020ms, Case01=4010ms, Case12=120ms", timing, "the slowest tests come first");
                    AssertFalse(timing.Contains("Case05=", StringComparison.Ordinal), "only the ten slowest tests are listed");
                    AssertContains("Case06=60ms", timing, "the tenth slowest test is listed");
                }
                finally
                {
                    Directory.Delete(workingDirectory, recursive: true);
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Test timing names the missing or unreadable TRX artifact instead of failing", () =>
            {
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada_trx_timing_missing_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);
                try
                {
                    AssertEqual("none (no fresh TRX artifact)", CheckRunParsingService.DescribeTestTiming(workingDirectory, new List<CheckRunArtifact>()));
                    AssertEqual("none (no fresh TRX artifact)", CheckRunParsingService.DescribeTestTiming(null, null));

                    File.WriteAllText(Path.Combine(workingDirectory, "broken.trx"), "<TestRun><Results><UnitTestResult");
                    List<CheckRunArtifact> broken = new List<CheckRunArtifact> { new CheckRunArtifact { Path = "broken.trx", LastWriteUtc = DateTime.UtcNow } };
                    string timing = CheckRunParsingService.DescribeTestTiming(workingDirectory, broken);
                    AssertTrue(timing.StartsWith("unreadable (broken.trx: ", StringComparison.Ordinal), "a TRX that does not parse is named with its reason: " + timing);
                    AssertFalse(timing.Contains('\n'), "the reason stays on one line");
                }
                finally
                {
                    Directory.Delete(workingDirectory, recursive: true);
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
