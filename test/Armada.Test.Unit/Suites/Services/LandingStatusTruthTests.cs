namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests that a landing reports where the work actually IS, not which step ran last.
    /// </summary>
    /// <remarks>
    /// The merge into the bare repository and the sync of the configured checkout are separate
    /// steps, and only the first decides whether the work landed. Reporting a post-step failure as
    /// a landing failure sends the retry loop back over work already on the target branch, which is
    /// how the working checkout and the bare repo diverge.
    /// </remarks>
    public class LandingStatusTruthTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Landing Status Truth";

        /// <summary>
        /// Merges cleanly, answers the given ancestry, and fails the checkout sync (dirty tree).
        /// </summary>
        private static StubGitService BuildGit(bool? ancestry)
            => new StubGitService { IsAncestorResult = ancestry, IsWorkingDirectoryCleanResult = false };

        private static LandingService BuildLandingService(TestDatabase testDb, StubGitService git)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new LandingService(logging, testDb.Driver, new ArmadaSettings(), git);
        }

        private static async Task<(Vessel, Mission)> SeedAsync(TestDatabase testDb)
        {
            Vessel vessel = new Vessel("landing-truth", "https://github.com/test/repo.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            vessel.LandingMode = LandingModeEnum.LocalMerge;
            vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Mission mission = new Mission("[Worker] land me", "work");
            mission.VesselId = vessel.Id;
            mission.BranchName = "armada/worker/msn-1";
            mission.CommitHash = "abc123";
            mission.Status = MissionStatusEnum.WorkProduced;
            mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

            return (vessel, mission);
        }

        /// <summary>Run all landing status truth tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Landed work with a failed checkout sync is not reported as a landing failure", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (Vessel vessel, Mission mission) = await SeedAsync(testDb).ConfigureAwait(false);
                    StubGitService git = BuildGit(true);
                    LandingService landing = BuildLandingService(testDb, git);

                    bool result = await landing.MergeInDedicatedWorktreeAsync(
                        vessel, mission, vessel.DefaultBranch!).ConfigureAwait(false);

                    AssertTrue(result, "Work verified on the target branch must not be reported as a landing failure");
                    AssertContains(
                        "work_landed_post_step_failed",
                        mission.FailureReason,
                        "The operator must be able to tell a stale checkout from lost work without running git");
                }
            });

            await RunTest("A checkout holding commits the landing repository lacks is reported, not reset", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (Vessel vessel, Mission mission) = await SeedAsync(testDb).ConfigureAwait(false);

                    // Clean and on the target branch, so the sync itself runs and succeeds -- but the
                    // checkout carries its own commits, so the merge leaves it AHEAD of the landing
                    // repository. Nothing tells anyone until the NEXT landing cannot fast-forward.
                    StubGitService git = new StubGitService
                    {
                        IsWorkingDirectoryCleanResult = true,
                        CurrentBranchResult = vessel.DefaultBranch,
                        IsAncestorResult = true
                    };
                    git.RevisionShas[vessel.LocalPath + "|" + vessel.DefaultBranch] = "landingtip";
                    git.RevisionShas[vessel.WorkingDirectory + "|HEAD"] = "checkoutlocalonly";

                    LandingService landing = BuildLandingService(testDb, git);

                    bool result = await landing.MergeInDedicatedWorktreeAsync(
                        vessel, mission, vessel.DefaultBranch!).ConfigureAwait(false);

                    AssertContains(
                        "working_directory_diverged",
                        mission.FailureReason,
                        "Divergence must be named at the landing that created it, not at the next one");
                    AssertContains(
                        "Do NOT reset",
                        mission.FailureReason,
                        "Those commits exist in one place only; the obvious repair is the data loss");
                    AssertTrue(result, "The work still landed, so this is a post-step problem, not a landing failure");
                }
            });

            await RunTest("Unverifiable ancestry keeps a failed sync as a real landing failure", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (Vessel vessel, Mission mission) = await SeedAsync(testDb).ConfigureAwait(false);

                    // Null is UNKNOWN. A check that could not run must never excuse the failure.
                    StubGitService git = BuildGit(null);
                    LandingService landing = BuildLandingService(testDb, git);

                    bool result = await landing.MergeInDedicatedWorktreeAsync(
                        vessel, mission, vessel.DefaultBranch!).ConfigureAwait(false);

                    AssertFalse(result, "Unverified ancestry must not be reported as landed");
                }
            });

            await RunTest("Work absent from the target branch stays a landing failure", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (Vessel vessel, Mission mission) = await SeedAsync(testDb).ConfigureAwait(false);
                    StubGitService git = BuildGit(false);
                    LandingService landing = BuildLandingService(testDb, git);

                    bool result = await landing.MergeInDedicatedWorktreeAsync(
                        vessel, mission, vessel.DefaultBranch!).ConfigureAwait(false);

                    AssertFalse(result, "Work that is not on the target branch is genuinely lost and must fail");
                }
            });
        }
    }
}
