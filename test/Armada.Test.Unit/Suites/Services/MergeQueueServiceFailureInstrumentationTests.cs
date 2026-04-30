namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading.Tasks;
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
    /// Integration tests for <see cref="MergeQueueService"/>'s fail-time
    /// auto-recovery instrumentation. Each test seeds a real local git
    /// scenario (working repo + bare clone) producing a deterministic merge
    /// failure, then runs <c>ProcessEntryByIdAsync</c> end-to-end and asserts
    /// the persisted entry carries the structured classification fields
    /// (<see cref="MergeEntry.MergeFailureClass"/>,
    /// <see cref="MergeEntry.ConflictedFiles"/>,
    /// <see cref="MergeEntry.MergeFailureSummary"/>) when its status flips to
    /// <see cref="MergeStatusEnum.Failed"/>.
    /// </summary>
    public class MergeQueueServiceFailureInstrumentationTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Merge Queue Failure Instrumentation";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_mq_inst_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_mq_inst_repos_" + Guid.NewGuid().ToString("N"));
            settings.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
            return settings;
        }

        /// <summary>Run all instrumentation tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await ProcessEntry_TextConflict_PersistsClassifiedFieldsBeforeFailedTransition();
            await ProcessEntry_CleanLandingPath_LeavesRecoveryFieldsNull();
        }

        private async Task ProcessEntry_TextConflict_PersistsClassifiedFieldsBeforeFailedTransition()
        {
            await RunTest("ProcessEntry_TextConflict_PersistsClassifiedFieldsBeforeFailedTransition", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_inst_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    ConflictRepoSetup repos = await CreateConflictRepoAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("inst-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.BranchName = repos.CaptainBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git);
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "Entry should still exist");
                        AssertEqual(MergeStatusEnum.Failed, updated!.Status, "Entry should be Failed for text-conflict scenario");
                        AssertNotNull(updated.MergeFailureClass, "MergeFailureClass should be set at fail-time");
                        AssertEqual((int)MergeFailureClass.TextConflict, (int)updated.MergeFailureClass!.Value, "Failure class should be TextConflict");
                        AssertNotNull(updated.ConflictedFiles, "ConflictedFiles JSON should be persisted for TextConflict");
                        AssertContains("conflict.txt", updated.ConflictedFiles!, "ConflictedFiles JSON should mention the conflicting file");
                        AssertNotNull(updated.MergeFailureSummary, "MergeFailureSummary should be set");
                        AssertContains("conflicted file", updated.MergeFailureSummary!, "Summary should describe the text-conflict shape");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });
        }

        private async Task ProcessEntry_CleanLandingPath_LeavesRecoveryFieldsNull()
        {
            await RunTest("ProcessEntry_CleanLandingPath_LeavesRecoveryFieldsNull", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_inst_clean_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    CleanRepoSetup repos = await CreateCleanRepoAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("inst-clean-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.BranchName = repos.CaptainBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git);
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "Entry should still exist");
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should be Landed for clean scenario");
                        AssertNull(updated.MergeFailureClass, "MergeFailureClass should remain null for non-failed entries");
                        AssertNull(updated.ConflictedFiles, "ConflictedFiles should remain null for non-failed entries");
                        AssertNull(updated.MergeFailureSummary, "MergeFailureSummary should remain null for non-failed entries");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });
        }

        /// <summary>
        /// Build a git scenario that deterministically produces a 3-way fold
        /// conflict on <c>conflict.txt</c>: main and a captain branch both
        /// edit the same line.
        /// </summary>
        private async Task<ConflictRepoSetup> CreateConflictRepoAsync(string rootDir)
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
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "conflict.txt"), "base line\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", ".").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

            string captainBranch = "armada/captain-inst/msn_test_conflict";
            await RunGitAsync(sourceDir, "checkout", "-b", captainBranch).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "conflict.txt"), "captain edit\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", "conflict.txt").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "commit", "-m", "Captain edit").ConfigureAwait(false);

            await RunGitAsync(sourceDir, "checkout", "main").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "conflict.txt"), "main edit\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", "conflict.txt").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "commit", "-m", "Main edit").ConfigureAwait(false);

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

            return new ConflictRepoSetup(remoteDir, bareDir, workingDir, captainBranch);
        }

        /// <summary>
        /// Build a git scenario where the captain branch can be merged cleanly
        /// (no conflicts, no diverging edits on the same file). Used to verify
        /// the recovery fields stay null on the success path.
        /// </summary>
        private async Task<CleanRepoSetup> CreateCleanRepoAsync(string rootDir)
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

            string captainBranch = "armada/captain-inst/msn_test_clean";
            await RunGitAsync(sourceDir, "checkout", "-b", captainBranch).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "feature.txt"), "feature\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", "feature.txt").ConfigureAwait(false);
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

            return new CleanRepoSetup(remoteDir, bareDir, workingDir, captainBranch);
        }

        private async Task RunGitAsync(string workingDir, params string[] args)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (string arg in args) psi.ArgumentList.Add(arg);

            using (Process process = new Process { StartInfo = psi })
            {
                process.Start();
                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("git " + String.Join(" ", args) + " failed (exit " + process.ExitCode + "): " + stderr.Trim());
                }
            }
        }

        private sealed class ConflictRepoSetup
        {
            public string RemoteDir { get; }
            public string BareDir { get; }
            public string WorkingDir { get; }
            public string CaptainBranch { get; }

            public ConflictRepoSetup(string remoteDir, string bareDir, string workingDir, string captainBranch)
            {
                RemoteDir = remoteDir;
                BareDir = bareDir;
                WorkingDir = workingDir;
                CaptainBranch = captainBranch;
            }
        }

        private sealed class CleanRepoSetup
        {
            public string RemoteDir { get; }
            public string BareDir { get; }
            public string WorkingDir { get; }
            public string CaptainBranch { get; }

            public CleanRepoSetup(string remoteDir, string bareDir, string workingDir, string captainBranch)
            {
                RemoteDir = remoteDir;
                BareDir = bareDir;
                WorkingDir = workingDir;
                CaptainBranch = captainBranch;
            }
        }
    }
}
