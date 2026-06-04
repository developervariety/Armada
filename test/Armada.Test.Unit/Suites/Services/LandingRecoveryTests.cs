namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for startup recovery of landing jobs left mid-flight by a previous admiral process.
    /// </summary>
    public class LandingRecoveryTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Landing Recovery";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Recovery_WhenEntryPersistedMidMerging_RecoversToLanded", async () =>
            {
                string rootDir = NewTempDirectory("armada_landing_recover_merging_");
                try
                {
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, conflict: false).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings(rootDir);
                        GitService git = new GitService(logging);
                        MergeEntry entry = await CreateEntryAsync(testDb.Driver, repos, "recover-merging").ConfigureAwait(false);

                        // Advance one durable step so the entry is persisted mid-land (Merging).
                        MergeQueueService before = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await before.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Merging).ConfigureAwait(false);

                        // Simulate an admiral restart with a fresh service instance and recover.
                        MergeQueueService afterRestart = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        int recovered = await afterRestart.RecoverInFlightLandingsAsync().ConfigureAwait(false);

                        AssertEqual(1, recovered, "Recovery should report the single in-flight entry");
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Landed).ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            });

            await RunTest("Recovery_WhenEntryAlreadyLanded_RemainsLandedAndIsNoOp", async () =>
            {
                string rootDir = NewTempDirectory("armada_landing_recover_landed_");
                try
                {
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, conflict: false).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings(rootDir);
                        GitService git = new GitService(logging);
                        MergeEntry entry = await CreateEntryAsync(testDb.Driver, repos, "recover-landed").ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        for (int i = 0; i < 6; i++)
                        {
                            await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        }
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Landed).ConfigureAwait(false);

                        int recovered = await service.RecoverInFlightLandingsAsync().ConfigureAwait(false);

                        AssertEqual(0, recovered, "A terminal Landed entry must not be recovered");
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Landed).ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            });

            await RunTest("Recovery_WhenEntryAlreadyFailed_RemainsFailedAndIsNoOp", async () =>
            {
                string rootDir = NewTempDirectory("armada_landing_recover_failed_");
                try
                {
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, conflict: true).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings(rootDir);
                        GitService git = new GitService(logging);
                        MergeEntry entry = await CreateEntryAsync(testDb.Driver, repos, "recover-failed").ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Failed).ConfigureAwait(false);

                        int recovered = await service.RecoverInFlightLandingsAsync().ConfigureAwait(false);

                        AssertEqual(0, recovered, "A terminal Failed entry must not be recovered");
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Failed).ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            });

            await RunTest("Recovery_WhenInFlightEntryHasConflict_RecoversToFailed", async () =>
            {
                // A mid-flight entry whose merge conflicts must recover to a CLEAN FAIL,
                // not be left stranded in an in-flight state. Covers acceptance (b)'s
                // "clean fail" half: the worker tested resume-to-Landed and already-Failed
                // no-op, but never an in-flight entry that fails DURING recovery.
                string rootDir = NewTempDirectory("armada_landing_recover_inflight_conflict_");
                try
                {
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, conflict: true).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings(rootDir);
                        GitService git = new GitService(logging);
                        string vesselId = await CreateVesselAsync(testDb.Driver, repos, "recover-inflight-conflict").ConfigureAwait(false);
                        MergeEntry entry = await CreateEntryForVesselAsync(testDb.Driver, vesselId, repos.CaptainBranch, MergeStatusEnum.Merging, DateTime.UtcNow).ConfigureAwait(false);

                        MergeQueueService afterRestart = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        int recovered = await afterRestart.RecoverInFlightLandingsAsync().ConfigureAwait(false);

                        AssertEqual(1, recovered, "Recovery should report the single in-flight entry it drove to a terminal state");
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Failed).ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            });

            await RunTest("Recovery_WhenVesselMissing_TransitionsInFlightEntryToFailed", async () =>
            {
                // An in-flight entry whose vessel row is gone cannot resolve a repo path.
                // Recovery must fail it deterministically (counted) rather than loop or
                // strand it. Exercises the repoPath == null branch + TransitionEntryToFailureAsync.
                string rootDir = NewTempDirectory("armada_landing_recover_no_vessel_");
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings(rootDir);
                        GitService git = new GitService(logging);
                        MergeEntry entry = await CreateEntryForVesselAsync(testDb.Driver, "vsl_does_not_exist", "armada/captain-1/msn_orphan", MergeStatusEnum.Merging, DateTime.UtcNow).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        int recovered = await service.RecoverInFlightLandingsAsync().ConfigureAwait(false);

                        AssertEqual(1, recovered, "An unresolvable in-flight entry is recovered by being failed");
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Failed).ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            });

            await RunTest("Recovery_WhenDuplicateInFlightSameVesselBranch_RecoversLeaderOnly", async () =>
            {
                // Two in-flight entries on the same vessel+target must not both land in a
                // single pass: only the leader is driven, the follower stays in-flight
                // (deferred to the steady-state reconciler), not stranded. Covers the
                // per-vessel:branch dedup (activeKeys) branch and the count semantics.
                string rootDir = NewTempDirectory("armada_landing_recover_dedup_");
                try
                {
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, conflict: false).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings(rootDir);
                        GitService git = new GitService(logging);
                        string vesselId = await CreateVesselAsync(testDb.Driver, repos, "recover-dedup").ConfigureAwait(false);
                        DateTime now = DateTime.UtcNow;
                        MergeEntry leader = await CreateEntryForVesselAsync(testDb.Driver, vesselId, repos.CaptainBranch, MergeStatusEnum.Merging, now.AddSeconds(-30)).ConfigureAwait(false);
                        MergeEntry follower = await CreateEntryForVesselAsync(testDb.Driver, vesselId, repos.CaptainBranch, MergeStatusEnum.Merging, now).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        int recovered = await service.RecoverInFlightLandingsAsync().ConfigureAwait(false);

                        AssertEqual(1, recovered, "Only the leader of a duplicate vessel:branch group is recovered per pass");
                        await AssertStatusAsync(testDb.Driver, leader.Id, MergeStatusEnum.Landed).ConfigureAwait(false);
                        await AssertStatusAsync(testDb.Driver, follower.Id, MergeStatusEnum.Merging).ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            });

            await RunTest("Recovery_WhenEntryQueued_NotRecoveredAndStaysQueued", async () =>
            {
                // A Queued entry has not begun landing, so startup recovery must ignore it
                // (unlike ReconcileLandingStateMachineAsync, which DOES include Queued).
                // The entry must be left untouched for the normal queue drain to pick up.
                string rootDir = NewTempDirectory("armada_landing_recover_queued_");
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings(rootDir);
                        GitService git = new GitService(logging);
                        MergeEntry entry = await CreateEntryForVesselAsync(testDb.Driver, "vsl_queued", "armada/captain-1/msn_queued", MergeStatusEnum.Queued, DateTime.UtcNow).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        int recovered = await service.RecoverInFlightLandingsAsync().ConfigureAwait(false);

                        AssertEqual(0, recovered, "A Queued entry is not an in-flight landing and must not be recovered");
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Queued).ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            });

            await RunTest("Recovery_WhenNoEntries_ReturnsZero", async () =>
            {
                // Boot with an empty merge queue: recovery is a clean no-op returning 0.
                string rootDir = NewTempDirectory("armada_landing_recover_empty_");
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings(rootDir);
                        GitService git = new GitService(logging);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        int recovered = await service.RecoverInFlightLandingsAsync().ConfigureAwait(false);

                        AssertEqual(0, recovered, "Recovery over an empty queue returns zero");
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            });
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ArmadaSettings CreateSettings(string rootDir)
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(rootDir, "docks");
            settings.ReposDirectory = Path.Combine(rootDir, "repos");
            settings.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
            return settings;
        }

        private static async Task<MergeEntry> CreateEntryAsync(SqliteDatabaseDriver db, GitRepoSetup repos, string vesselName)
        {
            string vesselId = await CreateVesselAsync(db, repos, vesselName).ConfigureAwait(false);
            return await CreateEntryForVesselAsync(db, vesselId, repos.CaptainBranch, MergeStatusEnum.Queued, DateTime.UtcNow).ConfigureAwait(false);
        }

        private static async Task<string> CreateVesselAsync(SqliteDatabaseDriver db, GitRepoSetup repos, string vesselName)
        {
            Vessel vessel = new Vessel(vesselName, repos.RemoteDir);
            vessel.LocalPath = repos.BareDir;
            vessel.WorkingDirectory = repos.WorkingDir;
            vessel.DefaultBranch = "main";
            vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
            await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);
            return vessel.Id;
        }

        private static async Task<MergeEntry> CreateEntryForVesselAsync(SqliteDatabaseDriver db, string vesselId, string branchName, MergeStatusEnum status, DateTime createdUtc)
        {
            MergeEntry entry = new MergeEntry();
            entry.VesselId = vesselId;
            entry.BranchName = branchName;
            entry.TargetBranch = "main";
            entry.Status = status;
            entry.CreatedUtc = createdUtc;
            entry.LastUpdateUtc = createdUtc;
            await db.MergeEntries.CreateAsync(entry).ConfigureAwait(false);
            return entry;
        }

        private async Task AssertStatusAsync(SqliteDatabaseDriver db, string entryId, MergeStatusEnum expected)
        {
            MergeEntry? updated = await db.MergeEntries.ReadAsync(entryId).ConfigureAwait(false);
            AssertNotNull(updated, "Entry should exist");
            AssertEqual(expected, updated!.Status, "Entry status should match");
        }

        private static async Task<GitRepoSetup> CreateGitSetupAsync(string rootDir, bool conflict)
        {
            string remoteDir = Path.Combine(rootDir, "remote.git");
            string sourceDir = Path.Combine(rootDir, "source");
            string bareDir = Path.Combine(rootDir, "bare.git");
            string workingDir = Path.Combine(rootDir, "working");
            string captainBranch = "armada/captain-1/msn_landing_recovery";

            Directory.CreateDirectory(sourceDir);
            await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            await File.WriteAllTextAsync(Path.Combine(sourceDir, "feature.txt"), "base\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", "feature.txt").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

            await RunGitAsync(sourceDir, "checkout", "-b", captainBranch).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "feature.txt"), conflict ? "captain\n" : "base\ncaptain\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", "feature.txt").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "commit", "-m", "Captain change").ConfigureAwait(false);

            await RunGitAsync(sourceDir, "checkout", "main").ConfigureAwait(false);
            if (conflict)
            {
                await File.WriteAllTextAsync(Path.Combine(sourceDir, "feature.txt"), "main\n").ConfigureAwait(false);
                await RunGitAsync(sourceDir, "add", "feature.txt").ConfigureAwait(false);
                await RunGitAsync(sourceDir, "commit", "-m", "Main change").ConfigureAwait(false);
            }

            await RunGitAsync(rootDir, "clone", "--bare", sourceDir, remoteDir).ConfigureAwait(false);
            await RunGitAsync(sourceDir, "remote", "add", "origin", remoteDir).ConfigureAwait(false);
            await RunGitAsync(sourceDir, "push", "origin", "main").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "push", "origin", captainBranch).ConfigureAwait(false);

            await RunGitAsync(rootDir, "clone", "--bare", remoteDir, bareDir).ConfigureAwait(false);
            await RunGitAsync(bareDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(bareDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            await RunGitAsync(rootDir, "clone", remoteDir, workingDir).ConfigureAwait(false);
            await RunGitAsync(workingDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(workingDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            return new GitRepoSetup(remoteDir, bareDir, workingDir, captainBranch);
        }

        private static async Task RunGitAsync(string workingDir, params string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("git " + String.Join(" ", args) + " failed: " + stderr);
                }
            }
        }

        private static string NewTempDirectory(string prefix)
        {
            string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDelete(string path)
        {
            try { Directory.Delete(path, true); } catch { }
        }

        private sealed class GitRepoSetup
        {
            public string RemoteDir { get; }
            public string BareDir { get; }
            public string WorkingDir { get; }
            public string CaptainBranch { get; }

            public GitRepoSetup(string remoteDir, string bareDir, string workingDir, string captainBranch)
            {
                RemoteDir = remoteDir;
                BareDir = bareDir;
                WorkingDir = workingDir;
                CaptainBranch = captainBranch;
            }
        }
    }
}
