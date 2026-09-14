namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Coverage for the Slop diff classifier: the FAIL and WARN split, what counts as an added line,
    /// and the per-site suppression marker. The diffs live in fixture files so that this suite's own
    /// source carries none of the patterns it asserts on.
    /// </summary>
    public class SlopDiffClassifierTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Slop Diff Classifier";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A skipped test is a FAIL finding at its new-side line", () =>
            {
                SlopClassificationResult result = SlopDiffClassifier.Classify(Fixture("skipped-test.diff"), false);

                AssertEqual(1, result.Findings.Count);
                SlopFinding finding = result.Findings[0];
                AssertEqual(SlopRuleEnum.SkippedTest, finding.Rule);
                AssertEqual(SlopSeverityEnum.Fail, finding.Severity);
                AssertEqual("tests/Widget.Tests/WidgetTests.cs", finding.Path);
                AssertEqual(12, finding.Line, "the removed line must not advance the new-side line counter");
                AssertFalse(result.Passed, "an unsuppressed FAIL finding fails the classification");
                AssertEqual(1, result.FilesExamined);
                AssertEqual(1, result.AddedLinesExamined);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Ignore attributes, dynamic skips and a compiled-out test block are FAIL findings", () =>
            {
                SlopClassificationResult result = SlopDiffClassifier.Classify(Fixture("disabled-tests.diff"), false);

                AssertEqual(3, result.FailCount);
                AssertTrue(result.Findings.All(f => f.Rule == SlopRuleEnum.SkippedTest));
                AssertEqual("3,6,9", String.Join(",", result.Findings.Select(f => f.Line)));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A project-wide NoWarn element fails and a per-package NoWarn attribute does not", () =>
            {
                SlopClassificationResult result = SlopDiffClassifier.Classify(Fixture("project-nowarn.diff"), false);

                AssertEqual(1, result.Findings.Count);
                AssertEqual(SlopRuleEnum.ProjectWideNoWarn, result.Findings[0].Rule);
                AssertEqual("Directory.Build.props", result.Findings[0].Path);
                AssertEqual(4, result.Findings[0].Line);
                AssertFalse(result.Passed);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("An inline version and a VersionOverride fail only under central package management", () =>
            {
                SlopClassificationResult central = SlopDiffClassifier.Classify(Fixture("cpm-bypass.diff"), true);
                AssertEqual(2, central.FailCount, "both the inline Version and the VersionOverride bypass the central file");
                AssertTrue(central.Findings.All(f => f.Rule == SlopRuleEnum.CentralPackageVersionBypass));
                AssertTrue(central.Findings.All(f => f.Path == "src/App/App.csproj"), "the central Directory.Packages.props itself is never a bypass");

                SlopClassificationResult local = SlopDiffClassifier.Classify(Fixture("cpm-bypass.diff"), false);
                AssertEqual(0, local.Findings.Count, "without central management an inline version is the normal form");
                AssertTrue(local.Passed);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A WARN-only diff passes and every WARN finding is reported", () =>
            {
                SlopClassificationResult result = SlopDiffClassifier.Classify(Fixture("warn-only.diff"), false);

                AssertTrue(result.Passed, "WARN findings never fail the classification");
                AssertEqual(0, result.FailCount);
                AssertEqual(3, result.WarnCount);

                SlopFinding emptyCatch = result.Findings.Single(f => f.Rule == SlopRuleEnum.EmptyCatch);
                AssertEqual(36, emptyCatch.Line, "a multi-line empty catch is reported at its catch line");
                AssertEqual(1, result.Findings.Count(f => f.Rule == SlopRuleEnum.ArbitraryDelay), "a named delay is not arbitrary");
                AssertEqual(1, result.Findings.Count(f => f.Rule == SlopRuleEnum.WarningSuppression));

                string report = SlopDiffClassifier.FormatFindings(result);
                AssertContains("WARN EmptyCatch src/Port/Decoder.cs:36", report);
                AssertContains("WARN ArbitraryDelay src/Port/Decoder.cs:39", report);
                AssertContains("WARN WarningSuppression src/Port/Decoder.cs:40", report);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A marker with a recorded reason suppresses only the rule it names", () =>
            {
                SlopClassificationResult result = SlopDiffClassifier.Classify(Fixture("suppressed.diff"), false);

                SlopFinding pinned = result.Findings.Single(f => f.Line == 4);
                AssertTrue(pinned.Suppressed, "a marker on the line above with a reason suppresses the finding");
                AssertContains("pinned source defect", pinned.SuppressionReason ?? String.Empty);

                SlopFinding noReason = result.Findings.Single(f => f.Line == 6);
                AssertFalse(noReason.Suppressed, "a marker without a reason is not a recorded reason");
                AssertContains("no reason", noReason.SuppressionProblem ?? String.Empty);

                SlopFinding wrongRule = result.Findings.Single(f => f.Line == 9);
                AssertFalse(wrongRule.Suppressed, "a marker for another rule does not cover this finding");
                AssertContains("EmptyCatch", wrongRule.SuppressionProblem ?? String.Empty);

                SlopFinding inline = result.Findings.Single(f => f.Line == 10);
                AssertEqual(SlopRuleEnum.EmptyCatch, inline.Rule);
                AssertTrue(inline.Suppressed, "a trailing marker on the flagged line suppresses it");

                AssertEqual(2, result.FailCount);
                AssertEqual(2, result.SuppressedCount);
                AssertFalse(result.Passed);

                string report = SlopDiffClassifier.FormatFindings(result);
                AssertContains("SUPPRESSED SkippedTest tests/Port.Tests/DecoderTests.cs:4", report);
                AssertContains("Suppression not honored", report);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Removed lines, context lines, deleted files, non-.NET files and build output are not classified", () =>
            {
                SlopClassificationResult result = SlopDiffClassifier.Classify(Fixture("ignored-lines.diff"), true);

                AssertEqual(0, result.Findings.Count,
                    "only added C# and MSBuild lines outside bin and obj are classified; a compiled-out block outside a test path is not a skipped test");
                AssertEqual(2, result.FilesExamined, "Service.cs and Legacy.cs carry added lines; the rest are excluded");
                AssertTrue(result.Passed);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("An empty diff classifies nothing and reports that plainly", () =>
            {
                SlopClassificationResult result = SlopDiffClassifier.Classify(String.Empty, false);

                AssertEqual(0, result.FilesExamined);
                AssertTrue(result.Passed);
                AssertContains("No slop patterns on added lines", SlopDiffClassifier.FormatFindings(result));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Every rule has a declared severity matching the enforcement split", () =>
            {
                AssertEqual(SlopSeverityEnum.Fail, SlopDiffClassifier.SeverityOf(SlopRuleEnum.SkippedTest));
                AssertEqual(SlopSeverityEnum.Fail, SlopDiffClassifier.SeverityOf(SlopRuleEnum.ProjectWideNoWarn));
                AssertEqual(SlopSeverityEnum.Fail, SlopDiffClassifier.SeverityOf(SlopRuleEnum.CentralPackageVersionBypass));
                AssertEqual(SlopSeverityEnum.Warn, SlopDiffClassifier.SeverityOf(SlopRuleEnum.EmptyCatch));
                AssertEqual(SlopSeverityEnum.Warn, SlopDiffClassifier.SeverityOf(SlopRuleEnum.ArbitraryDelay));
                AssertEqual(SlopSeverityEnum.Warn, SlopDiffClassifier.SeverityOf(SlopRuleEnum.WarningSuppression));

                foreach (SlopRuleEnum rule in Enum.GetValues<SlopRuleEnum>())
                    SlopDiffClassifier.SeverityOf(rule);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Central package management is read from Directory.Packages.props content", () =>
            {
                AssertTrue(SlopDiffClassifier.IsCentralPackageManagementEnabled(Fixture("Directory.Packages.props.txt")));
                AssertFalse(SlopDiffClassifier.IsCentralPackageManagementEnabled(null));
                AssertFalse(SlopDiffClassifier.IsCentralPackageManagementEnabled("<Project><ItemGroup /></Project>"));
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        #region Private-Methods

        /// <summary>
        /// Read a fixture copied beside the test assembly.
        /// </summary>
        internal static string Fixture(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Slop", name);
            if (!File.Exists(path))
                throw new FileNotFoundException("Slop fixture is missing from the test output; the project must copy Fixtures.", path);
            return File.ReadAllText(path).Replace("\r\n", "\n");
        }

        #endregion
    }
}
