namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Coverage for MergeQueueService.BuildMergeRefCandidates.
    ///
    /// Regression: a merge entry whose stored BranchName already carried a remote prefix was
    /// resolved by probing the name itself and then "origin/" + the name. For a name that was
    /// already "origin/armada/...", the second probe asked git for "origin/origin/armada/..."
    /// and the bare "armada/..." spelling was never tried at all. The integration worktree is cut
    /// from a bare repository where mission branches live under refs/heads, so the bare spelling
    /// is usually the one that resolves -- the entry was reported as a branch that never existed.
    ///
    /// Observed on mrg_mscbrv8k_Dxm92priVBO, whose branch was recorded as
    /// origin/armada/claude-opus-1/msn_ms6w5m6b_HBv3ZWSj43J.
    /// </summary>
    public class MergeRefCandidateTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Merge Ref Candidates";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            // The exact branch name from the entry that stranded a mission.
            await RunTest("A branch already carrying origin/ also probes the bare spelling", () =>
            {
                IReadOnlyList<string> candidates =
                    MergeQueueService.BuildMergeRefCandidates("origin/armada/claude-opus-1/msn_ms6w5m6b_HBv3ZWSj43J");

                AssertTrue(
                    candidates.Contains("armada/claude-opus-1/msn_ms6w5m6b_HBv3ZWSj43J"),
                    "the bare spelling must be probed; it is the one that resolves in a bare repo");
            }).ConfigureAwait(false);

            await RunTest("A branch already carrying origin/ never stacks a second prefix", () =>
            {
                IReadOnlyList<string> candidates =
                    MergeQueueService.BuildMergeRefCandidates("origin/armada/claude-opus-1/msn_example");

                AssertFalse(
                    candidates.Any(c => c.StartsWith("origin/origin/")),
                    "origin/origin/... can never resolve and must not be produced");
            }).ConfigureAwait(false);

            // The ordinary case must keep working: a bare name still tries the remote-tracking form.
            await RunTest("A bare branch probes itself and the origin-prefixed spelling", () =>
            {
                IReadOnlyList<string> candidates =
                    MergeQueueService.BuildMergeRefCandidates("armada/captain-1/msn_example");

                AssertEqual(2, candidates.Count, "candidate count");
                AssertEqual("armada/captain-1/msn_example", candidates[0], "bare spelling probed first");
                AssertEqual("origin/armada/captain-1/msn_example", candidates[1], "remote-tracking spelling probed second");
            }).ConfigureAwait(false);

            await RunTest("Candidates are de-duplicated and the stored spelling is probed first", () =>
            {
                IReadOnlyList<string> candidates = MergeQueueService.BuildMergeRefCandidates("origin/main");

                AssertEqual("origin/main", candidates[0], "the stored spelling is probed first");
                AssertEqual(candidates.Count, candidates.Distinct().Count(), "no duplicate candidates");
            }).ConfigureAwait(false);

            await RunTest("Blank or whitespace branch names produce no candidates", () =>
            {
                AssertEqual(0, MergeQueueService.BuildMergeRefCandidates("").Count, "empty");
                AssertEqual(0, MergeQueueService.BuildMergeRefCandidates("   ").Count, "whitespace");
                AssertEqual(0, MergeQueueService.BuildMergeRefCandidates(null!).Count, "null");
            }).ConfigureAwait(false);

            // A name that is exactly the prefix must not yield an empty-string candidate, which
            // would make rev-parse probe "^{commit}" and behave unpredictably.
            await RunTest("A name that is only the prefix yields no empty candidate", () =>
            {
                IReadOnlyList<string> candidates = MergeQueueService.BuildMergeRefCandidates("origin/");

                AssertFalse(candidates.Any(string.IsNullOrWhiteSpace), "no blank candidate may be produced");
            }).ConfigureAwait(false);
        }
    }
}
