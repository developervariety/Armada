namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using SyslogLogging;

    public class GitServiceTests : TestSuite
    {
        public override string Name => "Git Service";

        private GitService CreateService()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new GitService(logging);
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Constructor NullLogging Throws", () =>
            {
                AssertThrows<ArgumentNullException>(() => new GitService(null!));
            });

            await RunTest("CloneBareAsync NullRepoUrl Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.CloneBareAsync(null!, "/tmp/path"));
            });

            await RunTest("CloneBareAsync EmptyRepoUrl Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.CloneBareAsync("", "/tmp/path"));
            });

            await RunTest("CloneBareAsync NullLocalPath Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.CloneBareAsync("https://github.com/test/repo", null!));
            });

            await RunTest("CreateWorktreeAsync NullRepoPath Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.CreateWorktreeAsync(null!, "/tmp/wt", "branch"));
            });

            await RunTest("CreateWorktreeAsync NullWorktreePath Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.CreateWorktreeAsync("/tmp/repo", null!, "branch"));
            });

            await RunTest("CreateWorktreeAsync NullBranchName Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.CreateWorktreeAsync("/tmp/repo", "/tmp/wt", null!));
            });

            await RunTest("RemoveWorktreeAsync NullPath Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.RemoveWorktreeAsync(null!));
            });

            await RunTest("RemoveWorktreeAsync Removes Registered Worktree From Outside Repo", async () =>
            {
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");
                string worktreeDir = Path.Combine(rootDir, "worktree");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "hello\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
                    await service.CreateWorktreeAsync(bareDir, worktreeDir, "armada/remove-me", "main").ConfigureAwait(false);

                    bool before = await service.IsWorktreeRegisteredAsync(bareDir, worktreeDir).ConfigureAwait(false);
                    AssertTrue(before, "Worktree should be registered before removal");

                    await service.RemoveWorktreeAsync(worktreeDir).ConfigureAwait(false);

                    AssertFalse(Directory.Exists(worktreeDir), "Worktree directory should be removed");

                    bool after = await service.IsWorktreeRegisteredAsync(bareDir, worktreeDir).ConfigureAwait(false);
                    AssertFalse(after, "Worktree should no longer be registered after removal");
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("RemoveWorktreeAsync Tolerates Missing Worktree Directory", async () =>
            {
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");
                string worktreeDir = Path.Combine(rootDir, "worktree");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "hello\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
                    await service.CreateWorktreeAsync(bareDir, worktreeDir, "armada/remove-me", "main").ConfigureAwait(false);

                    // Simulate the worktree directory vanishing out-of-band (the registration
                    // in the bare repo still points at it). Cleanup must not throw.
                    Directory.Delete(worktreeDir, true);
                    AssertFalse(Directory.Exists(worktreeDir), "Worktree directory should be gone before removal");

                    await service.RemoveWorktreeAsync(worktreeDir).ConfigureAwait(false);
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("RemoveWorktreeAsync NonExistentPath IsNoOp", async () =>
            {
                // The guard must short-circuit before resolving the owning repo, so a path
                // that never existed (no bare repo, no registration) is a silent success
                // rather than a shell-out failure.
                GitService service = CreateService();
                string missingPath = Path.Combine(Path.GetTempPath(), "armada-gitservice-missing-" + Guid.NewGuid().ToString("N"), "worktree");
                AssertFalse(Directory.Exists(missingPath), "Synthetic worktree path should not exist");

                await service.RemoveWorktreeAsync(missingPath).ConfigureAwait(false);
            });

            await RunTest("RemoveWorktreeAsync Twice Is Idempotent", async () =>
            {
                // Models a partially-completed prior cleanup: the first removal deletes the
                // directory, the retry observes it already gone. The second call must be a
                // no-op rather than throwing on the now-missing directory.
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");
                string worktreeDir = Path.Combine(rootDir, "worktree");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "hello\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
                    await service.CreateWorktreeAsync(bareDir, worktreeDir, "armada/remove-twice", "main").ConfigureAwait(false);

                    await service.RemoveWorktreeAsync(worktreeDir).ConfigureAwait(false);
                    AssertFalse(Directory.Exists(worktreeDir), "First removal should delete the worktree directory");

                    // Second removal sees the directory already gone — must not throw.
                    await service.RemoveWorktreeAsync(worktreeDir).ConfigureAwait(false);
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("RemoveWorktreeAsync MissingDir LeavesRegistrationForPrune", async () =>
            {
                // Pins the documented contract: when the directory has vanished out-of-band,
                // removal is a no-op and the stale registration is intentionally left intact
                // to be reclaimed separately by PruneWorktreesAsync.
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");
                string worktreeDir = Path.Combine(rootDir, "worktree");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "hello\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
                    await service.CreateWorktreeAsync(bareDir, worktreeDir, "armada/stale-reg", "main").ConfigureAwait(false);

                    Directory.Delete(worktreeDir, true);
                    AssertFalse(Directory.Exists(worktreeDir), "Worktree directory should be gone before removal");

                    await service.RemoveWorktreeAsync(worktreeDir).ConfigureAwait(false);

                    bool stillRegistered = await service.IsWorktreeRegisteredAsync(bareDir, worktreeDir).ConfigureAwait(false);
                    AssertTrue(stillRegistered, "Missing-dir removal must leave the stale registration for prune to reclaim");

                    await service.PruneWorktreesAsync(bareDir).ConfigureAwait(false);

                    bool afterPrune = await service.IsWorktreeRegisteredAsync(bareDir, worktreeDir).ConfigureAwait(false);
                    AssertFalse(afterPrune, "PruneWorktreesAsync should reclaim the stale registration left by the no-op removal");
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("FetchAsync NullRepoPath Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.FetchAsync(null!));
            });

            await RunTest("PushBranchAsync NullPath Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.PushBranchAsync(null!));
            });

            await RunTest("GetRepositoryHeadRefAsync NullRepoPath Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.GetRepositoryHeadRefAsync(null!));
            });

            await RunTest("GetRepositoryHeadRefAsync EmptyRepoPath Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.GetRepositoryHeadRefAsync(""));
            });

            await RunTest("SetRepositoryHeadAsync NullRepoPath Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.SetRepositoryHeadAsync(null!, "main"));
            });

            await RunTest("SetRepositoryHeadAsync EmptyRepoPath Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.SetRepositoryHeadAsync("", "main"));
            });

            await RunTest("SetRepositoryHeadAsync NullBranchName Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.SetRepositoryHeadAsync("/tmp/repo", null!));
            });

            await RunTest("SetRepositoryHeadAsync EmptyBranchName Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.SetRepositoryHeadAsync("/tmp/repo", ""));
            });

            await RunTest("SetRepositoryHeadAsync BareRepo RestoresMainSymbolicRef", async () =>
            {
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "hello\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "branch", "feature/head-test").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
                    await RunGitAsync(bareDir, "symbolic-ref", "HEAD", "refs/heads/feature/head-test").ConfigureAwait(false);

                    string movedHead = await service.GetRepositoryHeadRefAsync(bareDir).ConfigureAwait(false);
                    AssertEqual("refs/heads/feature/head-test", movedHead);

                    await service.SetRepositoryHeadAsync(bareDir, "main").ConfigureAwait(false);

                    string restoredHead = (await RunGitAsync(bareDir, "symbolic-ref", "HEAD").ConfigureAwait(false)).Trim();
                    AssertEqual("refs/heads/main", restoredHead);
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("CreatePullRequestAsync NullPath Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.CreatePullRequestAsync(null!, "title", "body"));
            });

            await RunTest("CreatePullRequestAsync NullTitle Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.CreatePullRequestAsync("/tmp/wt", null!, "body"));
            });

            await RunTest("RepairWorktreeAsync NullPath Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.RepairWorktreeAsync(null!));
            });

            await RunTest("SetHeadSymbolicRefAsync NullRepoPath Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.SetHeadSymbolicRefAsync(null!, "refs/heads/main"));
            });

            await RunTest("SetHeadSymbolicRefAsync EmptyRepoPath Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.SetHeadSymbolicRefAsync("", "refs/heads/main"));
            });

            await RunTest("SetHeadSymbolicRefAsync NullTargetRef Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.SetHeadSymbolicRefAsync("/tmp/repo.git", null!));
            });

            await RunTest("SetHeadSymbolicRefAsync EmptyTargetRef Throws", async () =>
            {
                GitService service = CreateService();
                await AssertThrowsAsync<ArgumentNullException>(() => service.SetHeadSymbolicRefAsync("/tmp/repo.git", ""));
            });

            await RunTest("SetHeadSymbolicRefAsync BareRepo UpdatesHeadSymbolicRef", async () =>
            {
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-head-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "hello\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await RunGitAsync(sourceDir, "checkout", "-b", "armada/captain-1").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "feature.txt"), "feature\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "feature.txt").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Captain commit").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
                    await RunGitAsync(bareDir, "symbolic-ref", "HEAD", "refs/heads/armada/captain-1").ConfigureAwait(false);

                    await service.SetHeadSymbolicRefAsync(bareDir, "refs/heads/main").ConfigureAwait(false);

                    string bareHead = (await RunGitAsync(bareDir, "symbolic-ref", "HEAD").ConfigureAwait(false)).Trim();
                    AssertEqual("refs/heads/main", bareHead, "SetHeadSymbolicRefAsync should point bare HEAD at the requested ref");
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("IsRepositoryAsync NullPath ReturnsFalse", async () =>
            {
                GitService service = CreateService();
                bool result = await service.IsRepositoryAsync(null!);
                AssertFalse(result);
            });

            await RunTest("IsRepositoryAsync EmptyPath ReturnsFalse", async () =>
            {
                GitService service = CreateService();
                bool result = await service.IsRepositoryAsync("");
                AssertFalse(result);
            });

            await RunTest("IsRepositoryAsync NonExistentPath ReturnsFalse", async () =>
            {
                GitService service = CreateService();
                bool result = await service.IsRepositoryAsync("/tmp/nonexistent_" + Guid.NewGuid().ToString("N"));
                AssertFalse(result);
            });

            await RunTest("CreateWorktreeAsync NewBranch StartsAtBaseCommit", async () =>
            {
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");
                string worktreeDir = Path.Combine(rootDir, "worktree");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "hello\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    string baseCommit = (await RunGitAsync(sourceDir, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
                    await service.CreateWorktreeAsync(bareDir, worktreeDir, "armada/test-branch", "main").ConfigureAwait(false);

                    string worktreeHead = (await RunGitAsync(worktreeDir, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    AssertEqual(baseCommit, worktreeHead);

                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("CreateWorktreeAsync NewBranch UsesLatestRemoteBaseCommit", async () =>
            {
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");
                string worktreeDir = Path.Combine(rootDir, "worktree");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "hello\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await service.CloneBareAsync(sourceDir, bareDir).ConfigureAwait(false);

                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "hello\nlatest base\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-am", "Advance main").ConfigureAwait(false);
                    string latestBaseCommit = (await RunGitAsync(sourceDir, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();

                    await service.CreateWorktreeAsync(bareDir, worktreeDir, "armada/latest-base", "main").ConfigureAwait(false);

                    string worktreeHead = (await RunGitAsync(worktreeDir, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    AssertEqual(latestBaseCommit, worktreeHead, "New worktree should start from the latest fetched base branch commit");
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("EnsureLocalBranchAsync MissingBranch UsesExistingRepoHistory", async () =>
            {
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "hello\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    string sourceHead = (await RunGitAsync(sourceDir, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();

                    await service.CloneBareAsync(sourceDir, bareDir).ConfigureAwait(false);

                    bool ensured = await service.EnsureLocalBranchAsync(bareDir, "release/e2e").ConfigureAwait(false);
                    string ensuredCommit = (await RunGitAsync(bareDir, "rev-parse", "refs/heads/release/e2e").ConfigureAwait(false)).Trim();

                    AssertTrue(ensured, "EnsureLocalBranchAsync should create a missing branch when repo history exists");
                    AssertEqual(sourceHead, ensuredCommit, "Created branch should point at the existing default history");
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("EnsureLocalBranchAsync TargetBranchCheckedOutInWorktree SkipsLocalRefSync", async () =>
            {
                // Sibling-dock conflict: the target branch is checked out in another
                // worktree of the same repo while origin has advanced. The force-update
                // (git branch -f) would fail with "checked out in a worktree", so the
                // production code must detect the worktree, skip the local ref sync,
                // and still return success without throwing.
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");
                string workDir = Path.Combine(rootDir, "work");
                string pusherDir = Path.Combine(rootDir, "pusher");
                string worktreeDir = Path.Combine(rootDir, "main-worktree");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "core.autocrlf", "false").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "base\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
                    await RunGitAsync(rootDir, "clone", "-c", "core.autocrlf=false", bareDir, workDir).ConfigureAwait(false);
                    await RunGitAsync(workDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(workDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

                    string originalMain = (await RunGitAsync(workDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();

                    // Advance origin/main from an independent clone so the bare repo is ahead of the work clone.
                    await RunGitAsync(rootDir, "clone", "-c", "core.autocrlf=false", bareDir, pusherDir).ConfigureAwait(false);
                    await RunGitAsync(pusherDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(pusherDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(pusherDir, "README.md"), "base\nupstream advance\n").ConfigureAwait(false);
                    await RunGitAsync(pusherDir, "commit", "-am", "Advance main").ConfigureAwait(false);
                    await RunGitAsync(pusherDir, "push", "origin", "main").ConfigureAwait(false);

                    string advancedMain = (await RunGitAsync(bareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();
                    AssertFalse(String.Equals(originalMain, advancedMain, StringComparison.OrdinalIgnoreCase),
                        "Origin main should be ahead of the work clone for this scenario to be meaningful");

                    // Move the work clone's primary checkout off main, then check out main in a sibling worktree.
                    await RunGitAsync(workDir, "checkout", "-b", "holding").ConfigureAwait(false);
                    await RunGitAsync(workDir, "worktree", "add", worktreeDir, "main").ConfigureAwait(false);

                    bool ensured = await service.EnsureLocalBranchAsync(workDir, "main").ConfigureAwait(false);

                    string postLocalMain = (await RunGitAsync(workDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();
                    string postRemoteMain = (await RunGitAsync(workDir, "rev-parse", "refs/remotes/origin/main").ConfigureAwait(false)).Trim();

                    AssertTrue(ensured, "EnsureLocalBranchAsync should report success even when the branch is checked out in a worktree");
                    AssertEqual(advancedMain, postRemoteMain, "Fetch should still advance the remote-tracking ref");
                    AssertEqual(originalMain, postLocalMain, "Local target ref must be left untouched when it is checked out in a sibling worktree");
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("EnsureLocalBranchAsync ExistingBranchNotInWorktree ForceUpdatesToOrigin", async () =>
            {
                // Complement to the worktree-skip case: when the branch is NOT checked
                // out in any worktree, the guard must not over-trigger -- the local ref
                // should be force-updated to match the advanced origin branch.
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");
                string workDir = Path.Combine(rootDir, "work");
                string pusherDir = Path.Combine(rootDir, "pusher");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "core.autocrlf", "false").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "base\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
                    await RunGitAsync(rootDir, "clone", "-c", "core.autocrlf=false", bareDir, workDir).ConfigureAwait(false);
                    await RunGitAsync(workDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(workDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "-c", "core.autocrlf=false", bareDir, pusherDir).ConfigureAwait(false);
                    await RunGitAsync(pusherDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(pusherDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(pusherDir, "README.md"), "base\nupstream advance\n").ConfigureAwait(false);
                    await RunGitAsync(pusherDir, "commit", "-am", "Advance main").ConfigureAwait(false);
                    await RunGitAsync(pusherDir, "push", "origin", "main").ConfigureAwait(false);

                    string advancedMain = (await RunGitAsync(bareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();

                    // Move off main so it is not checked out anywhere; no worktree is created.
                    await RunGitAsync(workDir, "checkout", "-b", "holding").ConfigureAwait(false);

                    bool ensured = await service.EnsureLocalBranchAsync(workDir, "main").ConfigureAwait(false);

                    string postLocalMain = (await RunGitAsync(workDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();

                    AssertTrue(ensured, "EnsureLocalBranchAsync should report success for an existing local branch");
                    AssertEqual(advancedMain, postLocalMain, "Local ref should be force-updated to origin when the branch is not checked out in a worktree");
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("EnsureLocalBranchAsync LocalBranchAheadOfOrigin PreservesLocalRef", async () =>
            {
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");
                string workDir = Path.Combine(rootDir, "work");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "core.autocrlf", "false").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "base\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
                    await RunGitAsync(rootDir, "clone", "-c", "core.autocrlf=false", bareDir, workDir).ConfigureAwait(false);
                    await RunGitAsync(workDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(workDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

                    string originMain = (await RunGitAsync(workDir, "rev-parse", "refs/remotes/origin/main").ConfigureAwait(false)).Trim();
                    await File.WriteAllTextAsync(Path.Combine(workDir, "local.txt"), "local commit\n").ConfigureAwait(false);
                    await RunGitAsync(workDir, "add", "local.txt").ConfigureAwait(false);
                    await RunGitAsync(workDir, "commit", "-m", "Add local commit").ConfigureAwait(false);
                    string localMain = (await RunGitAsync(workDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();

                    string preSyncAheadCount = (await RunGitAsync(
                        workDir,
                        "rev-list",
                        "--count",
                        "refs/remotes/origin/main..refs/heads/main").ConfigureAwait(false)).Trim();
                    AssertEqual("1", preSyncAheadCount, "Local main should contain a commit absent from origin before synchronization");

                    await RunGitAsync(workDir, "checkout", "-b", "holding").ConfigureAwait(false);

                    bool ensured = await service.EnsureLocalBranchAsync(workDir, "main").ConfigureAwait(false);

                    string postLocalMain = (await RunGitAsync(workDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();
                    string postRemoteMain = (await RunGitAsync(workDir, "rev-parse", "refs/remotes/origin/main").ConfigureAwait(false)).Trim();
                    string postSyncAheadCount = (await RunGitAsync(
                        workDir,
                        "rev-list",
                        "--count",
                        "refs/remotes/origin/main..refs/heads/main").ConfigureAwait(false)).Trim();

                    AssertTrue(ensured, "EnsureLocalBranchAsync should report success while preserving an ahead local branch");
                    AssertEqual(localMain, postLocalMain, "Local main must remain unchanged when it contains a commit absent from origin");
                    AssertEqual(originMain, postRemoteMain, "Origin tracking ref should remain the synchronization comparison source");
                    AssertEqual("1", postSyncAheadCount, "The local-only commit should remain reachable after synchronization is aborted");
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("CreateWorktreeAsync ExistingBranch StaysOnNamedBranch", async () =>
            {
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");
                string worktreeDir = Path.Combine(rootDir, "worktree");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "hello\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
                    await RunGitAsync(bareDir, "branch", "armada/existing", "main").ConfigureAwait(false);

                    await service.CreateWorktreeAsync(bareDir, worktreeDir, "armada/existing", "main").ConfigureAwait(false);

                    string currentBranch = (await RunGitAsync(worktreeDir, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Trim();
                    AssertEqual("armada/existing", currentBranch);
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("CreateWorktreeAsync DirtyTrackedFiles Throws And Cleans Up", async () =>
            {
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");
                string hooksDir = Path.Combine(rootDir, "hooks");
                string worktreeDir = Path.Combine(rootDir, "worktree");
                string branchName = "armada/dirty-worktree";

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    Directory.CreateDirectory(Path.Combine(sourceDir, "test"));
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

                    await File.WriteAllTextAsync(
                        Path.Combine(sourceDir, "test", "Dirty.csproj"),
                        "<Project>\n  <PropertyGroup />\n</Project>\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "test/Dirty.csproj").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Add tracked file").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
                    Directory.CreateDirectory(hooksDir);

                    // Dirty the tracked checkout deterministically during `git worktree add`
                    // without depending on line-ending behavior in the host Git install.
                    await File.WriteAllTextAsync(
                        Path.Combine(hooksDir, "post-checkout"),
                        "#!/bin/sh\nprintf '\\n<!-- dirty -->\\n' >> test/Dirty.csproj\n").ConfigureAwait(false);
                    await RunGitAsync(bareDir, "config", "core.hooksPath", hooksDir).ConfigureAwait(false);

                    InvalidOperationException? ex = null;
                    try
                    {
                        await service.CreateWorktreeAsync(bareDir, worktreeDir, branchName, "main").ConfigureAwait(false);
                        throw new Exception("Assertion failed: expected InvalidOperationException but no exception was thrown");
                    }
                    catch (InvalidOperationException caught)
                    {
                        ex = caught;
                    }

                    AssertTrue(ex != null, "Expected dirty worktree creation to throw");
                    AssertTrue(ex!.Message.Contains("contains tracked modifications", StringComparison.Ordinal), "Exception should explain that the checkout is dirty");
                    AssertTrue(ex.Message.Contains("test/Dirty.csproj", StringComparison.Ordinal), "Exception should list the dirty tracked file");
                    AssertFalse(Directory.Exists(worktreeDir), "Failed worktree creation should clean up the worktree directory");

                    string branchList = await RunGitAsync(bareDir, "branch", "--list", branchName).ConfigureAwait(false);
                    AssertEqual(String.Empty, branchList.Trim(), "Failed worktree creation should delete the created branch ref");
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("FetchAsync CheckedOutWorktreeBranch UsesRemoteTrackingRefs", async () =>
            {
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");
                string worktreeDir = Path.Combine(rootDir, "worktree");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "hello\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await service.CloneBareAsync(sourceDir, bareDir).ConfigureAwait(false);
                    await service.CreateWorktreeAsync(bareDir, worktreeDir, "armada/feature", "main").ConfigureAwait(false);

                    string originalLocalBranchCommit = (await RunGitAsync(bareDir, "rev-parse", "refs/heads/armada/feature").ConfigureAwait(false)).Trim();

                    await RunGitAsync(sourceDir, "checkout", "-b", "armada/feature").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "hello\nremote feature change\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-am", "Advance remote feature").ConfigureAwait(false);
                    string remoteFeatureCommit = (await RunGitAsync(sourceDir, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();

                    await service.FetchAsync(bareDir).ConfigureAwait(false);

                    string trackedRemoteCommit = (await RunGitAsync(bareDir, "rev-parse", "refs/remotes/origin/armada/feature").ConfigureAwait(false)).Trim();
                    string localBranchCommit = (await RunGitAsync(bareDir, "rev-parse", "refs/heads/armada/feature").ConfigureAwait(false)).Trim();
                    string checkedOutBranch = (await RunGitAsync(worktreeDir, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Trim();

                    AssertEqual(remoteFeatureCommit, trackedRemoteCommit, "Fetch should update the remote-tracking ref for the checked-out branch");
                    AssertEqual("armada/feature", checkedOutBranch, "Fetch should not disturb the active worktree branch");
                    AssertEqual(originalLocalBranchCommit, localBranchCommit, "Fetch should not rewrite the checked-out local branch ref");
                    AssertNotEqual(remoteFeatureCommit, localBranchCommit, "The checked-out local branch should remain untouched when the remote advances");
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("DiffAsync NoMergeBase FallsBackToTwoDotDiff", async () =>
            {
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));

                try
                {
                    Directory.CreateDirectory(rootDir);
                    await RunGitAsync(rootDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(rootDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(rootDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(rootDir, "README.md"), "hello\n").ConfigureAwait(false);
                    await RunGitAsync(rootDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(rootDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "checkout", "--orphan", "armada/orphan").ConfigureAwait(false);
                    await RunGitAsync(rootDir, "rm", "-rf", ".").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(rootDir, "README.md"), "hello\norphan change\n").ConfigureAwait(false);
                    await RunGitAsync(rootDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(rootDir, "commit", "-m", "Orphan commit").ConfigureAwait(false);

                    string diff = await service.DiffAsync(rootDir, "main").ConfigureAwait(false);

                    AssertTrue(diff.Contains("README.md", StringComparison.Ordinal), "Diff should include the changed file");
                    AssertTrue(diff.Contains("orphan change", StringComparison.Ordinal), "Diff should include the orphan-branch change");
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("MergeBranchLocalAsync Cleans Conflict State After Failure", async () =>
            {
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");
                string targetDir = Path.Combine(rootDir, "target");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "core.autocrlf", "false").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "base change\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "remote", "add", "armada", bareDir).ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "checkout", "-b", "armada/conflict").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "branch change\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-am", "Branch change").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "push", "armada", "armada/conflict").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "-c", "core.autocrlf=false", bareDir, targetDir).ConfigureAwait(false);
                    await RunGitAsync(targetDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(targetDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(targetDir, "README.md"), "target change\n").ConfigureAwait(false);
                    await RunGitAsync(targetDir, "commit", "-am", "Target change").ConfigureAwait(false);

                    InvalidOperationException? mergeEx = null;
                    try
                    {
                        await service.MergeBranchLocalAsync(targetDir, bareDir, "armada/conflict", "main").ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        mergeEx = ex;
                    }

                    string status = (await RunGitAsync(targetDir, "status", "--porcelain", "--untracked-files=no").ConfigureAwait(false)).Trim();
                    string currentBranch = (await RunGitAsync(targetDir, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Trim();
                    string fileContents = await File.ReadAllTextAsync(Path.Combine(targetDir, "README.md")).ConfigureAwait(false);

                    AssertNotNull(mergeEx, "Conflicting landing merge should throw");
                    AssertTrue(
                        mergeEx.Message.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase) ||
                        mergeEx.Message.Contains("Automatic merge failed", StringComparison.OrdinalIgnoreCase),
                        "Conflict exception should include git's merge details");
                    AssertEqual(String.Empty, status, "Conflict cleanup should leave no staged or unmerged changes");
                    AssertEqual("main", currentBranch, "Conflict cleanup should return to the target branch");
                    AssertEqual("target change\n", fileContents, "Conflict cleanup should restore the pre-merge working tree");
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("MergeBranchLocalAsync Succeeds When TargetCheckout Is A GitWorktree", async () =>
            {
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");
                string targetRepoDir = Path.Combine(rootDir, "target");
                string landingWorktreeDir = Path.Combine(rootDir, "landing-worktree");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "core.autocrlf", "false").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "base\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "remote", "add", "armada", bareDir).ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "checkout", "-b", "armada/worktree-merge").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "base\nworker change\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-am", "Worker change").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "push", "armada", "armada/worktree-merge").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "-c", "core.autocrlf=false", bareDir, targetRepoDir).ConfigureAwait(false);
                    await RunGitAsync(targetRepoDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(targetRepoDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await RunGitAsync(targetRepoDir, "checkout", "-b", "hold").ConfigureAwait(false);
                    await RunGitAsync(targetRepoDir, "worktree", "add", landingWorktreeDir, "main").ConfigureAwait(false);

                    await service.MergeBranchLocalAsync(landingWorktreeDir, bareDir, "armada/worktree-merge", "main").ConfigureAwait(false);

                    string currentBranch = (await RunGitAsync(landingWorktreeDir, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Trim();
                    string mergedReadme = await File.ReadAllTextAsync(Path.Combine(landingWorktreeDir, "README.md")).ConfigureAwait(false);

                    AssertEqual("main", currentBranch, "Landing worktree should stay on the target branch");
                    AssertEqual("base\nworker change\n", mergedReadme, "Landing merge should succeed in a git worktree checkout");
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("MergeBranchLocalAsync Materializes MissingTargetBranch In Landing Checkout", async () =>
            {
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");
                string targetDir = Path.Combine(rootDir, "target");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "core.autocrlf", "false").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "base\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
                    await RunGitAsync(rootDir, "clone", "-c", "core.autocrlf=false", bareDir, targetDir).ConfigureAwait(false);
                    await RunGitAsync(targetDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(targetDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

                    await RunGitAsync(sourceDir, "remote", "add", "armada", bareDir).ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "checkout", "-b", "armada-v050-live").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "target-only.txt"), "target branch content\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "target-only.txt").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Create target branch").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "push", "armada", "armada-v050-live").ConfigureAwait(false);

                    await RunGitAsync(sourceDir, "checkout", "-b", "armada/worker-1").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "base\nworker change\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-am", "Worker change").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "push", "armada", "armada/worker-1").ConfigureAwait(false);

                    string missingLocalBranch = (await RunGitAsync(targetDir, "branch", "--list", "armada-v050-live").ConfigureAwait(false)).Trim();
                    AssertEqual(String.Empty, missingLocalBranch, "Landing checkout should not already have the target branch locally");

                    await service.MergeBranchLocalAsync(targetDir, bareDir, "armada/worker-1", "armada-v050-live").ConfigureAwait(false);

                    string currentBranch = (await RunGitAsync(targetDir, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Trim();
                    string localBranch = (await RunGitAsync(targetDir, "branch", "--list", "armada-v050-live").ConfigureAwait(false)).Trim();
                    string mergedReadme = await File.ReadAllTextAsync(Path.Combine(targetDir, "README.md")).ConfigureAwait(false);
                    string targetBranchFile = await File.ReadAllTextAsync(Path.Combine(targetDir, "target-only.txt")).ConfigureAwait(false);

                    AssertEqual("armada-v050-live", currentBranch, "Landing checkout should end on the materialized target branch");
                    AssertTrue(!String.IsNullOrWhiteSpace(localBranch), "Landing checkout should create a local target branch when it is missing");
                    AssertEqual("base\nworker change\n", mergedReadme, "Landing merge should include worker changes");
                    AssertEqual("target branch content\n", targetBranchFile, "Landing merge should preserve target branch files");
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("MergeBranchLocalAsync DirtyLandingCheckout Throws Before Merge", async () =>
            {
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");
                string targetDir = Path.Combine(rootDir, "target");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "base\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
                    await RunGitAsync(rootDir, "clone", bareDir, targetDir).ConfigureAwait(false);
                    await RunGitAsync(targetDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(targetDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

                    await RunGitAsync(sourceDir, "remote", "add", "armada", bareDir).ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "checkout", "-b", "armada/worker-2").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "base\nworker change\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-am", "Worker change").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "push", "armada", "armada/worker-2").ConfigureAwait(false);

                    await File.WriteAllTextAsync(Path.Combine(targetDir, "README.md"), "dirty landing checkout\n").ConfigureAwait(false);

                    InvalidOperationException? ex = null;
                    try
                    {
                        await service.MergeBranchLocalAsync(targetDir, bareDir, "armada/worker-2", "main").ConfigureAwait(false);
                    }
                    catch (InvalidOperationException caught)
                    {
                        ex = caught;
                    }

                    string currentBranch = (await RunGitAsync(targetDir, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Trim();
                    string fileContents = await File.ReadAllTextAsync(Path.Combine(targetDir, "README.md")).ConfigureAwait(false);

                    AssertNotNull(ex, "Dirty landing checkout should throw");
                    AssertTrue(ex.Message.Contains("contains tracked modifications", StringComparison.Ordinal), "Dirty landing checkout should be rejected with a clear error");
                    AssertEqual("main", currentBranch, "Dirty landing checkout should not switch branches");
                    AssertEqual("dirty landing checkout\n", fileContents, "Dirty landing checkout should remain untouched");
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("CreateWorktreeAsync Detached AllowsSecondWorktreeForCheckedOutBranch", async () =>
            {
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");
                string firstWorktree = Path.Combine(rootDir, "first");
                string secondWorktree = Path.Combine(rootDir, "second");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "hello\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);

                    // First worktree checks out main normally (non-detached).
                    await service.CreateWorktreeAsync(bareDir, firstWorktree, "main").ConfigureAwait(false);
                    string firstBranch = (await RunGitAsync(firstWorktree, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Trim();
                    AssertEqual("main", firstBranch, "First worktree should be on named branch main");

                    // Second worktree on the same bare repo must use detached to avoid the
                    // "already checked out" error that git raises when main is checked out in firstWorktree.
                    await service.CreateWorktreeAsync(bareDir, secondWorktree, "main", "main", detached: true).ConfigureAwait(false);

                    string secondBranch = (await RunGitAsync(secondWorktree, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Trim();
                    AssertEqual("HEAD", secondBranch, "Second worktree created detached should report HEAD as branch name");

                    string firstCommit = (await RunGitAsync(firstWorktree, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    string secondCommit = (await RunGitAsync(secondWorktree, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    AssertEqual(firstCommit, secondCommit, "Both worktrees should point at the same commit");
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
                }
            });

            await RunTest("CreateWorktreeAsync Detached MissingBranch FallsBackToBaseCommit", async () =>
            {
                GitService service = CreateService();
                string rootDir = Path.Combine(Path.GetTempPath(), "armada-gitservice-" + Guid.NewGuid().ToString("N"));
                string sourceDir = Path.Combine(rootDir, "source");
                string bareDir = Path.Combine(rootDir, "bare.git");
                string worktreeDir = Path.Combine(rootDir, "detached-fallback");

                try
                {
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "hello\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
                    string baseCommit = (await RunGitAsync(bareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();

                    // Detached request for a branch that exists in neither the bare repo nor origin.
                    // The detached path must fall back to the base branch ref rather than throw or
                    // create a named branch, and the resulting worktree must be detached at base HEAD.
                    await service.CreateWorktreeAsync(bareDir, worktreeDir, "armada/never-existed", "main", detached: true).ConfigureAwait(false);

                    string head = (await RunGitAsync(worktreeDir, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Trim();
                    AssertEqual("HEAD", head, "Detached fallback worktree should report HEAD as branch name (no named branch created)");

                    string worktreeCommit = (await RunGitAsync(worktreeDir, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    AssertEqual(baseCommit, worktreeCommit, "Detached fallback worktree should be checked out at the base branch commit");

                    bool fabricatedBranch = (await RunGitAsync(bareDir, "branch", "--list", "armada/never-existed").ConfigureAwait(false)).Trim().Length > 0;
                    AssertFalse(fabricatedBranch, "Detached fallback must not fabricate a named branch for the missing ref");
                }
                finally
                {
                    if (Directory.Exists(rootDir))
                    {
                        try { Directory.Delete(rootDir, true); }
                        catch { }
                    }
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

            using Process process = new Process { StartInfo = startInfo };
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
}
