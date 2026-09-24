namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Text;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>Tests for AutoLandEvaluator: evaluation order, path globs, and cap enforcement.</summary>
    public class AutoLandEvaluatorTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "AutoLand Evaluator";

        private static AutoLandEvaluator CreateSut() => new AutoLandEvaluator();

        private static string BuildDiff(string[] paths, int[] addedLineCounts)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < paths.Length; i++)
            {
                string path = paths[i];
                int count = addedLineCounts[i];
                sb.AppendLine("diff --git a/" + path + " b/" + path);
                sb.AppendLine("index 0000000..1111111 100644");
                sb.AppendLine("--- a/" + path);
                sb.AppendLine("+++ b/" + path);
                sb.AppendLine("@@ -0,0 +1," + count + " @@");
                for (int j = 0; j < count; j++)
                    sb.AppendLine("+line " + j);
            }
            return sb.ToString();
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Evaluate_Disabled_ReturnsFail_WithReason", () =>
            {
                AutoLandEvaluator sut = CreateSut();
                AutoLandPredicate p = new AutoLandPredicate { Enabled = false };
                EvaluationResult r = sut.Evaluate(BuildDiff(new string[] { "src/A.cs" }, new int[] { 1 }), p);
                AssertTrue(r is EvaluationResult.Fail, "Expected Fail when predicate is disabled");
                EvaluationResult.Fail fail = (EvaluationResult.Fail)r;
                AssertEqual("disabled", fail.Reason, "Reason should be 'disabled'");
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_DiffWithinAllowPathsAndUnderCaps_ReturnsPass", () =>
            {
                AutoLandEvaluator sut = CreateSut();
                AutoLandPredicate p = new AutoLandPredicate
                {
                    MaxAddedLines = 100,
                    MaxFiles = 5,
                    AllowPaths = new List<string> { "src/**" }
                };
                EvaluationResult r = sut.Evaluate(BuildDiff(new string[] { "src/A.cs", "src/B.cs" }, new int[] { 10, 5 }), p);
                AssertTrue(r is EvaluationResult.Pass, "Expected Pass for diff within caps and allow paths");
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_DiffTouchesDeniedPath_ReturnsFail_WithPathReason", () =>
            {
                AutoLandEvaluator sut = CreateSut();
                AutoLandPredicate p = new AutoLandPredicate { DenyPaths = new List<string> { "**/CLAUDE.md" } };
                EvaluationResult r = sut.Evaluate(BuildDiff(new string[] { "repo/CLAUDE.md" }, new int[] { 1 }), p);
                AssertTrue(r is EvaluationResult.Fail, "Expected Fail when a denied path is touched");
                EvaluationResult.Fail fail = (EvaluationResult.Fail)r;
                AssertContains("denyPath", fail.Reason, "Reason should mention denyPath");
                AssertContains("CLAUDE.md", fail.Reason, "Reason should include the denied path");
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_DiffOutsideAllowPaths_ReturnsFail", () =>
            {
                AutoLandEvaluator sut = CreateSut();
                AutoLandPredicate p = new AutoLandPredicate { AllowPaths = new List<string> { "src/**" } };
                EvaluationResult r = sut.Evaluate(BuildDiff(new string[] { "tools/script.sh" }, new int[] { 5 }), p);
                AssertTrue(r is EvaluationResult.Fail, "Expected Fail when path is outside AllowPaths");
                EvaluationResult.Fail fail = (EvaluationResult.Fail)r;
                AssertContains("allowPaths", fail.Reason, "Reason should mention allowPaths");
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_AllowPathsEmpty_NotEnforced", () =>
            {
                AutoLandEvaluator sut = CreateSut();
                AutoLandPredicate p = new AutoLandPredicate { AllowPaths = new List<string>() };
                EvaluationResult r = sut.Evaluate(BuildDiff(new string[] { "anywhere/file.txt" }, new int[] { 1 }), p);
                AssertTrue(r is EvaluationResult.Pass, "Empty AllowPaths list should not be enforced");
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_AddedLinesExceedsCap_ReturnsFail", () =>
            {
                AutoLandEvaluator sut = CreateSut();
                AutoLandPredicate p = new AutoLandPredicate { MaxAddedLines = 50 };
                EvaluationResult r = sut.Evaluate(BuildDiff(new string[] { "src/A.cs" }, new int[] { 100 }), p);
                AssertTrue(r is EvaluationResult.Fail, "Expected Fail when added lines exceed cap");
                EvaluationResult.Fail fail = (EvaluationResult.Fail)r;
                AssertContains("maxAddedLines", fail.Reason, "Reason should mention maxAddedLines");
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_FileCountExceedsCap_ReturnsFail", () =>
            {
                AutoLandEvaluator sut = CreateSut();
                AutoLandPredicate p = new AutoLandPredicate { MaxFiles = 2 };
                EvaluationResult r = sut.Evaluate(BuildDiff(new string[] { "a.cs", "b.cs", "c.cs" }, new int[] { 1, 1, 1 }), p);
                AssertTrue(r is EvaluationResult.Fail, "Expected Fail when file count exceeds cap");
                EvaluationResult.Fail fail = (EvaluationResult.Fail)r;
                AssertContains("maxFiles", fail.Reason, "Reason should mention maxFiles");
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_NullCaps_NotEnforced", () =>
            {
                AutoLandEvaluator sut = CreateSut();
                AutoLandPredicate p = new AutoLandPredicate();
                EvaluationResult r = sut.Evaluate(BuildDiff(new string[] { "a.cs" }, new int[] { 100000 }), p);
                AssertTrue(r is EvaluationResult.Pass, "Null caps should not block auto-land");
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_EvaluationOrder_FailsAtFirstReason_FileCountBeforeLines", () =>
            {
                AutoLandEvaluator sut = CreateSut();
                AutoLandPredicate p = new AutoLandPredicate { MaxFiles = 1, MaxAddedLines = 5 };
                EvaluationResult r = sut.Evaluate(BuildDiff(new string[] { "a.cs", "b.cs" }, new int[] { 50, 50 }), p);
                AssertTrue(r is EvaluationResult.Fail, "Expected Fail when both MaxFiles and MaxAddedLines exceeded");
                EvaluationResult.Fail fail = (EvaluationResult.Fail)r;
                AssertContains("maxFiles", fail.Reason, "MaxFiles rule should fire before MaxAddedLines");
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_GlobsHandleRecursive_DenyPath", () =>
            {
                AutoLandEvaluator sut = CreateSut();
                AutoLandPredicate p = new AutoLandPredicate { DenyPaths = new List<string> { "**/CLAUDE.md" } };

                EvaluationResult r1 = sut.Evaluate(BuildDiff(new string[] { "CLAUDE.md" }, new int[] { 1 }), p);
                AssertTrue(r1 is EvaluationResult.Fail, "Root-level CLAUDE.md should be denied");

                EvaluationResult r2 = sut.Evaluate(BuildDiff(new string[] { "repo/CLAUDE.md" }, new int[] { 1 }), p);
                AssertTrue(r2 is EvaluationResult.Fail, "One-level nested CLAUDE.md should be denied");

                EvaluationResult r3 = sut.Evaluate(BuildDiff(new string[] { "repo/sub/CLAUDE.md" }, new int[] { 1 }), p);
                AssertTrue(r3 is EvaluationResult.Fail, "Deeply nested CLAUDE.md should be denied");

                return Task.CompletedTask;
            });

            await RunTest("Evaluate_DeletionOnlyPatch_DenyPathAndFileCountSeeDeletedFile", () =>
            {
                AutoLandEvaluator sut = CreateSut();
                string diff =
                    "diff --git a/secrets/key.pem b/secrets/key.pem\n" +
                    "deleted file mode 100644\n" +
                    "index 1111111..0000000\n" +
                    "--- a/secrets/key.pem\n" +
                    "+++ /dev/null\n" +
                    "@@ -1 +0,0 @@\n" +
                    "-key material\n";

                EvaluationResult denied = sut.Evaluate(diff, new AutoLandPredicate { DenyPaths = new List<string> { "secrets/**" } });
                AssertTrue(denied is EvaluationResult.Fail, "Deleting a denied path must not auto-land");
                AssertEqual("denyPath:secrets/key.pem", ((EvaluationResult.Fail)denied).Reason, "The deleted path is the denied one");

                EvaluationResult capped = sut.Evaluate(diff, new AutoLandPredicate { MaxFiles = 0 });
                AssertTrue(capped is EvaluationResult.Fail, "A deletion counts as a changed file");
                AssertEqual("maxFiles:1>0", ((EvaluationResult.Fail)capped).Reason, "One deleted file");
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_PureRename_AllowPathsCheckBothNames", () =>
            {
                AutoLandEvaluator sut = CreateSut();
                string diff =
                    "diff --git a/src/Widget.cs b/tools/Widget.cs\n" +
                    "similarity index 100%\n" +
                    "rename from src/Widget.cs\n" +
                    "rename to tools/Widget.cs\n";

                EvaluationResult r = sut.Evaluate(diff, new AutoLandPredicate { AllowPaths = new List<string> { "src/**" }, MaxFiles = 1 });
                AssertTrue(r is EvaluationResult.Fail, "Moving a file out of the allowed tree must not auto-land");
                AssertEqual("allowPaths:violated:tools/Widget.cs", ((EvaluationResult.Fail)r).Reason, "The rename target is outside AllowPaths");
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_AddedLinesStartingWithPlusPlus_AreCounted", () =>
            {
                AutoLandEvaluator sut = CreateSut();
                string diff =
                    "diff --git a/docs/hugo.md b/docs/hugo.md\n" +
                    "new file mode 100644\n" +
                    "--- /dev/null\n" +
                    "+++ b/docs/hugo.md\n" +
                    "@@ -0,0 +1,3 @@\n" +
                    "++++\n" +
                    "+title = 1\n" +
                    "++++\n";

                EvaluationResult r = sut.Evaluate(diff, new AutoLandPredicate { MaxAddedLines = 2 });
                AssertTrue(r is EvaluationResult.Fail, "Three added lines exceed a cap of two");
                AssertEqual("maxAddedLines:3>2", ((EvaluationResult.Fail)r).Reason);
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_QuotedAccentedPath_DenyPathMatchesDecodedName", () =>
            {
                AutoLandEvaluator sut = CreateSut();
                string diff =
                    "diff --git \"a/docs/r\\303\\251sum\\303\\251.md\" \"b/docs/r\\303\\251sum\\303\\251.md\"\n" +
                    "new file mode 100644\n" +
                    "--- /dev/null\n" +
                    "+++ \"b/docs/r\\303\\251sum\\303\\251.md\"\n" +
                    "@@ -0,0 +1 @@\n" +
                    "+hello\n";

                EvaluationResult r = sut.Evaluate(diff, new AutoLandPredicate { DenyPaths = new List<string> { "docs/r\u00e9sum\u00e9.md" } });
                AssertTrue(r is EvaluationResult.Fail, "A Git-quoted name must match its real file name");
                AssertEqual("denyPath:docs/r\u00e9sum\u00e9.md", ((EvaluationResult.Fail)r).Reason, "Decoded path");
                return Task.CompletedTask;
            });
        }
    }
}
