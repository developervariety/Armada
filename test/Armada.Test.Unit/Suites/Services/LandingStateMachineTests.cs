namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for durable merge-entry landing state-machine advancement.
    /// </summary>
    public class LandingStateMachineTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Landing State Machine";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Landing_WhenHealthCheckAdvancesSuccess_PersistsEachState", async () =>
            {
                string rootDir = NewTempDirectory("armada_landing_sm_success_");
                try
                {
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, conflict: false).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings(rootDir);
                        GitService git = new GitService(logging);
                        MergeEntry entry = await CreateEntryAsync(testDb.Driver, repos, "state-success").ConfigureAwait(false);
                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());

                        await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Merging).ConfigureAwait(false);

                        await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Testing).ConfigureAwait(false);

                        await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Passed).ConfigureAwait(false);

                        await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Pushing).ConfigureAwait(false);

                        await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Landed).ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            });

            await RunTest("Landing_WhenServiceRestartsFromPersistedMerging_ResumesToLanded", async () =>
            {
                string rootDir = NewTempDirectory("armada_landing_sm_resume_");
                try
                {
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, conflict: false).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings(rootDir);
                        GitService git = new GitService(logging);
                        MergeEntry entry = await CreateEntryAsync(testDb.Driver, repos, "state-resume").ConfigureAwait(false);
                        MergeQueueService serviceBeforeRestart = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());

                        await serviceBeforeRestart.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Merging).ConfigureAwait(false);

                        MergeQueueService serviceAfterRestart = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        for (int i = 0; i < 4; i++)
                        {
                            await serviceAfterRestart.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        }

                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Landed).ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            });

            await RunTest("Landing_WhenPushingResumeFindsRemoteUpdated_DoesNotPushAgain", async () =>
            {
                string rootDir = NewTempDirectory("armada_landing_sm_push_");
                try
                {
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, conflict: false).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings(rootDir);
                        GitService realGit = new GitService(logging);
                        MergeEntry entry = await CreateEntryAsync(testDb.Driver, repos, "state-push").ConfigureAwait(false);
                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, realGit, new MergeFailureClassifier());

                        for (int i = 0; i < 4; i++)
                        {
                            await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        }
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Pushing).ConfigureAwait(false);

                        await RunGitAsync(repos.BareDir, "push", "origin", "armada/merge-queue/" + entry.Id + ":main").ConfigureAwait(false);

                        CountingGitService countingGit = new CountingGitService(realGit);
                        MergeQueueService resumed = new MergeQueueService(logging, testDb.Driver, settings, countingGit, new MergeFailureClassifier());
                        await resumed.ReconcileLandingStateMachineAsync().ConfigureAwait(false);

                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Landed).ConfigureAwait(false);
                        AssertEqual(0, countingGit.PushRefSpecCalls, "Pushing resume should verify remote state instead of pushing again");
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            });

            await RunTest("Landing_WhenMergeConflicts_MarksEntryFailed", async () =>
            {
                string rootDir = NewTempDirectory("armada_landing_sm_conflict_");
                try
                {
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, conflict: true).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings(rootDir);
                        GitService git = new GitService(logging);
                        MergeEntry entry = await CreateEntryAsync(testDb.Driver, repos, "state-conflict").ConfigureAwait(false);
                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());

                        await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "Entry should exist after conflict");
                        AssertEqual(MergeStatusEnum.Failed, updated!.Status, "Conflict should mark entry Failed");
                        AssertContains("Merge conflict", updated.TestOutput ?? "", "Failure output should identify merge conflict");
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            });

            await RunTest("Landing_WhenPushingResumeAndRemoteNotUpdated_PushesOnceAndLands", async () =>
            {
                string rootDir = NewTempDirectory("armada_landing_sm_push_real_");
                try
                {
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, conflict: false).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings(rootDir);
                        GitService realGit = new GitService(logging);
                        MergeEntry entry = await CreateEntryAsync(testDb.Driver, repos, "state-push-real").ConfigureAwait(false);
                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, realGit, new MergeFailureClassifier());

                        for (int i = 0; i < 4; i++)
                        {
                            await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        }
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Pushing).ConfigureAwait(false);

                        CountingGitService countingGit = new CountingGitService(realGit);
                        MergeQueueService resumed = new MergeQueueService(logging, testDb.Driver, settings, countingGit, new MergeFailureClassifier());
                        await resumed.ReconcileLandingStateMachineAsync().ConfigureAwait(false);

                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Landed).ConfigureAwait(false);
                        AssertEqual(1, countingGit.PushRefSpecCalls, "Pushing resume must push exactly once when remote is not yet at the integration head");
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            });

            await RunTest("Landing_WhenIntegrationWorktreeMissingDuringMerging_RebuildsAndLands", async () =>
            {
                string rootDir = NewTempDirectory("armada_landing_sm_wtloss_");
                try
                {
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, conflict: false).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings(rootDir);
                        GitService git = new GitService(logging);
                        MergeEntry entry = await CreateEntryAsync(testDb.Driver, repos, "state-wtloss").ConfigureAwait(false);
                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());

                        await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Merging).ConfigureAwait(false);

                        // Simulate a crash that lost the on-disk integration worktree while the
                        // persisted state still says Merging. The reconciler must rebuild it.
                        string integrationPath = Path.Combine(settings.DocksDirectory, "_merge-queue", entry.Id);
                        TryDelete(integrationPath);
                        AssertFalse(Directory.Exists(integrationPath), "Integration worktree should be deleted before resume");

                        MergeStatusEnum sawRebasing = MergeStatusEnum.Merging;
                        bool landed = false;
                        for (int i = 0; i < 8; i++)
                        {
                            await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                            MergeEntry? cur = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                            if (cur != null && cur.Status == MergeStatusEnum.Rebasing) sawRebasing = MergeStatusEnum.Rebasing;
                            if (cur != null && cur.Status == MergeStatusEnum.Landed) { landed = true; break; }
                        }

                        AssertEqual(MergeStatusEnum.Rebasing, sawRebasing, "Missing worktree should drive the entry back through Rebasing");
                        AssertTrue(landed, "Entry should rebuild the worktree and reach Landed after a lost worktree");
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            });

            await RunTest("Reconcile_WhenVesselUnresolvable_MarksEntryFailed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings(NewTempDirectory("armada_landing_sm_norepo_"));
                    GitService git = new GitService(logging);
                    MergeEntry entry = await CreateQueuedEntryAsync(testDb.Driver, "vsl_does_not_exist", "armada/captain-1/missing", "main").ConfigureAwait(false);
                    MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());

                    int advanced = await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);

                    AssertEqual(1, advanced, "Unresolvable repo path should still count as one advanced (terminal) entry");
                    MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Entry should exist after failure");
                    AssertEqual(MergeStatusEnum.Failed, updated!.Status, "Unresolvable vessel should mark entry Failed");
                    AssertContains("resolve repository path", updated.TestOutput ?? "", "Failure reason should mention repo path resolution");
                }
            });

            await RunTest("Reconcile_WhenMoreThanTenCandidates_AdvancesAtMostTenPerCycle", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings(NewTempDirectory("armada_landing_sm_ratelimit_"));
                    GitService git = new GitService(logging);

                    // 11 distinct (unresolvable) vessels => 11 distinct serialization keys, none deduped.
                    for (int i = 0; i < 11; i++)
                    {
                        await CreateQueuedEntryAsync(testDb.Driver, "vsl_fake_" + i, "armada/captain-1/branch-" + i, "main").ConfigureAwait(false);
                    }

                    MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());

                    int advanced = await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                    AssertEqual(10, advanced, "Reconciler must advance at most ten entries per cycle");

                    List<MergeEntry> failed = await testDb.Driver.MergeEntries.EnumerateByStatusAsync(MergeStatusEnum.Failed).ConfigureAwait(false);
                    List<MergeEntry> queued = await testDb.Driver.MergeEntries.EnumerateByStatusAsync(MergeStatusEnum.Queued).ConfigureAwait(false);
                    AssertEqual(10, failed.Count, "Exactly ten entries should be processed in the first cycle");
                    AssertEqual(1, queued.Count, "The eleventh entry must remain Queued for the next cycle");
                }
            });

            await RunTest("Reconcile_WhenQueueEmpty_ReturnsZero", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings(NewTempDirectory("armada_landing_sm_empty_"));
                    GitService git = new GitService(logging);
                    MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());

                    int advanced = await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                    AssertEqual(0, advanced, "An empty landing queue should advance nothing");
                }
            });

            await RunTest("Reconcile_WhenTwoEntriesShareVesselAndTarget_AdvancesOnlyOnePerCycle", async () =>
            {
                string rootDir = NewTempDirectory("armada_landing_sm_serialize_");
                try
                {
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, conflict: false).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings(rootDir);
                        GitService git = new GitService(logging);
                        Vessel vessel = await CreateVesselAsync(testDb.Driver, repos, "state-serialize").ConfigureAwait(false);
                        MergeEntry first = await CreateQueuedEntryAsync(testDb.Driver, vessel.Id, repos.CaptainBranch, "main").ConfigureAwait(false);
                        MergeEntry second = await CreateQueuedEntryAsync(testDb.Driver, vessel.Id, repos.CaptainBranch, "main").ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());

                        int advanced = await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        AssertEqual(1, advanced, "Entries sharing a vessel+target must be serialized to one advance per cycle");

                        MergeEntry? firstUpdated = await testDb.Driver.MergeEntries.ReadAsync(first.Id).ConfigureAwait(false);
                        MergeEntry? secondUpdated = await testDb.Driver.MergeEntries.ReadAsync(second.Id).ConfigureAwait(false);
                        AssertNotNull(firstUpdated, "First entry should exist");
                        AssertNotNull(secondUpdated, "Second entry should exist");
                        AssertEqual(MergeStatusEnum.Merging, firstUpdated!.Status, "First entry advances out of Queued");
                        AssertEqual(MergeStatusEnum.Queued, secondUpdated!.Status, "Second entry on the same key stays Queued this cycle");
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            });
        }

        private static async Task<Vessel> CreateVesselAsync(SqliteDatabaseDriver db, GitRepoSetup repos, string vesselName)
        {
            Vessel vessel = new Vessel(vesselName, repos.RemoteDir);
            vessel.LocalPath = repos.BareDir;
            vessel.WorkingDirectory = repos.WorkingDir;
            vessel.DefaultBranch = "main";
            vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
            await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);
            return vessel;
        }

        private static async Task<MergeEntry> CreateQueuedEntryAsync(SqliteDatabaseDriver db, string vesselId, string branchName, string targetBranch)
        {
            MergeEntry entry = new MergeEntry();
            entry.VesselId = vesselId;
            entry.BranchName = branchName;
            entry.TargetBranch = targetBranch;
            entry.Status = MergeStatusEnum.Queued;
            entry.CreatedUtc = DateTime.UtcNow;
            entry.LastUpdateUtc = DateTime.UtcNow;
            await db.MergeEntries.CreateAsync(entry).ConfigureAwait(false);
            return entry;
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
            Vessel vessel = new Vessel(vesselName, repos.RemoteDir);
            vessel.LocalPath = repos.BareDir;
            vessel.WorkingDirectory = repos.WorkingDir;
            vessel.DefaultBranch = "main";
            vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
            await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            MergeEntry entry = new MergeEntry();
            entry.VesselId = vessel.Id;
            entry.BranchName = repos.CaptainBranch;
            entry.TargetBranch = "main";
            entry.Status = MergeStatusEnum.Queued;
            entry.CreatedUtc = DateTime.UtcNow;
            entry.LastUpdateUtc = DateTime.UtcNow;
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
            string captainBranch = "armada/captain-1/msn_state_machine";

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

        private sealed class CountingGitService : IGitService
        {
            private readonly IGitService _Inner;

            public int PushRefSpecCalls { get; private set; }

            public CountingGitService(IGitService inner)
            {
                _Inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default) => _Inner.CloneBareAsync(repoUrl, localPath, token);
            public Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", CancellationToken token = default) => _Inner.CreateWorktreeAsync(repoPath, worktreePath, branchName, baseBranch, token);
            public Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default) => _Inner.RemoveWorktreeAsync(worktreePath, token);
            public Task FetchAsync(string repoPath, CancellationToken token = default) => _Inner.FetchAsync(repoPath, token);
            public Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default) => _Inner.PushBranchAsync(worktreePath, remoteName, token);
            public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default) => _Inner.CreatePullRequestAsync(worktreePath, title, body, token);
            public Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default) => _Inner.RepairWorktreeAsync(worktreePath, token);
            public Task<bool> IsRepositoryAsync(string path, CancellationToken token = default) => _Inner.IsRepositoryAsync(path, token);
            public Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => _Inner.DeleteLocalBranchAsync(repoPath, branchName, token);
            public Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default) => _Inner.DeleteRemoteBranchAsync(repoPath, branchName, token);
            public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default) => _Inner.PruneWorktreesAsync(repoPath, token);
            public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default) => _Inner.EnableAutoMergeAsync(worktreePath, prUrl, token);
            public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default) => _Inner.MergeBranchLocalAsync(targetWorkDir, sourceRepoPath, branchName, targetBranch, commitMessage, token);
            public Task PullAsync(string workingDirectory, CancellationToken token = default) => _Inner.PullAsync(workingDirectory, token);
            public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default) => _Inner.DiffAsync(worktreePath, baseBranch, token);
            public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default) => _Inner.GetHeadCommitHashAsync(worktreePath, token);
            public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default) => _Inner.GetChangedFilesSinceAsync(worktreePath, startCommit, token);
            public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) => _Inner.IsPrMergedAsync(workingDirectory, prUrl, token);
            public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default) => _Inner.BranchExistsAsync(repoPath, branchName, token);
            public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => _Inner.EnsureLocalBranchAsync(repoPath, branchName, token);
            public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) => _Inner.IsWorktreeRegisteredAsync(repoPath, worktreePath, token);

            public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default)
            {
                PushRefSpecCalls++;
                return _Inner.PushRefSpecAsync(repoPath, srcRef, destRef, token);
            }
        }
    }
}
