namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for self-deploy build gating, incident creation, and supervised restart.
    /// </summary>
    public class SelfDeployServiceTests : TestSuite
    {
        public override string Name => "Self Deploy Service";

        protected override async Task RunTestsAsync()
        {
            await RunTest("ExecuteAsync_Disabled_SkipsDeploy", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: false);
                    bool restarted = await context.Service.ExecuteAsync(context.Vessel.Id, "mrg_test", "test land");
                    AssertFalse(restarted, "restarted");
                    AssertEqual(0, context.Supervisor.Calls.Count, "supervisor calls");
                    AssertEqual(0, context.BuildRunner.Calls.Count, "build calls");
                }
            });

            await RunTest("ExecuteAsync_NonSelfVessel_SkipsDeploy", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true);
                    bool restarted = await context.Service.ExecuteAsync("vsl_other", "mrg_test", "test land");
                    AssertFalse(restarted, "restarted");
                    AssertEqual(0, context.BuildRunner.Calls.Count, "build calls");
                }
            });

            await RunTest("ExecuteAsync_BuildFails_AbortsRestart_OpensIncident", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true);
                    context.BuildRunner.NextResult = new SelfDeployBuildResult
                    {
                        Succeeded = false,
                        ExitCode = 1,
                        OutputTail = "error CS0000"
                    };

                    bool restarted = await context.Service.ExecuteAsync(context.Vessel.Id, "mrg_test", "test land");
                    AssertFalse(restarted, "restarted");
                    AssertEqual(0, context.Supervisor.Calls.Count, "supervisor calls");
                    AssertEqual(1, context.BuildRunner.Calls.Count, "build calls");

                    IncidentService incidents = new IncidentService(testDb.Driver);
                    AuthContext auth = AuthContext.Authenticated(
                        Constants.DefaultTenantId,
                        Constants.DefaultUserId,
                        true,
                        true,
                        "Test");
                    EnumerationResult<Incident> page = await incidents.EnumerateAsync(auth, new IncidentQuery
                    {
                        VesselId = context.Vessel.Id,
                        PageNumber = 1,
                        PageSize = 10
                    });
                    AssertTrue(page.Objects.Count >= 1, "incident count");
                    AssertContains("Self-deploy blocked", page.Objects[0].Title);
                }
            });

            await RunTest("ExecuteAsync_BuildSucceeds_RequestsSupervisedRestart", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true);
                    context.BuildRunner.NextResult = new SelfDeployBuildResult
                    {
                        Succeeded = true,
                        ExitCode = 0,
                        OutputTail = "Build succeeded."
                    };
                    context.Supervisor.NextResult = true;
                    Directory.CreateDirectory(Path.GetDirectoryName(context.ServerDllPath)!);
                    File.WriteAllText(context.SupervisorScriptPath, "stub");
                    File.WriteAllText(context.ServerDllPath, "stub");

                    bool restarted = await context.Service.ExecuteAsync(context.Vessel.Id, "mrg_test", "test land");
                    AssertTrue(restarted, "restarted");
                    AssertEqual(1, context.Supervisor.Calls.Count, "supervisor calls");
                    AssertEqual(1, context.BuildRunner.Calls.Count, "build calls");
                }
            });

            await RunTest("ExecuteAsync_UnpushedLocalCommits_SkipsRestart", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true);
                    context.Git.AheadCount = 2;
                    context.Git.BehindCount = 0;

                    bool restarted = await context.Service.ExecuteAsync(context.Vessel.Id, "mrg_test", "test land");
                    AssertFalse(restarted, "restarted");
                    AssertEqual(0, context.BuildRunner.Calls.Count, "build calls");
                    AssertEqual(0, context.Supervisor.Calls.Count, "supervisor calls");
                }
            });
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static async Task<SelfDeployTestContext> CreateContextAsync(TestDatabase testDb, bool enabled)
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "selfdeploy_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            Vessel vessel = new Vessel("armada-self", "https://github.com/test/armada");
            vessel.WorkingDirectory = tempRoot;
            vessel.DefaultBranch = "main";
            await testDb.Driver.Vessels.CreateAsync(vessel);

            ArmadaSettings settings = new ArmadaSettings();
            settings.SelfDeploy.Enabled = enabled;
            settings.SelfDeploy.SelfVesselId = vessel.Id;
            settings.SelfDeploy.DebounceSeconds = 0;
            settings.SelfDeploy.MergeQueueDrainTimeoutSeconds = 5;
            settings.SelfDeploy.SupervisorScriptRelativePath = "watchdog.ps1";
            settings.SelfDeploy.ServerDllRelativePath = "bin/Armada.Server.dll";

            SelfDeployStubGitService git = new SelfDeployStubGitService();
            RecordingSelfDeployBuildRunner buildRunner = new RecordingSelfDeployBuildRunner();
            RecordingSelfDeploySupervisor supervisor = new RecordingSelfDeploySupervisor();
            SelfDeployService service = new SelfDeployService(
                CreateLogging(),
                testDb.Driver,
                settings,
                git,
                buildRunner,
                supervisor);

            return new SelfDeployTestContext
            {
                Vessel = vessel,
                Service = service,
                Git = git,
                BuildRunner = buildRunner,
                Supervisor = supervisor,
                SupervisorScriptPath = Path.Combine(tempRoot, "watchdog.ps1"),
                ServerDllPath = Path.Combine(tempRoot, "bin", "Armada.Server.dll")
            };
        }

        private sealed class SelfDeployTestContext
        {
            public Vessel Vessel { get; set; } = null!;
            public SelfDeployService Service { get; set; } = null!;
            public SelfDeployStubGitService Git { get; set; } = null!;
            public RecordingSelfDeployBuildRunner BuildRunner { get; set; } = null!;
            public RecordingSelfDeploySupervisor Supervisor { get; set; } = null!;
            public string SupervisorScriptPath { get; set; } = String.Empty;
            public string ServerDllPath { get; set; } = String.Empty;
        }

        private sealed class RecordingSelfDeployBuildRunner : ISelfDeployBuildRunner
        {
            public List<string> Calls { get; } = new List<string>();
            public SelfDeployBuildResult NextResult { get; set; } = new SelfDeployBuildResult
            {
                Succeeded = true,
                ExitCode = 0
            };

            public Task<SelfDeployBuildResult> BuildAsync(
                string workingDirectory,
                SelfDeploySettings settings,
                CancellationToken token = default)
            {
                Calls.Add(workingDirectory);
                return Task.FromResult(NextResult);
            }
        }

        private sealed class RecordingSelfDeploySupervisor : ISelfDeploySupervisor
        {
            public List<string> Calls { get; } = new List<string>();
            public bool NextResult { get; set; } = true;

            public Task<bool> RequestSupervisedRestartAsync(
                string workingDirectory,
                int admiralProcessId,
                string serverDllPath,
                string supervisorScriptPath,
                CancellationToken token = default)
            {
                Calls.Add(workingDirectory + "|" + admiralProcessId + "|" + serverDllPath);
                return Task.FromResult(NextResult);
            }
        }

        private sealed class SelfDeployStubGitService : IGitService
        {
            public int AheadCount { get; set; }
            public int BehindCount { get; set; }

            public Task<bool> IsRepositoryAsync(string path, CancellationToken token = default) => Task.FromResult(true);
            public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult<string?>("main");
            public Task<bool> IsWorkingDirectoryCleanAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult(true);
            public Task FetchAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task PullFastForwardOnlyAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;

            public Task<int> GetCommitCountBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default)
            {
                if (fromRef.StartsWith("origin/", StringComparison.Ordinal))
                {
                    return Task.FromResult(AheadCount);
                }

                return Task.FromResult(BehindCount);
            }

            public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default) => Task.CompletedTask;
            public Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", CancellationToken token = default) => Task.CompletedTask;
            public Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default) => Task.CompletedTask;
            public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default) => Task.FromResult(String.Empty);
            public Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default) => Task.CompletedTask;
            public Task<string> GetRepositoryHeadRefAsync(string repoPath, CancellationToken token = default) => Task.FromResult("refs/heads/main");
            public Task SetRepositoryHeadAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default) => Task.CompletedTask;
            public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default) => Task.CompletedTask;
            public Task PullAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default) => Task.FromResult(String.Empty);
            public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default) => Task.FromResult<string?>("abc123");
            public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) => Task.FromResult(false);
            public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(true);
            public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(true);
            public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) => Task.FromResult(false);
            public Task SetHeadSymbolicRefAsync(string repoPath, string targetRef, CancellationToken token = default) => Task.CompletedTask;
        }
    }
}
