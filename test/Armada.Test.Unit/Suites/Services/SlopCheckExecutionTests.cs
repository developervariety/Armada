namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// End-to-end coverage for the Slop check executed by <see cref="CheckRunService"/> against a real
    /// git repository: an unambiguous slop pattern fails the check, a WARN-only diff passes with the
    /// finding in the output, and every condition that prevents classification fails loudly.
    /// </summary>
    public class SlopCheckExecutionTests : TestSuite
    {
        private const string _Tenant = "ten_slop_exec";
        private const string _User = "usr_slop_exec";

        /// <inheritdoc />
        public override string Name => "Slop Check Execution";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Slop check fails loudly when the vessel path is not a git repository", async () =>
            {
                string directory = CreateTempDirectory("armada-slop-nogit-");
                try
                {
                    CheckRun run = await RunSlopAsync(directory, "work", null).ConfigureAwait(false);

                    AssertEqual(CheckRunStatusEnum.Failed, run.Status);
                    AssertEqual(-1, run.ExitCode ?? 0);
                    AssertContains("Nothing was examined", run.Output ?? String.Empty);
                    AssertEqual(SlopCheckRunner.CommandLabel, run.Command, "an executed Slop record must not read as an unexecuted intent marker");
                }
                finally
                {
                    SafeDeleteDirectory(directory);
                }
            }).ConfigureAwait(false);

            if (!IsGitOnPath())
            {
                Console.WriteLine("  SKIP  SlopCheckExecutionTests (git-backed cases) -- git not found on PATH");
                return;
            }

            await RunTest("Slop check fails a reviewed diff that skips a test", async () =>
            {
                string repo = await CreateRepoWithWorkBranchAsync("tests/Widget.Tests/WidgetTests.cs", "SkippedTest.cs.txt").ConfigureAwait(false);
                try
                {
                    CheckRun run = await RunSlopAsync(repo, "work", null).ConfigureAwait(false);

                    AssertEqual(CheckRunStatusEnum.Failed, run.Status);
                    AssertEqual(1, run.ExitCode ?? 0);
                    AssertContains("FAIL SkippedTest tests/Widget.Tests/WidgetTests.cs:7", run.Output ?? String.Empty);
                    AssertContains("Slop failed", run.Summary ?? String.Empty);
                }
                finally
                {
                    SafeDeleteDirectory(repo);
                }
            }).ConfigureAwait(false);

            await RunTest("Slop check passes a WARN-only diff and surfaces the finding", async () =>
            {
                string repo = await CreateRepoWithWorkBranchAsync("src/Port/Decoder.cs", "EmptyCatch.cs.txt").ConfigureAwait(false);
                try
                {
                    CheckRun run = await RunSlopAsync(repo, "work", null).ConfigureAwait(false);

                    AssertEqual(CheckRunStatusEnum.Passed, run.Status);
                    AssertEqual(0, run.ExitCode ?? -1);
                    AssertContains("WARN EmptyCatch src/Port/Decoder.cs:13", run.Output ?? String.Empty);
                    AssertContains("1 WARN finding", run.Summary ?? String.Empty);
                }
                finally
                {
                    SafeDeleteDirectory(repo);
                }
            }).ConfigureAwait(false);

            await RunTest("Slop check reads central package management from the reviewed commit", async () =>
            {
                string repo = await CreateRepoAsync().ConfigureAwait(false);
                try
                {
                    WriteFixture(repo, "Directory.Packages.props", "Directory.Packages.props.txt");
                    await GitAsync(repo, "add", "-A").ConfigureAwait(false);
                    await GitAsync(repo, "commit", "-m", "Enable central package management").ConfigureAwait(false);
                    await GitAsync(repo, "checkout", "-b", "work").ConfigureAwait(false);
                    WriteFixture(repo, "src/App/App.csproj", "InlineVersion.csproj.txt");
                    await GitAsync(repo, "add", "-A").ConfigureAwait(false);
                    await GitAsync(repo, "commit", "-m", "Add project").ConfigureAwait(false);
                    await GitAsync(repo, "checkout", "main").ConfigureAwait(false);

                    CheckRun run = await RunSlopAsync(repo, "work", null).ConfigureAwait(false);

                    AssertEqual(CheckRunStatusEnum.Failed, run.Status);
                    AssertContains("FAIL CentralPackageVersionBypass src/App/App.csproj:3", run.Output ?? String.Empty);
                    AssertContains("Central package management at the reviewed commit: enabled", run.Output ?? String.Empty);
                }
                finally
                {
                    SafeDeleteDirectory(repo);
                }
            }).ConfigureAwait(false);

            await RunTest("Slop check fails loudly when there is no commit or branch to classify", async () =>
            {
                string repo = await CreateRepoAsync().ConfigureAwait(false);
                try
                {
                    CheckRun run = await RunSlopAsync(repo, null, null).ConfigureAwait(false);

                    AssertEqual(CheckRunStatusEnum.Failed, run.Status);
                    AssertEqual(-1, run.ExitCode ?? 0);
                    AssertContains("no commit or branch", run.Output ?? String.Empty);
                }
                finally
                {
                    SafeDeleteDirectory(repo);
                }
            }).ConfigureAwait(false);

            await RunTest("Slop check fails loudly when the reviewed branch does not exist", async () =>
            {
                string repo = await CreateRepoAsync().ConfigureAwait(false);
                try
                {
                    CheckRun run = await RunSlopAsync(repo, "missing-branch", null).ConfigureAwait(false);

                    AssertEqual(CheckRunStatusEnum.Failed, run.Status);
                    AssertContains("could not resolve the reviewed commit", run.Output ?? String.Empty);
                }
                finally
                {
                    SafeDeleteDirectory(repo);
                }
            }).ConfigureAwait(false);

            await RunTest("An armed Slop record measures the voyage's work under review", async () =>
            {
                string repo = await CreateRepoWithWorkBranchAsync("tests/Widget.Tests/WidgetTests.cs", "SkippedTest.cs.txt").ConfigureAwait(false);
                try
                {
                    string workCommit = await GitAsync(repo, "rev-parse", "work").ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        CheckRunService service = await CreateServiceAsync(testDb).ConfigureAwait(false);
                        Vessel vessel = await testDb.Driver.Vessels.CreateAsync(CreateVessel(repo)).ConfigureAwait(false);
                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("slop-voyage")
                        {
                            TenantId = _Tenant,
                            UserId = _User
                        }).ConfigureAwait(false);
                        await testDb.Driver.Missions.CreateAsync(new Mission("Worker", "Port the widget")
                        {
                            TenantId = _Tenant,
                            UserId = _User,
                            VesselId = vessel.Id,
                            VoyageId = voyage.Id,
                            Status = MissionStatusEnum.WorkProduced,
                            BranchName = "work",
                            CommitHash = workCommit
                        }).ConfigureAwait(false);

                        CheckRun armed = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                        {
                            TenantId = _Tenant,
                            UserId = _User,
                            VesselId = vessel.Id,
                            VoyageId = voyage.Id,
                            Type = CheckRunTypeEnum.Slop,
                            Source = CheckRunSourceEnum.Armada,
                            Status = CheckRunStatusEnum.Pending,
                            Label = "Slop (armed at dispatch)"
                        }).ConfigureAwait(false);
                        AssertTrue(CheckRunGateRules.IsUnexecutedIntentMarker(armed), "an armed Slop record starts as an intent marker like Build and UnitTest");

                        CheckRun run = await service.RunPendingAsync(Auth(), armed.Id).ConfigureAwait(false);

                        AssertEqual(CheckRunStatusEnum.Failed, run.Status);
                        AssertEqual(workCommit, run.CommitHash, "the record must carry the commit it measured");
                        AssertContains("FAIL SkippedTest", run.Output ?? String.Empty);
                    }
                }
                finally
                {
                    SafeDeleteDirectory(repo);
                }
            }).ConfigureAwait(false);
        }

        #region Private-Methods

        private async Task<CheckRun> RunSlopAsync(string repo, string? branch, string? commit)
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                CheckRunService service = await CreateServiceAsync(testDb).ConfigureAwait(false);
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(CreateVessel(repo)).ConfigureAwait(false);

                return await service.RunAsync(Auth(), new CheckRunRequest
                {
                    VesselId = vessel.Id,
                    Type = CheckRunTypeEnum.Slop,
                    Label = "Slop",
                    BranchName = branch,
                    CommitHash = commit
                }).ConfigureAwait(false);
            }
        }

        private static async Task<CheckRunService> CreateServiceAsync(TestDatabase testDb)
        {
            await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
            VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
            return new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);
        }

        private static AuthContext Auth()
        {
            return AuthContext.Authenticated(_Tenant, _User, false, true, "UnitTest");
        }

        private static Vessel CreateVessel(string repo)
        {
            return new Vessel
            {
                TenantId = _Tenant,
                UserId = _User,
                Name = "Slop Vessel " + Guid.NewGuid().ToString("N"),
                RepoUrl = "file:///tmp/armada-slop-tests.git",
                LocalPath = repo,
                WorkingDirectory = repo,
                DefaultBranch = "main"
            };
        }

        private static async Task EnsureTenantAndUserAsync(TestDatabase testDb)
        {
            if (await testDb.Driver.Tenants.ReadAsync(_Tenant).ConfigureAwait(false) == null)
            {
                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata { Id = _Tenant, Name = _Tenant }).ConfigureAwait(false);
            }

            if (await testDb.Driver.Users.ReadByIdAsync(_User).ConfigureAwait(false) == null)
            {
                await testDb.Driver.Users.CreateAsync(new UserMaster
                {
                    Id = _User,
                    TenantId = _Tenant,
                    Email = _User + "@armada.test",
                    PasswordSha256 = UserMaster.ComputePasswordHash("password"),
                    IsTenantAdmin = true
                }).ConfigureAwait(false);
            }
        }

        private static async Task<string> CreateRepoAsync()
        {
            string repo = CreateTempDirectory("armada-slop-repo-");
            await GitAsync(repo, "init", "-b", "main").ConfigureAwait(false);
            await GitAsync(repo, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await GitAsync(repo, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(repo, "README.txt"), "slop check source\n").ConfigureAwait(false);
            await GitAsync(repo, "add", "README.txt").ConfigureAwait(false);
            await GitAsync(repo, "commit", "-m", "Initial commit").ConfigureAwait(false);
            return repo;
        }

        private static async Task<string> CreateRepoWithWorkBranchAsync(string relativePath, string fixtureName)
        {
            string repo = await CreateRepoAsync().ConfigureAwait(false);
            await GitAsync(repo, "checkout", "-b", "work").ConfigureAwait(false);
            WriteFixture(repo, relativePath, fixtureName);
            await GitAsync(repo, "add", "-A").ConfigureAwait(false);
            await GitAsync(repo, "commit", "-m", "Add work").ConfigureAwait(false);
            await GitAsync(repo, "checkout", "main").ConfigureAwait(false);
            return repo;
        }

        private static void WriteFixture(string repo, string relativePath, string fixtureName)
        {
            string target = Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, SlopDiffClassifierTests.Fixture(fixtureName));
        }

        private static string CreateTempDirectory(string prefix)
        {
            string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
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
                using (Process process = Process.Start(info)!)
                {
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        private static async Task<string> GitAsync(string workingDirectory, params string[] args)
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
            foreach (string arg in args) startInfo.ArgumentList.Add(arg);

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("git " + String.Join(" ", args) + " failed (exit " + process.ExitCode + "): " + stderr.Trim());
                return stdout.Trim();
            }
        }

        private static void SafeDeleteDirectory(string path)
        {
            if (!Directory.Exists(path)) return;
            try
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, true);
            }
            catch (IOException ex)
            {
                Console.WriteLine("  NOTE  could not remove temp directory " + path + ": " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine("  NOTE  could not remove temp directory " + path + ": " + ex.Message);
            }
        }

        #endregion
    }
}
