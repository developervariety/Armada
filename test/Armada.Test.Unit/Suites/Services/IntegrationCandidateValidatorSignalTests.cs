namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// A required integration check that the OS terminates by a signal (128 + signal; SIGTERM is 143) with no
    /// failing test named did not finish on its own -- a stray kill from another process sharing the host, not
    /// the merged tree. The validator re-runs it once and, when it is still interrupted, records the candidate as
    /// <see cref="IntegrationValidationOutcomeEnum.Unavailable"/> rather than
    /// <see cref="IntegrationValidationOutcomeEnum.Failed"/>, so the interruption refuses publication without a
    /// false code-failure rescue. A genuine non-signal failure still fails the candidate.
    /// </summary>
    public class IntegrationCandidateValidatorSignalTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Integration Candidate Validator Signal";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            if (OperatingSystem.IsWindows())
            {
                SkipTest("A signal-terminated integration check is not a candidate failure", "POSIX signal exit codes only");
                SkipTest("A genuine non-signal failure still fails the candidate", "POSIX signal exit codes only");
                return;
            }

            if (!IsGitOnPath())
            {
                const string reason = "git not found on PATH";
                SkipTest("A signal-terminated integration check is not a candidate failure", reason);
                SkipTest("A genuine non-signal failure still fails the candidate", reason);
                return;
            }

            await RunTest("A signal-terminated integration check is not a candidate failure", async () =>
            {
                // The UnitTest command reports every test passed, then is killed by SIGTERM (exit 143). No test
                // failed, so the kill is an environment interruption. Pre-fix the candidate was recorded Failed
                // (integration_validation_failed); it must instead re-run and, still interrupted, be Unavailable.
                IntegrationValidationResult result = await ValidateWithUnitTestCommandAsync(
                    "sig_interrupt",
                    "echo 'Passed!  - Failed:     0, Passed:   944, Skipped:     0, Total:   944'; kill -s TERM $$").ConfigureAwait(false);

                AssertEqual(IntegrationValidationOutcomeEnum.Unavailable, result.Outcome,
                    "a signal-terminated check with no failing test is an interruption, not a candidate failure");
                AssertFalse(result.Outcome == IntegrationValidationOutcomeEnum.Failed,
                    "an interruption must never read as a code failure that fires a rescue");
                AssertFalse(result.AllowsPublication, "an unmeasured candidate must not publish");
                AssertContains("integration_validation_interrupted", result.Reason);
            }).ConfigureAwait(false);

            await RunTest("A genuine non-signal failure still fails the candidate", async () =>
            {
                // A named failing test with an ordinary non-zero exit is a real failure of the combined tree and
                // must still fail the candidate, so the interruption path never weakens a genuine TestFail.
                IntegrationValidationResult result = await ValidateWithUnitTestCommandAsync(
                    "genuine_fail",
                    "echo '  Failed Example.Tests.ParserTests.Parse [1 ms]'; echo 'Failed!  - Failed: 1, Passed: 10, Total: 11'; exit 1").ConfigureAwait(false);

                AssertEqual(IntegrationValidationOutcomeEnum.Failed, result.Outcome,
                    "a named failing test is a candidate failure whatever else the run printed");
                AssertContains("integration_validation_failed", result.Reason);
            }).ConfigureAwait(false);
        }

        private async Task<IntegrationValidationResult> ValidateWithUnitTestCommandAsync(string slug, string unitTestCommand)
        {
            using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
            LoggingModule logging = CreateLogging();
            WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
            VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
            CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);
            ArmadaSettings settings = new ArmadaSettings();
            IntegrationCandidateValidator validator = new IntegrationCandidateValidator(
                logging, testDb.Driver, settings, checkRuns, workflowProfiles);

            string tenantId = "ten_icv_" + slug;
            string userId = "usr_icv_" + slug;
            await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

            string sourceRepo = await CreateSourceRepoAsync().ConfigureAwait(false);
            string integrationCommit = await RunGitAsync(sourceRepo, "rev-parse", "HEAD").ConfigureAwait(false);
            string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-icv-" + slug + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDirectory);

            try
            {
                Vessel vessel = new Vessel
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Name = "Integration Candidate Vessel",
                    RepoUrl = "file:///tmp/armada-icv.git",
                    LocalPath = sourceRepo,
                    WorkingDirectory = workingDirectory,
                    DefaultBranch = "main"
                };
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Name = "Integration Candidate Workflow",
                    Scope = WorkflowProfileScopeEnum.Vessel,
                    VesselId = vessel.Id,
                    UnitTestCommand = unitTestCommand,
                    IsDefault = true,
                    Active = true
                }).ConfigureAwait(false);

                IntegrationCandidate candidate = new IntegrationCandidate
                {
                    VesselId = vessel.Id,
                    LandingPath = "LocalMerge",
                    TargetBranch = "main",
                    SourceBranch = "feature",
                    IntegrationCommit = integrationCommit,
                    TenantId = tenantId,
                    UserId = userId
                };

                return await validator.ValidateAsync(vessel, candidate).ConfigureAwait(false);
            }
            finally
            {
                SafeDeleteDirectory(workingDirectory);
                SafeDeleteDirectory(sourceRepo);
            }
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
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
            catch
            {
                return false;
            }
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

        private static async Task<string> CreateSourceRepoAsync()
        {
            string repoPath = Path.Combine(Path.GetTempPath(), "armada-icv-src-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(repoPath);

            await RunGitAsync(repoPath, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(repoPath, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(repoPath, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            await File.WriteAllTextAsync(Path.Combine(repoPath, "README.txt"), "integration candidate source\n").ConfigureAwait(false);
            await RunGitAsync(repoPath, "add", "README.txt").ConfigureAwait(false);
            await RunGitAsync(repoPath, "commit", "-m", "Initial commit").ConfigureAwait(false);

            return repoPath;
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
    }
}
