namespace Armada.Test.Unit.Suites.Services
{
    using System.Text;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Verifies BuildReviewDiff scopes a prior-stage diff so a large generated-output diff cannot overflow the
    /// reviewing model's context: small code-file diffs survive whole, the largest (bulk data) files are elided
    /// to a header + line-count, every changed file is still listed, and the result stays within budget.
    /// </summary>
    public sealed class MissionReviewDiffTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Review Diff";

        private static string CodeFile(string path, string uniqueBody)
        {
            return "diff --git a/" + path + " b/" + path + "\n" +
                   "--- a/" + path + "\n+++ b/" + path + "\n@@ -1,1 +1,2 @@\n+" + uniqueBody + "\n";
        }

        private static string HugeDataFile(string path, string uniqueToken, int lines)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("diff --git a/").Append(path).Append(" b/").Append(path).Append('\n');
            sb.Append("--- a/").Append(path).Append("\n+++ b/").Append(path).Append("\n@@ -1,1 +1,").Append(lines).Append(" @@\n");
            for (int i = 0; i < lines; i++) sb.Append('+').Append(uniqueToken).Append(i).Append('\n');
            return sb.ToString();
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("UnderBudget_DiffUnchanged", () =>
            {
                string diff = "diff --git a/src/Foo.cs b/src/Foo.cs\n@@ -1 +1 @@\n-foo\n+bar\n";
                AssertEqual(diff, MissionService.BuildReviewDiff(diff, 10000), "a diff under the budget is returned unchanged");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("OverBudget_SmallCodeSectionsKept_LargeDataElided", () =>
            {
                StringBuilder data = new StringBuilder();
                data.Append("diff --git a/src/ExampleExtractor/Output/export/example.json b/src/ExampleExtractor/Output/export/example.json\n");
                for (int i = 0; i < 2000; i++) data.Append("{\"row\": " + i + ", \"redacted\": \"<redacted:key-material:length=11>\"}\n");

                StringBuilder code = new StringBuilder();
                code.Append("diff --git a/src/Foo.cs b/src/Foo.cs\n@@ -1 +1 @@\n-foo\n+bar\n");

                string diff = data.ToString() + code.ToString();
                string scoped = MissionService.BuildReviewDiff(diff, 4000);

                AssertTrue(scoped.Contains("generated data file"), "bulk generated data must be elided with the generated-data marker");
                AssertTrue(scoped.Contains("example.json"), "the elided data file's header must remain so the reviewer sees WHICH file changed");
                AssertTrue(scoped.Contains("lines elided; review the code and manifest"), "the elided data file must report its line count");
                AssertTrue(scoped.Contains("+bar"), "the small code diff must survive whole even though the total is over budget");
                AssertTrue(!scoped.Contains("redacted\": \"<redacted"), "the data rows must not reach the review context at all");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("SmallDataFile_StaysReviewable", () =>
            {
                StringBuilder small = new StringBuilder();
                small.Append("diff --git a/output/manifest.json b/output/manifest.json\n@@ -1 +1 @@\n-{\"v\":1}\n+{\"v\":2}\n");
                string diff = small.ToString();
                string scoped = MissionService.BuildReviewDiff(diff, 4000);
                AssertTrue(scoped.Contains("{\"v\":2}"), "a small data file under the threshold stays reviewable");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("LargeCodeFile_ElidesWithGenericNote", () =>
            {
                StringBuilder code = new StringBuilder();
                code.Append("diff --git a/src/Big.cs b/src/Big.cs\n");
                for (int i = 0; i < 1500; i++) code.Append("+line " + i + "\n");
                string diff = code.ToString();
                string scoped = MissionService.BuildReviewDiff(diff, 2000);
                AssertTrue(scoped.Contains("lines elided to fit review context"), "a large code file elides with the generic note, not the generated-data note");
                AssertTrue(!scoped.Contains("generated data file"), "code files are never labeled generated data");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("BuildReviewDiff_QuotedAndSpacedDataFileNames_AreElided", () =>
            {
                // A non-ASCII name is C-quoted in the header; a name holding " b/" is not quoted at all.
                string quoted = HugeDataFile("PLACEHOLDER", "QUOTED_ROW_", 3000)
                    .Replace("a/PLACEHOLDER", "\"a/tools/output/donn\\303\\251es.json\"")
                    .Replace("b/PLACEHOLDER", "\"b/tools/output/donn\\303\\251es.json\"");
                string spaced = HugeDataFile("Plan b/output/data.json", "SPACED_ROW_", 3000);
                string diff = CodeFile("src/A.cs", "CODE_A") + quoted + spaced;

                string scoped = MissionService.BuildReviewDiff(diff, 4000);

                AssertTrue(scoped.Contains("CODE_A"), "the code diff is kept whole");
                AssertTrue(!scoped.Contains("QUOTED_ROW_10"), "the quoted data file's rows are elided");
                AssertTrue(!scoped.Contains("SPACED_ROW_10"), "the spaced data file's rows are elided");
                AssertTrue(scoped.Contains("2 of them are bulk generated data files"),
                    "both data files are recognized as generated data: " + scoped);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("SummarizeDiffStat_CountsPlusPlusAndMinusMinusContentLines", () =>
            {
                string diff =
                    "diff --git a/docs/hugo.md b/docs/hugo.md\n" +
                    "index 1111111..2222222 100644\n" +
                    "--- a/docs/hugo.md\n" +
                    "+++ b/docs/hugo.md\n" +
                    "@@ -1,2 +1,4 @@\n" +
                    "--- a SQL comment\n" +
                    " keep\n" +
                    "++++\n" +
                    "+title = 1\n" +
                    "++++\n";

                AssertEqual("1 files, +3/-1", MissionService.SummarizeDiffStat(diff));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("BuildReviewDiff_UnderBudget_ReturnedUnchanged", () =>
            {
                string diff = CodeFile("src/A.cs", "CODE_A") + CodeFile("src/B.cs", "CODE_B");
                AssertEqual(diff, MissionService.BuildReviewDiff(diff, 60000), "a diff under budget is returned verbatim");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("BuildReviewDiff_OverBudget_KeepsCodeElidesBulkData_ListsAllFiles", () =>
            {
                string codeA = CodeFile("src/RedactorWiring.cs", "REDACTOR_CODE_MARKER");
                string codeB = CodeFile("test/GuardTest.cs", "GUARD_TEST_MARKER");
                string huge = HugeDataFile("output/export/example/ExampleVendor.tokens.json", "SNAPSHOT_ROW_", 6000);
                string diff = codeA + codeB + huge;

                string scoped = MissionService.BuildReviewDiff(diff, 4000);

                // Code diffs survive whole (their content is what the Judge must review).
                AssertTrue(scoped.Contains("REDACTOR_CODE_MARKER"), "small code diff must be kept whole");
                AssertTrue(scoped.Contains("GUARD_TEST_MARKER"), "the guard-test diff must be kept whole");
                // The bulk data file is elided: its header/path is listed, but its rows are gone.
                AssertTrue(scoped.Contains("ExampleVendor.tokens.json"), "the bulk data file must still be listed by name");
                AssertTrue(!scoped.Contains("SNAPSHOT_ROW_500"), "the bulk data file's content must be elided");
                AssertTrue(scoped.Contains("lines elided"), "the elision note must be present");
                // Bounded (allow headroom for the elision notes/header lines).
                AssertTrue(scoped.Length < 8000, "scoped diff must be far smaller than the raw diff (" + scoped.Length + ")");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
