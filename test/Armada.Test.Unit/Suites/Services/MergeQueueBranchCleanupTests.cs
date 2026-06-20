namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
    using System.Text.Json;
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

            await RunTest("LandEntryAsync_TargetBranchInWorktree_SkipsRefSyncAndStillLands", async () =>
            {
                // When the target branch is checked out in a sibling worktree, the local
                // ref sync is skipped (not a failure). The land still succeeds and a
                // structured target_ref_sync_skipped event is emitted.
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_wt_skip_" + Guid.NewGuid().ToString("N"));
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

                        Vessel vessel = new Vessel("wt-skip-vessel", repos.RemoteDir);
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
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should land even when local target ref sync is skipped due to worktree");

                        // Remote advances; bare local ref stays at old commit (sync was skipped)
                        string postRemoteHead = await ResolveGitRefAsync(repos.RemoteDir, "refs/heads/main").ConfigureAwait(false);
                        string postBareHead = await ResolveGitRefAsync(repos.BareDir, "refs/heads/main").ConfigureAwait(false);
                        AssertFalse(String.Equals(preRemoteHead, postRemoteHead, StringComparison.OrdinalIgnoreCase),
                            "Remote target branch should advance after successful land");
                        AssertEqual(preBareHead, postBareHead, "Bare local target branch remains at old commit when sync is skipped");

                        // Structured skip event emitted; no false-positive rollback event
                        List<ArmadaEvent> skipEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.target_ref_sync_skipped").ConfigureAwait(false);
                        AssertEqual(1, skipEvents.Count, "Should emit one target_ref_sync_skipped event when worktree blocks local sync");
                        AssertContains("branch_checked_out_in_worktree", skipEvents[0].Payload ?? "", "Payload should include the skip reason");
                        AssertContains(entry.Id, skipEvents[0].Payload ?? "", "Payload should include the entry id");

                        List<ArmadaEvent> rollbackEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.failed_target_advanced").ConfigureAwait(false);
                        AssertEqual(0, rollbackEvents.Count, "Successful land must not emit a failed_target_advanced event");
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

            await RunTest("LandEntryAsync_TargetBranchInWorktree_FeatureReachesTargetAndSkipEventEmitted", async () =>
            {
                // Pins the event contract when the target branch is in a worktree:
                // the land succeeds, the feature reaches origin/main, and a structured
                // target_ref_sync_skipped event (not failed_target_advanced) is emitted.
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_wt_event_" + Guid.NewGuid().ToString("N"));
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

                        Vessel vessel = new Vessel("wt-event-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Mission mission = new Mission("worktree skip event mission");
                        mission.VesselId = vessel.Id;
                        mission.Status = MissionStatusEnum.WorkProduced;
                        mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.MissionId = mission.Id;
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
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should land despite local target ref sync being skipped");

                        // Feature must be present on origin/main after a successful land
                        string remoteMainFiles = await RunGitAsync(repos.RemoteDir, "ls-tree", "-r", "--name-only", "main").ConfigureAwait(false);
                        AssertTrue(remoteMainFiles.Contains("feature.txt"), "Captain feature file should be on origin/main after successful land");

                        // Structured skip event emitted; no rollback event
                        List<ArmadaEvent> skipEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.target_ref_sync_skipped").ConfigureAwait(false);
                        AssertEqual(1, skipEvents.Count, "Should emit one target_ref_sync_skipped event");
                        AssertEqual("merge_entry", skipEvents[0].EntityType ?? "", "Skip event should be scoped to the merge entry");
                        AssertEqual(entry.Id, skipEvents[0].EntityId ?? "", "Skip event should reference the entry id");
                        AssertEqual(mission.Id, skipEvents[0].MissionId ?? "", "Skip event should carry the mission id");
                        AssertEqual(vessel.Id, skipEvents[0].VesselId ?? "", "Skip event should carry the vessel id");
                        AssertContains("branch_checked_out_in_worktree", skipEvents[0].Payload ?? "", "Payload should include the skip reason");

                        List<ArmadaEvent> rollbackEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.failed_target_advanced").ConfigureAwait(false);
                        AssertEqual(0, rollbackEvents.Count, "Successful land must not emit a failed_target_advanced event");
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

            await RunTest("LandEntryAsync_BareHeadPointsAtCaptainBranch_DeletesBranchAndRestoresHead", async () =>
            {
                // Regression for the root cause: a bare repo whose HEAD symbolic-ref points at the
                // captain branch must still land and must delete the captain branch, leaving HEAD on
                // a valid default ref.
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_head_captain_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);
                    await RunGitAsync(repos.BareDir, "symbolic-ref", "HEAD", "refs/heads/" + repos.CaptainBranch).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("head-captain-vessel", repos.RemoteDir);
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
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should be Landed even when bare HEAD starts on the captain branch");

                        bool captainInBare = await BranchExistsInRepoAsync(repos.BareDir, repos.CaptainBranch).ConfigureAwait(false);
                        AssertFalse(captainInBare, "Captain branch should be deleted from bare repo when HEAD starts on it");

                        string bareHead = (await RunGitAsync(repos.BareDir, "symbolic-ref", "HEAD").ConfigureAwait(false)).Trim();
                        AssertEqual("refs/heads/main", bareHead, "Bare repo HEAD should end on the default branch");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_PostSuccessLocalDeleteFails_EmitsBranchCleanupFailedEvent", async () =>
            {
                // When the post-success local delete is forced to fail (captain branch checked out in
                // a separate worktree on the bare repo), the land still succeeds and a retriable
                // merge_queue.branch_cleanup_failed audit event is emitted.
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_cleanup_fail_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);
                    string blockingWorktreeDir = Path.Combine(rootDir, "blocking-worktree");
                    await RunGitAsync(repos.BareDir, "worktree", "add", blockingWorktreeDir, repos.CaptainBranch).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("cleanup-fail-vessel", repos.RemoteDir);
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
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should be Landed even when post-success branch cleanup fails");

                        bool captainInBare = await BranchExistsInRepoAsync(repos.BareDir, repos.CaptainBranch).ConfigureAwait(false);
                        AssertTrue(captainInBare, "Captain branch should still exist because the worktree blocked deletion");

                        List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.branch_cleanup_failed").ConfigureAwait(false);
                        AssertEqual(1, events.Count, "Should emit one branch_cleanup_failed event when post-success local delete fails");
                        BranchCleanupFailedPayload? payload = JsonSerializer.Deserialize<BranchCleanupFailedPayload>(
                            events[0].Payload ?? "",
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        AssertNotNull(payload, "branch_cleanup_failed payload should deserialize");
                        AssertEqual(entry.Id, payload!.EntryId ?? "", "Payload should include entry id");
                        AssertEqual(repos.CaptainBranch, payload.BranchName ?? "", "Payload should include branch name");
                        AssertEqual(repos.BareDir, payload.RepoPath ?? "", "Payload should include repo path");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_FailedLand_DoesNotEmitBranchCleanupFailedEvent", async () =>
            {
                // A genuinely failed land (gate rejects) must not emit branch_cleanup_failed: the
                // branch is intentionally preserved for retry, so a failure signal would be noise.
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_cleanup_noevent_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("cleanup-noevent-vessel", repos.RemoteDir);
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
                        entry.TestCommand = "exit 1";
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "Entry should still exist");
                        AssertEqual(MergeStatusEnum.Failed, updated!.Status, "Entry should be Failed when gate rejects");

                        List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.branch_cleanup_failed").ConfigureAwait(false);
                        AssertEqual(0, events.Count, "Failed land should not emit branch_cleanup_failed event");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            // === WorkingDirectory Sync and Bare HEAD Restore Tests ===

            await RunTest("LandEntryAsync_CleanDefaultBranchWorkingDirectory_FastForwardedAfterLand", async () =>
            {
                // Clean WD on main branch -> fast-forward to landed commit -> workdir_synced event
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_wd_ff_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, "feature.txt").ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("wd-ff-vessel", repos.RemoteDir);
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

                        string preWdHead = await ResolveGitRefAsync(repos.WorkingDir, "HEAD").ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should be Landed");

                        // WD HEAD must have advanced to the new merged commit
                        string postWdHead = await ResolveGitRefAsync(repos.WorkingDir, "HEAD").ConfigureAwait(false);
                        string remoteHead = await ResolveGitRefAsync(repos.RemoteDir, "refs/heads/main").ConfigureAwait(false);
                        AssertFalse(String.Equals(preWdHead, postWdHead, StringComparison.OrdinalIgnoreCase),
                            "WorkingDirectory HEAD should have advanced after fast-forward");
                        AssertEqual(remoteHead, postWdHead, "WorkingDirectory HEAD should match origin/main after fast-forward");

                        // feature.txt should be visible in the WD
                        AssertTrue(File.Exists(Path.Combine(repos.WorkingDir, "feature.txt")), "feature.txt should be present in WorkingDirectory after fast-forward");

                        // workdir_synced event emitted
                        List<ArmadaEvent> syncEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_synced").ConfigureAwait(false);
                        AssertEqual(1, syncEvents.Count, "Should emit one workdir_synced event");
                        AssertContains(entry.Id, syncEvents[0].Payload ?? "", "workdir_synced payload should include entry id");
                        AssertContains("workingDirectory", syncEvents[0].Payload ?? "", "workdir_synced payload should include workingDirectory key");

                        List<ArmadaEvent> skipEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_sync_skipped").ConfigureAwait(false);
                        AssertEqual(0, skipEvents.Count, "No workdir_sync_skipped event should fire for a successful fast-forward");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_DirtyWorkingDirectory_SkipsWithWorkdirSyncSkippedEvent", async () =>
            {
                // Dirty WD -> sync skipped, files left untouched, workdir_sync_skipped event emitted
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_wd_dirty_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    // Make the working directory dirty with a new untracked file
                    string dirtyFile = Path.Combine(repos.WorkingDir, "uncommitted.txt");
                    await File.WriteAllTextAsync(dirtyFile, "dirty content").ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("wd-dirty-vessel", repos.RemoteDir);
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

                        string preWdHead = await ResolveGitRefAsync(repos.WorkingDir, "HEAD").ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should land despite dirty WorkingDirectory");

                        // WD HEAD must not have changed; dirty file must still exist
                        string postWdHead = await ResolveGitRefAsync(repos.WorkingDir, "HEAD").ConfigureAwait(false);
                        AssertEqual(preWdHead, postWdHead, "WorkingDirectory HEAD should not change when WD is dirty");
                        AssertTrue(File.Exists(dirtyFile), "Dirty file should remain untouched after skipped sync");

                        // workdir_sync_skipped event emitted
                        List<ArmadaEvent> skipEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_sync_skipped").ConfigureAwait(false);
                        AssertEqual(1, skipEvents.Count, "Should emit one workdir_sync_skipped event for dirty WD");
                        AssertContains("dirty_working_directory", skipEvents[0].Payload ?? "", "Payload should name the skip reason");
                        AssertContains(entry.Id, skipEvents[0].Payload ?? "", "Payload should include entry id");

                        List<ArmadaEvent> syncEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_synced").ConfigureAwait(false);
                        AssertEqual(0, syncEvents.Count, "No workdir_synced event should fire for a skipped sync");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_WorkingDirectoryOnNonDefaultBranch_SkipsWithEvent", async () =>
            {
                // WD checked out on a non-default branch -> sync skipped with workdir_sync_skipped event
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_wd_branch_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    // Check out the captain branch in the working directory so it is not on main
                    await RunGitAsync(repos.WorkingDir, "fetch", "origin").ConfigureAwait(false);
                    await RunGitAsync(repos.WorkingDir, "checkout", "-b", "local-feature", "origin/main").ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("wd-branch-vessel", repos.RemoteDir);
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
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should land despite WD on non-default branch");

                        // WD should still be on local-feature
                        string currentBranch = (await RunGitAsync(repos.WorkingDir, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Trim();
                        AssertEqual("local-feature", currentBranch, "WorkingDirectory branch should be unchanged when sync is skipped");

                        // workdir_sync_skipped event emitted
                        List<ArmadaEvent> skipEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_sync_skipped").ConfigureAwait(false);
                        AssertEqual(1, skipEvents.Count, "Should emit one workdir_sync_skipped event for non-default branch");
                        AssertContains("on_non_default_branch", skipEvents[0].Payload ?? "", "Payload should name the skip reason");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_BareHeadRestoredToDefaultAfterBranchCleanup", async () =>
            {
                // After a successful land, the bare repo HEAD symbolic-ref is restored to
                // refs/heads/<defaultBranch> and a bare_head_restored event is emitted.
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_bare_head_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("bare-head-vessel", repos.RemoteDir);
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
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should be Landed");

                        // bare repo HEAD should point to refs/heads/main
                        string bareHead = (await RunGitAsync(repos.BareDir, "symbolic-ref", "HEAD").ConfigureAwait(false)).Trim();
                        AssertEqual("refs/heads/main", bareHead, "Bare repo HEAD should be restored to refs/heads/main after land");

                        // bare_head_restored event emitted
                        List<ArmadaEvent> headEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.bare_head_restored").ConfigureAwait(false);
                        AssertEqual(1, headEvents.Count, "Should emit one bare_head_restored event after land");
                        AssertContains(entry.Id, headEvents[0].Payload ?? "", "bare_head_restored payload should include entry id");
                        AssertContains("main", headEvents[0].Payload ?? "", "bare_head_restored payload should include the default branch");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_NullWorkingDirectory_NoSyncEventEmitted", async () =>
            {
                // When the vessel has no WorkingDirectory configured, the sync step returns
                // silently: neither workdir_synced nor workdir_sync_skipped is emitted, and
                // the land still succeeds. This pins the early null/empty guard as distinct
                // from the directory_missing skip path.
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_wd_null_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("wd-null-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = null; // no working directory configured
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
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should land when no WorkingDirectory is configured");

                        // Neither sync nor skip event should fire for an unconfigured WorkingDirectory
                        List<ArmadaEvent> syncEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_synced").ConfigureAwait(false);
                        AssertEqual(0, syncEvents.Count, "No workdir_synced event should fire when WorkingDirectory is null");

                        List<ArmadaEvent> skipEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_sync_skipped").ConfigureAwait(false);
                        AssertEqual(0, skipEvents.Count, "No workdir_sync_skipped event should fire when WorkingDirectory is null");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_WorkingDirectoryMissing_SkipsWithDirectoryMissingEvent", async () =>
            {
                // WorkingDirectory configured but the path does not exist -> sync skipped with
                // a directory_missing reason. Verify the full structured payload shape.
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_wd_missing_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    string missingWorkingDir = Path.Combine(rootDir, "does-not-exist");

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("wd-missing-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = missingWorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Mission mission = new Mission("workdir missing mission");
                        mission.VesselId = vessel.Id;
                        mission.Status = MissionStatusEnum.WorkProduced;
                        mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.MissionId = mission.Id;
                        entry.BranchName = repos.CaptainBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should land despite a missing WorkingDirectory");

                        AssertFalse(Directory.Exists(missingWorkingDir), "Sync must not create the missing WorkingDirectory");

                        List<ArmadaEvent> skipEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_sync_skipped").ConfigureAwait(false);
                        AssertEqual(1, skipEvents.Count, "Should emit one workdir_sync_skipped event for a missing WorkingDirectory");

                        WorkdirSyncSkippedPayload? payload = JsonSerializer.Deserialize<WorkdirSyncSkippedPayload>(
                            skipEvents[0].Payload ?? "",
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        AssertNotNull(payload, "workdir_sync_skipped payload should deserialize");
                        AssertEqual("directory_missing", payload!.Reason ?? "", "Payload reason should be directory_missing");
                        AssertEqual(entry.Id, payload.EntryId ?? "", "Payload entry id");
                        AssertEqual(mission.Id, payload.MissionId ?? "", "Payload mission id");
                        AssertEqual(vessel.Id, payload.VesselId ?? "", "Payload vessel id");
                        AssertEqual("main", payload.TargetBranch ?? "", "Payload target branch");
                        AssertEqual(missingWorkingDir, payload.WorkingDirectory ?? "", "Payload working directory");

                        List<ArmadaEvent> syncEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_synced").ConfigureAwait(false);
                        AssertEqual(0, syncEvents.Count, "No workdir_synced event should fire for a missing WorkingDirectory");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_WorkingDirectoryNotARepository_SkipsWithNotARepositoryEvent", async () =>
            {
                // WorkingDirectory exists but is not a git repository -> sync skipped with a
                // not_a_repository reason. The land still succeeds.
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_wd_norepo_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    string nonRepoWorkingDir = Path.Combine(rootDir, "plain-dir");
                    Directory.CreateDirectory(nonRepoWorkingDir);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("wd-norepo-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = nonRepoWorkingDir;
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
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should land when WorkingDirectory is not a repository");

                        List<ArmadaEvent> skipEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_sync_skipped").ConfigureAwait(false);
                        AssertEqual(1, skipEvents.Count, "Should emit one workdir_sync_skipped event when WorkingDirectory is not a repository");
                        AssertContains("not_a_repository", skipEvents[0].Payload ?? "", "Payload should name the not_a_repository reason");
                        AssertContains(entry.Id, skipEvents[0].Payload ?? "", "Payload should include entry id");

                        List<ArmadaEvent> syncEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_synced").ConfigureAwait(false);
                        AssertEqual(0, syncEvents.Count, "No workdir_synced event should fire when WorkingDirectory is not a repository");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_WorkingDirectoryDivergedOnDefaultBranch_SkipsWithFastForwardFailedEvent", async () =>
            {
                // WorkingDirectory is clean and on the default branch but has a divergent local
                // commit, so the fast-forward-only pull fails. The sync is skipped with a
                // fast_forward_failed reason, the local commit is preserved (never discarded),
                // and the land still succeeds.
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_wd_fffail_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    // Create a divergent local commit on main in the WorkingDirectory. The tree
                    // is clean afterward (committed), but origin/main will advance to a different
                    // commit on land, making a fast-forward impossible.
                    await File.WriteAllTextAsync(Path.Combine(repos.WorkingDir, "local-only.txt"), "local\n").ConfigureAwait(false);
                    await RunGitAsync(repos.WorkingDir, "add", "local-only.txt").ConfigureAwait(false);
                    await RunGitAsync(repos.WorkingDir, "commit", "-m", "Local divergent commit").ConfigureAwait(false);
                    string preWdHead = await ResolveGitRefAsync(repos.WorkingDir, "HEAD").ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("wd-fffail-vessel", repos.RemoteDir);
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
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should land even when the WorkingDirectory fast-forward fails");

                        // The divergent local commit must be preserved -- the sync never discards local changes
                        string postWdHead = await ResolveGitRefAsync(repos.WorkingDir, "HEAD").ConfigureAwait(false);
                        AssertEqual(preWdHead, postWdHead, "WorkingDirectory HEAD must be unchanged when fast-forward fails");
                        AssertTrue(File.Exists(Path.Combine(repos.WorkingDir, "local-only.txt")), "Local divergent commit content must be preserved");

                        List<ArmadaEvent> skipEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_sync_skipped").ConfigureAwait(false);
                        AssertEqual(1, skipEvents.Count, "Should emit one workdir_sync_skipped event when fast-forward fails");
                        AssertContains("fast_forward_failed", skipEvents[0].Payload ?? "", "Payload should name the fast_forward_failed reason");
                        AssertContains(entry.Id, skipEvents[0].Payload ?? "", "Payload should include entry id");

                        List<ArmadaEvent> syncEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_synced").ConfigureAwait(false);
                        AssertEqual(0, syncEvents.Count, "No workdir_synced event should fire when fast-forward fails");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_BranchCheckThrows_SkipsWithBranchCheckFailedEvent", async () =>
            {
                // The current-branch probe throws (e.g. corrupt HEAD / git error). The sync must
                // swallow the exception, emit a branch_check_failed skip event, never touch the
                // WorkingDirectory, and let the land still succeed.
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_wd_branchthrow_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        // Real git for the land; only the WorkingDirectory branch probe is faulted.
                        FaultInjectingGitService git = new FaultInjectingGitService(new GitService(logging), throwOnGetCurrentBranch: true, throwOnIsClean: false);

                        Vessel vessel = new Vessel("wd-branchthrow-vessel", repos.RemoteDir);
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

                        string preWdHead = await ResolveGitRefAsync(repos.WorkingDir, "HEAD").ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should land even when the branch check throws");

                        // WorkingDirectory must be untouched -- the sync bailed before any pull
                        string postWdHead = await ResolveGitRefAsync(repos.WorkingDir, "HEAD").ConfigureAwait(false);
                        AssertEqual(preWdHead, postWdHead, "WorkingDirectory HEAD must not move when the branch check fails");

                        List<ArmadaEvent> skipEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_sync_skipped").ConfigureAwait(false);
                        AssertEqual(1, skipEvents.Count, "Should emit one workdir_sync_skipped event when the branch check throws");
                        AssertContains("branch_check_failed", skipEvents[0].Payload ?? "", "Payload should name the branch_check_failed reason");
                        AssertContains(entry.Id, skipEvents[0].Payload ?? "", "Payload should include entry id");

                        List<ArmadaEvent> syncEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_synced").ConfigureAwait(false);
                        AssertEqual(0, syncEvents.Count, "No workdir_synced event should fire when the branch check fails");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_CleanCheckThrows_SkipsWithCleanCheckFailedEvent", async () =>
            {
                // The cleanliness probe throws after the branch check passes. The sync must
                // swallow the exception, emit a clean_check_failed skip event, never fast-forward,
                // and let the land still succeed.
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_wd_cleanthrow_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        // Branch check uses real git (returns "main"); only the clean probe is faulted.
                        FaultInjectingGitService git = new FaultInjectingGitService(new GitService(logging), throwOnGetCurrentBranch: false, throwOnIsClean: true);

                        Vessel vessel = new Vessel("wd-cleanthrow-vessel", repos.RemoteDir);
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

                        string preWdHead = await ResolveGitRefAsync(repos.WorkingDir, "HEAD").ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should land even when the clean check throws");

                        // WorkingDirectory must be untouched -- the sync bailed before any pull
                        string postWdHead = await ResolveGitRefAsync(repos.WorkingDir, "HEAD").ConfigureAwait(false);
                        AssertEqual(preWdHead, postWdHead, "WorkingDirectory HEAD must not move when the clean check fails");

                        List<ArmadaEvent> skipEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_sync_skipped").ConfigureAwait(false);
                        AssertEqual(1, skipEvents.Count, "Should emit one workdir_sync_skipped event when the clean check throws");
                        AssertContains("clean_check_failed", skipEvents[0].Payload ?? "", "Payload should name the clean_check_failed reason");
                        AssertContains(entry.Id, skipEvents[0].Payload ?? "", "Payload should include entry id");

                        List<ArmadaEvent> syncEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_synced").ConfigureAwait(false);
                        AssertEqual(0, syncEvents.Count, "No workdir_synced event should fire when the clean check fails");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("LandEntryAsync_EmptyDefaultBranch_UsesTargetBranchAndFastForwards", async () =>
            {
                // When the vessel has no DefaultBranch configured, the expected branch falls back
                // to the entry's TargetBranch. A clean WorkingDirectory on that target must still
                // fast-forward and emit workdir_synced -- proving the fallback resolution path.
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_wd_nodefault_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, "feature.txt").ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("wd-nodefault-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = ""; // no default branch -- expected branch falls back to entry.TargetBranch
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

                        string preWdHead = await ResolveGitRefAsync(repos.WorkingDir, "HEAD").ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertEqual(MergeStatusEnum.Landed, updated!.Status, "Entry should be Landed");

                        // WD HEAD must advance to origin/main via the TargetBranch fallback
                        string postWdHead = await ResolveGitRefAsync(repos.WorkingDir, "HEAD").ConfigureAwait(false);
                        string remoteHead = await ResolveGitRefAsync(repos.RemoteDir, "refs/heads/main").ConfigureAwait(false);
                        AssertFalse(String.Equals(preWdHead, postWdHead, StringComparison.OrdinalIgnoreCase),
                            "WorkingDirectory HEAD should advance even when DefaultBranch is unset");
                        AssertEqual(remoteHead, postWdHead, "WorkingDirectory HEAD should match origin/main after fallback fast-forward");

                        List<ArmadaEvent> syncEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_synced").ConfigureAwait(false);
                        AssertEqual(1, syncEvents.Count, "Should emit one workdir_synced event using the TargetBranch fallback");

                        List<ArmadaEvent> skipEvents = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.workdir_sync_skipped").ConfigureAwait(false);
                        AssertEqual(0, skipEvents.Count, "No workdir_sync_skipped event should fire when the TargetBranch fallback fast-forward succeeds");
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

            await RunTest("DockDeleteAsync_LocalOnlyPolicy_RestoresBareRepoHead", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    Vessel vessel = new Vessel("dock-head-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.BranchName = "armada/captain-1/msn_dock_head01";
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_wt_" + Guid.NewGuid().ToString("N"));
                    dock.Active = false;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    bool deleted = await dockService.DeleteAsync(dock.Id).ConfigureAwait(false);

                    AssertTrue(deleted, "Delete should succeed");
                    AssertTrue(
                        git.OperationCalls.Contains("set-head-symbolic-ref:refs/heads/main"),
                        "Bare repo HEAD should be restored to refs/heads/main after captain branch deletion");
                }
            });

            await RunTest("DockDeleteAsync_NonePolicy_DoesNotRestoreBareRepoHead", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    Vessel vessel = new Vessel("dock-head-vessel2", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.BranchName = "armada/captain-1/msn_dock_head02";
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_wt_" + Guid.NewGuid().ToString("N"));
                    dock.Active = false;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    bool deleted = await dockService.DeleteAsync(dock.Id).ConfigureAwait(false);

                    AssertTrue(deleted, "Delete should succeed");
                    AssertFalse(
                        git.OperationCalls.Contains("set-head-symbolic-ref:refs/heads/main"),
                        "Bare repo HEAD should NOT be restored when cleanup policy is None (branch still exists)");
                }
            });

            await RunTest("DockDeleteAsync_CustomDefaultBranch_RestoresBareRepoHead", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    Vessel vessel = new Vessel("dock-head-vessel3", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "develop";
                    vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.BranchName = "armada/captain-1/msn_dock_head03";
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_wt_" + Guid.NewGuid().ToString("N"));
                    dock.Active = false;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    bool deleted = await dockService.DeleteAsync(dock.Id).ConfigureAwait(false);

                    AssertTrue(deleted, "Delete should succeed");
                    AssertTrue(
                        git.OperationCalls.Contains("set-head-symbolic-ref:refs/heads/develop"),
                        "Bare repo HEAD should be restored to the vessel default branch ref");
                }
            });

            await RunTest("DockDeleteAsync_EmptyDefaultBranch_FallsBackToMain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    Vessel vessel = new Vessel("dock-head-vessel4", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "";
                    vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.BranchName = "armada/captain-1/msn_dock_head04";
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_wt_" + Guid.NewGuid().ToString("N"));
                    dock.Active = false;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    bool deleted = await dockService.DeleteAsync(dock.Id).ConfigureAwait(false);

                    AssertTrue(deleted, "Delete should succeed");
                    AssertTrue(
                        git.OperationCalls.Contains("set-head-symbolic-ref:refs/heads/main"),
                        "Bare repo HEAD should fall back to refs/heads/main when DefaultBranch is empty");
                }
            });

            await RunTest("DockDeleteAsync_LocalAndRemotePolicy_RestoresBareRepoHead", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    Vessel vessel = new Vessel("dock-head-vessel5", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalAndRemote;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.BranchName = "armada/captain-1/msn_dock_head05";
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_wt_" + Guid.NewGuid().ToString("N"));
                    dock.Active = false;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    bool deleted = await dockService.DeleteAsync(dock.Id).ConfigureAwait(false);

                    AssertTrue(deleted, "Delete should succeed");
                    AssertTrue(
                        git.OperationCalls.Contains("set-head-symbolic-ref:refs/heads/main"),
                        "Bare repo HEAD should be restored after LocalAndRemote branch cleanup");
                }
            });

            await RunTest("DockDeleteAsync_HeadRestoreFailure_StillDeletesDock", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    git.ShouldThrowOnSetHeadSymbolicRef = true;
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    Vessel vessel = new Vessel("dock-head-vessel6", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.BranchName = "armada/captain-1/msn_dock_head06";
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_wt_" + Guid.NewGuid().ToString("N"));
                    dock.Active = false;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    bool deleted = await dockService.DeleteAsync(dock.Id).ConfigureAwait(false);

                    AssertTrue(deleted, "Delete should succeed even when bare HEAD restore fails");
                    AssertNull(await testDb.Driver.Docks.ReadAsync(dock.Id).ConfigureAwait(false), "Dock record should be removed");
                }
            });

            await RunTest("DockDeleteAsync_RealGit_BareHeadRestoredAfterCaptainBranchDeleted", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_dock_head_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    // Simulate bare HEAD pointing at the captain branch before cleanup.
                    await RunGitAsync(repos.BareDir, "symbolic-ref", "HEAD", "refs/heads/" + repos.CaptainBranch).ConfigureAwait(false);

                    string worktreeDir = Path.Combine(rootDir, "dock-worktree");
                    await RunGitAsync(repos.BareDir, "worktree", "add", worktreeDir, repos.CaptainBranch).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("dock-real-head-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Dock dock = new Dock(vessel.Id);
                        dock.BranchName = repos.CaptainBranch;
                        dock.WorktreePath = worktreeDir;
                        dock.Active = false;
                        await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                        IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                        bool deleted = await dockService.DeleteAsync(dock.Id).ConfigureAwait(false);

                        AssertTrue(deleted, "Delete should succeed");

                        string bareHead = (await RunGitAsync(repos.BareDir, "symbolic-ref", "HEAD").ConfigureAwait(false)).Trim();
                        AssertEqual("refs/heads/main", bareHead, "Bare repo HEAD should be restored to refs/heads/main after dock delete");

                        bool captainInBare = await BranchExistsInRepoAsync(repos.BareDir, repos.CaptainBranch).ConfigureAwait(false);
                        AssertFalse(captainInBare, "Captain branch should be deleted from the bare repo");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
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

        private sealed class FailedTargetAdvancedPayload
        {
            public string? EntryId { get; set; }

            public string? MissionId { get; set; }

            public string? VesselId { get; set; }

            public string? TargetBranch { get; set; }

            public string? PreviousTargetHead { get; set; }

            public string? AdvancedTargetHead { get; set; }

            public string? IntegrationHead { get; set; }

            public string? RollbackResult { get; set; }

            public string? Reason { get; set; }
        }

        private sealed class WorkdirSyncSkippedPayload
        {
            public string? EntryId { get; set; }

            public string? MissionId { get; set; }

            public string? VesselId { get; set; }

            public string? TargetBranch { get; set; }

            public string? WorkingDirectory { get; set; }

            public string? Reason { get; set; }
        }

        private sealed class BranchCleanupFailedPayload
        {
            public string? EntryId { get; set; }

            public string? MissionId { get; set; }

            public string? VesselId { get; set; }

            public string? BranchName { get; set; }

            public string? RepoPath { get; set; }

            public string? Reason { get; set; }
        }

        /// <summary>
        /// Decorates a real <see cref="IGitService"/> and delegates every operation to it,
        /// except that the WorkingDirectory branch probe (<c>GetCurrentBranchAsync</c>) or the
        /// cleanliness probe (<c>IsWorkingDirectoryCleanAsync</c>) can be forced to throw.
        /// Used to exercise the defensive branch_check_failed and clean_check_failed skip paths
        /// in MergeQueueService WorkingDirectory sync while the real land still runs.
        /// </summary>
        private sealed class FaultInjectingGitService : IGitService
        {
            private readonly IGitService _Inner;
            private readonly bool _ThrowOnGetCurrentBranch;
            private readonly bool _ThrowOnIsClean;

            public FaultInjectingGitService(IGitService inner, bool throwOnGetCurrentBranch, bool throwOnIsClean)
            {
                _Inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _ThrowOnGetCurrentBranch = throwOnGetCurrentBranch;
                _ThrowOnIsClean = throwOnIsClean;
            }

            public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken token = default)
            {
                if (_ThrowOnGetCurrentBranch) throw new InvalidOperationException("injected branch-check failure");
                return _Inner.GetCurrentBranchAsync(workingDirectory, token);
            }

            public Task<bool> IsWorkingDirectoryCleanAsync(string workingDirectory, CancellationToken token = default)
            {
                if (_ThrowOnIsClean) throw new InvalidOperationException("injected clean-check failure");
                return _Inner.IsWorkingDirectoryCleanAsync(workingDirectory, token);
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

            public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default) => _Inner.PushRefSpecAsync(repoPath, srcRef, destRef, token);

            public Task<int> GetCommitCountBetweenAsync(string repoPath, string baseCommit, string tipCommit, CancellationToken token = default) => _Inner.GetCommitCountBetweenAsync(repoPath, baseCommit, tipCommit, token);

            public Task<string> GetRepositoryHeadRefAsync(string repoPath, CancellationToken token = default) => _Inner.GetRepositoryHeadRefAsync(repoPath, token);

            public Task SetRepositoryHeadAsync(string repoPath, string branchName, CancellationToken token = default) => _Inner.SetRepositoryHeadAsync(repoPath, branchName, token);

            public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default) => _Inner.PruneWorktreesAsync(repoPath, token);

            public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default) => _Inner.EnableAutoMergeAsync(worktreePath, prUrl, token);

            public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default) => _Inner.MergeBranchLocalAsync(targetWorkDir, sourceRepoPath, branchName, targetBranch, commitMessage, token);

            public Task PullAsync(string workingDirectory, CancellationToken token = default) => _Inner.PullAsync(workingDirectory, token);

            public Task PullFastForwardOnlyAsync(string workingDirectory, CancellationToken token = default) => _Inner.PullFastForwardOnlyAsync(workingDirectory, token);

            public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default) => _Inner.DiffAsync(worktreePath, baseBranch, token);

            public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default) => _Inner.GetHeadCommitHashAsync(worktreePath, token);

            public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default) => _Inner.GetChangedFilesSinceAsync(worktreePath, startCommit, token);

            public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) => _Inner.IsPrMergedAsync(workingDirectory, prUrl, token);

            public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default) => _Inner.BranchExistsAsync(repoPath, branchName, token);

            public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => _Inner.EnsureLocalBranchAsync(repoPath, branchName, token);

            public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) => _Inner.IsWorktreeRegisteredAsync(repoPath, worktreePath, token);

            public Task SetHeadSymbolicRefAsync(string repoPath, string targetRef, CancellationToken token = default) => _Inner.SetHeadSymbolicRefAsync(repoPath, targetRef, token);
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
