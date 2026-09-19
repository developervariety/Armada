namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Text;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// The deterministic keep-and-drop rules for large command output. A test-failure line that the
    /// compaction classifier once scored 0.41-0.44 must stay by this rule even when Jev is Off, and
    /// a progress-only download log must be droppable once it is past the token floor.
    /// </summary>
    public class ToolOutputRetentionTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Tool Output Retention";

        /// <inheritdoc />
        protected override Task RunTestsAsync()
        {
            RunTest("A log below the token floor is left whole", () =>
            {
                ToolOutputPruneResult result = ToolOutputRetention.Prune("make", "Downloading a\nDownloading b\n");
                AssertFalse(result.Pruned, "a small log is not pruned");
                AssertEqual("below_threshold", result.Reason);
                AssertContains("Downloading a", result.Output);
            });

            RunTest("A test-failure line is protected, including the 0.41-0.44 calibration pair", () =>
            {
                // Those two live readings sat below the spare floor, so Jev compacted them. The
                // diagnostic rule is the recovery: assertion text is a shape, not a meaning judgment.
                string first = "xunit.dll\n   Failed Armada.Test.Foo [1 ms]\n   Expected: 4\n   Actual:   5\n";
                string second = "Starting test execution, please wait...\nFAILED: Assert.Equal() Failure\nFailed: 1\n";
                AssertTrue(ToolOutputRetention.IsProtected(first), "Expected/Actual assertion text is protected");
                AssertTrue(ToolOutputRetention.IsProtected(second), "FAILED assertion text is protected");
                AssertContains("Expected: 4", ToolOutputRetention.DistinctiveExcerpt(first));
                AssertContains("FAILED:", ToolOutputRetention.DistinctiveExcerpt(second));
            });

            RunTest("Progress chunks past the token floor drop; the failing assertion stays", () =>
            {
                StringBuilder log = new StringBuilder();
                for (int index = 0; index < 4000; index++)
                    log.Append("Downloading package ").Append(index).Append('\n');
                log.Append("FAILED: Assert.Equal expected 4 actual 5\nFailed: 1\n");
                AssertTrue(ToolOutputRetention.EstimateTokens(log.ToString()) > ToolOutputRetention.MinimumOutputTokens);

                ToolOutputPruneResult result = ToolOutputRetention.Prune("dotnet test", log.ToString());
                AssertTrue(result.Pruned, "progress past the floor is pruned");
                AssertTrue(result.Dropped > 0, "at least one chunk is dropped");
                AssertContains("FAILED: Assert.Equal", result.Output, "the assertion is kept");
                AssertFalse(result.Output.Contains("Downloading package 2000", StringComparison.Ordinal),
                    "a middle progress line is gone");
                AssertTrue(result.Output.Length < log.Length, "the pruned log is smaller");
            });

            RunTest("A JSON document and a git diff stay whole", () =>
            {
                string json = "{\"ok\":true," + new string('a', ToolOutputRetention.MinimumOutputTokens * ToolOutputRetention.CharsPerToken) + "}";
                ToolOutputPruneResult jsonResult = ToolOutputRetention.Prune("cat data.json", json);
                AssertFalse(jsonResult.Pruned, "JSON is a document");
                AssertEqual("document", jsonResult.Reason);

                string diff = "diff --git a/x b/x\n--- a/x\n+++ b/x\n" + new string('x', ToolOutputRetention.MinimumOutputTokens * ToolOutputRetention.CharsPerToken);
                ToolOutputPruneResult diffResult = ToolOutputRetention.Prune("git diff", diff);
                AssertFalse(diffResult.Pruned, "a diff is a document");
            });

            RunTest("A binary blob stays whole", () =>
            {
                string blob = "\0" + new string('x', ToolOutputRetention.MinimumOutputTokens * ToolOutputRetention.CharsPerToken);
                ToolOutputPruneResult result = ToolOutputRetention.Prune("cat blob.bin", blob);
                AssertFalse(result.Pruned);
                AssertEqual("binary", result.Reason);
            });

            return Task.CompletedTask;
        }
    }
}
