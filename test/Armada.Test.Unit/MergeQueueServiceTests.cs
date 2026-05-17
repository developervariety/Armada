namespace Armada.Test.Unit
{
    using System.Diagnostics;
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
    /// Tests for <see cref="MergeQueueService"/> code-index refresh hooks after a land.
    /// </summary>
    public sealed class MergeQueueServiceTests : TestSuite
    {
        public override string Name => "Merge Queue Service";

        protected override async Task RunTestsAsync()
        {
            await RunTest("LandEntry_FiresIndexRefresh_WhenVesselIdSet", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_idx_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);
                        RecordingCodeIndexService recordingIndex = new RecordingCodeIndexService();

                        Vessel vessel = new Vessel("mqidx-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.BranchName = repos.CaptainBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(
                            logging,
                            testDb.Driver,
                            settings,
                            git,
                            new MergeFailureClassifier(),
                            null,
                            recordingIndex);

                        MergeEntry? afterProcess = await service.ProcessSingleAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(afterProcess, "Entry after process");
                        AssertEqual(MergeStatusEnum.Landed, afterProcess!.Status, "Expected Landed merge entry");

                        await WaitUntilAsync(() => recordingIndex.HasUpdateForVessel(vessel.Id)).ConfigureAwait(false);
                        AssertTrue(recordingIndex.HasUpdateForVessel(vessel.Id), "Code index refresh should observe vessel id");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { /* best-effort */ }
                }
            });

            await RunTest("LandEntry_DoesNotThrow_WhenCodeIndexServiceNull", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_idx_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("mqidx-null-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.BranchName = repos.CaptainBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(
                            logging,
                            testDb.Driver,
                            settings,
                            git,
                            new MergeFailureClassifier());

                        MergeEntry? afterProcess = await service.ProcessSingleAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(afterProcess, "Entry after process");
                        AssertEqual(MergeStatusEnum.Landed, afterProcess!.Status, "Expected Landed without code index service");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { /* best-effort */ }
                }
            });
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_mq_idx_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_mq_idx_repos_" + Guid.NewGuid().ToString("N"));
            settings.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
            return settings;
        }

        private static async Task WaitUntilAsync(Func<bool> condition, int maxAttempts = 300, int delayMs = 100)
        {
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (condition()) return;
                await Task.Delay(delayMs).ConfigureAwait(false);
            }
            throw new InvalidOperationException("Timed out waiting for background code-index refresh");
        }

        private static async Task<GitRepoSetup> CreateGitSetupAsync(string rootDir, string captainFilePath = "feature.txt")
        {
            string remoteDir = Path.Combine(rootDir, "remote.git");
            string sourceDir = Path.Combine(rootDir, "source");
            string bareDir = Path.Combine(rootDir, "bare.git");
            string workingDir = Path.Combine(rootDir, "working");

            Directory.CreateDirectory(sourceDir);
            await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "receive.denyCurrentBranch", "ignore").ConfigureAwait(false);

            await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "# test\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

            string captainBranch = "armada/captain-1/msn_idx001";
            await RunGitAsync(sourceDir, "checkout", "-b", captainBranch).ConfigureAwait(false);
            string captainFileAbsolutePath = Path.Combine(sourceDir, captainFilePath);
            string? captainFileDirectory = Path.GetDirectoryName(captainFileAbsolutePath);
            if (!String.IsNullOrEmpty(captainFileDirectory))
            {
                Directory.CreateDirectory(captainFileDirectory);
            }
            await File.WriteAllTextAsync(captainFileAbsolutePath, "feature\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", captainFilePath).ConfigureAwait(false);
            await RunGitAsync(sourceDir, "commit", "-m", "Add feature").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "checkout", "main").ConfigureAwait(false);

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

                return stdout;
            }
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
