namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Unit tests for <see cref="FailedTestNameExtractor"/>: it reads real dotnet and python failure
    /// output into an ordered, de-duplicated set, keeps the "Failed! - Failed: N" summary out of the
    /// set, and reports overflow when it cannot keep every distinct name.
    /// </summary>
    public class FailedTestNameExtractorTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Failed Test Name Extractor";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("dotnet VSTest per-test Failed lines are extracted and the summary line is excluded", () =>
            {
                string output =
                    "Starting test execution, please wait...\n" +
                    "  Failed Example.Api.Tests.CatalogueTests.Materialise_Produces_Operations [1 ms]\n" +
                    "  Error Message:\n" +
                    "   Assert.Equal() Failure: Values differ\n" +
                    "  Failed Example.Api.Tests.ParserTests.Parse_ReadsHeader [3 ms]\n" +
                    "Failed!  - Failed:     2, Passed:  2560, Skipped:    14, Total:  2576, Duration: 1 m 49 s - Example.Api.Tests.dll (net10.0)\n";

                FailedTestNameExtractor.FailedTestNameSet set = FailedTestNameExtractor.Extract(output);
                AssertFalse(set.Overflow, "A small run does not overflow.");
                AssertEqual(2, set.Names.Count, "Two per-test Failed lines, and the summary is not one of them.");
                AssertEqual("Example.Api.Tests.CatalogueTests.Materialise_Produces_Operations", set.Names[0], "First failing test, without the duration marker.");
                AssertEqual("Example.Api.Tests.ParserTests.Parse_ReadsHeader", set.Names[1], "Second failing test.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("python unittest FAIL and ERROR headers normalise to module.Class.method", () =>
            {
                string output =
                    "======================================================================\n" +
                    "FAIL: test_reads_header (tests.parser_tests.ParserTests)\n" +
                    "----------------------------------------------------------------------\n" +
                    "Traceback (most recent call last):\n" +
                    "AssertionError: 154 != 270\n" +
                    "ERROR: test_missing_input (tests.parser_tests.ParserTests.test_missing_input)\n" +
                    "----------------------------------------------------------------------\n" +
                    "Ran 42 tests in 0.512s\n" +
                    "FAILED (failures=1, errors=1)\n";

                FailedTestNameExtractor.FailedTestNameSet set = FailedTestNameExtractor.Extract(output);
                AssertFalse(set.Overflow, "A small run does not overflow.");
                AssertEqual(2, set.Names.Count, "One FAIL and one ERROR both count as failed tests.");
                AssertEqual("tests.parser_tests.ParserTests.test_reads_header", set.Names[0], "Classic form adds the method onto the context.");
                AssertEqual("tests.parser_tests.ParserTests.test_missing_input", set.Names[1], "Newer form already carries the method in the context.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Duplicate failing lines collapse to one ordered entry", () =>
            {
                string output =
                    "  Failed Example.Tests.FlakyTests.Retries [1 ms]\n" +
                    "  Failed Example.Tests.FlakyTests.Retries [1 ms]\n" +
                    "  Failed Example.Tests.FlakyTests.Other [2 ms]\n";

                FailedTestNameExtractor.FailedTestNameSet set = FailedTestNameExtractor.Extract(output);
                AssertEqual(2, set.Names.Count, "The repeated name appears once.");
                AssertEqual("Example.Tests.FlakyTests.Retries", set.Names[0], "First occurrence keeps its position.");
                AssertEqual("Example.Tests.FlakyTests.Other", set.Names[1], "The distinct second name follows.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Output with no failing test lines yields an empty, non-overflowed set", () =>
            {
                FailedTestNameExtractor.FailedTestNameSet set = FailedTestNameExtractor.Extract(
                    "Passed!  - Failed:     0, Passed:  2588, Skipped:     0, Total:  2588\n");
                AssertEqual(0, set.Names.Count, "A clean run names no failing test.");
                AssertFalse(set.Overflow, "An empty set is complete, not overflowed.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("More distinct failing tests than the cap sets the overflow flag", () =>
            {
                System.Text.StringBuilder builder = new System.Text.StringBuilder();
                int total = FailedTestNameExtractor.MaxNames + 5;
                for (int i = 0; i < total; i++)
                    builder.Append("  Failed Example.Tests.Bulk.Case").Append(i).Append(" [1 ms]\n");

                FailedTestNameExtractor.FailedTestNameSet set = FailedTestNameExtractor.Extract(builder.ToString());
                AssertTrue(set.Overflow, "A run naming more than the cap cannot keep a complete set.");
                AssertEqual(FailedTestNameExtractor.MaxNames, set.Names.Count, "The set holds exactly the cap.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Null and empty output return the empty set", () =>
            {
                AssertEqual(0, FailedTestNameExtractor.Extract(null).Names.Count, "Null output names nothing.");
                AssertEqual(0, FailedTestNameExtractor.Extract("").Names.Count, "Empty output names nothing.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
