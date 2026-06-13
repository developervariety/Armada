namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for BuildDriftService: self-vessel resolution, commit comparison, and error handling.
    /// </summary>
    public class BuildDriftServiceTests : TestSuite
    {
        #region Public-Members

        /// <inheritdoc/>
        public override string Name => "Build Drift Service";

        #endregion

        #region Protected-Methods

        /// <inheritdoc/>
        protected override async Task RunTestsAsync()
        {
            await RunTest("GetReportAsync_LandedDiffersFromRunning_IsDriftedTrueWarningHasN", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    string ancestorDir = GetAncestorOfBaseDirectory();
                    Vessel vessel = new Vessel("drift-vessel", "https://github.com/test/repo");
                    vessel.WorkingDirectory = ancestorDir;
                    await testDb.Driver.Vessels.CreateAsync(vessel);

                    DriftStubGitService git = new DriftStubGitService
                    {
                        HeadCommitHashResult = "landedsha999",
                        CommitCountBetweenResult = 3
                    };
                    BuildDriftService service = new BuildDriftService(git, testDb.Driver, "runningsha111", CreateLogging());

                    BuildDriftReport report = await service.GetReportAsync();

                    AssertTrue(report.IsDrifted, "IsDrifted");
                    AssertNotNull(report.Warning, "Warning");
                    AssertContains("commits behind landed main", report.Warning!);
                    AssertContains("3", report.Warning!);
                    AssertEqual("runningsha111", report.RunningCommit);
                    AssertEqual("landedsha999", report.LandedCommit);
                    AssertEqual(3, report.BehindBy);
                }
            });

            await RunTest("GetReportAsync_LandedEqualsRunning_IsDriftedFalseWarningNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    string ancestorDir = GetAncestorOfBaseDirectory();
                    Vessel vessel = new Vessel("same-commit-vessel", "https://github.com/test/repo");
                    vessel.WorkingDirectory = ancestorDir;
                    await testDb.Driver.Vessels.CreateAsync(vessel);

                    DriftStubGitService git = new DriftStubGitService
                    {
                        HeadCommitHashResult = "abc123same",
                        CommitCountBetweenResult = 0
                    };
                    BuildDriftService service = new BuildDriftService(git, testDb.Driver, "abc123same", CreateLogging());

                    BuildDriftReport report = await service.GetReportAsync();

                    AssertFalse(report.IsDrifted, "IsDrifted");
                    AssertNull(report.Warning, "Warning");
                }
            });

            await RunTest("GetReportAsync_NoMatchingSelfVessel_LandedCommitNullNoWarning", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Vessel vessel = new Vessel("remote-vessel", "https://github.com/test/repo");
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "unrelated_" + Guid.NewGuid().ToString("N"));
                    await testDb.Driver.Vessels.CreateAsync(vessel);

                    DriftStubGitService git = new DriftStubGitService { HeadCommitHashResult = "somesha" };
                    BuildDriftService service = new BuildDriftService(git, testDb.Driver, "runningsha", CreateLogging());

                    BuildDriftReport report = await service.GetReportAsync();

                    AssertNull(report.LandedCommit, "LandedCommit");
                    AssertNull(report.Warning, "Warning");
                    AssertFalse(report.IsDrifted, "IsDrifted");
                }
            });

            await RunTest("GetReportAsync_GitThrows_DoesNotThrow", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    string ancestorDir = GetAncestorOfBaseDirectory();
                    Vessel vessel = new Vessel("throw-vessel", "https://github.com/test/repo");
                    vessel.WorkingDirectory = ancestorDir;
                    await testDb.Driver.Vessels.CreateAsync(vessel);

                    DriftStubGitService git = new DriftStubGitService { ShouldThrow = true };
                    BuildDriftService service = new BuildDriftService(git, testDb.Driver, "runningsha", CreateLogging());

                    BuildDriftReport report = await service.GetReportAsync();

                    AssertNull(report.LandedCommit, "LandedCommit should be null after git failure");
                }
            });

            await RunTest("GetReportAsync_AdmiralServicePopulatesBuildDrift", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubBuildDriftService buildDrift = new StubBuildDriftService(new BuildDriftReport
                    {
                        RunningCommit = "run0",
                        LandedCommit = "landed0",
                        BehindBy = 2,
                        IsDrifted = true,
                        Warning = "running build is 2 commits behind landed main -- rebuild + restart to deploy"
                    });

                    StubGitService stubGit = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, stubGit);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, stubGit, dockService);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService);
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                    AdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService, buildDrift: buildDrift);

                    ArmadaStatus status = await admiral.GetStatusAsync();

                    AssertNotNull(status.BuildDrift, "BuildDrift");
                    AssertTrue(status.BuildDrift!.IsDrifted, "IsDrifted");
                    AssertEqual(2, status.BuildDrift.BehindBy);
                }
            });
        }

        #endregion

        #region Private-Methods

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        private static string GetAncestorOfBaseDirectory()
        {
            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? parent = Path.GetDirectoryName(baseDir);
            return String.IsNullOrEmpty(parent) ? baseDir : parent;
        }

        #endregion

        #region Private-Types

        private class DriftStubGitService : IGitService
        {
            public string? HeadCommitHashResult { get; set; }
            public int CommitCountBetweenResult { get; set; }
            public bool ShouldThrow { get; set; }

            public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default)
            {
                if (ShouldThrow) throw new InvalidOperationException("Simulated git failure");
                return Task.FromResult(HeadCommitHashResult);
            }

            public Task<int> GetCommitCountBetweenAsync(string repoPath, string baseCommit, string tipCommit, CancellationToken token = default)
            {
                if (ShouldThrow) throw new InvalidOperationException("Simulated git failure");
                return Task.FromResult(CommitCountBetweenResult);
            }

            public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default) => Task.CompletedTask;
            public Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", CancellationToken token = default) => Task.CompletedTask;
            public Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task FetchAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default) => Task.CompletedTask;
            public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default) => Task.FromResult(String.Empty);
            public Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task<bool> IsRepositoryAsync(string path, CancellationToken token = default) => Task.FromResult(false);
            public Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default) => Task.CompletedTask;
            public Task<string> GetRepositoryHeadRefAsync(string repoPath, CancellationToken token = default) => Task.FromResult("refs/heads/main");
            public Task SetRepositoryHeadAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default) => Task.CompletedTask;
            public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default) => Task.CompletedTask;
            public Task PullAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task PullFastForwardOnlyAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult<string?>(null);
            public Task<bool> IsWorkingDirectoryCleanAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult(true);
            public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default) => Task.FromResult(String.Empty);
            public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) => Task.FromResult(false);
            public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(false);
            public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(false);
            public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) => Task.FromResult(false);
        }

        private class StubBuildDriftService : IBuildDriftService
        {
            private BuildDriftReport _Report;

            public StubBuildDriftService(BuildDriftReport report)
            {
                _Report = report ?? throw new ArgumentNullException(nameof(report));
            }

            public Task<BuildDriftReport> GetReportAsync(CancellationToken token = default)
                => Task.FromResult(_Report);
        }

        #endregion
    }
}
