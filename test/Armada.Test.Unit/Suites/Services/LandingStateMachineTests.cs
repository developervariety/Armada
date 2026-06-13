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
                        await AssertLandingJobStateAsync(testDb.Driver, entry.Id, LandingJobStateEnum.Merging).ConfigureAwait(false);

                        await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Testing).ConfigureAwait(false);
                        await AssertLandingJobStateAsync(testDb.Driver, entry.Id, LandingJobStateEnum.Testing).ConfigureAwait(false);

                        await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Passed).ConfigureAwait(false);
                        await AssertLandingJobStateAsync(testDb.Driver, entry.Id, LandingJobStateEnum.Passed).ConfigureAwait(false);

                        await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Pushing).ConfigureAwait(false);
                        await AssertLandingJobStateAsync(testDb.Driver, entry.Id, LandingJobStateEnum.Pushing).ConfigureAwait(false);

                        await service.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Landed).ConfigureAwait(false);
                        await AssertLandingJobStateAsync(testDb.Driver, entry.Id, LandingJobStateEnum.Landed).ConfigureAwait(false);
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
                        await AssertLandingJobStateAsync(testDb.Driver, entry.Id, LandingJobStateEnum.Merging).ConfigureAwait(false);

                        MergeQueueService serviceAfterRestart = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        for (int i = 0; i < 4; i++)
                        {
                            await serviceAfterRestart.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        }

                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Landed).ConfigureAwait(false);
                        await AssertLandingJobStateAsync(testDb.Driver, entry.Id, LandingJobStateEnum.Landed).ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            });

            await RunTest("Landing_WhenStartupRecoveryRunsFromPersistedMerging_RecoversJobToLanded", async () =>
            {
                string rootDir = NewTempDirectory("armada_landing_sm_recover_");
                try
                {
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, conflict: false).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings(rootDir);
                        GitService git = new GitService(logging);
                        MergeEntry entry = await CreateEntryAsync(testDb.Driver, repos, "state-recover").ConfigureAwait(false);
                        MergeQueueService beforeRestart = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());

                        await beforeRestart.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Merging).ConfigureAwait(false);
                        await AssertLandingJobStateAsync(testDb.Driver, entry.Id, LandingJobStateEnum.Merging).ConfigureAwait(false);

                        MergeQueueService afterRestart = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        int recovered = await afterRestart.RecoverInFlightLandingsAsync().ConfigureAwait(false);

                        AssertEqual(1, recovered, "Startup recovery should recover one in-flight landing job");
                        await AssertStatusAsync(testDb.Driver, entry.Id, MergeStatusEnum.Landed).ConfigureAwait(false);
                        await AssertLandingJobStateAsync(testDb.Driver, entry.Id, LandingJobStateEnum.Landed).ConfigureAwait(false);
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
                        await AssertLandingJobStateAsync(testDb.Driver, entry.Id, LandingJobStateEnum.Landed).ConfigureAwait(false);
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
                        await AssertLandingJobStateAsync(testDb.Driver, entry.Id, LandingJobStateEnum.Failed).ConfigureAwait(false);
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

        private async Task AssertLandingJobStateAsync(SqliteDatabaseDriver db, string entryId, LandingJobStateEnum expected)
        {
            LandingJob? job = await db.LandingJobs.ReadByMergeEntryAsync(entryId).ConfigureAwait(false);
            AssertNotNull(job, "Landing job should exist");
            AssertEqual(expected, job!.State, "Landing job state should match");
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
            public Task PullFastForwardOnlyAsync(string workingDirectory, CancellationToken token = default) => _Inner.PullFastForwardOnlyAsync(workingDirectory, token);
            public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken token = default) => _Inner.GetCurrentBranchAsync(workingDirectory, token);
            public Task<bool> IsWorkingDirectoryCleanAsync(string workingDirectory, CancellationToken token = default) => _Inner.IsWorkingDirectoryCleanAsync(workingDirectory, token);
            public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default) => _Inner.DiffAsync(worktreePath, baseBranch, token);
            public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default) => _Inner.GetHeadCommitHashAsync(worktreePath, token);
            public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default) => _Inner.GetChangedFilesSinceAsync(worktreePath, startCommit, token);
            public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) => _Inner.IsPrMergedAsync(workingDirectory, prUrl, token);
            public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default) => _Inner.BranchExistsAsync(repoPath, branchName, token);
            public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => _Inner.EnsureLocalBranchAsync(repoPath, branchName, token);
            public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) => _Inner.IsWorktreeRegisteredAsync(repoPath, worktreePath, token);
            public Task SetHeadSymbolicRefAsync(string repoPath, string targetRef, CancellationToken token = default) => _Inner.SetHeadSymbolicRefAsync(repoPath, targetRef, token);

            public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default)
            {
                PushRefSpecCalls++;
                return _Inner.PushRefSpecAsync(repoPath, srcRef, destRef, token);
            }
        }
    }
}
