namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Coverage for proving a pipeline stage is cut from its predecessor's commit. Inheriting a
    /// branch NAME is not inheriting its commit: a local ref can predate the upstream stage's
    /// push, and the worktree then looks correct while missing the work. One stage rebuilt on such
    /// a base, failed on errors already fixed upstream, and cancelled ten downstream missions.
    /// </summary>
    public class StageBaseVerifierTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Stage Base Verifier";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A checkout missing the upstream commit is BaseMissing", () =>
            {
                AssertEqual(
                    StageBaseVerdictEnum.BaseMissing,
                    StageBaseVerifier.Evaluate("msn_upstream", "abc123", false, false),
                    "This is the failure the whole check exists for.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A checkout containing the upstream commit is Verified", () =>
            {
                AssertEqual(
                    StageBaseVerdictEnum.Verified,
                    StageBaseVerifier.Evaluate("msn_upstream", "abc123", false, true));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A standalone mission has no base to check", () =>
            {
                AssertEqual(
                    StageBaseVerdictEnum.NotApplicable,
                    StageBaseVerifier.Evaluate(null, "abc123", false, false),
                    "A mission with no upstream stage must never fail this check.");
                AssertEqual(
                    StageBaseVerdictEnum.NotApplicable,
                    StageBaseVerifier.Evaluate("", "abc123", false, false));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A cross-vessel dependency is NotApplicable, not a failure", () =>
            {
                // A different repository has a different commit graph. Demanding ancestry there
                // would fail every legitimate cross-vessel stage.
                AssertEqual(
                    StageBaseVerdictEnum.NotApplicable,
                    StageBaseVerifier.Evaluate("msn_upstream", "abc123", true, false));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("An upstream that produced no commit is Unverifiable, not a failure", () =>
            {
                // Report-only stages deliver a document and commit nothing. There is nothing to
                // inherit, so there is nothing to fail.
                AssertEqual(
                    StageBaseVerdictEnum.Unverifiable,
                    StageBaseVerifier.Evaluate("msn_upstream", null, false, null));
                AssertEqual(
                    StageBaseVerdictEnum.Unverifiable,
                    StageBaseVerifier.Evaluate("msn_upstream", "   ", false, false),
                    "A blank upstream hash proves nothing, so it must not condemn the stage.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("An ancestry probe that could not answer is Unverifiable, not Verified", () =>
            {
                // A failed probe must never read as proof. Unknown is its own answer.
                AssertEqual(
                    StageBaseVerdictEnum.Unverifiable,
                    StageBaseVerifier.Evaluate("msn_upstream", "abc123", false, null));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("The failure reason names the commit and blames provisioning", () =>
            {
                string reason = StageBaseVerifier.BuildBaseMissingReason(
                    "msn_upstream", "abcdef1234567890", "armada/example/msn_stage");

                AssertContains("stage_base_missing", reason);
                AssertContains("abcdef123456", reason);
                AssertContains("msn_upstream", reason);
                AssertContains("armada/example/msn_stage", reason);
                AssertContains("provisioning fault", reason);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("An unknown commit or branch still yields a readable reason", () =>
            {
                string reason = StageBaseVerifier.BuildBaseMissingReason(null, null, null);

                AssertContains("stage_base_missing", reason);
                AssertContains("(unknown)", reason);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
