namespace Armada.Test.Unit
{
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Pins when a pipeline stage must be proven to sit on its predecessor's commit.
    ///
    /// Two rules meet here. A stage that continues its predecessor's branch must contain that
    /// predecessor's commit, or it would rebuild on a base the predecessor already moved past.
    /// But an Architect fan-out worker is deliberately spawned with NO branch and cut a fresh one,
    /// so it can never contain the Architect's commit.
    ///
    /// Those rules only collide when the Architect also COMMITS code, which happens when it
    /// overreaches and implements its own plan instead of emitting one. The base check then failed
    /// every fan-out worker for doing exactly what the pipeline intends, and because the base never
    /// changes, each rescue reproduced it. One voyage failed this way three times while the
    /// Architect's completed work sat on a commit reachable from no branch.
    /// </summary>
    public sealed class StageBranchInheritanceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Stage Branch Inheritance";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("standalone mission is not base-checked", () =>
            {
                AssertEqual(
                    StageBaseVerdictEnum.NotApplicable,
                    StageBaseVerifier.Evaluate(null, "abc123", false, false),
                    "a mission with no dependency has no base to inherit");
            });

            await RunTest("cross-vessel dependency is not base-checked", () =>
            {
                AssertEqual(
                    StageBaseVerdictEnum.NotApplicable,
                    StageBaseVerifier.Evaluate("msn_up", "abc123", true, false),
                    "another repository has another commit graph");
            });

            // The regression: a stage cut a fresh branch was failed for not containing a commit
            // it was never meant to inherit.
            await RunTest("stage cut a fresh branch is not base-checked", () =>
            {
                AssertEqual(
                    StageBaseVerdictEnum.NotApplicable,
                    StageBaseVerifier.Evaluate("msn_up", "abc123", false, false, stageContinuesUpstreamBranch: false),
                    "an Architect fan-out worker is spawned branchless by design; containment is not expected");
            });

            await RunTest("stage continuing the upstream branch without the commit still fails", () =>
            {
                AssertEqual(
                    StageBaseVerdictEnum.BaseMissing,
                    StageBaseVerifier.Evaluate("msn_up", "abc123", false, false, stageContinuesUpstreamBranch: true),
                    "the guard must keep catching a genuine provisioning fault");
            });

            await RunTest("stage continuing the upstream branch with the commit is verified", () =>
            {
                AssertEqual(
                    StageBaseVerdictEnum.Verified,
                    StageBaseVerifier.Evaluate("msn_up", "abc123", false, true, stageContinuesUpstreamBranch: true),
                    "the normal healthy handoff");
            });

            await RunTest("upstream that produced no commit is unverifiable, not failed", () =>
            {
                AssertEqual(
                    StageBaseVerdictEnum.Unverifiable,
                    StageBaseVerifier.Evaluate("msn_up", null, false, null, stageContinuesUpstreamBranch: true),
                    "report-only upstream work leaves nothing to inherit");
            });
        }
    }
}
