namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
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

            await RunTest("A diverged checkout is preserved under a recover branch with one event and one incident", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_divergence_" + Guid.NewGuid().ToString("N"));
                try
                {
                    RealGitVessel repos = await CreateRealGitVesselAsync(rootDir).ConfigureAwait(false);

                    // A commit that exists only in the configured checkout. Landing then advances the
                    // landing repository past it, so the fast-forward sync can never succeed.
                    await File.WriteAllTextAsync(Path.Combine(repos.WorkingDir, "local-only.txt"), "local\n").ConfigureAwait(false);
                    await RunGitAsync(repos.WorkingDir, "add", "local-only.txt").ConfigureAwait(false);
                    await RunGitAsync(repos.WorkingDir, "commit", "-m", "Checkout-only commit").ConfigureAwait(false);
                    string checkoutHead = (await RunGitAsync(repos.WorkingDir, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    string recoverBranch = "recover/working-checkout-" + checkoutHead.Substring(0, 12);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateRealVesselRecordAsync(testDb, repos).ConfigureAwait(false);
                        LandingService landing = BuildRealLandingService(testDb, repos);

                        Mission first = await CreateRealMissionAsync(testDb, vessel, repos.FirstBranch, repos.FirstHead).ConfigureAwait(false);
                        bool firstResult = await landing.MergeInDedicatedWorktreeAsync(vessel, first, "main").ConfigureAwait(false);

                        List<ArmadaEvent> eventsAfterFirst = await testDb.Driver.Events.EnumerateByTypeAsync("landing.working_checkout_diverged").ConfigureAwait(false);
                        List<Incident> incidentsAfterFirst = await ReadVesselIncidentsAsync(testDb, vessel).ConfigureAwait(false);
                        string observed = "observed reason=" + (first.FailureReason ?? "(none)")
                            + "; events=" + eventsAfterFirst.Count + "; incidents=" + incidentsAfterFirst.Count;

                        AssertTrue(firstResult, "The work landed; " + observed);
                        string? recoverTip = await TryRevParseAsync(repos.BareDir, "refs/heads/" + recoverBranch).ConfigureAwait(false);
                        AssertEqual(checkoutHead, recoverTip ?? "(missing)", "The recover branch must hold the checkout HEAD in the landing repository; " + observed);
                        AssertEqual(1, eventsAfterFirst.Count, "Divergence must emit one named event; " + observed);
                        AssertEqual(1, incidentsAfterFirst.Count, "Divergence must open one incident; " + observed);
                        string notes = incidentsAfterFirst[0].RecoveryNotes ?? String.Empty;
                        AssertContains(recoverBranch, notes, "The incident must name the recover branch");
                        AssertContains(checkoutHead, notes, "The incident must name the full checkout SHA");
                        AssertContains("Commits only in the checkout: 1", notes, "The incident must count the checkout-only commits");
                        AssertContains(recoverBranch, first.FailureReason, "The mission reason must point at the recover branch");

                        Mission second = await CreateRealMissionAsync(testDb, vessel, repos.SecondBranch, repos.SecondHead).ConfigureAwait(false);
                        bool secondResult = await landing.MergeInDedicatedWorktreeAsync(vessel, second, "main").ConfigureAwait(false);

                        AssertTrue(secondResult, "The second landing still lands; reason=" + (second.FailureReason ?? "(none)"));
                        AssertEqual(1, (await testDb.Driver.Events.EnumerateByTypeAsync("landing.working_checkout_diverged").ConfigureAwait(false)).Count,
                            "A second sync of the same divergence must not emit another event");
                        AssertEqual(1, (await ReadVesselIncidentsAsync(testDb, vessel).ConfigureAwait(false)).Count,
                            "A second sync of the same divergence must not open another incident");

                        string headAfter = (await RunGitAsync(repos.WorkingDir, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                        AssertEqual(checkoutHead, headAfter, "The checkout HEAD must never move");
                        string status = (await RunGitAsync(repos.WorkingDir, "status", "--porcelain").ConfigureAwait(false)).Trim();
                        AssertEqual(String.Empty, status, "The checkout tree must never be modified");
                        AssertTrue(File.Exists(Path.Combine(repos.WorkingDir, "local-only.txt")), "Checkout-only work must survive");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch (Exception ex) { Console.WriteLine("cleanup of " + rootDir + " failed: " + ex.Message); }
                }
            });

            await RunTest("A checkout in step with the landing repository opens no incident", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_nodivergence_" + Guid.NewGuid().ToString("N"));
                try
                {
                    RealGitVessel repos = await CreateRealGitVesselAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateRealVesselRecordAsync(testDb, repos).ConfigureAwait(false);
                        LandingService landing = BuildRealLandingService(testDb, repos);
                        Mission mission = await CreateRealMissionAsync(testDb, vessel, repos.FirstBranch, repos.FirstHead).ConfigureAwait(false);

                        bool result = await landing.MergeInDedicatedWorktreeAsync(vessel, mission, "main").ConfigureAwait(false);

                        AssertTrue(result, "The landing must succeed; reason=" + (mission.FailureReason ?? "(none)"));
                        AssertTrue(String.IsNullOrEmpty(mission.FailureReason), "A clean sync records no reason, got: " + mission.FailureReason);
                        string bareMain = (await RunGitAsync(repos.BareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();
                        string checkoutHead = (await RunGitAsync(repos.WorkingDir, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                        AssertEqual(bareMain, checkoutHead, "The checkout must fast-forward to the landed tip");
                        string recoverRefs = (await RunGitAsync(repos.BareDir, "for-each-ref", "refs/heads/recover").ConfigureAwait(false)).Trim();
                        AssertEqual(String.Empty, recoverRefs, "No recover branch may be created");
                        AssertEqual(0, (await testDb.Driver.Events.EnumerateByTypeAsync("landing.working_checkout_diverged").ConfigureAwait(false)).Count, "No divergence event");
                        AssertEqual(0, (await ReadVesselIncidentsAsync(testDb, vessel).ConfigureAwait(false)).Count, "No divergence incident");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch (Exception ex) { Console.WriteLine("cleanup of " + rootDir + " failed: " + ex.Message); }
                }
            });

            await RunTest("A checkout whose extra commits cannot be counted opens an incident that says so", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (Vessel vessel, Mission mission) = await SeedAsync(testDb).ConfigureAwait(false);

                    // The stub answers no commit count, so the preservation cannot decide what to push.
                    StubGitService git = new StubGitService
                    {
                        IsWorkingDirectoryCleanResult = true,
                        CurrentBranchResult = vessel.DefaultBranch,
                        IsAncestorResult = true
                    };
                    git.RevisionShas[vessel.LocalPath + "|" + vessel.DefaultBranch] = "landingtip";
                    git.RevisionShas[vessel.WorkingDirectory + "|HEAD"] = "checkoutlocalonly";
                    git.RevisionCommitShas[vessel.WorkingDirectory + "|HEAD"] = new string('d', 40);

                    LandingService landing = BuildLandingService(testDb, git);
                    bool result = await landing.MergeInDedicatedWorktreeAsync(vessel, mission, vessel.DefaultBranch!).ConfigureAwait(false);

                    AssertTrue(result, "The work landed");
                    List<Incident> incidents = await ReadVesselIncidentsAsync(testDb, vessel).ConfigureAwait(false);
                    AssertEqual(1, incidents.Count, "An unreadable checkout must still open an incident; reason=" + (mission.FailureReason ?? "(none)"));
                    AssertContains("could not be counted", incidents[0].RootCause, "The incident must name why nothing was preserved");
                    AssertContains("could not be counted", mission.FailureReason, "The mission reason must name why nothing was preserved");
                }
            });
        }

        private sealed class RealGitVessel
        {
            public string BareDir { get; set; } = String.Empty;
            public string WorkingDir { get; set; } = String.Empty;
            public string DocksDir { get; set; } = String.Empty;
            public string FirstBranch { get; set; } = "armada/worker/msn-first";
            public string FirstHead { get; set; } = String.Empty;
            public string SecondBranch { get; set; } = "armada/worker/msn-second";
            public string SecondHead { get; set; } = String.Empty;
        }

        /// <summary>
        /// Builds a landing repository (bare) holding two captain branches, and a separate working
        /// clone of it on main.
        /// </summary>
        private static async Task<RealGitVessel> CreateRealGitVesselAsync(string rootDir)
        {
            RealGitVessel repos = new RealGitVessel();
            string sourceDir = Path.Combine(rootDir, "source");
            repos.BareDir = Path.Combine(rootDir, "bare.git");
            repos.WorkingDir = Path.Combine(rootDir, "work");
            repos.DocksDir = Path.Combine(rootDir, "docks");
            Directory.CreateDirectory(sourceDir);

            await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
            await ConfigureIdentityAsync(sourceDir).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "# test\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

            repos.FirstHead = await CommitOnBranchAsync(sourceDir, repos.FirstBranch, "first.txt").ConfigureAwait(false);
            repos.SecondHead = await CommitOnBranchAsync(sourceDir, repos.SecondBranch, "second.txt").ConfigureAwait(false);

            await RunGitAsync(rootDir, "clone", "--bare", sourceDir, repos.BareDir).ConfigureAwait(false);
            await ConfigureIdentityAsync(repos.BareDir).ConfigureAwait(false);
            await RunGitAsync(rootDir, "clone", repos.BareDir, repos.WorkingDir).ConfigureAwait(false);
            await ConfigureIdentityAsync(repos.WorkingDir).ConfigureAwait(false);
            return repos;
        }

        private static async Task<string> CommitOnBranchAsync(string repoDir, string branch, string fileName)
        {
            await RunGitAsync(repoDir, "checkout", "-b", branch, "main").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(repoDir, fileName), fileName + "\n").ConfigureAwait(false);
            await RunGitAsync(repoDir, "add", fileName).ConfigureAwait(false);
            await RunGitAsync(repoDir, "commit", "-m", "Add " + fileName).ConfigureAwait(false);
            string head = (await RunGitAsync(repoDir, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
            await RunGitAsync(repoDir, "checkout", "main").ConfigureAwait(false);
            return head;
        }

        private static async Task ConfigureIdentityAsync(string repoDir)
        {
            await RunGitAsync(repoDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(repoDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
        }

        private static async Task<Vessel> CreateRealVesselRecordAsync(TestDatabase testDb, RealGitVessel repos)
        {
            Vessel vessel = new Vessel("landing-divergence", repos.BareDir);
            vessel.LocalPath = repos.BareDir;
            vessel.WorkingDirectory = repos.WorkingDir;
            vessel.DefaultBranch = "main";
            vessel.LandingMode = LandingModeEnum.LocalMerge;
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static LandingService BuildRealLandingService(TestDatabase testDb, RealGitVessel repos)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = repos.DocksDir;
            return new LandingService(logging, testDb.Driver, settings, new GitService(logging));
        }

        private static async Task<Mission> CreateRealMissionAsync(TestDatabase testDb, Vessel vessel, string branch, string head)
        {
            Mission mission = new Mission("[Worker] " + branch, "work");
            mission.VesselId = vessel.Id;
            mission.BranchName = branch;
            mission.CommitHash = head;
            mission.Status = MissionStatusEnum.WorkProduced;
            return await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task<List<Incident>> ReadVesselIncidentsAsync(TestDatabase testDb, Vessel vessel)
        {
            IncidentService incidents = new IncidentService(testDb.Driver);
            AuthContext auth = AuthContext.Authenticated(
                Constants.DefaultTenantId, Constants.DefaultUserId, isAdmin: false, isTenantAdmin: true, authMethod: "System");
            EnumerationResult<Incident> page = await incidents.EnumerateAsync(auth, new IncidentQuery { VesselId = vessel.Id }).ConfigureAwait(false);
            return page.Objects;
        }

        private static async Task<string?> TryRevParseAsync(string repoDir, string revision)
        {
            try
            {
                return (await RunGitAsync(repoDir, "rev-parse", "--verify", revision).ConfigureAwait(false)).Trim();
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine("rev-parse " + revision + " in " + repoDir + " failed: " + ex.Message);
                return null;
            }
        }

        private static async Task<string> RunGitAsync(string workingDirectory, params string[] args)
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
            foreach (string arg in args) startInfo.ArgumentList.Add(arg);
            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().ConfigureAwait(false);
                string stdout = await stdoutTask.ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("git " + String.Join(" ", args) + " failed (exit " + process.ExitCode + "): " + stderr.Trim());
                return stdout;
            }
        }
    }
}
