namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for <see cref="DeploymentEvidenceEvaluator"/>: which Check records prove a required type at one
    /// deployment candidate commit, and which never can.
    /// </summary>
    public class DeploymentEvidenceEvaluatorTests : TestSuite
    {
        #region Private-Members

        private const string _VesselId = "vsl_evidence";
        private const string _Candidate = "1111111111111111111111111111111111111111";
        private const string _OtherCommit = "2222222222222222222222222222222222222222";
        private const string _UnitCommand = "dotnet test Tests.sln";
        private const string _BuildCommand = "dotnet build Tests.sln";

        #endregion

        #region Public-Members

        /// <inheritdoc/>
        public override string Name => "Deployment Evidence Evaluator";

        #endregion

        #region Protected-Methods

        /// <inheritdoc/>
        protected override async Task RunTestsAsync()
        {
            await RunTest("No required types is satisfied without records", () =>
            {
                DeploymentEvidenceResult result = Evaluate(new List<CheckRunTypeEnum>(), _Candidate, new List<CheckRun>());
                AssertTrue(result.Satisfied, "an environment with no required types requires nothing");
                AssertEqual(0, result.CheckRunIds.Count);
            }).ConfigureAwait(false);

            await RunTest("A passing UnitTest at the exact candidate commit with the profile command proves the type", () =>
            {
                CheckRun pass = Record("chk_pass", CheckRunTypeEnum.UnitTest, _Candidate, _UnitCommand, CheckRunStatusEnum.Passed, 1);
                DeploymentEvidenceResult result = Evaluate(UnitTestOnly(), _Candidate, new List<CheckRun> { pass });
                AssertTrue(result.Satisfied, result.Summary);
                AssertEqual("chk_pass", String.Join(",", result.CheckRunIds));
            }).ConfigureAwait(false);

            await RunTest("A passing Build never satisfies a required UnitTest", () =>
            {
                CheckRun build = Record("chk_build", CheckRunTypeEnum.Build, _Candidate, _BuildCommand, CheckRunStatusEnum.Passed, 1);
                DeploymentEvidenceResult result = Evaluate(UnitTestOnly(), _Candidate, new List<CheckRun> { build });
                AssertFalse(result.Satisfied, "a Build record is not UnitTest proof");
                AssertStartsWith(DeploymentEvidenceEvaluator.MissingReason, result.Summary);
                AssertEqual(0, result.CheckRunIds.Count);
            }).ConfigureAwait(false);

            await RunTest("A pass at another commit is stale and names the commit it measured", () =>
            {
                CheckRun stale = Record("chk_stale", CheckRunTypeEnum.UnitTest, _OtherCommit, _UnitCommand, CheckRunStatusEnum.Passed, 1);
                DeploymentEvidenceResult result = Evaluate(UnitTestOnly(), _Candidate, new List<CheckRun> { stale });
                AssertFalse(result.Satisfied, "a pass at another commit says nothing about the candidate");
                AssertStartsWith(DeploymentEvidenceEvaluator.MissingReason, result.Summary);
                AssertContains(_OtherCommit.Substring(0, 12), result.Summary, "the refusal names the stale commit");
            }).ConfigureAwait(false);

            await RunTest("A pass at an abbreviated commit is not exact-commit proof", () =>
            {
                CheckRun abbreviated = Record("chk_abbrev", CheckRunTypeEnum.UnitTest, _Candidate.Substring(0, 12), _UnitCommand, CheckRunStatusEnum.Passed, 1);
                DeploymentEvidenceResult result = Evaluate(UnitTestOnly(), _Candidate, new List<CheckRun> { abbreviated });
                AssertFalse(result.Satisfied, "a prefix may name a different commit, so it is not proof");
            }).ConfigureAwait(false);

            await RunTest("A filtered or overridden Armada run is not proof of the whole Check", () =>
            {
                CheckRun filtered = Record("chk_filtered", CheckRunTypeEnum.UnitTest, _Candidate, _UnitCommand + " --filter Smoke", CheckRunStatusEnum.Passed, 1);
                DeploymentEvidenceResult result = Evaluate(UnitTestOnly(), _Candidate, new List<CheckRun> { filtered });
                AssertFalse(result.Satisfied, "a narrowed command did not run the profile's suite");
                AssertContains("different command", result.Summary);
            }).ConfigureAwait(false);

            await RunTest("Whitespace differences in the profile command still count as the same command", () =>
            {
                CheckRun spaced = Record("chk_spaced", CheckRunTypeEnum.UnitTest, _Candidate, "  dotnet   test Tests.sln ", CheckRunStatusEnum.Passed, 1);
                DeploymentEvidenceResult result = Evaluate(UnitTestOnly(), _Candidate, new List<CheckRun> { spaced });
                AssertTrue(result.Satisfied, result.Summary);
            }).ConfigureAwait(false);

            await RunTest("A failure newer than a pass at the candidate withdraws the proof", () =>
            {
                CheckRun pass = Record("chk_old_pass", CheckRunTypeEnum.UnitTest, _Candidate, _UnitCommand, CheckRunStatusEnum.Passed, 1);
                CheckRun fail = Record("chk_new_fail", CheckRunTypeEnum.UnitTest, _Candidate, _UnitCommand, CheckRunStatusEnum.Failed, 2);
                DeploymentEvidenceResult result = Evaluate(UnitTestOnly(), _Candidate, new List<CheckRun> { pass, fail });
                AssertFalse(result.Satisfied, "the newest verdict decides");
                AssertStartsWith(DeploymentEvidenceEvaluator.FailedReason, result.Summary);
                AssertContains("chk_new_fail", result.Summary);
            }).ConfigureAwait(false);

            await RunTest("A pass newer than a failure at the candidate proves the type", () =>
            {
                CheckRun fail = Record("chk_old_fail", CheckRunTypeEnum.UnitTest, _Candidate, _UnitCommand, CheckRunStatusEnum.Failed, 1);
                CheckRun pass = Record("chk_new_pass", CheckRunTypeEnum.UnitTest, _Candidate, _UnitCommand, CheckRunStatusEnum.Passed, 2);
                DeploymentEvidenceResult result = Evaluate(UnitTestOnly(), _Candidate, new List<CheckRun> { fail, pass });
                AssertTrue(result.Satisfied, result.Summary);
                AssertEqual("chk_new_pass", String.Join(",", result.CheckRunIds));
            }).ConfigureAwait(false);

            await RunTest("A canceled or pending record neither proves nor withdraws the type", () =>
            {
                CheckRun pass = Record("chk_pass", CheckRunTypeEnum.UnitTest, _Candidate, _UnitCommand, CheckRunStatusEnum.Passed, 1);
                CheckRun canceled = Record("chk_canceled", CheckRunTypeEnum.UnitTest, _Candidate, _UnitCommand, CheckRunStatusEnum.Canceled, 2);
                CheckRun pending = Record("chk_pending", CheckRunTypeEnum.UnitTest, _Candidate, _UnitCommand, CheckRunStatusEnum.Pending, 3);
                DeploymentEvidenceResult proven = Evaluate(UnitTestOnly(), _Candidate, new List<CheckRun> { pass, canceled, pending });
                AssertTrue(proven.Satisfied, proven.Summary);

                DeploymentEvidenceResult unproven = Evaluate(UnitTestOnly(), _Candidate, new List<CheckRun> { canceled, pending });
                AssertFalse(unproven.Satisfied, "records without a verdict are not proof");
                AssertContains("did not finish with a verdict", unproven.Summary);
            }).ConfigureAwait(false);

            await RunTest("A record of another vessel is not proof", () =>
            {
                CheckRun foreign = Record("chk_foreign", CheckRunTypeEnum.UnitTest, _Candidate, _UnitCommand, CheckRunStatusEnum.Passed, 1);
                foreign.VesselId = "vsl_other";
                DeploymentEvidenceResult result = Evaluate(UnitTestOnly(), _Candidate, new List<CheckRun> { foreign });
                AssertFalse(result.Satisfied, "another vessel's commit history is unrelated");
            }).ConfigureAwait(false);

            await RunTest("An imported external pass at the candidate counts as that system's verdict", () =>
            {
                CheckRun external = Record("chk_external", CheckRunTypeEnum.UnitTest, _Candidate, "external ci", CheckRunStatusEnum.Passed, 1);
                external.Source = CheckRunSourceEnum.External;
                DeploymentEvidenceResult result = Evaluate(UnitTestOnly(), _Candidate, new List<CheckRun> { external });
                AssertTrue(result.Satisfied, result.Summary);
            }).ConfigureAwait(false);

            await RunTest("A profile without the required command cannot be proven by an Armada run", () =>
            {
                CheckRun pass = Record("chk_pass", CheckRunTypeEnum.UnitTest, _Candidate, _UnitCommand, CheckRunStatusEnum.Passed, 1);
                DeploymentEvidenceResult result = DeploymentEvidenceEvaluator.Evaluate(
                    UnitTestOnly(), _VesselId, _Candidate, new List<CheckRun> { pass }, new Dictionary<CheckRunTypeEnum, string>());
                AssertFalse(result.Satisfied, "with no profile command there is no whole-suite command to match");
                AssertContains("defines no UnitTest command", result.Summary);
            }).ConfigureAwait(false);

            await RunTest("Each required type is decided on its own", () =>
            {
                List<CheckRunTypeEnum> both = new List<CheckRunTypeEnum> { CheckRunTypeEnum.Build, CheckRunTypeEnum.UnitTest };
                CheckRun build = Record("chk_build", CheckRunTypeEnum.Build, _Candidate, _BuildCommand, CheckRunStatusEnum.Passed, 1);
                DeploymentEvidenceResult partial = Evaluate(both, _Candidate, new List<CheckRun> { build });
                AssertFalse(partial.Satisfied, "a missing UnitTest refuses even when Build passed");
                AssertContains("Build passed in chk_build", partial.Summary);
                AssertContains("UnitTest not proven", partial.Summary);

                CheckRun unit = Record("chk_unit", CheckRunTypeEnum.UnitTest, _Candidate, _UnitCommand, CheckRunStatusEnum.Passed, 2);
                DeploymentEvidenceResult complete = Evaluate(both, _Candidate, new List<CheckRun> { build, unit });
                AssertTrue(complete.Satisfied, complete.Summary);
                AssertEqual("chk_build,chk_unit", String.Join(",", complete.CheckRunIds));
            }).ConfigureAwait(false);

            await RunTest("An unpinned deployment cannot satisfy a required type", () =>
            {
                CheckRun pass = Record("chk_pass", CheckRunTypeEnum.UnitTest, _Candidate, _UnitCommand, CheckRunStatusEnum.Passed, 1);
                DeploymentEvidenceResult result = Evaluate(UnitTestOnly(), null, new List<CheckRun> { pass });
                AssertFalse(result.Satisfied, "without a pinned commit there is nothing to measure");
                AssertStartsWith(DeploymentEvidenceEvaluator.UnpinnedReason, result.Summary);
            }).ConfigureAwait(false);

            await RunTest("Target-measuring types cannot be required", () =>
            {
                AssertFalse(DeploymentEvidenceEvaluator.IsRequirableType(CheckRunTypeEnum.Deploy), "Deploy");
                AssertFalse(DeploymentEvidenceEvaluator.IsRequirableType(CheckRunTypeEnum.Rollback), "Rollback");
                AssertFalse(DeploymentEvidenceEvaluator.IsRequirableType(CheckRunTypeEnum.SmokeTest), "SmokeTest");
                AssertFalse(DeploymentEvidenceEvaluator.IsRequirableType(CheckRunTypeEnum.HealthCheck), "HealthCheck");
                AssertFalse(DeploymentEvidenceEvaluator.IsRequirableType(CheckRunTypeEnum.DeploymentVerification), "DeploymentVerification");
                AssertFalse(DeploymentEvidenceEvaluator.IsRequirableType(CheckRunTypeEnum.RollbackVerification), "RollbackVerification");
                AssertTrue(DeploymentEvidenceEvaluator.IsRequirableType(CheckRunTypeEnum.UnitTest), "UnitTest");
                AssertTrue(DeploymentEvidenceEvaluator.IsRequirableType(CheckRunTypeEnum.Build), "Build");
            }).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private static List<CheckRunTypeEnum> UnitTestOnly()
        {
            return new List<CheckRunTypeEnum> { CheckRunTypeEnum.UnitTest };
        }

        private static DeploymentEvidenceResult Evaluate(List<CheckRunTypeEnum> required, string? commit, List<CheckRun> records)
        {
            Dictionary<CheckRunTypeEnum, string> commands = new Dictionary<CheckRunTypeEnum, string>
            {
                [CheckRunTypeEnum.UnitTest] = _UnitCommand,
                [CheckRunTypeEnum.Build] = _BuildCommand
            };
            return DeploymentEvidenceEvaluator.Evaluate(required, _VesselId, commit, records, commands);
        }

        private static CheckRun Record(string id, CheckRunTypeEnum type, string commit, string command, CheckRunStatusEnum status, int minute)
        {
            return new CheckRun
            {
                Id = id,
                VesselId = _VesselId,
                Type = type,
                Source = CheckRunSourceEnum.Armada,
                Status = status,
                CommitHash = commit,
                Command = command,
                ExitCode = status == CheckRunStatusEnum.Passed ? 0 : (status == CheckRunStatusEnum.Failed ? 1 : (int?)null),
                CreatedUtc = new DateTime(2000, 1, 1, 0, minute, 0, DateTimeKind.Utc)
            };
        }

        #endregion
    }
}
