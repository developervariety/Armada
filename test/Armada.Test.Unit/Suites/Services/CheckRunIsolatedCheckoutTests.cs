namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Coverage for the CheckRunService isolated-checkout behavior: Armada-generated Build and
    /// UnitTest check runs clone the vessel repo into a dedicated temp checkout and execute there
    /// instead of in the live <c>Vessel.WorkingDirectory</c>, so a running Admiral cannot lock build
    /// outputs and create false failures. Also covers the <c>--no-restore</c> strip on the executed
    /// command and the working-directory fallback when no usable repo source is available.
    /// </summary>
    public class CheckRunIsolatedCheckoutTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Check Run Isolated Checkout";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            // Negative / fallback path: a repo source resolves (so readiness passes) but the clone
            // fails because LocalPath is not a real git repository. The check must degrade to running
            // in the live working directory rather than erroring. Runs even without git (the clone
            // attempt simply fails fast and returns null).
            await RunTest("Build check falls back to live working directory when the clone fails", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_iso_fallback", "usr_iso_fallback").ConfigureAwait(false);

                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-iso-fallback-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    // Default vessel: LocalPath == WorkingDirectory (exists, satisfies readiness) but is NOT a
                    // git repository, so ResolveRepoSource returns it yet TryCloneToTempAsync fails and the
                    // service falls back to the live working directory.
                    Vessel vessel = CreateVessel("ten_iso_fallback", "usr_iso_fallback", workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_iso_fallback",
                        UserId = "usr_iso_fallback",
                        Name = "Fallback Build Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = PrintWorkingDirectoryCommand()
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_iso_fallback", "usr_iso_fallback", false, false, "UnitTest");
                    CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Build,
                        Label = "Build"
                    }).ConfigureAwait(false);

                    AssertEqual(CheckRunStatusEnum.Passed, run.Status);
                    AssertContains(workingDirectory, run.Output ?? String.Empty);
                    AssertFalse((run.Output ?? String.Empty).Contains("armada-chk-"), "Fallback run must not execute in an isolated checkout.");
                }
                finally
                {
                    SafeDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);

            if (!IsGitOnPath())
            {
                Console.WriteLine("  SKIP  CheckRunIsolatedCheckoutTests (git-backed cases) -- git not found on PATH");
                return;
            }

            // Build check clones the repo and executes in the isolated temp checkout, not WorkingDirectory.
            await RunTest("Build check executes in isolated temp checkout, not the live working directory", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_iso_build", "usr_iso_build").ConfigureAwait(false);

                string sourceRepo = await CreateSourceRepoAsync().ConfigureAwait(false);
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-iso-live-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    Vessel vessel = CreateVessel("ten_iso_build", "usr_iso_build", workingDirectory);
                    vessel.LocalPath = sourceRepo;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_iso_build",
                        UserId = "usr_iso_build",
                        Name = "Isolated Build Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = PrintWorkingDirectoryCommand()
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_iso_build", "usr_iso_build", false, false, "UnitTest");
                    CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Build,
                        Label = "Build"
                    }).ConfigureAwait(false);

                    AssertEqual(CheckRunStatusEnum.Passed, run.Status);
                    // The printed working directory proves the command ran inside the isolated clone,
                    // not the vessel's live working directory.
                    AssertContains("armada-chk-", run.Output ?? String.Empty);
                    AssertFalse((run.Output ?? String.Empty).Contains(workingDirectory), "Build check must not execute in the live working directory.");
                }
                finally
                {
                    SafeDeleteDirectory(workingDirectory);
                    SafeDeleteDirectory(sourceRepo);
                }
            }).ConfigureAwait(false);

            // Regression: a file held open exclusively in the live working directory cannot fail the
            // check, because the command runs in the isolated checkout instead.
            await RunTest("Locked file in live working directory does not fail the Build check", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_iso_lock", "usr_iso_lock").ConfigureAwait(false);

                string sourceRepo = await CreateSourceRepoAsync().ConfigureAwait(false);
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-iso-lock-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);
                string lockedFilePath = Path.Combine(workingDirectory, "locked-output.bin");
                FileStream? lockedFile = null;

                try
                {
                    // Exclusive lock that mimics a running Admiral holding a build output open.
                    lockedFile = new FileStream(lockedFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                    await lockedFile.WriteAsync(new byte[] { 1, 2, 3 }).ConfigureAwait(false);
                    await lockedFile.FlushAsync().ConfigureAwait(false);

                    Vessel vessel = CreateVessel("ten_iso_lock", "usr_iso_lock", workingDirectory);
                    vessel.LocalPath = sourceRepo;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_iso_lock",
                        UserId = "usr_iso_lock",
                        Name = "Locked Build Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        UnitTestCommand = PrintWorkingDirectoryCommand()
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_iso_lock", "usr_iso_lock", false, false, "UnitTest");
                    CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.UnitTest,
                        Label = "Unit Tests"
                    }).ConfigureAwait(false);

                    AssertEqual(CheckRunStatusEnum.Passed, run.Status);
                    AssertContains("armada-chk-", run.Output ?? String.Empty);
                    AssertFalse((run.Output ?? String.Empty).Contains(workingDirectory), "UnitTest check must not execute in the locked live working directory.");
                }
                finally
                {
                    lockedFile?.Dispose();
                    SafeDeleteDirectory(workingDirectory);
                    SafeDeleteDirectory(sourceRepo);
                }
            }).ConfigureAwait(false);

            // The executed command has --no-restore stripped, while the stored run.Command keeps the
            // original text. This guards against NETSDK1005 from --no-restore in a fresh clone.
            await RunTest("Executed isolated command strips --no-restore but stored command is unchanged", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_iso_strip", "usr_iso_strip").ConfigureAwait(false);

                string sourceRepo = await CreateSourceRepoAsync().ConfigureAwait(false);
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-iso-strip-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    Vessel vessel = CreateVessel("ten_iso_strip", "usr_iso_strip", workingDirectory);
                    vessel.LocalPath = sourceRepo;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_iso_strip",
                        UserId = "usr_iso_strip",
                        Name = "Strip Restore Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = "echo ARMADAMARKER --no-restore"
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_iso_strip", "usr_iso_strip", false, false, "UnitTest");
                    CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Build,
                        Label = "Build"
                    }).ConfigureAwait(false);

                    AssertEqual(CheckRunStatusEnum.Passed, run.Status);
                    // Stored command retains the original text for audit/replay.
                    AssertContains("--no-restore", run.Command);
                    // Executed output reflects the stripped command, so the marker is present without the flag.
                    AssertContains("ARMADAMARKER", run.Output ?? String.Empty);
                    AssertFalse((run.Output ?? String.Empty).Contains("--no-restore"), "Executed isolated command must have --no-restore stripped.");
                }
                finally
                {
                    SafeDeleteDirectory(workingDirectory);
                    SafeDeleteDirectory(sourceRepo);
                }
            }).ConfigureAwait(false);

            // A non-isolated check type (Lint) must keep running in the live working directory even when
            // a repo source exists, proving the isolation is scoped to Build/UnitTest only.
            await RunTest("Non-isolated check type runs in live working directory even with a repo source", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_iso_lint", "usr_iso_lint").ConfigureAwait(false);

                string sourceRepo = await CreateSourceRepoAsync().ConfigureAwait(false);
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-iso-lint-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    Vessel vessel = CreateVessel("ten_iso_lint", "usr_iso_lint", workingDirectory);
                    vessel.LocalPath = sourceRepo;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_iso_lint", "usr_iso_lint", false, false, "UnitTest");
                    CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Lint,
                        Label = "Lint",
                        CommandOverride = PrintWorkingDirectoryCommand()
                    }).ConfigureAwait(false);

                    AssertEqual(CheckRunStatusEnum.Passed, run.Status);
                    AssertContains(workingDirectory, run.Output ?? String.Empty);
                    AssertFalse((run.Output ?? String.Empty).Contains("armada-chk-"), "Lint check must not be routed through an isolated checkout.");
                }
                finally
                {
                    SafeDeleteDirectory(workingDirectory);
                    SafeDeleteDirectory(sourceRepo);
                }
            }).ConfigureAwait(false);

            // Regression for the checkout-ref defect: a non-default BranchName must actually be checked
            // out in the isolated clone. The feature branch carries a marker file absent on main, so the
            // command reading it back proves the requested ref was switched to. With the original
            // "git checkout -- <ref>" form the ref was treated as a pathspec, the switch silently failed,
            // and the command ran against main (marker absent) -- this case would have failed it.
            await RunTest("Build check targeting a non-default BranchName runs against that branch's content", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_iso_branch", "usr_iso_branch").ConfigureAwait(false);

                SourceRepoWithRef source = await CreateSourceRepoWithFeatureRefAsync().ConfigureAwait(false);
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-iso-branch-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    Vessel vessel = CreateVessel("ten_iso_branch", "usr_iso_branch", workingDirectory);
                    vessel.LocalPath = source.RepoPath;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_iso_branch",
                        UserId = "usr_iso_branch",
                        Name = "Branch Ref Build Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = PrintFileCommand(source.MarkerFileName)
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_iso_branch", "usr_iso_branch", false, false, "UnitTest");
                    CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Build,
                        Label = "Build",
                        BranchName = source.FeatureBranch
                    }).ConfigureAwait(false);

                    AssertEqual(CheckRunStatusEnum.Passed, run.Status);
                    // The marker only exists on the feature branch; its presence proves the ref was checked out.
                    AssertContains(source.MarkerContent, run.Output ?? String.Empty);
                }
                finally
                {
                    SafeDeleteDirectory(workingDirectory);
                    SafeDeleteDirectory(source.RepoPath);
                }
            }).ConfigureAwait(false);

            // Same regression via CommitHash, which takes precedence over BranchName and exercises the
            // detached-HEAD checkout path. The commit is reachable only from the feature branch.
            await RunTest("Build check targeting a non-default CommitHash runs against that commit's content", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_iso_commit", "usr_iso_commit").ConfigureAwait(false);

                SourceRepoWithRef source = await CreateSourceRepoWithFeatureRefAsync().ConfigureAwait(false);
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-iso-commit-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    Vessel vessel = CreateVessel("ten_iso_commit", "usr_iso_commit", workingDirectory);
                    vessel.LocalPath = source.RepoPath;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_iso_commit",
                        UserId = "usr_iso_commit",
                        Name = "Commit Ref Build Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = PrintFileCommand(source.MarkerFileName)
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_iso_commit", "usr_iso_commit", false, false, "UnitTest");
                    CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Build,
                        Label = "Build",
                        CommitHash = source.FeatureCommitHash
                    }).ConfigureAwait(false);

                    AssertEqual(CheckRunStatusEnum.Passed, run.Status);
                    AssertContains(source.MarkerContent, run.Output ?? String.Empty);
                }
                finally
                {
                    SafeDeleteDirectory(workingDirectory);
                    SafeDeleteDirectory(source.RepoPath);
                }
            }).ConfigureAwait(false);
        }

        #region Private-Methods

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static string PrintWorkingDirectoryCommand()
        {
            return OperatingSystem.IsWindows() ? "cd" : "pwd";
        }

        private static string PrintFileCommand(string fileName)
        {
            return OperatingSystem.IsWindows() ? "type " + fileName : "cat " + fileName;
        }

        private static bool IsGitOnPath()
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(info)!)
                {
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch
            {
                return false;
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

                return stdout.Trim();
            }
        }

        private static async Task<string> CreateSourceRepoAsync()
        {
            string repoPath = Path.Combine(Path.GetTempPath(), "armada-iso-src-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(repoPath);

            await RunGitAsync(repoPath, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(repoPath, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(repoPath, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            await File.WriteAllTextAsync(Path.Combine(repoPath, "README.txt"), "isolated checkout source\n").ConfigureAwait(false);
            await RunGitAsync(repoPath, "add", "README.txt").ConfigureAwait(false);
            await RunGitAsync(repoPath, "commit", "-m", "Initial commit").ConfigureAwait(false);

            return repoPath;
        }

        /// <summary>
        /// Create a source repo whose <c>main</c> branch lacks a marker file that a separate
        /// <c>feature</c> branch carries, so a check targeting the non-default ref can be proven to
        /// have actually switched to it (the marker only appears if the requested ref was checked out).
        /// The source is left on <c>main</c> so a plain clone defaults to main with no marker present.
        /// </summary>
        private static async Task<SourceRepoWithRef> CreateSourceRepoWithFeatureRefAsync()
        {
            string repoPath = Path.Combine(Path.GetTempPath(), "armada-iso-ref-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(repoPath);

            await RunGitAsync(repoPath, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(repoPath, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(repoPath, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            await File.WriteAllTextAsync(Path.Combine(repoPath, "README.txt"), "isolated checkout source\n").ConfigureAwait(false);
            await RunGitAsync(repoPath, "add", "README.txt").ConfigureAwait(false);
            await RunGitAsync(repoPath, "commit", "-m", "Initial commit").ConfigureAwait(false);

            string markerFileName = "feature-marker.txt";
            string markerContent = "FEATUREBRANCHMARKER";
            await RunGitAsync(repoPath, "checkout", "-b", "feature").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(repoPath, markerFileName), markerContent + "\n").ConfigureAwait(false);
            await RunGitAsync(repoPath, "add", markerFileName).ConfigureAwait(false);
            await RunGitAsync(repoPath, "commit", "-m", "Add feature marker").ConfigureAwait(false);
            string featureCommitHash = await RunGitAsync(repoPath, "rev-parse", "HEAD").ConfigureAwait(false);

            // Return HEAD to main so the clone's default checkout does NOT contain the marker.
            await RunGitAsync(repoPath, "checkout", "main").ConfigureAwait(false);

            return new SourceRepoWithRef
            {
                RepoPath = repoPath,
                FeatureBranch = "feature",
                FeatureCommitHash = featureCommitHash,
                MarkerFileName = markerFileName,
                MarkerContent = markerContent
            };
        }

        /// <summary>
        /// Recursively delete a temp directory, clearing read-only attributes first so that Windows
        /// <c>.git/objects</c> files can be removed (mirrors the SafeDeleteDirectory pattern used in
        /// the production service and other git-backed suites).
        /// </summary>
        private static void SafeDeleteDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return;

                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                Directory.Delete(path, true);
            }
            catch { }
        }

        private static async Task EnsureTenantAndUserAsync(TestDatabase testDb, string tenantId, string userId)
        {
            TenantMetadata? existingTenant = await testDb.Driver.Tenants.ReadAsync(tenantId).ConfigureAwait(false);
            if (existingTenant == null)
            {
                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata
                {
                    Id = tenantId,
                    Name = tenantId
                }).ConfigureAwait(false);
            }

            UserMaster? existingUser = await testDb.Driver.Users.ReadByIdAsync(userId).ConfigureAwait(false);
            if (existingUser == null)
            {
                await testDb.Driver.Users.CreateAsync(new UserMaster
                {
                    Id = userId,
                    TenantId = tenantId,
                    Email = userId + "@armada.test",
                    PasswordSha256 = UserMaster.ComputePasswordHash("password"),
                    IsTenantAdmin = true
                }).ConfigureAwait(false);
            }
        }

        private static Vessel CreateVessel(string tenantId, string userId, string workingDirectory)
        {
            return new Vessel
            {
                TenantId = tenantId,
                UserId = userId,
                Name = "Isolated Checkout Vessel",
                RepoUrl = "file:///tmp/armada-tests.git",
                LocalPath = workingDirectory,
                WorkingDirectory = workingDirectory,
                DefaultBranch = "main"
            };
        }

        /// <summary>
        /// Holder for a source repo seeded with a non-default <c>feature</c> ref carrying a marker file,
        /// used to prove the isolated checkout switched to the requested ref rather than the clone default.
        /// </summary>
        private sealed class SourceRepoWithRef
        {
            /// <summary>Absolute path to the seeded source repository.</summary>
            public string RepoPath { get; set; } = String.Empty;

            /// <summary>Name of the non-default branch carrying the marker file.</summary>
            public string FeatureBranch { get; set; } = String.Empty;

            /// <summary>Commit hash on the feature branch that introduced the marker file.</summary>
            public string FeatureCommitHash { get; set; } = String.Empty;

            /// <summary>Name of the marker file present only on the feature ref.</summary>
            public string MarkerFileName { get; set; } = String.Empty;

            /// <summary>Content written into the marker file, asserted in the command output.</summary>
            public string MarkerContent { get; set; } = String.Empty;
        }

        #endregion
    }
}
