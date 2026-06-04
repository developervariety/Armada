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
