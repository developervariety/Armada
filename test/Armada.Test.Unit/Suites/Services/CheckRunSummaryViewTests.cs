namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
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
