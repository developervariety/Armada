namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Memory;
    using Armada.Test.Common;

    /// <summary>
    /// Direct unit tests for <see cref="ReflectionDispatcher.BuildCandidateDiff(string?, string?, int)"/>,
    /// the pure helper that powers the MemoryConsolidator candidate-emit payload. Covers the
    /// no-diff short-circuit, null/empty handling, line-ending normalization, prefix/suffix
    /// trimming, and bounded-preview truncation that the landing handler relies on.
    /// </summary>
    public class CandidateDiffBuilderTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Candidate Diff Builder";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("IdenticalContent_NoDiff_NullPreview", () =>
            {
                string content = "# Vessel Learned Facts\n\nSame fact.";
                CandidateDiffResult result = ReflectionDispatcher.BuildCandidateDiff(content, content);

                AssertFalse(result.HasDiff, "identical content must report no diff");
                AssertNull(result.Preview, "preview must be null when there is no diff");
                AssertEqual(content.Length, result.ProposedContentLength, "proposed length tracks candidate length");
                return Task.CompletedTask;
            });

            await RunTest("BothNull_NoDiff_ZeroLength", () =>
            {
                CandidateDiffResult result = ReflectionDispatcher.BuildCandidateDiff(null, null);

                AssertFalse(result.HasDiff, "two null inputs are treated as equal empty content");
                AssertNull(result.Preview, "no preview for equal empty content");
                AssertEqual(0, result.ProposedContentLength, "null candidate has zero length");
                return Task.CompletedTask;
            });

            await RunTest("NullCanonical_TreatedAsEmpty_AllCandidateLinesAdded", () =>
            {
                string candidate = "line one\nline two";
                CandidateDiffResult result = ReflectionDispatcher.BuildCandidateDiff(null, candidate);

                AssertTrue(result.HasDiff, "null canonical vs non-empty candidate must differ");
                AssertEqual(candidate.Length, result.ProposedContentLength, "proposed length");
                AssertNotNull(result.Preview, "preview present when content differs");
                AssertContains("+line one", result.Preview!, "candidate lines are additions");
                AssertContains("+line two", result.Preview!, "candidate lines are additions");
                AssertFalse(result.Preview!.Contains("\n-"), "no removed lines when canonical was empty");
                return Task.CompletedTask;
            });

            await RunTest("NullCandidate_TreatedAsEmpty_AllCanonicalLinesRemoved", () =>
            {
                string canonical = "old one\nold two";
                CandidateDiffResult result = ReflectionDispatcher.BuildCandidateDiff(canonical, null);

                AssertTrue(result.HasDiff, "non-empty canonical vs null candidate must differ");
                AssertEqual(0, result.ProposedContentLength, "null candidate has zero proposed length");
                AssertContains("-old one", result.Preview!, "canonical lines are removals");
                AssertContains("-old two", result.Preview!, "canonical lines are removals");
                AssertFalse(result.Preview!.Contains("\n+"), "no added lines when candidate is empty");
                return Task.CompletedTask;
            });

            await RunTest("MiddleLineChange_PrefixSuffixTrimmed_HunkHeaderCorrect", () =>
            {
                // Shared first and last line; only the middle line changes. The diff must keep
                // the common prefix/suffix out of the +/- body and point the hunk header at
                // line 2 (1-based, after the single common prefix line).
                string canonical = "header\nold middle\nfooter";
                string candidate = "header\nnew middle\nfooter";
                CandidateDiffResult result = ReflectionDispatcher.BuildCandidateDiff(canonical, candidate);

                AssertTrue(result.HasDiff, "middle change must report a diff");
                AssertContains("@@ -2,1 +2,1 @@", result.Preview!, "hunk header reflects trimmed prefix/suffix");
                AssertContains("-old middle", result.Preview!, "only the changed canonical line is removed");
                AssertContains("+new middle", result.Preview!, "only the changed candidate line is added");
                AssertFalse(result.Preview!.Contains("header"), "common prefix line is not in the body");
                AssertFalse(result.Preview!.Contains("footer"), "common suffix line is not in the body");
                return Task.CompletedTask;
            });

            await RunTest("CrlfOnlyDifference_RawCompareReportsDiff_NormalizedBodyEmpty", () =>
            {
                // HasDiff compares the raw strings ordinally, so a pure CRLF-vs-LF difference is
                // flagged as a diff -- but the preview normalizes line endings, so the +/- body
                // collapses to nothing (only the hunk header remains). Pins this documented quirk.
                string canonical = "a\r\nb\r\nc";
                string candidate = "a\nb\nc";
                CandidateDiffResult result = ReflectionDispatcher.BuildCandidateDiff(canonical, candidate);

                AssertTrue(result.HasDiff, "raw CRLF vs LF strings are not ordinally equal");
                AssertFalse(result.Preview!.Contains("\n-"), "normalized lines produce no removals");
                AssertFalse(result.Preview!.Contains("\n+"), "normalized lines produce no additions");
                return Task.CompletedTask;
            });

            await RunTest("LongDiff_PreviewTruncatedToMax", () =>
            {
                string canonical = "";
                string candidate = new string('x', 5000);
                CandidateDiffResult result = ReflectionDispatcher.BuildCandidateDiff(canonical, candidate, 200);

                AssertTrue(result.HasDiff, "large candidate must differ from empty canonical");
                AssertEqual(5000, result.ProposedContentLength, "proposed length is full candidate length, not truncated");
                AssertNotNull(result.Preview, "preview present");
                AssertEqual(200, result.Preview!.Length, "preview is capped at maxPreviewLength");
                return Task.CompletedTask;
            });
        }
    }
}
