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
                string huge = HugeDataFile("output/otr-export/seed-key-signers/Cummins.seed-key.json", "SNAPSHOT_ROW_", 6000);
                string diff = codeA + codeB + huge;

                string scoped = MissionService.BuildReviewDiff(diff, 4000);

                // Code diffs survive whole (their content is what the Judge must review).
                AssertTrue(scoped.Contains("REDACTOR_CODE_MARKER"), "small code diff must be kept whole");
                AssertTrue(scoped.Contains("GUARD_TEST_MARKER"), "the guard-test diff must be kept whole");
                // The bulk data file is elided: its header/path is listed, but its rows are gone.
                AssertTrue(scoped.Contains("Cummins.seed-key.json"), "the bulk data file must still be listed by name");
                AssertTrue(!scoped.Contains("SNAPSHOT_ROW_500"), "the bulk data file's content must be elided");
                AssertTrue(scoped.Contains("lines elided"), "the elision note must be present");
                // Bounded (allow headroom for the elision notes/header lines).
                AssertTrue(scoped.Length < 8000, "scoped diff must be far smaller than the raw diff (" + scoped.Length + ")");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
