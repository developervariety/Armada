namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Coverage for the bounded check-run projection. A check's complete log is routinely
    /// megabytes and overruns the tool output limit, so these cases pin that the verdict survives
    /// the projection, that the tail is whole lines from the END of the log, and that the caller
    /// is told what it is not being sent.
    /// </summary>
    public class CheckRunSummaryViewTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Check Run Summary View";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Native classifier findings never become failing test outcomes", () =>
            {
                const string pattern = @"^FAIL (?<name>.+)$";
                foreach (int findings in new[] { 0, 1 })
                {
                    CheckRun run = MakeRun("FAIL findings: " + findings + "; WARN findings: 0; suppressed: 0");
                    run.Type = CheckRunTypeEnum.Slop;
                    run.Command = SlopCheckRunner.CommandLabel;
                    run.Status = findings == 0 ? CheckRunStatusEnum.Passed : CheckRunStatusEnum.Failed;
                    run.ExitCode = findings == 0 ? 0 : 1;
                    CheckRunSummaryView view = CheckRunSummaryView.From(run, 40, null, pattern);
                    AssertNull(view.TestOutcomes, "native findings are analysis results, not test names");
                    AssertEqual(run.Status, view.Status, "classification status is retained");
                    AssertEqual(run.ExitCode, view.ExitCode, "classification exit code is retained");
                    AssertEqual(run.Output, view.OutputTail, "analysis evidence remains visible");
                }
                CheckRun custom = MakeRun("FAIL Example.Tests.LoadRace: timed out");
                custom.Type = CheckRunTypeEnum.Slop;
                custom.Command = "sh scripts/check.sh";
                custom.Status = CheckRunStatusEnum.Failed;
                custom.ExitCode = 1;
                AssertContains("Example.Tests.LoadRace", CheckRunSummaryView.From(custom, 40, null, pattern).TestOutcomes!,
                    "a custom command still reports its real failing tests");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("The verdict fields survive the projection", () =>
            {
                CheckRun run = MakeRun("line1\nline2");
                run.Status = CheckRunStatusEnum.Failed;
                run.ExitCode = 1;
                run.Summary = "3 failed";

                CheckRunSummaryView view = CheckRunSummaryView.From(run);

                AssertEqual("chk_test", view.Id);
                AssertEqual(CheckRunStatusEnum.Failed, view.Status, "Status is the answer most callers want.");
                AssertEqual(1, view.ExitCode);
                AssertEqual("3 failed", view.Summary);
                AssertEqual("vsl_test", view.VesselId);
                AssertEqual("vyg_test", view.VoyageId);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Failing tests with their first exception line and every skip are read from the whole log, not the tail", () =>
            {
                System.Text.StringBuilder log = new System.Text.StringBuilder();
                log.Append("  Failed X.Y.EarlyTest [1 ms]\n  Error Message:\n   System.InvalidOperationException: missing service\n");
                for (int i = 1; i <= 17; i++) log.Append("  Skipped X.Y.SkippedTest").Append(i).Append(" [< 1 ms]\n");
                for (int i = 0; i < 200; i++) log.Append("progress line ").Append(i).Append('\n');
                log.Append("Failed!  - Failed: 2, Passed: 10, Skipped: 17, Total: 29\n");
                log.Append("\n--- STDERR ---\n  Failed X.Y.StderrTest [2 ms]\n  Error Message:\n   Assert.Equal() Failure: Values differ\n");
                for (int i = 0; i < 200; i++) log.Append("stderr progress line ").Append(i).Append('\n');
                CheckRun run = MakeRun(log.ToString());
                run.Status = CheckRunStatusEnum.Failed;

                CheckRunSummaryView view = CheckRunSummaryView.From(run, 40);
                string outcomes = view.TestOutcomes ?? String.Empty;

                AssertFalse((view.OutputTail ?? String.Empty).Contains("X.Y.EarlyTest", StringComparison.Ordinal), "The tail alone misses the early failure");
                AssertContains("X.Y.EarlyTest: System.InvalidOperationException: missing service", outcomes, "The early failure is named with its exception line");
                AssertContains("X.Y.StderrTest: Assert.Equal() Failure: Values differ", outcomes, "The stderr failure is named with its exception line");
                for (int i = 1; i <= 17; i++)
                    AssertContains("X.Y.SkippedTest" + i + "\n", outcomes + "\n", "Skip " + i + " of 17 is listed");

                string table = "unit-shard-1 Total: 3 Passed: 1 Failed: 1 Skipped: 1\n"
                    + "unit-shard-2 Total: 3 Passed: 1 Failed: 0 Skipped: 2\n"
                    + "unit Total: 6 Passed: 2 Failed: 1 Skipped: 3 (2 of 2 shards summarised)\n"
                    + "  Failed X.Y.RealFailure [1 ms]\n  Error Message:\n   System.InvalidOperationException: retained failure\n";
                string tableOutcomes = CheckRunSummaryView.From(MakeRun(table), 40).TestOutcomes ?? String.Empty;
                AssertContains("--- SKIPPED TESTS (3) ---", tableOutcomes, "shard and aggregate skips count once");
                AssertContains("X.Y.RealFailure: System.InvalidOperationException: retained failure", tableOutcomes, "table aggregation never hides a real failure");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A short log is returned whole and not marked truncated", () =>
            {
                CheckRunSummaryView view = CheckRunSummaryView.From(MakeRun("a\nb\nc"), 40);

                AssertEqual("a\nb\nc", view.OutputTail);
                AssertFalse(view.OutputTruncated, "A log shorter than the tail budget is complete, not truncated.");
                AssertNull(view.OutputRetrieval, "There is nothing further to retrieve.");
                AssertEqual(5, view.OutputLength);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A long log is cut to the LAST N whole lines", () =>
            {
                // The cause of a failure is at the end of a build log, so the tail is the half
                // worth keeping. Cutting mid-line would corrupt the one line that matters.
                CheckRunSummaryView view = CheckRunSummaryView.From(MakeRun("a\nb\nc\nd\ne"), 2);

                AssertEqual("d\ne", view.OutputTail, "Exactly the last two lines, whole.");
                AssertTrue(view.OutputTruncated);
                AssertNotNull(view.OutputRetrieval, "A truncated view must say how to get the rest.");
                AssertContains("get_check_run", view.OutputRetrieval!);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A trailing newline does not cost a line of the tail", () =>
            {
                CheckRunSummaryView view = CheckRunSummaryView.From(MakeRun("a\nb\nc\nd\n"), 2);

                AssertEqual("c\nd\n", view.OutputTail, "The final newline terminates the last line rather than starting one.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("OutputLength reports the COMPLETE log, not the tail", () =>
            {
                string big = new string('x', 500) + "\n" + new string('y', 500);
                CheckRunSummaryView view = CheckRunSummaryView.From(MakeRun(big), 1);

                AssertEqual(big.Length, view.OutputLength, "The caller must be able to tell how much was withheld.");
                AssertTrue(view.OutputTail!.Length < big.Length);
                AssertContains(view.OutputLength.ToString(), view.OutputRetrieval!);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("An empty or null log yields a null tail rather than an empty string", () =>
            {
                AssertNull(CheckRunSummaryView.From(MakeRun("")).OutputTail);
                AssertNull(CheckRunSummaryView.From(MakeRun(null)).OutputTail);
                AssertEqual(0, CheckRunSummaryView.From(MakeRun(null)).OutputLength);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A single-line log with no newline is returned whole", () =>
            {
                CheckRunSummaryView view = CheckRunSummaryView.From(MakeRun("no newline here"), 5);

                AssertEqual("no newline here", view.OutputTail);
                AssertFalse(view.OutputTruncated);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A zero or negative tail budget still returns a line", () =>
            {
                // A view that says nothing about why a check failed is worse than a large one.
                CheckRunSummaryView view = CheckRunSummaryView.From(MakeRun("a\nb\nc"), 0);

                AssertNotNull(view.OutputTail);
                AssertEqual("c", view.OutputTail);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Parsed summaries and artifacts are carried, since they replace the log", () =>
            {
                CheckRun run = MakeRun("...");
                run.TestSummary = new CheckRunTestSummary { Passed = 10, Failed = 2 };
                run.Artifacts.Add(new CheckRunArtifact { Path = "TestResults/unit-tests.trx" });

                CheckRunSummaryView view = CheckRunSummaryView.From(run);

                AssertNotNull(view.TestSummary);
                AssertEqual(10, view.TestSummary!.Passed);
                AssertEqual(2, view.TestSummary!.Failed);
                AssertEqual(1, view.Artifacts.Count, "Artifacts point at the detail the log would have carried.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Queue and execution durations stay separate in the projection", () =>
            {
                DateTime created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                CheckRun run = MakeRun("done");
                run.CreatedUtc = created;
                run.StartedUtc = created.AddMilliseconds(1234);
                run.DurationMs = 456;

                CheckRunSummaryView view = CheckRunSummaryView.From(run);

                AssertEqual(1234L, view.QueueDurationMs);
                AssertEqual(456L, view.DurationMs);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A null run is rejected rather than projected", () =>
            {
                AssertThrows<ArgumentNullException>(() => CheckRunSummaryView.From(null!));
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        #region Private-Methods

        private static CheckRun MakeRun(string? output)
        {
            return new CheckRun
            {
                Id = "chk_test",
                VesselId = "vsl_test",
                VoyageId = "vyg_test",
                Type = CheckRunTypeEnum.UnitTest,
                Output = output
            };
        }

        #endregion
    }
}
