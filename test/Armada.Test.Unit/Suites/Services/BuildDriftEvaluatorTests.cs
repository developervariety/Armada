namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for BuildDriftEvaluator and BuildInfo.ParseCommit.
    /// </summary>
    public class BuildDriftEvaluatorTests : TestSuite
    {
        /// <inheritdoc/>
        public override string Name => "Build Drift Evaluator";

        /// <inheritdoc/>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Evaluate_DifferentCommits_IsDriftedTrueWarningContainsBehindBy", () =>
            {
                BuildDriftReport report = BuildDriftEvaluator.Evaluate("aaa111", "bbb222", 5);
                AssertTrue(report.IsDrifted, "IsDrifted");
                AssertNotNull(report.Warning, "Warning");
                AssertContains("commits behind landed main", report.Warning!);
                AssertContains("5", report.Warning!);
            });

            await RunTest("Evaluate_SameCommits_IsDriftedFalseWarningNull", () =>
            {
                BuildDriftReport report = BuildDriftEvaluator.Evaluate("abc123", "abc123", 0);
                AssertFalse(report.IsDrifted, "IsDrifted");
                AssertNull(report.Warning, "Warning");
            });

            await RunTest("Evaluate_NullRunningCommit_IsDriftedFalse", () =>
            {
                BuildDriftReport report = BuildDriftEvaluator.Evaluate(null, "bbb222", 3);
                AssertFalse(report.IsDrifted, "IsDrifted");
                AssertNull(report.Warning, "Warning");
            });

            await RunTest("Evaluate_EmptyRunningCommit_IsDriftedFalse", () =>
            {
                BuildDriftReport report = BuildDriftEvaluator.Evaluate("", "bbb222", 3);
                AssertFalse(report.IsDrifted, "IsDrifted");
                AssertNull(report.Warning, "Warning");
            });

            await RunTest("Evaluate_NullLandedCommit_IsDriftedFalse", () =>
            {
                BuildDriftReport report = BuildDriftEvaluator.Evaluate("aaa111", null, 3);
                AssertFalse(report.IsDrifted, "IsDrifted");
                AssertNull(report.Warning, "Warning");
            });

            await RunTest("Evaluate_EmptyLandedCommit_IsDriftedFalse", () =>
            {
                BuildDriftReport report = BuildDriftEvaluator.Evaluate("aaa111", "", 3);
                AssertFalse(report.IsDrifted, "IsDrifted");
                AssertNull(report.Warning, "Warning");
            });

            await RunTest("Evaluate_SameCommitDifferentCase_IsDriftedFalse", () =>
            {
                BuildDriftReport report = BuildDriftEvaluator.Evaluate("ABC123def", "abc123DEF", 0);
                AssertFalse(report.IsDrifted, "IsDrifted");
                AssertNull(report.Warning, "Warning");
            });

            await RunTest("Evaluate_NegativeBehindBy_ClampedToZeroInReport", () =>
            {
                BuildDriftReport report = BuildDriftEvaluator.Evaluate("aaa111", "bbb222", -5);
                AssertEqual(0, report.BehindBy, "BehindBy");
                AssertTrue(report.IsDrifted, "IsDrifted");
                AssertNotNull(report.Warning, "Warning");
                AssertContains("0", report.Warning!);
            });

            await RunTest("Evaluate_DriftedReport_FieldsPopulated", () =>
            {
                BuildDriftReport report = BuildDriftEvaluator.Evaluate("running1", "landed2", 7);
                AssertEqual("running1", report.RunningCommit, "RunningCommit");
                AssertEqual("landed2", report.LandedCommit, "LandedCommit");
                AssertEqual(7, report.BehindBy, "BehindBy");
            });

            await RunTest("ParseCommit_WithPlusAndSha_ReturnsSha", () =>
            {
                string? result = BuildInfo.ParseCommit("0.8.0+abc1234");
                AssertEqual("abc1234", result, "ParseCommit result");
            });

            await RunTest("ParseCommit_NoPlus_ReturnsNull", () =>
            {
                string? result = BuildInfo.ParseCommit("0.8.0");
                AssertNull(result, "ParseCommit result");
            });

            await RunTest("ParseCommit_NullInput_ReturnsNull", () =>
            {
                string? result = BuildInfo.ParseCommit(null);
                AssertNull(result, "ParseCommit result");
            });

            await RunTest("ParseCommit_EmptyInput_ReturnsNull", () =>
            {
                string? result = BuildInfo.ParseCommit("");
                AssertNull(result, "ParseCommit result");
            });

            await RunTest("ParseCommit_PlusAtEnd_ReturnsNull", () =>
            {
                string? result = BuildInfo.ParseCommit("0.8.0+");
                AssertNull(result, "ParseCommit result");
            });
        }
    }
}
