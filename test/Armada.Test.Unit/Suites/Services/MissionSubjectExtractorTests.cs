namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Unit tests for the path and identifier subject extractor that feeds the mission git-anchors block.
    /// </summary>
    public class MissionSubjectExtractorTests : TestSuite
    {
        #region Public-Members

        /// <summary>Suite name.</summary>
        public override string Name => "Mission Subject Extractor";

        #endregion

        #region Protected-Methods

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Plain repo-relative path is extracted unchanged", () =>
            {
                List<string> paths = MissionSubjectExtractor.ExtractPaths(
                    "Fix the decoder in src/ExampleApp/Core/SpnDecoder.cs.");

                AssertEqual(1, paths.Count, "path count");
                AssertEqual("src/ExampleApp/Core/SpnDecoder.cs", paths[0], "extracted path");
            });

            await RunTest("a-prefixed and b-prefixed diff paths collapse to one repository path", () =>
            {
                string text =
                    "diff --git a/src/ExampleApp/SpnDecoder.cs b/src/ExampleApp/SpnDecoder.cs\n" +
                    "index 0000000..abcdef1\n" +
                    "--- a/src/ExampleApp/SpnDecoder.cs\n" +
                    "+++ b/src/ExampleApp/SpnDecoder.cs\n";

                List<string> paths = MissionSubjectExtractor.ExtractPaths(text);

                AssertEqual(1, paths.Count, "a/b pair must de-duplicate to a single path");
                AssertEqual("src/ExampleApp/SpnDecoder.cs", paths[0], "diff prefix stripped");
            });

            await RunTest("a-prefixed path stays intact when no diff is embedded", () =>
            {
                // A repository may genuinely own a top-level `a/` folder; without a diff header the
                // prefix is a real path segment and must not be stripped.
                List<string> paths = MissionSubjectExtractor.ExtractPaths(
                    "Update the shader at a/shaders/BlurShader.cs.");

                AssertEqual(1, paths.Count, "path count");
                AssertEqual("a/shaders/BlurShader.cs", paths[0], "prefix preserved outside a diff");
            });

            await RunTest("Parent-traversal sibling path is not extracted as an anchor subject", () =>
            {
                List<string> paths = MissionSubjectExtractor.ExtractPaths(
                    "Cross-check against ../ExampleSibling/output/decompiled-src/SampleDecoder.cs.");

                AssertEqual(0, paths.Count, "sibling path outside the repository is skipped");
            });

            await RunTest("Diff hunk markers and index lines produce no paths", () =>
            {
                List<string> paths = MissionSubjectExtractor.ExtractPaths(
                    "@@ -1,4 +1,5 @@\nindex 1234567..89abcde 100644\n");

                AssertEqual(0, paths.Count, "hunk and index lines yield no anchor subjects");
            });

            await RunTest("Paths are returned in first-appearance order and capped at the maximum", () =>
            {
                string text = string.Join("\n",
                    "src/Alpha.cs",
                    "src/Beta.cs",
                    "src/Gamma.cs",
                    "src/Delta.cs",
                    "src/Epsilon.cs",
                    "src/Zeta.cs",
                    "src/Eta.cs",
                    "src/Theta.cs",
                    "src/Iota.cs",
                    "src/Kappa.cs");

                List<string> paths = MissionSubjectExtractor.ExtractPaths(text);

                AssertEqual(MissionSubjectExtractor.MaxPaths, paths.Count, "capped at MaxPaths");
                AssertEqual("src/Alpha.cs", paths[0], "first path");
                AssertEqual("src/Theta.cs", paths[paths.Count - 1], "eighth path is the last one kept");
                AssertFalse(paths.Contains("src/Iota.cs"), "paths beyond the cap are not extracted");
            });
        }

        #endregion
    }
}
