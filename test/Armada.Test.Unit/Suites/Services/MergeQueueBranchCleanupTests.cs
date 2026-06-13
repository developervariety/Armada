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
    /// Tests that verify BranchCleanupPolicy is honored by MergeQueueService.LandEntryAsync
    /// and DockService.DeleteAsync.
    /// </summary>
    public class MergeQueueBranchCleanupTests : TestSuite
    {
        public override string Name => "Merge Queue Branch Cleanup";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_mq_test_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_mq_test_repos_" + Guid.NewGuid().ToString("N"));
            settings.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
            return settings;
        }

        /// <summary>
        /// Set up a local bare repo with a main branch and a captain branch, returning
        /// paths to the remote bare repo, the local bare clone, and a regular working clone.
        /// The caller is responsible for cleanup.
        /// </summary>
        private async Task<GitRepoSetup> CreateGitSetupAsync(string rootDir, string captainFilePath = "feature.txt")
        {
            string remoteDir = Path.Combine(rootDir, "remote.git");
            string sourceDir = Path.Combine(rootDir, "source");
            string bareDir = Path.Combine(rootDir, "bare.git");
            string workingDir = Path.Combine(rootDir, "working");

            // Init source repo and create main + captain branch
            Directory.CreateDirectory(sourceDir);
            await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "receive.denyCurrentBranch", "ignore").ConfigureAwait(false);

            await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "# test\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

            string captainBranch = "armada/captain-1/msn_test001";
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

            // Create remote bare repo and push both branches
            await RunGitAsync(rootDir, "clone", "--bare", sourceDir, remoteDir).ConfigureAwait(false);
            await RunGitAsync(sourceDir, "remote", "add", "origin", remoteDir).ConfigureAwait(false);
            await RunGitAsync(sourceDir, "push", "origin", "main").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "push", "origin", captainBranch).ConfigureAwait(false);

            // Clone remote to local bare (this is the repo MergeQueueService uses)
            await RunGitAsync(rootDir, "clone", "--bare", remoteDir, bareDir).ConfigureAwait(false);
            await RunGitAsync(bareDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(bareDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            // Clone remote to working dir (vessel.WorkingDirectory used for remote branch deletion)
            await RunGitAsync(rootDir, "clone", remoteDir, workingDir).ConfigureAwait(false);
            await RunGitAsync(workingDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(workingDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            return new GitRepoSetup(remoteDir, bareDir, workingDir, captainBranch);
        }

        private async Task<bool> BranchExistsInRepoAsync(string repoPath, string branchName)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("branch");
            startInfo.ArgumentList.Add("--list");
            startInfo.ArgumentList.Add(branchName);

            using (Process proc = new Process { StartInfo = startInfo })
            {
                proc.Start();
                string output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);
                return output.Trim().Length > 0;
            }
        }

        /// <summary>
        /// Resolve a git ref to its current commit hash.
        /// </summary>
        private async Task<string> ResolveGitRefAsync(string repoPath, string refName)
        {
            string output = await RunGitAsync(repoPath, "rev-parse", "--verify", refName).ConfigureAwait(false);
            return output.Trim();
        }

        /// <summary>
        /// Install a server-side pre-receive hook on a bare repo that rejects any push
        /// updating the given branch, so a land-push can be forced to fail without
        /// advancing the remote target.
        /// </summary>
        private async Task InstallRejectBranchPushHookAsync(string bareRepoDir, string branchName)
        {
            string hooksDir = Path.Combine(bareRepoDir, "hooks");
            Directory.CreateDirectory(hooksDir);

            // LF line endings and no BOM so git's bundled shell parses the shebang.
            string hookBody =
                "#!/bin/sh\n" +
                "while read oldrev newrev refname; do\n" +
                "  if [ \"$refname\" = \"refs/heads/" + branchName + "\" ]; then\n" +
                "    echo \"pre-receive hook: pushes to " + branchName + " are rejected for this test\" 1>&2\n" +
                "    exit 1\n" +
                "  fi\n" +
                "done\n" +
                "exit 0\n";

            await File.WriteAllTextAsync(Path.Combine(hooksDir, "pre-receive"), hookBody, new System.Text.UTF8Encoding(false)).ConfigureAwait(false);
        }

        protected override async Task RunTestsAsync()
        {
            // === MergeQueueService Branch Cleanup Tests ===

            await RunTest("LandEntryAsync_LocalOnlyPolicy_DeletesCaptainBranchFromBareOnly", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_cleanup_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        settings.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("cleanup-vessel", repos.RemoteDir);
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

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "Entry should still exist");
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should be Landed");

                        bool captainInBare = await BranchExistsInRepoAsync(repos.BareDir, repos.CaptainBranch).ConfigureAwait(false);
                        AssertFalse(captainInBare, "Captain branch should be deleted from bare repo on LocalOnly policy");

                        bool captainInRemote = await BranchExistsInRepoAsync(repos.RemoteDir, repos.CaptainBranch).ConfigureAwait(false);
                        AssertTrue(captainInRemote, "Captain branch should be preserved on remote for LocalOnly policy");

                        List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.failed_target_advanced").ConfigureAwait(false);
                        AssertEqual(0, events.Count, "Clean landed entries should not emit failed target-advanced audit events");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_LocalAndRemotePolicy_DeletesCaptainBranchFromBareAndRemote", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_cleanup_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("cleanup-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalAndRemote;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.BranchName = repos.CaptainBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "Entry should still exist");
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should be Landed");

                        bool captainInBare = await BranchExistsInRepoAsync(repos.BareDir, repos.CaptainBranch).ConfigureAwait(false);
                        AssertFalse(captainInBare, "Captain branch should be deleted from bare repo");

                        bool captainInRemote = await BranchExistsInRepoAsync(repos.RemoteDir, repos.CaptainBranch).ConfigureAwait(false);
                        AssertFalse(captainInRemote, "Captain branch should be deleted from remote on LocalAndRemote policy");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("ProcessEntryByIdAsync_BuiltInProtectedBriefingPath_FailsBeforeLanding", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_protected_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, "_briefing/spec.md").ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("protected-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalAndRemote;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.BranchName = repos.CaptainBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "Entry should still exist");
                        AssertEqual(MergeStatusEnum.Failed, updated!.Status, "Entry should fail before landing protected briefing files");
                        AssertContains("_briefing/spec.md", updated.TestOutput ?? "", "Failure should name the protected briefing path");

                        string mainFiles = await RunGitAsync(repos.RemoteDir, "ls-tree", "-r", "--name-only", "main").ConfigureAwait(false);
                        AssertFalse(mainFiles.Contains("_briefing/spec.md"), "Protected briefing file should not land on main");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_GateFails_TargetHeadUnchanged", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_gate_fail_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("gate-fail-vessel", repos.RemoteDir);
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
                        entry.TestCommand = "exit 1";
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        string preRemoteHead = await ResolveGitRefAsync(repos.RemoteDir, "refs/heads/main").ConfigureAwait(false);
                        string preBareHead = await ResolveGitRefAsync(repos.BareDir, "refs/heads/main").ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "Entry should still exist");
                        AssertEqual(MergeStatusEnum.Failed, updated!.Status, "Entry should fail when the merge gate command fails");

                        string postRemoteHead = await ResolveGitRefAsync(repos.RemoteDir, "refs/heads/main").ConfigureAwait(false);
                        string postBareHead = await ResolveGitRefAsync(repos.BareDir, "refs/heads/main").ConfigureAwait(false);
                        AssertEqual(preRemoteHead, postRemoteHead, "Remote target branch should not move when tests fail before pushing");
                        AssertEqual(preBareHead, postBareHead, "Bare local target branch should not move when tests fail before pushing");

                        List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.failed_target_advanced").ConfigureAwait(false);
                        AssertEqual(0, events.Count, "Gate failure before push should not emit target-advanced audit events");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_PushSucceedsThenSyncFails_RollsBackTargetAndEmitsAuditEvent", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_push_sync_fail_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);
                    string checkedOutMainDir = Path.Combine(rootDir, "checked-out-main");
                    await RunGitAsync(repos.BareDir, "worktree", "add", checkedOutMainDir, "main").ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("sync-fail-vessel", repos.RemoteDir);
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

                        string preRemoteHead = await ResolveGitRefAsync(repos.RemoteDir, "refs/heads/main").ConfigureAwait(false);
                        string preBareHead = await ResolveGitRefAsync(repos.BareDir, "refs/heads/main").ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "Entry should still exist");
                        AssertEqual(MergeStatusEnum.Failed, updated!.Status, "Entry should fail when local target sync fails after push");

                        string postRemoteHead = await ResolveGitRefAsync(repos.RemoteDir, "refs/heads/main").ConfigureAwait(false);
                        string postBareHead = await ResolveGitRefAsync(repos.BareDir, "refs/heads/main").ConfigureAwait(false);
                        AssertEqual(preRemoteHead, postRemoteHead, "Remote target branch should roll back to the pre-land head");
                        AssertEqual(preBareHead, postBareHead, "Bare local target branch should remain at the pre-land head");

                        List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.failed_target_advanced").ConfigureAwait(false);
                        AssertEqual(1, events.Count, "Post-push failure should emit one target-advanced audit event");
                        AssertEqual(entry.Id, events[0].EntityId, "Audit event should reference the failed merge entry");
                        AssertContains(preRemoteHead, events[0].Payload ?? "", "Audit payload should include the pre-land head");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_GateFails_FeatureCommitNeverReachesTarget", async () =>
            {
                // Regression for the landing-integrity bug: a Failed gate must leave the
                // target branch unchanged AND must never merge the captain's commit. A
                // HEAD-only equality check can pass even if the feature tree leaked, so
                // assert the captain's file is absent from the target tree directly.
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_gate_fail_tree_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, "feature.txt").ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("gate-fail-tree-vessel", repos.RemoteDir);
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
                        entry.TestCommand = "exit 1";
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "Entry should still exist");
                        AssertEqual(MergeStatusEnum.Failed, updated!.Status, "Entry should fail when the merge gate command fails");

                        string remoteMainFiles = await RunGitAsync(repos.RemoteDir, "ls-tree", "-r", "--name-only", "main").ConfigureAwait(false);
                        AssertFalse(remoteMainFiles.Contains("feature.txt"), "Captain feature file must not reach origin/main when the gate fails");

                        string bareMainFiles = await RunGitAsync(repos.BareDir, "ls-tree", "-r", "--name-only", "main").ConfigureAwait(false);
                        AssertFalse(bareMainFiles.Contains("feature.txt"), "Captain feature file must not reach the bare local main when the gate fails");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_PushSucceedsThenSyncFails_AuditEventShapeAndTargetUnmerged", async () =>
            {
                // Pins the full audit-event contract and proves the rollback actually un-merges
                // the feature from origin (not just resets the HEAD pointer) after a post-push failure.
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_audit_shape_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, "feature.txt").ConfigureAwait(false);
                    string checkedOutMainDir = Path.Combine(rootDir, "checked-out-main");
                    await RunGitAsync(repos.BareDir, "worktree", "add", checkedOutMainDir, "main").ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("audit-shape-vessel", repos.RemoteDir);
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

                        string preRemoteHead = await ResolveGitRefAsync(repos.RemoteDir, "refs/heads/main").ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "Entry should still exist");
                        AssertEqual(MergeStatusEnum.Failed, updated!.Status, "Entry should fail when local target sync fails after push");

                        List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.failed_target_advanced").ConfigureAwait(false);
                        AssertEqual(1, events.Count, "Post-push failure should emit exactly one target-advanced audit event");

                        ArmadaEvent auditEvent = events[0];
                        AssertEqual("merge_queue.failed_target_advanced", auditEvent.EventType, "Audit event type should be the failed-target-advanced marker");
                        AssertEqual("merge_entry", auditEvent.EntityType ?? "", "Audit event should be scoped to the merge entry");
                        AssertEqual(entry.Id, auditEvent.EntityId ?? "", "Audit event should reference the failed merge entry id");
                        AssertEqual(vessel.Id, auditEvent.VesselId ?? "", "Audit event should carry the vessel id");
                        AssertTrue((auditEvent.Message ?? "").Length > 0, "Audit event should carry a human-readable summary message");

                        string payload = auditEvent.Payload ?? "";
                        AssertContains(preRemoteHead, payload, "Audit payload should include the pre-land head");
                        AssertContains("main", payload, "Audit payload should include the target branch");
                        AssertContains("advancedHead", payload, "Audit payload should record the advanced head field");

                        // The rollback must un-merge the feature from origin, not merely reset the ref label.
                        string remoteMainFiles = await RunGitAsync(repos.RemoteDir, "ls-tree", "-r", "--name-only", "main").ConfigureAwait(false);
                        AssertFalse(remoteMainFiles.Contains("feature.txt"), "Rollback must remove the captain feature file from origin/main after a failed land");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_LandPushRejected_NoAdvanceNoAuditEventAndTargetUnchanged", async () =>
            {
                // Negative complement to the post-push rollback test: when the land-push to the
                // target is rejected outright, the target never advances. The rollback path must
                // recognize "not advanced" and stay silent -- no false-positive failed_target_advanced
                // audit event, no rollback force-push, and the target HEAD/tree must be untouched.
                // This exercises the no-advance early-return in RollbackTargetIfAdvancedAsync.
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_push_rejected_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, "feature.txt").ConfigureAwait(false);

                    // Reject the land-push to main at the remote. The integration branch is never
                    // pushed to origin (admiral-internal), so only the land-push hits this hook.
                    await InstallRejectBranchPushHookAsync(repos.RemoteDir, "main").ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("push-rejected-vessel", repos.RemoteDir);
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

                        string preRemoteHead = await ResolveGitRefAsync(repos.RemoteDir, "refs/heads/main").ConfigureAwait(false);
                        string preBareHead = await ResolveGitRefAsync(repos.BareDir, "refs/heads/main").ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "Entry should still exist");
                        AssertEqual(MergeStatusEnum.Failed, updated!.Status, "Entry should fail when the land-push is rejected by the remote");

                        string postRemoteHead = await ResolveGitRefAsync(repos.RemoteDir, "refs/heads/main").ConfigureAwait(false);
                        string postBareHead = await ResolveGitRefAsync(repos.BareDir, "refs/heads/main").ConfigureAwait(false);
                        AssertEqual(preRemoteHead, postRemoteHead, "Remote target branch must not move when the land-push is rejected");
                        AssertEqual(preBareHead, postBareHead, "Bare local target branch must not move when the land-push is rejected");

                        string remoteMainFiles = await RunGitAsync(repos.RemoteDir, "ls-tree", "-r", "--name-only", "main").ConfigureAwait(false);
                        AssertFalse(remoteMainFiles.Contains("feature.txt"), "Captain feature file must not reach origin/main when the land-push is rejected");

                        List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.failed_target_advanced").ConfigureAwait(false);
                        AssertEqual(0, events.Count, "A landing failure that did not advance the target must not emit a target-advanced audit event");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_NonePolicy_PreservesCaptainBranch", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_cleanup_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("cleanup-vessel", repos.RemoteDir);
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

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "Entry should still exist");
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should be Landed");

                        bool captainInBare = await BranchExistsInRepoAsync(repos.BareDir, repos.CaptainBranch).ConfigureAwait(false);
                        AssertTrue(captainInBare, "Captain branch should be preserved on bare repo with None policy");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_AlwaysDeletesIntegrationBranchFromBare", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_cleanup_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        // Use None so captain branch is preserved -- integration branch should still be deleted
                        settings.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("cleanup-vessel", repos.RemoteDir);
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

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "Entry should still exist");
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should be Landed");

                        // List all branches in bare repo -- verify no armada/merge-queue/* branch remains
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = "git",
                            WorkingDirectory = repos.BareDir,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };
                        psi.ArgumentList.Add("branch");
                        using (Process proc = new Process { StartInfo = psi })
                        {
                            proc.Start();
                            string branchList = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                            await proc.WaitForExitAsync().ConfigureAwait(false);
                            AssertFalse(branchList.Contains("armada/merge-queue/"), "Integration branch should be deleted from bare repo regardless of cleanup policy");
                        }
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            // === DockService.DeleteAsync Branch Cleanup Tests ===

            await RunTest("DockDeleteAsync_LocalOnlyPolicy_DeletesCaptainBranchFromBareOnly", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    Vessel vessel = new Vessel("dock-test-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_work_" + Guid.NewGuid().ToString("N"));
                    vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.BranchName = "armada/captain-1/msn_dock001";
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_wt_" + Guid.NewGuid().ToString("N"));
                    dock.Active = false;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    bool deleted = await dockService.DeleteAsync(dock.Id).ConfigureAwait(false);

                    AssertTrue(deleted, "Delete should succeed for inactive dock");

                    bool deletedLocal = git.OperationCalls.Contains("delete-local-branch:" + dock.BranchName);
                    bool deletedRemote = git.OperationCalls.Contains("delete-remote-branch:" + dock.BranchName);

                    AssertTrue(deletedLocal, "Captain branch should be deleted from bare repo on LocalOnly policy");
                    AssertFalse(deletedRemote, "Captain branch should NOT be deleted from remote on LocalOnly policy");
                }
            });

            await RunTest("DockDeleteAsync_LocalAndRemotePolicy_DeletesCaptainBranchFromBareAndRemote", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    Vessel vessel = new Vessel("dock-test-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_work_" + Guid.NewGuid().ToString("N"));
                    vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalAndRemote;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.BranchName = "armada/captain-1/msn_dock002";
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_wt_" + Guid.NewGuid().ToString("N"));
                    dock.Active = false;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    bool deleted = await dockService.DeleteAsync(dock.Id).ConfigureAwait(false);

                    AssertTrue(deleted, "Delete should succeed for inactive dock");

                    bool deletedLocal = git.OperationCalls.Contains("delete-local-branch:" + dock.BranchName);
                    bool deletedRemote = git.OperationCalls.Contains("delete-remote-branch:" + dock.BranchName);

                    AssertTrue(deletedLocal, "Captain branch should be deleted from bare repo on LocalAndRemote policy");
                    AssertTrue(deletedRemote, "Captain branch should be deleted from remote on LocalAndRemote policy");
                }
            });

            await RunTest("DockDeleteAsync_NonePolicy_PreservesCaptainBranch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    Vessel vessel = new Vessel("dock-test-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_work_" + Guid.NewGuid().ToString("N"));
                    vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.BranchName = "armada/captain-1/msn_dock003";
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_wt_" + Guid.NewGuid().ToString("N"));
                    dock.Active = false;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    bool deleted = await dockService.DeleteAsync(dock.Id).ConfigureAwait(false);

                    AssertTrue(deleted, "Delete should succeed for inactive dock");

                    bool deletedLocal = git.OperationCalls.Contains("delete-local-branch:" + dock.BranchName);
                    bool deletedRemote = git.OperationCalls.Contains("delete-remote-branch:" + dock.BranchName);

                    AssertFalse(deletedLocal, "Captain branch should be preserved with None policy");
                    AssertFalse(deletedRemote, "Captain branch should be preserved with None policy");
                }
            });

            await RunTest("DockDeleteAsync_GlobalFallbackPolicy_UsedWhenVesselPolicyIsNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    settings.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;

                    Vessel vessel = new Vessel("dock-test-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_work_" + Guid.NewGuid().ToString("N"));
                    vessel.BranchCleanupPolicy = null; // no vessel-level override -- fall back to global settings
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.BranchName = "armada/captain-1/msn_dock004";
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_wt_" + Guid.NewGuid().ToString("N"));
                    dock.Active = false;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    bool deleted = await dockService.DeleteAsync(dock.Id).ConfigureAwait(false);

                    AssertTrue(deleted, "Delete should succeed");

                    bool deletedLocal = git.OperationCalls.Contains("delete-local-branch:" + dock.BranchName);
                    AssertTrue(deletedLocal, "Global LocalOnly policy should delete local bare branch when vessel policy is null");
                }
            });

            await RunTest("DockDeleteAsync_BranchAlreadyGone_SwallowsGitFailure", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    git.ShouldThrowOnDeleteBranch = true;  // simulate branch already gone
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    Vessel vessel = new Vessel("dock-test-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.BranchName = "armada/captain-1/msn_dock005";
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_wt_" + Guid.NewGuid().ToString("N"));
                    dock.Active = false;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);

                    // Should NOT throw even though git delete fails
                    bool deleted = await dockService.DeleteAsync(dock.Id).ConfigureAwait(false);

                    AssertTrue(deleted, "Delete should still succeed even when git branch delete fails (branch may already be gone)");

                    Dock? afterDelete = await testDb.Driver.Docks.ReadAsync(dock.Id).ConfigureAwait(false);
                    AssertNull(afterDelete, "Dock record should be removed");
                }
            });
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
            /// <summary>The remote bare repo (acts as origin).</summary>
            public string RemoteDir { get; }
            /// <summary>The local bare clone (vessel.LocalPath).</summary>
            public string BareDir { get; }
            /// <summary>The regular clone (vessel.WorkingDirectory).</summary>
            public string WorkingDir { get; }
            /// <summary>The captain branch name created in the setup.</summary>
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
