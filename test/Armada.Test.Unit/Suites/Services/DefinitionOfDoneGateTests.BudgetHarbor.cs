namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
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
    using Armada.Test.Unit.TestHelpers;

    public partial class DefinitionOfDoneGateTests
    {
        private async Task AssertHarborExecutionBudgetAsync()
        {
            using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
            string localDirectory = CreateTempDir();
            string localMarker = Path.Combine(localDirectory, "must-not-execute");
            const string tenantId = "ten_dod_harbor_budget";
            const string userId = "usr_dod_harbor_budget";
            const string head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            try
            {
                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata { Id = tenantId, Name = tenantId }).ConfigureAwait(false);
                await testDb.Driver.Users.CreateAsync(new UserMaster
                {
                    Id = userId, TenantId = tenantId, Email = "harbor-budget@armada.test",
                    PasswordSha256 = UserMaster.ComputePasswordHash("test-password"), Active = true
                }).ConfigureAwait(false);
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("Harbor budget vessel", "file:///unused-harbor-budget.git")
                {
                    TenantId = tenantId, UserId = userId, LocalPath = localDirectory,
                    WorkingDirectory = localDirectory, DefaultBranch = "main"
                }).ConfigureAwait(false);
                string command = OperatingSystem.IsWindows()
                    ? "echo LOCAL_GATE_RAN>" + localMarker
                    : "printf LOCAL_GATE_RAN > '" + localMarker + "'";
                WorkflowProfile profile = await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                {
                    TenantId = tenantId, UserId = userId, Name = "Harbor budget profile",
                    Scope = WorkflowProfileScopeEnum.Vessel, VesselId = vessel.Id,
                    UnitTestCommand = command, IsolatedTestRerunCommand = "echo isolated-harbor-budget",
                    GateCommandTimeoutSeconds = 30, IsDefault = true, Active = true
                }).ConfigureAwait(false);
                string runnerId = "runner-dod-budget-" + Guid.NewGuid().ToString("N");
                string grantId = "grant-dod-budget";
                AssertTrue(await testDb.Driver.HarborRunnerEnrollments.TryEnrollAsync(new HarborRunnerEnrollment
                {
                    RunnerId = runnerId, TenantId = tenantId, UserId = userId,
                    AuthMethod = "unit-test", Generation = 1, Active = true
                }, 0).ConfigureAwait(false), "The Harbor owner is durably enrolled.");
                Dock dock = new Dock(vessel.Id)
                {
                    TenantId = tenantId, UserId = userId, Active = true, BranchName = "armada/test/budget",
                    HostCheckout = new HarborCheckoutBinding
                    {
                        RunnerId = runnerId, EnrollmentGeneration = 1, TenantId = tenantId, UserId = userId,
                        RepositoryGrantId = grantId, RepositoryFingerprint = new string('b', 64),
                        CheckoutId = "hco_" + Guid.NewGuid().ToString("N"), BaseCommit = head,
                        BranchName = "armada/test/budget", Revision = "revision-budget"
                    }
                };
                dock.WorktreePath = HarborCheckoutAddress.ForDock(dock.Id);
                await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);
                HarborSettings harbor = new HarborSettings { Enabled = true };
                HarborBudgetChannel channel = new HarborBudgetChannel(tenantId, userId, command, profile.IsolatedTestRerunCommand!);
                HarborCheckoutService checkouts = new HarborCheckoutService(testDb.Driver, new ArmadaSettings { Harbor = harbor });
                checkouts.SetChannel(channel);
                var logging = CreateLogging();
                DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                    new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false }, testDb.Driver, logging,
                    gitService: new HostAwareGitService(new GitService(logging), checkouts))
                {
                    HarborSettings = harbor, HarborCheckouts = checkouts
                };
                async Task<DefinitionOfDoneResult> EvaluateAsync()
                {
                    channel.ResetObservations();
                    Mission mission = CreateWorkerMission(tenantId, vessel.Id);
                    mission.UserId = userId;
                    mission.DockId = dock.Id;
                    mission.Status = MissionStatusEnum.WorkProduced;
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    return await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);
                }

                channel.FirstExitCode = 1;
                channel.FirstDurationMilliseconds = 28001;
                channel.RetryDurationMilliseconds = 500;
                // Transport/output retrieval is not execution. Even a positive delay must not
                // replace the authenticated 28001 ms runner duration when funding the retry.
                channel.OutputReadDelay = TimeSpan.FromMilliseconds(20);
                DefinitionOfDoneResult fits = await EvaluateAsync().ConfigureAwait(false);
                AssertTrue(fits.Passed, "A Harbor full command and retry that fit 30 seconds may pass.");
                AssertEqual("30,1", String.Join(",", channel.Timeouts), "The retry receives only one whole remaining second.");
                AssertEqual(2, channel.VerificationCount, "The successful case really entered the isolated retry.");
                AssertTrue(channel.SawBoundRetry, "The retry names the actual first output, Check identity and failing test.");

                channel.FirstDurationMilliseconds = 28999;
                DefinitionOfDoneResult transport = await EvaluateAsync().ConfigureAwait(false);
                AssertTrue(transport.Passed, "Output transport cannot spend the last whole second of runner execution time.");
                AssertEqual("30,1", String.Join(",", channel.Timeouts),
                    "A 1001 ms authenticated remainder survives the separate output-read delay.");

                channel.FirstDurationMilliseconds = 28001;
                channel.RetryDurationMilliseconds = 2500;
                DefinitionOfDoneResult late = await EvaluateAsync().ConfigureAwait(false);
                AssertFalse(late.Passed, "A late zero-exit retry cannot exceed the shared 30-second budget.");
                AssertEqual("30,1", String.Join(",", channel.Timeouts), "The late retry was actually admitted with the remainder.");
                AssertEqual(2, channel.VerificationCount, "The over-budget retry must execute, rather than fail setup.");

                profile.BuildCommand = command;
                await testDb.Driver.WorkflowProfiles.UpdateAsync(profile).ConfigureAwait(false);
                channel.RetryDurationMilliseconds = 500;
                channel.BuildDurationMilliseconds = 28001;
                DefinitionOfDoneResult separate = await EvaluateAsync().ConfigureAwait(false);
                AssertTrue(separate.Passed, "Build and UnitTest have separate logical execution budgets.");
                AssertEqual("30,30,1", String.Join(",", channel.Timeouts),
                    "A completed 28-second build does not spend the UnitTest and retry budget.");
                AssertEqual(3, channel.VerificationCount, "The separate-budget case runs Build, full UnitTest and retry.");
                profile.BuildCommand = null;
                await testDb.Driver.WorkflowProfiles.UpdateAsync(profile).ConfigureAwait(false);

                channel.FirstDurationMilliseconds = 29501;
                DefinitionOfDoneResult fractional = await EvaluateAsync().ConfigureAwait(false);
                AssertFalse(fractional.Passed, "Less than one whole remaining second cannot fund a Harbor retry.");
                AssertEqual(1, channel.VerificationCount, "No second command starts with a subsecond remote remainder.");

                channel.FirstExitCode = 0;
                foreach (long? duration in new long?[] { null, -1, 30001 })
                {
                    channel.FirstDurationMilliseconds = duration;
                    DefinitionOfDoneResult invalid = await EvaluateAsync().ConfigureAwait(false);
                    AssertFalse(invalid.Passed, "A zero exit with missing, invalid or over-budget duration cannot pass.");
                    AssertEqual(1, channel.VerificationCount, "Invalid initial execution proof cannot fund a retry.");
                }
                AssertFalse(File.Exists(localMarker), "Every bound Harbor case avoids the Admiral-local fallback command.");
            }
            finally { TryDeleteDirectory(localDirectory); }
        }

        private sealed class HarborBudgetChannel : IHarborWorkspaceOperations
        {
            private const string FailingName = "Sample.Tests.HarborBudget.Fits";
            private readonly string _Tenant;
            private readonly string _User;
            private readonly string _CommandHash;
            private readonly string _RerunHash;
            private readonly Dictionary<string, byte[]> _Outputs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            private string? _FirstOutputId;
            private string? _FirstCheckId;
            public HarborBudgetChannel(string tenant, string user, string command, string rerun)
            {
                _Tenant = tenant; _User = user;
                _CommandHash = HarborVerificationProtocol.CommandSha256(command);
                _RerunHash = HarborVerificationProtocol.CommandSha256(rerun);
            }
            public int FirstExitCode { get; set; }
            public long? FirstDurationMilliseconds { get; set; }
            public long RetryDurationMilliseconds { get; set; }
            public long BuildDurationMilliseconds { get; set; }
            public TimeSpan OutputReadDelay { get; set; }
            public int VerificationCount { get; private set; }
            public bool SawBoundRetry { get; private set; }
            public List<int> Timeouts { get; } = new List<int>();
            public void ResetObservations()
            {
                VerificationCount = 0; SawBoundRetry = false; Timeouts.Clear();
                _FirstOutputId = null; _FirstCheckId = null; _Outputs.Clear();
            }
            public async Task<HarborWorkspaceOperationResult> ExecuteAsync(AuthContext auth,
                HarborWorkspaceOperationRequest request, CancellationToken token = default)
            {
                if (!auth.IsAuthenticated || auth.TenantId != _Tenant || auth.UserId != _User
                    || request.Binding?.TenantId != _Tenant || request.Binding.UserId != _User)
                    throw new InvalidOperationException("Harbor budget fixture received an unbound owner.");
                HarborWorkspaceOperationResult result = new HarborWorkspaceOperationResult
                {
                    Accepted = true, RequestId = request.RequestId, Operation = request.Operation, Binding = request.Binding
                };
                if (request.Operation == HarborWorkspaceOperationEnum.Git)
                {
                    if (request.Git!.Operation == HarborGitOperationEnum.GetHeadCommitHash)
                        result.Git = new HarborGitOperationResult { Text = request.Binding!.BaseCommit };
                    else if (request.Git.Operation == HarborGitOperationEnum.HasUncommittedTrackedChanges)
                        result.Git = new HarborGitOperationResult { Flag = false };
                    else throw new InvalidOperationException("Unexpected Harbor budget Git operation: " + request.Git.Operation);
                    return result;
                }
                if (request.Operation == HarborWorkspaceOperationEnum.RunVerification)
                {
                    HarborVerificationRequest verification = request.Verification!;
                    bool build = verification.CommandKey == HarborVerificationProtocol.DefinitionOfDoneBuild;
                    if ((!build && verification.CommandKey != HarborVerificationProtocol.DefinitionOfDoneUnitTest)
                        || verification.ExpectedCommandSha256 != _CommandHash)
                        throw new InvalidOperationException("The Harbor budget fixture received the wrong configured command.");
                    bool retry = verification.Rerun != null;
                    if (retry)
                    {
                        HarborVerificationRerunRequest rerun = verification.Rerun!;
                        SawBoundRetry = rerun.PriorOutputId == _FirstOutputId && rerun.PriorCheckId == _FirstCheckId
                            && rerun.FailingTestNames.Count == 1 && rerun.FailingTestNames[0] == FailingName
                            && rerun.RerunCommandKey == HarborVerificationProtocol.DefinitionOfDoneUnitTestRerun
                            && rerun.RerunCommandSha256 == _RerunHash;
                        if (!SawBoundRetry) throw new InvalidOperationException("The isolated retry lost its first-command provenance.");
                    }
                    VerificationCount++;
                    Timeouts.Add(verification.TimeoutSeconds);
                    byte[] output = Encoding.UTF8.GetBytes(!build && !retry && FirstExitCode != 0
                        ? "  Failed " + FailingName + " [1 ms]\n0 passed; 1 failed.\n" : "1 passed; 0 failed.\n");
                    string outputId = "output-budget-" + Guid.NewGuid().ToString("N");
                    _Outputs.Add(outputId, output);
                    if (!retry) { _FirstOutputId = outputId; _FirstCheckId = verification.CheckId; }
                    result.ExitCode = build || retry ? 0 : FirstExitCode;
                    result.StartedUtc = DateTime.UtcNow;
                    result.DurationMilliseconds = build ? BuildDurationMilliseconds
                        : retry ? RetryDurationMilliseconds : FirstDurationMilliseconds;
                    result.KillConfirmed = true;
                    result.VerificationOutputId = outputId;
                    result.VerificationCheckId = verification.CheckId;
                    result.StandardOutputLength = result.StandardOutputRetainedBytes = output.LongLength;
                    result.StandardOutputSha256 = Hash(output);
                    result.StandardErrorSha256 = Hash(Array.Empty<byte>());
                    return result;
                }
                if (request.Operation == HarborWorkspaceOperationEnum.ReadVerificationOutput)
                {
                    if (OutputReadDelay > TimeSpan.Zero) await Task.Delay(OutputReadDelay, token).ConfigureAwait(false);
                    HarborVerificationOutputRequest read = request.VerificationOutput!;
                    byte[] bytes = _Outputs[read.OutputId];
                    result.VerificationOutputId = read.OutputId;
                    result.VerificationCheckId = read.CheckId;
                    result.Length = bytes.LongLength;
                    int offset = checked((int)read.Offset);
                    result.BytesBase64 = Convert.ToBase64String(bytes, offset, Math.Min(read.MaxBytes, bytes.Length - offset));
                    return result;
                }
                throw new InvalidOperationException("Unexpected Harbor budget operation: " + request.Operation);
            }
            private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
    }
}
