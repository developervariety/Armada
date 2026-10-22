namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Every launcher that runs a vessel's build or test command turns off MSBuild node reuse, the MSBuild server
    /// and the shared compiler server, so a build leaves no worker node or build server running in the admiral
    /// after it ends.
    /// </summary>
    public class BuildServerEnvironmentTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Build Server Environment";

        /// <summary>Run all build server environment tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Every vessel build or test command launcher runs its command with MSBuild node reuse and the build servers off", async () =>
            {
                if (OperatingSystem.IsWindows()) return;

                List<LauncherCase> cases = new List<LauncherCase>
                {
                    new LauncherCase("check run", null, "1/0/false", RunCheckAsync),
                    new LauncherCase("check run with a profile value", "0", "0/0/false", RunCheckAsync),
                    new LauncherCase("definition-of-done gate", null, "1/0/false", RunDefinitionOfDoneAsync),
                    new LauncherCase("merge queue test", null, "1/0/false", RunMergeQueueAsync),
                    new LauncherCase("workspace exec", null, "1/0/false", RunWorkspaceExecAsync)
                };

                // The variables are removed from this process, so a value the command reads can only come from
                // the launcher.
                Dictionary<string, string?> prior = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (string name in BuildServerEnvironment.Variables.Keys)
                {
                    prior[name] = Environment.GetEnvironmentVariable(name);
                    Environment.SetEnvironmentVariable(name, null);
                }
                try
                {
                    List<string> mismatches = new List<string>();
                    foreach (LauncherCase launcher in cases)
                    {
                        string root = Path.Combine(Path.GetTempPath(), "armada_build_env_" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(root);
                        try
                        {
                            string probePath = Path.Combine(root, "probe.txt");
                            await launcher.Run(root, ProbeCommand(probePath), launcher.ProfileNodeReuse).ConfigureAwait(false);
                            string observed = File.Exists(probePath) ? File.ReadAllText(probePath) : "(command did not run)";
                            if (observed != launcher.Expected)
                                mismatches.Add(launcher.Name + ": expected " + launcher.Expected + ", saw " + observed);
                        }
                        finally
                        {
                            try { Directory.Delete(root, true); } catch { }
                        }
                    }

                    AssertTrue(mismatches.Count == 0,
                        "Each launcher sets MSBUILDDISABLENODEREUSE/DOTNET_CLI_USE_MSBUILD_SERVER/UseSharedCompilation, and a profile value wins: "
                        + String.Join("; ", mismatches));
                }
                finally
                {
                    foreach (KeyValuePair<string, string?> variable in prior)
                        Environment.SetEnvironmentVariable(variable.Key, variable.Value);
                }
            });
        }

        private static string ProbeCommand(string probePath)
        {
            return "printf '%s/%s/%s' \"${" + BuildServerEnvironment.DisableNodeReuseVariable + "-unset}\" \"${"
                + BuildServerEnvironment.MsBuildServerVariable + "-unset}\" \"${"
                + BuildServerEnvironment.SharedCompilationVariable + "-unset}\" > '" + probePath + "'";
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static async Task RunCheckAsync(string root, string command, string? profileNodeReuse)
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                LoggingModule logging = CreateLogging();
                string workingDir = Path.Combine(root, "work");
                Directory.CreateDirectory(workingDir);

                Vessel vessel = new Vessel("build-env-check", "https://github.com/test/build-env.git");
                vessel.WorkingDirectory = workingDir;
                vessel.LocalPath = workingDir;
                vessel.DefaultBranch = "main";
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                Dictionary<string, string> profileVariables = new Dictionary<string, string>();
                if (profileNodeReuse != null) profileVariables[BuildServerEnvironment.DisableNodeReuseVariable] = profileNodeReuse;

                await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                {
                    TenantId = vessel.TenantId,
                    Name = "build-env-check-profile",
                    Scope = WorkflowProfileScopeEnum.Vessel,
                    VesselId = vessel.Id,
                    SecurityScanCommand = command,
                    IsDefault = true,
                    Active = true,
                    EnvironmentVariables = profileVariables
                }).ConfigureAwait(false);

                WorkflowProfileService profiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, profiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, profiles, readiness, logging);
                AuthContext auth = AuthContext.Authenticated(vessel.TenantId ?? "default", "usr_build_env", true, true, "UnitTest");

                await checkRuns.RunAsync(auth, new CheckRunRequest
                {
                    VesselId = vessel.Id,
                    Type = CheckRunTypeEnum.SecurityScan,
                    Label = "build env probe"
                }).ConfigureAwait(false);
            }
        }

        private static async Task RunDefinitionOfDoneAsync(string root, string command, string? profileNodeReuse)
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                string worktreePath = Path.Combine(root, "dock");
                Directory.CreateDirectory(worktreePath);
                const string tenantId = "ten_build_env";
                const string vesselId = "vsl_build_env";

                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata { Id = tenantId, Name = tenantId }).ConfigureAwait(false);
                await testDb.Driver.Vessels.CreateAsync(new Vessel
                {
                    Id = vesselId,
                    TenantId = tenantId,
                    Name = "Build Env Vessel",
                    RepoUrl = "file:///tmp/build-env-test.git",
                    LocalPath = worktreePath,
                    WorkingDirectory = worktreePath,
                    DefaultBranch = "main"
                }).ConfigureAwait(false);
                await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                {
                    TenantId = tenantId,
                    Name = "Build Env Profile",
                    Scope = WorkflowProfileScopeEnum.Vessel,
                    VesselId = vesselId,
                    BuildCommand = command,
                    UnitTestCommand = "echo ok",
                    IsDefault = true,
                    Active = true
                }).ConfigureAwait(false);

                DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                    new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                    testDb.Driver,
                    CreateLogging());
                Mission mission = new Mission("Build env probe mission") { TenantId = tenantId, VesselId = vesselId, Persona = "Worker" };
                await gate.EvaluateAsync(mission, new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);
            }
        }

        private static async Task RunMergeQueueAsync(string root, string command, string? profileNodeReuse)
        {
            MergeQueueServiceTests.GitRepoSetup repos = await MergeQueueServiceTests.CreateGitSetupAsync(root).ConfigureAwait(false);
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = MergeQueueServiceTests.CreateSettings();

                Vessel vessel = new Vessel("build-env-merge", repos.RemoteDir);
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
                entry.TestCommand = command;
                entry.CreatedUtc = DateTime.UtcNow;
                entry.LastUpdateUtc = DateTime.UtcNow;
                await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, new GitService(logging), new MergeFailureClassifier());
                await service.ProcessSingleAsync(entry.Id).ConfigureAwait(false);
            }
        }

        private static async Task RunWorkspaceExecAsync(string root, string command, string? profileNodeReuse)
        {
            WorkspaceService service = new WorkspaceService();
            Vessel vessel = new Vessel { Id = "vsl_build_env_workspace", Name = "Build Env Workspace", WorkingDirectory = root };
            await service.ExecAsync(vessel, new WorkspaceExecRequest { Command = command, TimeoutSeconds = 30 }).ConfigureAwait(false);
        }

        private sealed class LauncherCase
        {
            public LauncherCase(string name, string? profileNodeReuse, string expected, Func<string, string, string?, Task> run)
            {
                Name = name;
                ProfileNodeReuse = profileNodeReuse;
                Expected = expected;
                Run = run;
            }

            public string Name { get; }
            public string? ProfileNodeReuse { get; }
            public string Expected { get; }
            public Func<string, string, string?, Task> Run { get; }
        }
    }
}
