namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
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
    /// Tests for the branch-cleanup maintenance sweep: merged armada/* branches are
    /// pruned per policy, unmerged branches are never touched, and None-policy vessels are skipped.
    /// Uses real git repositories so the merged-only ancestry guard is genuinely exercised.
    /// </summary>
    public class BranchCleanupSweepTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Branch Cleanup Sweep";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Sweeps merged branches and preserves unmerged ones under LocalOnly", async () =>
            {
                string rootDir = NewTempDir();
                try
                {
                    SweepRepo repo = await CreateSweepRepoAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        Vessel vessel = new Vessel("sweep-vessel", "https://github.com/test/sweep.git");
                        vessel.LocalPath = repo.Repo;
                        vessel.WorkingDirectory = repo.Working;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        BranchCleanupSweepService service = new BranchCleanupSweepService(
                            logging, testDb.Driver, new ArmadaSettings(), new GitService(logging));

                        BranchCleanupSweepResult result = await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);

                        AssertEqual(1, result.SweptLocal, "the merged armada branch must be swept locally");
                        AssertEqual(1, result.KeptUnmerged, "the unmerged armada branch must be preserved");
                        AssertEqual(0, result.SweptRemote, "LocalOnly must not touch origin branches");

                        IReadOnlyList<string> remaining = await new GitService(logging)
                            .EnumerateLocalBranchesAsync(repo.Repo, "armada/").ConfigureAwait(false);
                        AssertEqual(1, remaining.Count, "only the unmerged branch may remain");
                        AssertEqual("armada/claude-1/msn_unmerged001", remaining[0], "the remaining branch must be the unmerged one");

                        EnumerationResult<ArmadaEvent> events = await testDb.Driver.Events.EnumerateAsync(
                            new EnumerationQuery { PageNumber = 1, PageSize = 50 }).ConfigureAwait(false);
                        AssertTrue(events.Objects.Any(e => e.EventType == "branch_cleanup.swept"), "the sweep must emit a branch_cleanup.swept event");
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            }).ConfigureAwait(false);

            await RunTest("LocalAndRemote also deletes the origin branch", async () =>
            {
                string rootDir = NewTempDir();
                try
                {
                    SweepRepo repo = await CreateSweepRepoAsync(rootDir, remote: true).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        Vessel vessel = new Vessel("sweep-vessel-2", "https://github.com/test/sweep.git");
                        vessel.LocalPath = repo.Repo;
                        vessel.WorkingDirectory = repo.Working;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalAndRemote;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        BranchCleanupSweepService service = new BranchCleanupSweepService(
                            logging, testDb.Driver, new ArmadaSettings(), new GitService(logging));

                        BranchCleanupSweepResult result = await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);

                        AssertEqual(1, result.SweptLocal, "the merged branch must be swept locally");
                        AssertEqual(1, result.SweptRemote, "LocalAndRemote must also delete the origin branch");

                        string remoteBranches = await RunGitAsync(repo.Remote!, "for-each-ref", "refs/heads/armada").ConfigureAwait(false);
                        AssertFalse(remoteBranches.Contains("msn_merged001"), "the merged branch must be gone from origin");
                        AssertTrue(remoteBranches.Contains("msn_unmerged001"), "the unmerged branch must remain on origin");
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            }).ConfigureAwait(false);

            await RunTest("Sweeps the armada-landing namespace and never touches a human branch", async () =>
            {
                string rootDir = NewTempDir();
                try
                {
                    SweepRepo repo = await CreateSweepRepoAsync(rootDir, extraNamespaces: true).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        Vessel vessel = new Vessel("sweep-vessel-4", "https://github.com/test/sweep.git");
                        vessel.LocalPath = repo.Repo;
                        vessel.WorkingDirectory = repo.Working;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        BranchCleanupSweepService service = new BranchCleanupSweepService(
                            logging, testDb.Driver, new ArmadaSettings(), new GitService(logging));

                        BranchCleanupSweepResult result = await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);

                        // Two merged Armada branches now exist: one armada/, one armada-landing/.
                        // Before the prefix fix this was 1, because armada-landing/ does not start
                        // with "armada/" and was never enumerated.
                        AssertEqual(2, result.SweptLocal, "both the armada and armada-landing merged branches must be swept");
                        AssertEqual(1, result.KeptUnmerged, "the unmerged armada branch must still be preserved");

                        GitService git = new GitService(logging);
                        IReadOnlyList<string> landing = await git
                            .EnumerateLocalBranchesAsync(repo.Repo, "armada-landing/").ConfigureAwait(false);
                        AssertEqual(0, landing.Count, "no armada-landing branch may survive the sweep");

                        IReadOnlyList<string> all = await git.EnumerateLocalBranchesAsync(repo.Repo, null).ConfigureAwait(false);
                        AssertTrue(all.Contains("salvage/wave-a"), "a merged human branch must survive: the sweep owns only its own namespaces");
                        AssertTrue(all.Contains("main"), "the default branch must survive");
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            }).ConfigureAwait(false);

            await RunTest("IsManagedBranch covers both Armada namespaces and nothing else", async () =>
            {
                AssertTrue(BranchCleanupSweepService.IsManagedBranch("armada/claude-1/msn_x"), "captain branches are managed");
                AssertTrue(BranchCleanupSweepService.IsManagedBranch("armada-landing/armada/claude-1/msn_x"), "landing branches are managed");
                AssertTrue(BranchCleanupSweepService.IsManagedBranch("armada/merge-queue/mrg_x"), "merge-queue branches are managed");
                AssertTrue(!BranchCleanupSweepService.IsManagedBranch("main"), "the default branch is not managed");
                AssertTrue(!BranchCleanupSweepService.IsManagedBranch("salvage/wave-a"), "a human branch is not managed");
                AssertTrue(!BranchCleanupSweepService.IsManagedBranch("armadillo/x"), "a lookalike prefix is not managed");
                AssertTrue(!BranchCleanupSweepService.IsManagedBranch(""), "an empty name is not managed");
                await Task.CompletedTask.ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("None policy skips the vessel entirely", async () =>
            {
                string rootDir = NewTempDir();
                try
                {
                    SweepRepo repo = await CreateSweepRepoAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        Vessel vessel = new Vessel("sweep-vessel-3", "https://github.com/test/sweep.git");
                        vessel.LocalPath = repo.Repo;
                        vessel.WorkingDirectory = repo.Working;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        BranchCleanupSweepService service = new BranchCleanupSweepService(
                            logging, testDb.Driver, new ArmadaSettings(), new GitService(logging));

                        BranchCleanupSweepResult result = await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);

                        AssertEqual(0, result.SweptLocal, "None policy must not sweep anything");
                        AssertEqual(1, result.SkippedVessels, "the vessel must be skipped");
                        AssertEqual(0, result.KeptUnmerged, "no branch must be evaluated under None policy");
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            }).ConfigureAwait(false);

            await RunTest("LocalAndRemote deletes a landed origin branch the vessel bare no longer holds", async () =>
            {
                string rootDir = NewTempDir();
                try
                {
                    LandedFixture fx = await CreateLandedFixtureAsync(rootDir).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        await CreateFixtureVesselAsync(testDb, fx, "sweep-remote-only", BranchCleanupPolicyEnum.LocalAndRemote).ConfigureAwait(false);
                        BranchCleanupSweepService service = new BranchCleanupSweepService(
                            logging, testDb.Driver, new ArmadaSettings(), new GitService(logging));

                        await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);

                        // The landing path deleted the bare branch but not the origin copy, so a sweep that
                        // only enumerates the bare never sees the origin branch at all.
                        HashSet<string> remote = await ListRefsAsync(fx.Remote).ConfigureAwait(false);
                        AssertFalse(remote.Contains("refs/heads/" + RemoteOnlyLanded), "a landed origin-only mission branch must be deleted from origin");
                        AssertFalse(remote.Contains("refs/heads/" + LandedOld), "a landed mission branch must be deleted from origin");
                        AssertTrue(remote.Contains("refs/heads/" + RemoteOnlyUnlanded), "an unlanded origin-only mission branch must stay on origin");
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            }).ConfigureAwait(false);

            await RunTest("Prunes a landed preserved ref past retention locally and on origin", async () =>
            {
                string rootDir = NewTempDir();
                try
                {
                    LandedFixture fx = await CreateLandedFixtureAsync(rootDir).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        await CreateFixtureVesselAsync(testDb, fx, "sweep-preserved", BranchCleanupPolicyEnum.LocalAndRemote).ConfigureAwait(false);
                        BranchCleanupSweepService service = new BranchCleanupSweepService(
                            logging, testDb.Driver, new ArmadaSettings(), new GitService(logging));

                        await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);

                        HashSet<string> local = await ListRefsAsync(fx.Bare).ConfigureAwait(false);
                        HashSet<string> remote = await ListRefsAsync(fx.Remote).ConfigureAwait(false);
                        AssertFalse(local.Contains(PreservedOldLanded), "a landed preserved ref older than retention must be deleted from the vessel bare");
                        AssertFalse(remote.Contains(PreservedOldLanded), "a landed preserved ref older than retention must be deleted from origin");
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            }).ConfigureAwait(false);

            await RunTest("Keeps an unlanded preserved ref and a landed preserved ref inside retention", async () =>
            {
                string rootDir = NewTempDir();
                try
                {
                    LandedFixture fx = await CreateLandedFixtureAsync(rootDir).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        await CreateFixtureVesselAsync(testDb, fx, "sweep-preserved-kept", BranchCleanupPolicyEnum.LocalAndRemote).ConfigureAwait(false);
                        BranchCleanupSweepService service = new BranchCleanupSweepService(
                            logging, testDb.Driver, new ArmadaSettings(), new GitService(logging));

                        await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);

                        HashSet<string> local = await ListRefsAsync(fx.Bare).ConfigureAwait(false);
                        HashSet<string> remote = await ListRefsAsync(fx.Remote).ConfigureAwait(false);
                        AssertTrue(local.Contains(PreservedUnlanded) && remote.Contains(PreservedUnlanded),
                            "an unlanded preserved ref must stay reachable by name however old it is");
                        AssertTrue(local.Contains(PreservedYoungLanded) && remote.Contains(PreservedYoungLanded),
                            "a landed preserved ref inside the retention window must be kept");
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            }).ConfigureAwait(false);

            await RunTest("Never deletes recover refs, human branches, or unlanded branches on either side", async () =>
            {
                string rootDir = NewTempDir();
                try
                {
                    LandedFixture fx = await CreateLandedFixtureAsync(rootDir).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        await CreateFixtureVesselAsync(testDb, fx, "sweep-guards", BranchCleanupPolicyEnum.LocalAndRemote).ConfigureAwait(false);
                        BranchCleanupSweepService service = new BranchCleanupSweepService(
                            logging, testDb.Driver, new ArmadaSettings(), new GitService(logging));

                        await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);

                        HashSet<string> local = await ListRefsAsync(fx.Bare).ConfigureAwait(false);
                        HashSet<string> remote = await ListRefsAsync(fx.Remote).ConfigureAwait(false);
                        foreach (string branch in new string[] { RecoverLanded, HumanLanded, Unlanded, "main" })
                        {
                            AssertTrue(local.Contains("refs/heads/" + branch), branch + " must survive in the vessel bare");
                            AssertTrue(remote.Contains("refs/heads/" + branch), branch + " must survive on origin");
                        }
                        AssertTrue(remote.Contains("refs/heads/" + RemoteOnlyUnlanded), "an unlanded origin-only branch must survive");
                        AssertFalse(local.Contains("refs/heads/" + LandedOld), "the sweep must still have removed the landed mission branch");
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            }).ConfigureAwait(false);

            await RunTest("Keeps a landed branch that a non-terminal mission still owns", async () =>
            {
                string rootDir = NewTempDir();
                try
                {
                    LandedFixture fx = await CreateLandedFixtureAsync(rootDir).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        Vessel vessel = await CreateFixtureVesselAsync(testDb, fx, "sweep-active", BranchCleanupPolicyEnum.LocalAndRemote).ConfigureAwait(false);

                        // A freshly provisioned mission branch sits at the default-branch tip, so it reads as
                        // landed before its captain has committed anything.
                        Mission mission = new Mission("active mission", "still running");
                        mission.VesselId = vessel.Id;
                        mission.BranchName = ActiveLanded;
                        mission.Status = MissionStatusEnum.InProgress;
                        await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                        BranchCleanupSweepService service = new BranchCleanupSweepService(
                            logging, testDb.Driver, new ArmadaSettings(), new GitService(logging));

                        await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);

                        HashSet<string> local = await ListRefsAsync(fx.Bare).ConfigureAwait(false);
                        HashSet<string> remote = await ListRefsAsync(fx.Remote).ConfigureAwait(false);
                        AssertTrue(local.Contains("refs/heads/" + ActiveLanded), "an active mission's branch must survive in the vessel bare");
                        AssertTrue(remote.Contains("refs/heads/" + ActiveLanded), "an active mission's branch must survive on origin");
                        AssertFalse(local.Contains("refs/heads/" + LandedOld), "the sweep must still have removed an unowned landed branch");
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            }).ConfigureAwait(false);

            await RunTest("Logs a summary on a run that removes nothing and names each skipped vessel", async () =>
            {
                string rootDir = NewTempDir();
                try
                {
                    SweepRepo repo = await CreateSweepRepoAsync(rootDir).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        List<string> messages = new List<string>();
                        object gate = new object();
                        logging.MessageLogged += entry =>
                        {
                            lock (gate)
                            {
                                messages.Add(entry.Message ?? String.Empty);
                            }
                        };

                        Vessel active = new Vessel("sweep-log-active", "https://github.com/test/sweep.git");
                        active.LocalPath = repo.Repo;
                        active.WorkingDirectory = repo.Working;
                        active.DefaultBranch = "main";
                        active.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                        await testDb.Driver.Vessels.CreateAsync(active).ConfigureAwait(false);

                        Vessel none = new Vessel("sweep-log-none", "https://github.com/test/sweep.git");
                        none.LocalPath = repo.Repo;
                        none.DefaultBranch = "main";
                        none.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                        none = await testDb.Driver.Vessels.CreateAsync(none).ConfigureAwait(false);

                        Vessel missing = new Vessel("sweep-log-missing", "https://github.com/test/sweep.git");
                        missing.LocalPath = Path.Combine(rootDir, "does-not-exist.git");
                        missing.DefaultBranch = "main";
                        missing.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                        missing = await testDb.Driver.Vessels.CreateAsync(missing).ConfigureAwait(false);

                        BranchCleanupSweepService service = new BranchCleanupSweepService(
                            logging, testDb.Driver, new ArmadaSettings(), new GitService(logging));

                        // The first run removes the merged branch; the second has nothing left to remove.
                        await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);
                        lock (gate)
                        {
                            messages.Clear();
                        }
                        await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);
                        await logging.FlushAsync(CancellationToken.None).ConfigureAwait(false);

                        List<string> snapshot;
                        lock (gate)
                        {
                            snapshot = new List<string>(messages);
                        }
                        AssertTrue(snapshot.Any(m => m.Contains("sweep complete")), "a sweep that removed nothing must still log its summary line");
                        AssertTrue(snapshot.Any(m => m.Contains(none.Id) && m.Contains("policy None")), "a vessel skipped for policy None must be named with its reason");
                        AssertTrue(snapshot.Any(m => m.Contains(missing.Id) && m.Contains("no local repository")), "a vessel skipped for a missing repository must be named with its reason");
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            }).ConfigureAwait(false);
        }

        #region Private-Methods

        private const string LandedOld = "armada/claude-1/msn_landedold";
        private const string RemoteOnlyLanded = "armada/claude-1/msn_remoteonly";
        private const string RemoteOnlyUnlanded = "armada/claude-1/msn_remoteunlanded";
        private const string Unlanded = "armada/claude-1/msn_unlanded";
        private const string ActiveLanded = "armada/claude-1/msn_activelanded";
        private const string RecoverLanded = "recover/landed-work";
        private const string HumanLanded = "salvage/wave-b";
        private const string PreservedOldLanded = "refs/armada-preserved/armada/claude-1/msn_preservedold";
        private const string PreservedYoungLanded = "refs/armada-preserved/armada/claude-1/msn_preservedyoung";
        private const string PreservedUnlanded = "refs/armada-preserved/armada/claude-1/msn_preservedunlanded";
        private const string OldDate = "2020-01-01T00:00:00Z";

        private sealed class LandedFixture
        {
            public string Bare { get; set; } = String.Empty;

            public string Remote { get; set; } = String.Empty;

            public string Working { get; set; } = String.Empty;
        }

        /// <summary>
        /// Builds a vessel bare, an origin bare and a working clone of origin that together hold every
        /// ref shape the sweep must decide on: landed and unlanded mission branches on both sides,
        /// landed and unlanded branches that exist only on origin, a landed recover ref and a landed
        /// human branch, and preserved refs that are landed-and-old, landed-and-recent, and unlanded.
        /// </summary>
        private static async Task<LandedFixture> CreateLandedFixtureAsync(string rootDir)
        {
            string source = Path.Combine(rootDir, "source");
            string bare = Path.Combine(rootDir, "vessel.git");
            string remote = Path.Combine(rootDir, "origin.git");
            string working = Path.Combine(rootDir, "working");
            Directory.CreateDirectory(source);

            await RunGitAsync(source, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(source, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(source, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
            await CommitFileAsync(source, "base.txt", OldDate).ConfigureAwait(false);

            foreach (string landed in new string[] { LandedOld, RemoteOnlyLanded, ActiveLanded, RecoverLanded, HumanLanded })
            {
                await RunGitAsync(source, "checkout", "-b", landed, "main").ConfigureAwait(false);
                await CommitFileAsync(source, landed.Replace('/', '_') + ".txt", OldDate).ConfigureAwait(false);
                await RunGitAsync(source, "checkout", "main").ConfigureAwait(false);
                await RunGitDatedAsync(source, OldDate, "merge", "--no-ff", "-m", "land " + landed, landed).ConfigureAwait(false);
            }
            string oldLandedSha = await RunGitAsync(source, "rev-parse", LandedOld).ConfigureAwait(false);

            await RunGitAsync(source, "checkout", "-b", "tmp/young", "main").ConfigureAwait(false);
            await CommitFileAsync(source, "young.txt", null).ConfigureAwait(false);
            string youngSha = await RunGitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false);
            await RunGitAsync(source, "checkout", "main").ConfigureAwait(false);
            await RunGitAsync(source, "merge", "--no-ff", "-m", "land young", "tmp/young").ConfigureAwait(false);
            await RunGitAsync(source, "branch", "-D", "tmp/young").ConfigureAwait(false);

            foreach (string unlanded in new string[] { Unlanded, RemoteOnlyUnlanded })
            {
                await RunGitAsync(source, "checkout", "-b", unlanded, "main").ConfigureAwait(false);
                await CommitFileAsync(source, unlanded.Replace('/', '_') + ".txt", OldDate).ConfigureAwait(false);
                await RunGitAsync(source, "checkout", "main").ConfigureAwait(false);
            }

            await RunGitAsync(source, "checkout", "-b", "tmp/preserved-unlanded", "main").ConfigureAwait(false);
            await CommitFileAsync(source, "preserved-unlanded.txt", OldDate).ConfigureAwait(false);
            string unlandedSha = await RunGitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false);
            await RunGitAsync(source, "checkout", "main").ConfigureAwait(false);

            await RunGitAsync(source, "update-ref", PreservedOldLanded, oldLandedSha).ConfigureAwait(false);
            await RunGitAsync(source, "update-ref", PreservedYoungLanded, youngSha).ConfigureAwait(false);
            await RunGitAsync(source, "update-ref", PreservedUnlanded, unlandedSha).ConfigureAwait(false);
            await RunGitAsync(source, "branch", "-D", "tmp/preserved-unlanded").ConfigureAwait(false);

            // Mirror clones carry every ref family, including refs/armada-preserved/.
            await RunGitAsync(rootDir, "clone", "--mirror", source, bare).ConfigureAwait(false);
            await RunGitAsync(rootDir, "clone", "--mirror", source, remote).ConfigureAwait(false);
            await RunGitAsync(rootDir, "clone", remote, working).ConfigureAwait(false);

            // These two exist only on origin: their bare copies were removed without the origin delete.
            await RunGitAsync(bare, "update-ref", "-d", "refs/heads/" + RemoteOnlyLanded).ConfigureAwait(false);
            await RunGitAsync(bare, "update-ref", "-d", "refs/heads/" + RemoteOnlyUnlanded).ConfigureAwait(false);

            return new LandedFixture { Bare = bare, Remote = remote, Working = working };
        }

        private static async Task<Vessel> CreateFixtureVesselAsync(TestDatabase testDb, LandedFixture fx, string name, BranchCleanupPolicyEnum policy)
        {
            Vessel vessel = new Vessel(name, "https://github.com/test/sweep.git");
            vessel.LocalPath = fx.Bare;
            vessel.WorkingDirectory = fx.Working;
            vessel.DefaultBranch = "main";
            vessel.BranchCleanupPolicy = policy;
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task CommitFileAsync(string repo, string fileName, string? date)
        {
            await File.WriteAllTextAsync(Path.Combine(repo, fileName), fileName + "\n").ConfigureAwait(false);
            await RunGitAsync(repo, "add", fileName).ConfigureAwait(false);
            if (date == null)
            {
                await RunGitAsync(repo, "commit", "-m", "add " + fileName).ConfigureAwait(false);
            }
            else
            {
                await RunGitDatedAsync(repo, date, "commit", "-m", "add " + fileName).ConfigureAwait(false);
            }
        }

        private static async Task<HashSet<string>> ListRefsAsync(string repo)
        {
            string output = await RunGitAsync(repo, "for-each-ref", "--format=%(refname)").ConfigureAwait(false);
            return new HashSet<string>(output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()), StringComparer.Ordinal);
        }

        private static Task<string> RunGitDatedAsync(string workingDirectory, string date, params string[] args)
        {
            return RunGitCoreAsync(workingDirectory, date, args);
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static string NewTempDir()
        {
            return Path.Combine(Path.GetTempPath(), "armada_sweep_" + Guid.NewGuid().ToString("N"));
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch
            {
            }
        }

        private sealed class SweepRepo
        {
            public string Repo { get; set; } = String.Empty;

            public string Working { get; set; } = String.Empty;

            public string? Remote { get; set; } = null;
        }

        /// <summary>
        /// Create a repository with a merged armada branch and an unmerged armada branch. When
        /// <paramref name="remote"/> is true, also creates a remote bare, pushes both branches, and
        /// clones a working checkout whose origin points at the remote. When
        /// <paramref name="extraNamespaces"/> is true, also creates a MERGED branch in the
        /// armada-landing namespace and a human branch, so a test can prove the sweep covers the
        /// second Armada namespace without reaching branches Armada does not own.
        /// </summary>
        private static async Task<SweepRepo> CreateSweepRepoAsync(string rootDir, bool remote = false, bool extraNamespaces = false)
        {
            string repo = Path.Combine(rootDir, "bare.git");
            string working = Path.Combine(rootDir, "working");
            string? remoteBare = remote ? Path.Combine(rootDir, "remote.git") : null;

            // Source repo to build history, then clone it bare (the vessel's LocalPath).
            string source = Path.Combine(rootDir, "source");
            Directory.CreateDirectory(source);
            await RunGitAsync(source, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(source, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(source, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "base\n").ConfigureAwait(false);
            await RunGitAsync(source, "add", "file.txt").ConfigureAwait(false);
            await RunGitAsync(source, "commit", "-m", "base").ConfigureAwait(false);
            string baseSha = await RunGitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false);

            // Merged branch: work, merge back into main, delete the branch.
            await RunGitAsync(source, "checkout", "-b", "armada/claude-1/msn_merged001").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(source, "merged.txt"), "merged work\n").ConfigureAwait(false);
            await RunGitAsync(source, "add", "merged.txt").ConfigureAwait(false);
            await RunGitAsync(source, "commit", "-m", "merged work").ConfigureAwait(false);
            await RunGitAsync(source, "checkout", "main").ConfigureAwait(false);
            await RunGitAsync(source, "merge", "--no-ff", "-m", "merge merged branch", "armada/claude-1/msn_merged001").ConfigureAwait(false);

            if (extraNamespaces)
            {
                // Landing branch, MERGED: this is the namespace an "armada/" prefix filter misses.
                await RunGitAsync(source, "checkout", "main").ConfigureAwait(false);
                await RunGitAsync(source, "checkout", "-b", "armada-landing/armada/claude-1/msn_landed001").ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(source, "landed.txt"), "landed work\n").ConfigureAwait(false);
                await RunGitAsync(source, "add", "landed.txt").ConfigureAwait(false);
                await RunGitAsync(source, "commit", "-m", "landed work").ConfigureAwait(false);
                await RunGitAsync(source, "checkout", "main").ConfigureAwait(false);
                await RunGitAsync(source, "merge", "--no-ff", "-m", "merge landing branch", "armada-landing/armada/claude-1/msn_landed001").ConfigureAwait(false);

                // Human branch, MERGED: eligible on ancestry, ineligible on ownership. The sweep must
                // leave it alone, or a maintenance job silently deletes a person's branch.
                await RunGitAsync(source, "checkout", "-b", "salvage/wave-a").ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(source, "salvage.txt"), "salvage work\n").ConfigureAwait(false);
                await RunGitAsync(source, "add", "salvage.txt").ConfigureAwait(false);
                await RunGitAsync(source, "commit", "-m", "salvage work").ConfigureAwait(false);
                await RunGitAsync(source, "checkout", "main").ConfigureAwait(false);
                await RunGitAsync(source, "merge", "--no-ff", "-m", "merge salvage branch", "salvage/wave-a").ConfigureAwait(false);
            }

            // Unmerged branch: work, do not merge.
            await RunGitAsync(source, "checkout", "-b", "armada/claude-1/msn_unmerged001").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(source, "unmerged.txt"), "unmerged work\n").ConfigureAwait(false);
            await RunGitAsync(source, "add", "unmerged.txt").ConfigureAwait(false);
            await RunGitAsync(source, "commit", "-m", "unmerged work").ConfigureAwait(false);

            if (remoteBare != null)
            {
                // A bare clone carries every local branch, so the remote and the vessel bare both
                // already hold the merged and unmerged armada branches at their real tips.
                await RunGitAsync(source, "clone", "--bare", source, repo).ConfigureAwait(false);
                await RunGitAsync(source, "clone", "--bare", source, remoteBare).ConfigureAwait(false);
                await RunGitAsync(source, "clone", remoteBare, working).ConfigureAwait(false);
                await RunGitAsync(working, "fetch", "origin").ConfigureAwait(false);
            }
            else
            {
                await RunGitAsync(source, "clone", "--bare", source, repo).ConfigureAwait(false);
                Directory.CreateDirectory(working);
            }

            return new SweepRepo { Repo = repo, Working = working, Remote = remoteBare };
        }

        private static Task<string> RunGitAsync(string workingDirectory, params string[] args)
        {
            return RunGitCoreAsync(workingDirectory, null, args);
        }

        private static async Task<string> RunGitCoreAsync(string workingDirectory, string? date, string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (date != null)
            {
                startInfo.EnvironmentVariables["GIT_AUTHOR_DATE"] = date;
                startInfo.EnvironmentVariables["GIT_COMMITTER_DATE"] = date;
            }

            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("git failed (exit " + process.ExitCode + "): " + stderr.Trim());
                }

                return stdout.Trim();
            }
        }

        #endregion
    }
}
