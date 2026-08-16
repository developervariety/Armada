namespace Armada.Test.Unit
{
    using System;
    using System.Text;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Verifies the review-diff scoping: bulk generated data files (large JSON/CSV
    /// under an output/export/bundle path) are always elided to a summary so a snapshot-regeneration
    /// voyage cannot overflow a Judge's context with hundreds of data rows, while small data files
    /// and code diffs stay reviewable.
    /// </summary>
    public sealed class ReviewDiffScopeTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "ReviewDiffScope";

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
        }
    }
}
