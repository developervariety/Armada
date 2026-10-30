namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Coverage for the CheckRunService isolated-checkout behavior: Armada-generated Build and
    /// UnitTest check runs clone the vessel repo into a dedicated temp checkout and execute there
    /// instead of in the live <c>Vessel.WorkingDirectory</c>, so a running Admiral cannot lock build
    /// outputs and create false failures. When a repo source resolves but the clone fails, the check
    /// fails loudly instead of silently falling back to the live directory. Also covers the
    /// <c>--no-restore</c> strip on the executed command, the live-dir path when no repo source
    /// resolves, and genuine command failures inside the isolated checkout.
    /// </summary>
    public class CheckRunIsolatedCheckoutTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Check Run Isolated Checkout";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            // Loud-fail path: a repo source resolves (so readiness passes) but the clone fails because
            // LocalPath is not a real git repository. The check must fail without executing in the live
            // working directory. Runs even without git (the clone attempt fails fast and returns null).
            await RunTest("Build check fails loudly when isolated checkout cannot be created", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_iso_loudfail", "usr_iso_loudfail").ConfigureAwait(false);

                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-iso-loudfail-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    // LocalPath exists (ResolveRepoSource returns it) but is NOT a git repository, so
                    // TryCloneToTempAsync fails. The service must fail loudly instead of falling back.
                    Vessel vessel = CreateVessel("ten_iso_loudfail", "usr_iso_loudfail", workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_iso_loudfail",
                        UserId = "usr_iso_loudfail",
                        Name = "Loud Fail Build Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = PrintWorkingDirectoryCommand()
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_iso_loudfail", "usr_iso_loudfail", false, false, "UnitTest");
                    CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Build,
                        Label = "Build"
                    }).ConfigureAwait(false);

                    AssertLoudIsolationFailure(run, workingDirectory);
                }
                finally
                {
                    SafeDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);

            // No-repo-source path: when both LocalPath and RepoUrl are unset, ResolveRepoSource returns
            // null and the isolated check must still execute in the live WorkingDirectory and pass.
            // CommandOverride bypasses profile requirements so the check runs without a configured profile.
            await RunTest("Graceful live-dir run when no repo source resolves", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_iso_norepo", "usr_iso_norepo").ConfigureAwait(false);

                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-iso-norepo-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    Vessel vessel = new Vessel
                    {
                        TenantId = "ten_iso_norepo",
                        UserId = "usr_iso_norepo",
                        Name = "No Repo Source Vessel",
                        RepoUrl = String.Empty,
                        LocalPath = String.Empty,
                        WorkingDirectory = workingDirectory,
                        DefaultBranch = "main"
                    };
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    // A command override runs as the server process, so only a global administrator may send one.
                    AuthContext auth = AuthContext.Authenticated("ten_iso_norepo", "usr_iso_norepo", true, false, "UnitTest");
                    CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Build,
                        Label = "Build",
                        CommandOverride = PrintWorkingDirectoryCommand()
                    }).ConfigureAwait(false);

                    AssertEqual(CheckRunStatusEnum.Passed, run.Status);
                    AssertContains(workingDirectory, run.Output ?? String.Empty);
                    AssertFalse((run.Output ?? String.Empty).Contains("armada-chk-"), "No-repo-source check must execute in the live working directory, not an isolated checkout.");
                }
                finally
                {
                    SafeDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);

            await RunTest("Harbor CheckRun uses the owned host and ignores stale remote artifacts", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                const string tenantId = "ten_iso_harbor";
                const string userId = "usr_iso_harbor";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                string liveDirectory = Path.Combine(Path.GetTempPath(), "armada-harbor-check-local-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(liveDirectory);
                string localMarker = Path.Combine(liveDirectory, "local-command-ran.txt");
                try
                {
                    Vessel vessel = CreateVessel(tenantId, userId, liveDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    string runnerId = "runner-check-" + Guid.NewGuid().ToString("N");
                    string grantId = "grant-check-" + Guid.NewGuid().ToString("N");
                    AssertTrue(await testDb.Driver.HarborRunnerEnrollments.TryEnrollAsync(new HarborRunnerEnrollment
                    {
                        RunnerId = runnerId, TenantId = tenantId, UserId = userId, AuthMethod = "unit-test", Generation = 1, Active = true
                    }, 0).ConfigureAwait(false), "the runner owner is durably enrolled");
                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        Name = "Harbor CheckRun profile",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        IsDefault = true,
                        UnitTestCommand = OperatingSystem.IsWindows()
                            ? "echo LOCAL_EXECUTED>" + localMarker
                            : "printf LOCAL_EXECUTED > '" + localMarker + "'",
                        ExpectedArtifacts = new List<string> { "results.xml" }
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);
                    HarborSettings harborSettings = new HarborSettings
                    {
                        Enabled = true,
                        MissionRoutes = new List<HarborMissionRoute>
                        {
                            new HarborMissionRoute
                            {
                                RunnerId = runnerId,
                                CheckoutMode = HarborCheckoutModeEnum.HarborOwned,
                                RepositoryGrantId = grantId,
                                VesselId = vessel.Id
                            }
                        }
                    };
                    ArmadaSettings settings = new ArmadaSettings { Harbor = harborSettings };
                    HarborCheckRunChannel channel = new HarborCheckRunChannel();
                    channel.ExpectedCommandHash = HarborVerificationProtocol.CommandSha256(profile.UnitTestCommand!);
                    HarborCheckoutService checkouts = new HarborCheckoutService(testDb.Driver, settings);
                    checkouts.SetChannel(channel);
                    LoggingModule logging = CreateLogging();
                    WorkflowProfileService profiles = new WorkflowProfileService(testDb.Driver, logging);
                    VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, profiles, logging)
                    {
                        HarborSettings = harborSettings,
                        HarborCheckouts = checkouts
                    };
                    CheckRunService checkRuns = new CheckRunService(testDb.Driver, profiles, readiness, logging)
                    {
                        HarborSettings = harborSettings,
                        HarborCheckouts = checkouts
                    };
                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest");

                    channel.ArtifactIsFresh = false;
                    CheckRun stale = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id, Type = CheckRunTypeEnum.UnitTest, Label = "Harbor stale artifact"
                    }).ConfigureAwait(false);
                    AssertEqual(CheckRunStatusEnum.Passed, stale.Status, "the remote command result is accepted without local execution");
                    AssertEqual(2, stale.TestSummary?.Total ?? -1, "a stale remote artifact cannot replace the current console summary");
                    AssertEqual(2, stale.TestSummary?.Passed ?? -1, "the stale artifact's conflicting counts are ignored");
                    AssertContains("harbor:/dock/", stale.WorkingDirectory ?? String.Empty,
                        "the CheckRun records an opaque Harbor checkout address");
                    AssertFalse(File.Exists(localMarker), "the Harbor route never falls back to the local command");

                    channel.ArtifactIsFresh = true;
                    CheckRun fresh = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id, Type = CheckRunTypeEnum.UnitTest, Label = "Harbor fresh artifact"
                    }).ConfigureAwait(false);
                    AssertEqual(CheckRunStatusEnum.Passed, fresh.Status);
                    AssertEqual(9, fresh.TestSummary?.Total ?? -1, "a fresh runner artifact overrides conflicting console counts");
                    AssertEqual(9, fresh.TestSummary?.Passed ?? -1, "the fresh artifact counters are used");
                    AssertEqual(2, channel.VerificationCount, "each stable CheckRun attempt starts one configured host verification");
                    AssertTrue(channel.AllVerificationKeysMatch, "the host receives the fixed Check unit-test key and exact command hash");
                    AssertFalse(File.Exists(localMarker), "repeated Harbor checks still do not run the local command");

                    profile.GateCommandTimeoutSeconds = 30;
                    profile.IsolatedTestRerunCommand = "echo isolated";
                    profile.ExpectedArtifacts = new List<string>();
                    await testDb.Driver.WorkflowProfiles.UpdateAsync(profile).ConfigureAwait(false);
                    async Task<CheckRun> RunBudgetedAsync(bool pending = false, CheckRunService? service = null)
                    {
                        CheckRunService selected = service ?? checkRuns;
                        if (!pending)
                            return await selected.RunAsync(auth, new CheckRunRequest
                            {
                                VesselId = vessel.Id, WorkflowProfileId = profile.Id, Type = CheckRunTypeEnum.UnitTest
                            }).ConfigureAwait(false);
                        CheckRun armed = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                        {
                            TenantId = tenantId, UserId = userId, VesselId = vessel.Id, WorkflowProfileId = profile.Id,
                            Type = CheckRunTypeEnum.UnitTest, Status = CheckRunStatusEnum.Pending,
                            Command = profile.UnitTestCommand, WorkingDirectory = liveDirectory
                        }).ConfigureAwait(false);
                        return await selected.RunPendingAsync(auth, armed.Id).ConfigureAwait(false);
                    }
                    foreach (bool pending in new[] { false, true })
                    {
                        channel.FirstExitCode = 1;
                        channel.FirstDurationMilliseconds = 28001;
                        channel.RetryDurationMilliseconds = 500;
                        channel.VerificationTimeouts.Clear();
                        CheckRun fits = await RunBudgetedAsync(pending).ConfigureAwait(false);
                        AssertEqual(CheckRunStatusEnum.Passed, fits.Status, "a measured Harbor retry can fit the remainder: " + fits.Summary + "\n" + fits.Output);
                        AssertEqual("30,1", String.Join(",", channel.VerificationTimeouts),
                            "both execution paths pass the profile budget and floor the remote retry remainder");
                        AssertContains("full run failed", fits.Summary ?? String.Empty);

                        channel.RetryDurationMilliseconds = 2500;
                        CheckRun late = await RunBudgetedAsync(pending).ConfigureAwait(false);
                        AssertEqual(CheckRunStatusEnum.Failed, late.Status, "a late zero exit cannot pass beyond the shared Harbor budget");
                    }
                    channel.FirstDurationMilliseconds = 29501;
                    int beforeNoRemainder = channel.VerificationCount;
                    CheckRun fractional = await RunBudgetedAsync().ConfigureAwait(false);
                    AssertEqual(CheckRunStatusEnum.Failed, fractional.Status);
                    AssertEqual(beforeNoRemainder + 1, channel.VerificationCount,
                        "a remote retry with less than one whole second remaining must not start");
                    foreach (bool pending in new[] { false, true })
                    {
                        foreach (int firstExit in new[] { 0, 1 })
                        foreach (long? unknown in new long?[] { null, -1 })
                        {
                            channel.FirstExitCode = firstExit;
                            channel.FirstDurationMilliseconds = unknown;
                            int beforeUnknown = channel.VerificationCount;
                            CheckRun missingTime = await RunBudgetedAsync(pending).ConfigureAwait(false);
                            AssertEqual(CheckRunStatusEnum.Failed, missingTime.Status,
                                "a started Harbor command without valid duration cannot establish shared-budget proof");
                            AssertEqual(beforeUnknown + 1, channel.VerificationCount, "unknown duration cannot fund another retry");
                        }
                        channel.FirstExitCode = 0;
                        channel.FirstDurationMilliseconds = 30001;
                        CheckRun overrun = await RunBudgetedAsync(pending).ConfigureAwait(false);
                        AssertEqual(CheckRunStatusEnum.Failed, overrun.Status, "an over-budget first command cannot pass either entry point");
                    }

                    profile.GateCommandTimeoutSeconds = 3600;
                    await testDb.Driver.WorkflowProfiles.UpdateAsync(profile).ConfigureAwait(false);
                    channel.FirstDurationMilliseconds = 9;
                    channel.VerificationTimeouts.Clear();
                    CheckRun capped = await RunBudgetedAsync().ConfigureAwait(false);
                    AssertEqual(CheckRunStatusEnum.Passed, capped.Status);
                    AssertEqual(1800, channel.VerificationTimeouts[0], "the Harbor protocol retains its stricter timeout cap");

                    CheckRunService shortRemote = new CheckRunService(testDb.Driver, profiles, readiness, logging,
                        commandTimeout: TimeSpan.FromMilliseconds(1200))
                    {
                        HarborSettings = harborSettings, HarborCheckouts = checkouts
                    };
                    channel.FirstExitCode = 1;
                    channel.FirstDurationMilliseconds = channel.RetryDurationMilliseconds = 100;
                    channel.ProvisionDelay = channel.OutputReadDelay = TimeSpan.FromMilliseconds(150);
                    channel.VerificationTimeouts.Clear();
                    CheckRun transportWait = await RunBudgetedAsync(service: shortRemote).ConfigureAwait(false);
                    AssertEqual(CheckRunStatusEnum.Passed, transportWait.Status,
                        "provisioning and output retrieval must not spend runner-reported execution budget");
                    AssertEqual("1,1", String.Join(",", channel.VerificationTimeouts));
                    AssertFalse(File.Exists(localMarker), "all budget cases remain Harbor-owned without local fallback");
                }
                finally
                {
                    SafeDeleteDirectory(liveDirectory);
                }
            }).ConfigureAwait(false);

            if (!IsGitOnPath())
            {
                // Every case below drives a real repository. Report each one as a named skip, so a host without git
                // shows 13 skips instead of a passing suite with no git-backed cases.
                const string reason = "git not found on PATH";
                SkipTest("Build check fails with real output when command fails in isolated checkout", reason);
                SkipTest("Build check executes in isolated temp checkout, not the live working directory", reason);
                SkipTest("Locked file in live working directory does not fail the Build check", reason);
                SkipTest("Executed isolated command strips --no-restore but stored command is unchanged", reason);
                SkipTest("Non-isolated check type runs in live working directory even with a repo source", reason);
                SkipTest("Build check targeting a non-default BranchName runs against that branch's content", reason);
                SkipTest("Build check targeting a non-default CommitHash runs against that commit's content", reason);
                SkipTest("Build check fails before the command when explicit CommitHash is unreachable", reason);
                SkipTest("Build check fails before the command when clone commit and branch are unavailable", reason);
                SkipTest("Isolated checkout matches dock-worktree bytes when the repo config sets core.autocrlf", reason);
                SkipTest("Isolated checkout removes its worktree registration after the check completes", reason);
                SkipTest("git worktree prune drops an unlocked worktree it cannot see and keeps a locked one", reason);
                SkipTest("Isolated checkout worktree is locked while the check command runs and named for its check", reason);
                return;
            }

            // Goal-3 guard: a real non-zero exit inside the isolated checkout must still fail with
            // genuine command output; the isolation path must not mask real build/test failures.
            await RunTest("Build check fails with real output when command fails in isolated checkout", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_iso_cmdfail", "usr_iso_cmdfail").ConfigureAwait(false);

                string sourceRepo = await CreateSourceRepoAsync().ConfigureAwait(false);
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-iso-cmdfail-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    Vessel vessel = CreateVessel("ten_iso_cmdfail", "usr_iso_cmdfail", workingDirectory);
                    vessel.LocalPath = sourceRepo;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_iso_cmdfail",
                        UserId = "usr_iso_cmdfail",
                        Name = "Isolated Command Failure Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = FailingCommandWithMarker("ISOLATEDCMDFAILMARKER")
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_iso_cmdfail", "usr_iso_cmdfail", false, false, "UnitTest");
                    CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Build,
                        Label = "Build"
                    }).ConfigureAwait(false);

                    AssertEqual(CheckRunStatusEnum.Failed, run.Status);
                    AssertTrue((run.ExitCode ?? 0) != 0, "Failed isolated check must preserve the non-zero exit code.");
                    AssertContains("ISOLATEDCMDFAILMARKER", run.Output ?? String.Empty);
                    AssertFalse((run.Output ?? String.Empty).Contains(workingDirectory), "Command failure must occur in the isolated checkout, not the live working directory.");
                }
                finally
                {
                    SafeDeleteDirectory(workingDirectory);
                    SafeDeleteDirectory(sourceRepo);
                }
            }).ConfigureAwait(false);

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

                    // A command override runs as the server process, so only a global administrator may send one.
                    AuthContext auth = AuthContext.Authenticated("ten_iso_lint", "usr_iso_lint", true, false, "UnitTest");
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
                        BuildCommand = PrintHeadAndFileCommand(source.MarkerFileName)
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
                    AssertEqual(source.FeatureCommitHash, run.CommitHash);
                    AssertContains(source.FeatureCommitHash, run.Output ?? String.Empty);
                    AssertContains(source.MarkerContent, run.Output ?? String.Empty);
                }
                finally
                {
                    SafeDeleteDirectory(workingDirectory);
                    SafeDeleteDirectory(source.RepoPath);
                }
            }).ConfigureAwait(false);

            // An explicit CommitHash is authoritative. If it cannot be resolved, the check must fail
            // before the command runs, even when a valid branch is available.
            await RunTest("Build check fails before the command when explicit CommitHash is unreachable", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_iso_fb", "usr_iso_fb").ConfigureAwait(false);

                SourceRepoWithRef source = await CreateSourceRepoWithFeatureRefAsync().ConfigureAwait(false);
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-iso-fb-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);
                const string unreachableCommitHash = "0123456789abcdef0123456789abcdef01234567";
                const string commandMarker = "UNREACHABLE_COMMIT_CHECK_COMMAND_RAN";

                try
                {
                    Vessel vessel = CreateVessel("ten_iso_fb", "usr_iso_fb", workingDirectory);
                    vessel.LocalPath = source.RepoPath;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_iso_fb",
                        UserId = "usr_iso_fb",
                        Name = "Fallback Ref Build Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = PrintMarkerAndFileCommand(commandMarker, source.MarkerFileName)
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_iso_fb", "usr_iso_fb", false, false, "UnitTest");
                    CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Build,
                        Label = "Build",
                        CommitHash = unreachableCommitHash,
                        BranchName = source.FeatureBranch
                    }).ConfigureAwait(false);

                    string output = run.Output ?? String.Empty;
                    AssertFalse(
                        output.Contains(commandMarker, StringComparison.Ordinal),
                        "An unavailable explicit CommitHash must fail before running the check command; output: " + output);
                    AssertFalse(
                        output.Contains(source.MarkerContent, StringComparison.Ordinal),
                        "An unavailable explicit CommitHash must not fall through to the feature branch; output: " + output);
                    AssertEqual(CheckRunStatusEnum.Failed, run.Status);
                    AssertEqual(-1, run.ExitCode ?? 0);
                    AssertEqual(unreachableCommitHash, run.CommitHash);

                }
                finally
                {
                    SafeDeleteDirectory(workingDirectory);
                    SafeDeleteDirectory(source.RepoPath);
                }
            }).ConfigureAwait(false);

            // The clone path must reject the requested hash when neither it nor the named branch
            // resolves. Returning the clone default would run a command against an unrelated tree.
            await RunTest("Build check fails before the command when clone commit and branch are unavailable", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                    VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                    CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                    await EnsureTenantAndUserAsync(testDb, "ten_iso_clonefb", "usr_iso_clonefb").ConfigureAwait(false);

                    SourceRepoWithRef source = await CreateSourceRepoWithFeatureRefAsync().ConfigureAwait(false);
                    string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-iso-clonefb-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(workingDirectory);
                    const string unreachableCommitHash = "0123456789abcdef0123456789abcdef01234567";
                    const string commandMarker = "CLONE_UNREACHABLE_COMMIT_COMMAND_RAN";

                    try
                    {
                        Vessel vessel = CreateVessel("ten_iso_clonefb", "usr_iso_clonefb", workingDirectory);
                        vessel.LocalPath = String.Empty;
                        vessel.RepoUrl = source.RepoPath;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        WorkflowProfile profile = new WorkflowProfile
                        {
                            TenantId = "ten_iso_clonefb",
                            UserId = "usr_iso_clonefb",
                            Name = "Clone Fallback Ref Build Workflow",
                            Scope = WorkflowProfileScopeEnum.Vessel,
                            VesselId = vessel.Id,
                            BuildCommand = PrintMarkerAndFileCommand(commandMarker, source.MarkerFileName)
                        };
                        await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                        AuthContext auth = AuthContext.Authenticated("ten_iso_clonefb", "usr_iso_clonefb", false, false, "UnitTest");
                        CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                        {
                            VesselId = vessel.Id,
                            Type = CheckRunTypeEnum.Build,
                            Label = "Build",
                            CommitHash = unreachableCommitHash,
                            BranchName = "missing"
                        }).ConfigureAwait(false);

                        string output = run.Output ?? String.Empty;
                        AssertFalse(
                            output.Contains(commandMarker, StringComparison.Ordinal),
                            "A failed commit and branch resolution must fail before running the check command; output: " + output);
                        AssertEqual(CheckRunStatusEnum.Failed, run.Status);
                        AssertEqual(-1, run.ExitCode ?? 0);
                        AssertEqual(unreachableCommitHash, run.CommitHash);

                        CheckRun branchRun = await checkRuns.RunAsync(auth, new CheckRunRequest
                        {
                            VesselId = vessel.Id,
                            Type = CheckRunTypeEnum.Build,
                            Label = "Build branch-only clone",
                            BranchName = source.FeatureBranch
                        }).ConfigureAwait(false);

                        AssertEqual(CheckRunStatusEnum.Passed, branchRun.Status);
                        AssertContains(source.MarkerContent, branchRun.Output ?? String.Empty);
                    }
                    finally
                    {
                        SafeDeleteDirectory(workingDirectory);
                        SafeDeleteDirectory(source.RepoPath);
                    }
                }
            }).ConfigureAwait(false);

            // Dock-faithfulness regression (run_check EOL): a repository whose own config carries
            // core.autocrlf=true checks out CRLF in a worktree (how mission docks check out), but a
            // fresh clone ignores the source repo's local config and checks out LF. The isolated
            // checkout must match the dock worktree bytes, or byte-exact manifest tests false-red
            // under run_check. With the old clone-based isolation the file hash differs from the
            // worktree hash and this test fails; with worktree-based isolation it matches.
            await RunTest("Isolated checkout matches dock-worktree bytes when the repo config sets core.autocrlf", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_iso_eol", "usr_iso_eol").ConfigureAwait(false);

                string sourceRepo = await CreateAutocrlfSourceRepoAsync().ConfigureAwait(false);
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-iso-eol-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);
                string referenceWorktree = Path.Combine(Path.GetTempPath(), "armada-iso-eol-wt-" + Guid.NewGuid().ToString("N"));

                try
                {
                    // Dock-equivalent reference: a detached worktree cut from the same repo.
                    await RunGitAsync(sourceRepo, "worktree", "add", "--detach", referenceWorktree, "HEAD").ConfigureAwait(false);

                    string trackedFile = "eol-tracked.txt";
                    string expectedHash = ComputeFileSha256(Path.Combine(referenceWorktree, trackedFile));

                    Vessel vessel = CreateVessel("ten_iso_eol", "usr_iso_eol", workingDirectory);
                    vessel.LocalPath = sourceRepo;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_iso_eol",
                        UserId = "usr_iso_eol",
                        Name = "EOL Faithful Build Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = HashFileCommand(trackedFile)
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_iso_eol", "usr_iso_eol", false, false, "UnitTest");
                    CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Build,
                        Label = "Build"
                    }).ConfigureAwait(false);

                    AssertEqual(CheckRunStatusEnum.Passed, run.Status);
                    AssertContains(expectedHash, run.Output ?? String.Empty);
                }
                finally
                {
                    SafeDeleteDirectory(referenceWorktree);
                    SafeDeleteDirectory(workingDirectory);
                    SafeDeleteDirectory(sourceRepo);
                }
            }).ConfigureAwait(false);

            // Cleanup regression: a worktree-backed isolated checkout must be removed from the
            // repository's worktree registration when the check completes, so repeated checks do not
            // accumulate stale worktree entries in the shared vessel repository.
            await RunTest("Build check of an objective that provisions a sibling pin runs beside that pinned sibling and receives the pin", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_iso_pin", "usr_iso_pin").ConfigureAwait(false);

                SourceRepoWithRef producerRepo = await CreateSourceRepoWithFeatureRefAsync().ConfigureAwait(false);
                string consumerRepo = await CreateSourceRepoAsync().ConfigureAwait(false);
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-iso-pin-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    Vessel producer = CreateVessel("ten_iso_pin", "usr_iso_pin", producerRepo.RepoPath);
                    producer.Name = "PinProducer";
                    producer = await testDb.Driver.Vessels.CreateAsync(producer).ConfigureAwait(false);

                    Vessel consumer = CreateVessel("ten_iso_pin", "usr_iso_pin", workingDirectory);
                    consumer.Name = "PinConsumer";
                    consumer.LocalPath = consumerRepo;
                    consumer.SiblingRepos = System.Text.Json.JsonSerializer.Serialize(new List<SiblingRepo>
                    {
                        new SiblingRepo { VesselRef = producer.Id, RelativePath = "../Producer", BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly, DefaultBranch = "main" }
                    });
                    consumer = await testDb.Driver.Vessels.CreateAsync(consumer).ConfigureAwait(false);

                    // The pin is the producer's unlanded feature commit; its main never has the marker.
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("pinned check voyage") { TenantId = "ten_iso_pin", UserId = "usr_iso_pin" }).ConfigureAwait(false);
                    Objective objective = new Objective { TenantId = "ten_iso_pin", UserId = "usr_iso_pin", Title = "measure against the candidate" };
                    objective.VoyageIds.Add(voyage.Id);
                    objective.Preparation.RequiredSiblingInputs.Add(new ObjectivePreparationSiblingInput
                    {
                        VesselRef = producer.Id,
                        RelativePath = "../Producer",
                        PinnedCommit = producerRepo.FeatureCommitHash.Substring(0, 12),
                        ProvisionPinnedCommit = true
                    });
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("pinned consumer stage")
                    {
                        TenantId = "ten_iso_pin",
                        UserId = "usr_iso_pin",
                        VoyageId = voyage.Id,
                        VesselId = consumer.Id
                    }).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_iso_pin",
                        UserId = "usr_iso_pin",
                        Name = "Pinned Sibling Build Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = consumer.Id,
                        BuildCommand = OperatingSystem.IsWindows()
                            ? "git -C ..\\Producer rev-parse HEAD & echo pin=%ARMADA_SIBLING_PIN_PRODUCER% & type ..\\Producer\\" + producerRepo.MarkerFileName
                            : "git -C ../Producer rev-parse HEAD && echo pin=$ARMADA_SIBLING_PIN_PRODUCER && cat ../Producer/" + producerRepo.MarkerFileName
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_iso_pin", "usr_iso_pin", false, false, "UnitTest");
                    CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = consumer.Id,
                        MissionId = mission.Id,
                        Type = CheckRunTypeEnum.Build,
                        Label = "Build"
                    }).ConfigureAwait(false);

                    string output = run.Output ?? String.Empty;
                    AssertEqual(CheckRunStatusEnum.Passed, run.Status, output);
                    AssertContains(producerRepo.FeatureCommitHash + "\n", output.Replace("\r\n", "\n"));
                    AssertContains("pin=" + producerRepo.FeatureCommitHash, output);
                    AssertContains(producerRepo.MarkerContent, output);

                    string producerWorktrees = await RunGitAsync(producerRepo.RepoPath, "worktree", "list").ConfigureAwait(false);
                    AssertFalse(producerWorktrees.Contains("armada-chk-", StringComparison.OrdinalIgnoreCase),
                        "the pinned sibling worktree is unregistered after the check completes");
                    AssertEqual("main", await RunGitAsync(producerRepo.RepoPath, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false),
                        "the producer repository's own checkout is not moved");
                }
                finally
                {
                    SafeDeleteDirectory(workingDirectory);
                    SafeDeleteDirectory(consumerRepo);
                    SafeDeleteDirectory(producerRepo.RepoPath);
                }
            }).ConfigureAwait(false);

            await RunTest("Isolated checkout removes its worktree registration after the check completes", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_iso_wtclean", "usr_iso_wtclean").ConfigureAwait(false);

                string sourceRepo = await CreateSourceRepoAsync().ConfigureAwait(false);
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-iso-wtclean-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    Vessel vessel = CreateVessel("ten_iso_wtclean", "usr_iso_wtclean", workingDirectory);
                    vessel.LocalPath = sourceRepo;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_iso_wtclean",
                        UserId = "usr_iso_wtclean",
                        Name = "Worktree Cleanup Build Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = PrintWorkingDirectoryCommand()
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_iso_wtclean", "usr_iso_wtclean", false, false, "UnitTest");
                    CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Build,
                        Label = "Build"
                    }).ConfigureAwait(false);

                    AssertEqual(CheckRunStatusEnum.Passed, run.Status);

                    string worktreeList = await RunGitAsync(sourceRepo, "worktree", "list").ConfigureAwait(false);
                    AssertFalse(
                        worktreeList.Contains("armada-chk-", StringComparison.OrdinalIgnoreCase),
                        "The isolated checkout worktree must be unregistered after the check completes.");
                }
                finally
                {
                    SafeDeleteDirectory(workingDirectory);
                    SafeDeleteDirectory(sourceRepo);
                }
            }).ConfigureAwait(false);

            // Premise: a prune run from a view of the repository where a worktree's directory does not
            // exist removes that worktree's admin entry, unless the worktree is locked.
            await RunTest("git worktree prune drops an unlocked worktree it cannot see and keeps a locked one", async () =>
            {
                string sourceRepo = await CreateSourceRepoAsync().ConfigureAwait(false);
                string parent = Path.Combine(Path.GetTempPath(), "armada-iso-prune-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(parent);

                try
                {
                    string open = Path.Combine(parent, "wt-open");
                    string held = Path.Combine(parent, "wt-held");
                    await RunGitAsync(sourceRepo, "worktree", "add", "--detach", open, "HEAD").ConfigureAwait(false);
                    await RunGitAsync(sourceRepo, "worktree", "add", "--detach", held, "HEAD").ConfigureAwait(false);
                    await RunGitAsync(sourceRepo, "worktree", "lock", "--reason", "check run in progress", held).ConfigureAwait(false);

                    // Hide both directories from this repository's view of the filesystem.
                    Directory.Move(open, open + "-elsewhere");
                    Directory.Move(held, held + "-elsewhere");

                    await RunGitAsync(sourceRepo, "worktree", "prune").ConfigureAwait(false);
                    string list = await RunGitAsync(sourceRepo, "worktree", "list", "--porcelain").ConfigureAwait(false);

                    AssertFalse(list.Contains("wt-open", StringComparison.Ordinal), "an unlocked invisible worktree is pruned: " + list);
                    AssertTrue(list.Contains("wt-held", StringComparison.Ordinal), "a locked invisible worktree survives prune: " + list);
                }
                finally
                {
                    SafeDeleteDirectory(parent);
                    SafeDeleteDirectory(sourceRepo);
                }
            }).ConfigureAwait(false);

            // The checkout is made before the wait for the host command slot, so a long wait leaves time for
            // something else to remove it. Once the slot is granted the check makes the checkout again and
            // runs; it never runs its command in a directory that is gone.
            await RunTest("Build check makes its isolated checkout again when it was removed while the check waited for the host slot", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_iso_regone", "usr_iso_regone").ConfigureAwait(false);

                string sourceRepo = await CreateSourceRepoAsync().ConfigureAwait(false);
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-iso-regone-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);
                IDisposable? holder = null;

                try
                {
                    Vessel vessel = CreateVessel("ten_iso_regone", "usr_iso_regone", workingDirectory);
                    vessel.LocalPath = sourceRepo;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_iso_regone",
                        UserId = "usr_iso_regone",
                        Name = "Removed Checkout Build Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = PrintWorkingDirectoryCommand()
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    holder = await HostWideCommandLock.AcquireAsync("removed_checkout_holder", "holder", CancellationToken.None).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_iso_regone", "usr_iso_regone", false, false, "UnitTest");
                    Task<CheckRun> running = checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Build,
                        Label = "Build"
                    });

                    // A request waits for the slot only after its checkout exists.
                    string? waitingId = null;
                    DateTime deadline = DateTime.UtcNow.AddSeconds(60);
                    while (waitingId == null && DateTime.UtcNow < deadline && !running.IsCompleted)
                    {
                        foreach (HostSlotWaiter waiter in HostWideCommandLock.Snapshot().Waiters)
                        {
                            if (waiter.Key != null && waiter.Key.StartsWith("chk_", StringComparison.Ordinal)) waitingId = waiter.Key;
                        }

                        if (waitingId == null) await Task.Delay(50).ConfigureAwait(false);
                    }

                    AssertNotNull(waitingId, "the check must wait for the host slot held by the test");
                    string[] checkouts = Directory.GetDirectories(Path.GetTempPath(), CheckRunService.CheckoutDirectoryPrefix + waitingId + "-*");
                    AssertEqual(1, checkouts.Length, "the waiting check has one isolated checkout");
                    string removed = checkouts[0];
                    Directory.Delete(removed, true);
                    AssertFalse(Directory.Exists(removed), "the checkout is removed while the check waits");

                    holder.Dispose();
                    holder = null;
                    CheckRun run = await running.WaitAsync(TimeSpan.FromSeconds(120)).ConfigureAwait(false);

                    string output = run.Output ?? String.Empty;
                    AssertEqual(CheckRunStatusEnum.Passed, run.Status, "the check runs in a new checkout once it holds the slot: " + output);
                    AssertContains(CheckRunService.CheckoutDirectoryPrefix + run.Id, output, "the command runs in an isolated checkout named for its check");
                    AssertFalse(output.Contains(removed, StringComparison.Ordinal), "the command must not run in the removed checkout: " + output);
                    AssertFalse(output.Contains(workingDirectory, StringComparison.Ordinal), "the command must not fall back to the live working directory");
                }
                finally
                {
                    holder?.Dispose();
                    SafeDeleteDirectory(workingDirectory);
                    SafeDeleteDirectory(sourceRepo);
                }
            }).ConfigureAwait(false);

            await RunTest("Isolated checkout worktree is locked while the check command runs and named for its check", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_iso_wtlock", "usr_iso_wtlock").ConfigureAwait(false);

                string sourceRepo = await CreateSourceRepoAsync().ConfigureAwait(false);
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-iso-wtlock-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    Vessel vessel = CreateVessel("ten_iso_wtlock", "usr_iso_wtlock", workingDirectory);
                    vessel.LocalPath = sourceRepo;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_iso_wtlock",
                        UserId = "usr_iso_wtlock",
                        Name = "Worktree Lock Build Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = "git worktree list --porcelain"
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_iso_wtlock", "usr_iso_wtlock", false, false, "UnitTest");
                    CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Build,
                        Label = "Build"
                    }).ConfigureAwait(false);

                    string output = run.Output ?? String.Empty;
                    AssertEqual(CheckRunStatusEnum.Passed, run.Status, output);
                    AssertTrue(output.Contains("\nlocked", StringComparison.Ordinal), "the running checkout must be locked: " + output);
                    AssertContains("armada-chk-" + run.Id, output, "the checkout directory must name its check run");

                    string worktreeList = await RunGitAsync(sourceRepo, "worktree", "list").ConfigureAwait(false);
                    AssertFalse(
                        worktreeList.Contains("armada-chk-", StringComparison.OrdinalIgnoreCase),
                        "the locked worktree must still be unregistered after the check completes: " + worktreeList);
                }
                finally
                {
                    SafeDeleteDirectory(workingDirectory);
                    SafeDeleteDirectory(sourceRepo);
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

        private static string FailingCommandWithMarker(string marker)
        {
            return OperatingSystem.IsWindows()
                ? "echo " + marker + " & exit 1"
                : "echo " + marker + "; exit 1";
        }

        private void AssertLoudIsolationFailure(CheckRun run, string liveWorkingDirectory)
        {
            AssertEqual(CheckRunStatusEnum.Failed, run.Status);
            AssertEqual(-1, run.ExitCode ?? 0);

            string output = run.Output ?? String.Empty;
            AssertFalse(
                output.Contains(liveWorkingDirectory, StringComparison.OrdinalIgnoreCase),
                "Check must not execute in the live working directory when isolated checkout cannot be created.");
            AssertFalse(
                output.Contains("armada-chk-", StringComparison.OrdinalIgnoreCase),
                "Check must not execute inside a partial isolated checkout path.");

            bool hasIsolationMessage =
                output.IndexOf("isolated", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("checkout", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("clone", StringComparison.OrdinalIgnoreCase) >= 0;
            AssertTrue(
                hasIsolationMessage,
                "Failure output must contain a self-contained isolated-checkout error message.");
            AssertFalse(String.IsNullOrWhiteSpace(output), "Failure output must not be empty.");
        }

        private static string PrintFileCommand(string fileName)
        {
            return OperatingSystem.IsWindows() ? "type " + fileName : "cat " + fileName;
        }

        private static string PrintMarkerAndFileCommand(string marker, string fileName)
        {
            return OperatingSystem.IsWindows()
                ? "echo " + marker + " & type " + fileName
                : "echo " + marker + "; cat " + fileName;
        }

        private static string PrintHeadAndFileCommand(string fileName)
        {
            return OperatingSystem.IsWindows()
                ? "git rev-parse HEAD & type " + fileName
                : "git rev-parse HEAD && cat " + fileName;
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

        private sealed class HarborCheckRunChannel : IHarborWorkspaceOperations
        {
            private readonly Dictionary<string, HarborVerificationOutput> _Outputs = new Dictionary<string, HarborVerificationOutput>(StringComparer.Ordinal);
            private readonly byte[] _Artifact = Encoding.UTF8.GetBytes("<testsuite tests=\"9\" failures=\"0\" errors=\"0\" skipped=\"0\" />");
            private DateTime _VerificationStartedUtc;

            public bool ArtifactIsFresh { get; set; }
            public int VerificationCount { get; private set; }
            public bool AllVerificationKeysMatch { get; private set; } = true;
            public string ExpectedCommandHash { get; set; } = String.Empty;
            public int FirstExitCode { get; set; }
            public long? FirstDurationMilliseconds { get; set; } = 9;
            public long? RetryDurationMilliseconds { get; set; } = 9;
            public TimeSpan ProvisionDelay { get; set; }
            public TimeSpan OutputReadDelay { get; set; }
            public List<int> VerificationTimeouts { get; } = new List<int>();

            public async Task<HarborWorkspaceOperationResult> ExecuteAsync(
                AuthContext auth, HarborWorkspaceOperationRequest request, CancellationToken token = default)
            {
                if (request.Operation == HarborWorkspaceOperationEnum.ProvisionCheckout && ProvisionDelay > TimeSpan.Zero)
                    await Task.Delay(ProvisionDelay, token).ConfigureAwait(false);
                if (request.Operation == HarborWorkspaceOperationEnum.ReadVerificationOutput && OutputReadDelay > TimeSpan.Zero)
                    await Task.Delay(OutputReadDelay, token).ConfigureAwait(false);
                if (!auth.IsAuthenticated || auth.TenantId != request.TenantId || auth.UserId != request.UserId)
                    throw new InvalidOperationException("Harbor CheckRun used an unexpected owner context.");
                HarborWorkspaceOperationResult result = new HarborWorkspaceOperationResult
                {
                    RequestId = request.RequestId,
                    Operation = request.Operation,
                    Accepted = true,
                    Binding = request.Binding
                };
                switch (request.Operation)
                {
                    case HarborWorkspaceOperationEnum.InspectRepository:
                        result.RepositoryFingerprint = request.ExpectedRepositoryFingerprint;
                        result.HeadCommit = new string('a', 40);
                        result.BranchName = request.RequestedRevision;
                        break;
                    case HarborWorkspaceOperationEnum.ProvisionCheckout:
                        result.HeadCommit = request.Binding!.BaseCommit;
                        result.BranchName = request.Detached ? "(detached)" : request.Binding.BranchName;
                        break;
                    case HarborWorkspaceOperationEnum.RunVerification:
                        VerificationCount++;
                        string checkId = request.Verification!.CheckId;
                        _VerificationStartedUtc = DateTime.UtcNow;
                        AllVerificationKeysMatch &= request.Verification.CommandKey == "check-unit-test"
                            && request.Verification.ExpectedCommandSha256 == ExpectedCommandHash;
                        bool retry = request.Verification.Rerun != null;
                        VerificationTimeouts.Add(request.Verification.TimeoutSeconds);
                        byte[] stdout = Encoding.UTF8.GetBytes(!retry && FirstExitCode != 0
                            ? "Failed Example.Tests.Case [1 ms]\n0 passed; 1 failed.\n"
                            : "Tests: 2 passed, 0 failed, 2 total\n");
                        byte[] stderr = Array.Empty<byte>();
                        string outputId = "fake-output-" + checkId;
                        _Outputs[outputId] = new HarborVerificationOutput(stdout, stderr);
                        result.ExitCode = retry ? 0 : FirstExitCode;
                        result.StartedUtc = _VerificationStartedUtc;
                        result.DurationMilliseconds = retry ? RetryDurationMilliseconds : FirstDurationMilliseconds;
                        result.VerificationOutputId = outputId;
                        result.VerificationCheckId = checkId;
                        result.StandardOutputLength = stdout.LongLength;
                        result.StandardOutputRetainedBytes = stdout.LongLength;
                        result.StandardOutputSha256 = Hash(stdout);
                        result.StandardErrorLength = 0;
                        result.StandardErrorRetainedBytes = 0;
                        result.StandardErrorSha256 = Hash(stderr);
                        break;
                    case HarborWorkspaceOperationEnum.ReadVerificationOutput:
                        HarborVerificationOutputRequest outputRequest = request.VerificationOutput!;
                        HarborVerificationOutput output = _Outputs[outputRequest.OutputId];
                        byte[] bytes = outputRequest.StandardError ? output.Stderr : output.Stdout;
                        int offset = checked((int)outputRequest.Offset);
                        int length = Math.Min(outputRequest.MaxBytes, bytes.Length - offset);
                        result.VerificationOutputId = outputRequest.OutputId;
                        result.VerificationCheckId = outputRequest.CheckId;
                        result.Length = bytes.Length;
                        result.BytesBase64 = Convert.ToBase64String(bytes, offset, length);
                        break;
                    case HarborWorkspaceOperationEnum.Stat:
                        result.Exists = true;
                        result.Length = _Artifact.LongLength;
                        result.LastWriteUtc = ArtifactIsFresh ? _VerificationStartedUtc.AddSeconds(1) : _VerificationStartedUtc.AddSeconds(-1);
                        break;
                    case HarborWorkspaceOperationEnum.ReadBytes:
                        result.Length = _Artifact.LongLength;
                        result.BytesBase64 = Convert.ToBase64String(_Artifact);
                        break;
                    default:
                        result.Accepted = false;
                        result.Reason = "fake_operation_unsupported";
                        break;
                }
                return result;
            }

            private static string Hash(byte[] bytes)
                => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            private sealed class HarborVerificationOutput
            {
                public HarborVerificationOutput(byte[] stdout, byte[] stderr) { Stdout = stdout; Stderr = stderr; }
                public byte[] Stdout { get; }
                public byte[] Stderr { get; }
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
        /// Create a source repo whose own local config sets <c>core.autocrlf=true</c> and whose tracked
        /// text file is materialized under that setting, so a worktree cut from the repo checks the
        /// file out with CRLF while a fresh clone checks it out with LF. This is the mechanism that
        /// made run_check an unfaithful gate for byte-exact manifest tests.
        /// </summary>
        private static async Task<string> CreateAutocrlfSourceRepoAsync()
        {
            string repoPath = Path.Combine(Path.GetTempPath(), "armada-iso-eol-src-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(repoPath);

            await RunGitAsync(repoPath, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(repoPath, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(repoPath, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            string trackedFile = "eol-tracked.txt";
            await File.WriteAllTextAsync(Path.Combine(repoPath, trackedFile), "line1\nline2\n").ConfigureAwait(false);
            await RunGitAsync(repoPath, "add", trackedFile).ConfigureAwait(false);
            await RunGitAsync(repoPath, "commit", "-m", "Initial commit").ConfigureAwait(false);

            // Repo-local autocrlf applies to worktrees cut from this repo but is NOT copied by clone.
            await RunGitAsync(repoPath, "config", "core.autocrlf", "true").ConfigureAwait(false);
            File.Delete(Path.Combine(repoPath, trackedFile));
            await RunGitAsync(repoPath, "checkout", "--", trackedFile).ConfigureAwait(false);

            return repoPath;
        }

        /// <summary>
        /// Command that prints the lowercase SHA-256 of a file relative to the checkout root, for the
        /// cross-platform hash comparison used by the dock-faithfulness regression.
        /// </summary>
        private static string HashFileCommand(string fileName)
        {
            return OperatingSystem.IsWindows()
                ? "powershell -NoProfile -Command \"(Get-FileHash -Algorithm SHA256 -Path '" + fileName + "').Hash.ToLowerInvariant()\""
                : "shasum -a 256 " + fileName;
        }

        private static string ComputeFileSha256(string path)
        {
            using System.Security.Cryptography.SHA256 sha256 = System.Security.Cryptography.SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(File.ReadAllBytes(path));
            StringBuilder sb = new StringBuilder();
            foreach (byte b in hashBytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
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
