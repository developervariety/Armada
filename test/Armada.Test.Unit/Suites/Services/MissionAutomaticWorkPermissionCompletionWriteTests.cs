namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
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
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    /// <summary>Completion survives real in-command permission changes without losing its verdict or attempt fence.</summary>
    public sealed class MissionAutomaticWorkPermissionCompletionWriteTests : TestSuite
    {
        private static readonly TimeSpan _Limit = TimeSpan.FromSeconds(30);
        public override string Name => "Mission Automatic Work Permission Completion Writes";
        protected override async Task RunTestsAsync()
        {
            await RunTest("Dependent permission granted inside the real final gate command preserves Review and both grants",
                () => DuringGateAsync("grant", false, null)).ConfigureAwait(false);
            await RunTest("Verification revoked inside the real final gate command preserves its completed verdict and revoke",
                () => DuringGateAsync("revoke", false, null)).ConfigureAwait(false);
            await RunTest("Concurrent dependent grant does not replace a real failed command verdict with Infra or WorkProduced",
                () => DuringGateAsync("grant", true, null)).ConfigureAwait(false);
            await RunTest("Cancellation during the real command remains Cancelled after a concurrent permission change",
                () => DuringGateAsync("grant", false, "cancel")).ConfigureAwait(false);
            await RunTest("A later source and attempt written during the real command cannot be replaced by old completion",
                () => DuringGateAsync("grant", false, "later-attempt")).ConfigureAwait(false);

            await RunTest("Generated review approval refuses stale or missing StartedUtc with every other expected binding current", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    GeneratedArtifactReviewBinding prior = new GeneratedArtifactReviewBinding
                    {
                        SourceCommit = new string('b', 40), CandidateCommit = new string('a', 40),
                        TargetBaseCommit = new string('c', 40), PreparationAttempt = 1,
                        RequestedUtc = DateTime.UtcNow.AddMinutes(-10)
                    };
                    Mission mission = await db.Driver.Missions.CreateAsync(new Mission("Exact generated review launch")
                    {
                        TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                        Persona = "Judge", Status = MissionStatusEnum.WorkProduced,
                        StartedUtc = DateTime.UtcNow.AddMinutes(-5), CommitHash = prior.CandidateCommit,
                        BranchName = "exact-generated-review", GeneratedArtifactReview = prior,
                        AgentOutput = "[ARMADA:VERDICT] PASS", RecoveryAttempts = 2, LandingRetryCount = 1
                    }).ConfigureAwait(false);
                    Mission fresh = (await db.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    GeneratedArtifactReviewBinding approved = JsonSerializer.Deserialize<GeneratedArtifactReviewBinding>(JsonSerializer.Serialize(prior))!;
                    approved.ApprovedUtc = DateTime.UtcNow;
                    foreach (DateTime? staleStart in new DateTime?[] { null, fresh.StartedUtc!.Value.AddSeconds(-1) })
                    {
                        Mission stale = (await db.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                        // Keep the real loaded raw stamp/source/status/owner/branch/counters unchanged.
                        // Only this expected launch field is stale, which isolates its atomic predicate.
                        stale.StartedUtc = staleStart;
                        AssertFalse(await db.Driver.Missions.TryWriteGeneratedArtifactReviewAsync(stale, approved, false).ConfigureAwait(false));
                        Mission unchanged = (await db.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                        AssertEqual(JsonSerializer.Serialize(fresh), JsonSerializer.Serialize(unchanged));
                        AssertNull(unchanged.GeneratedArtifactReview!.ApprovedUtc);
                    }
                    AssertTrue(await db.Driver.Missions.TryWriteGeneratedArtifactReviewAsync(fresh, approved, false).ConfigureAwait(false),
                        "The current exact launch remains eligible through the same specialized persistence owner.");
                    Mission saved = (await db.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    AssertEqual(fresh.StartedUtc, saved.StartedUtc);
                    AssertEqual(fresh.Status, saved.Status);
                    AssertEqual(fresh.RecoveryAttempts, saved.RecoveryAttempts);
                    AssertEqual(fresh.LandingRetryCount, saved.LandingRetryCount);
                    AssertEqual(JsonSerializer.Serialize(approved), JsonSerializer.Serialize(saved.GeneratedArtifactReview));
                }
            }).ConfigureAwait(false);
        }

        private async Task DuringGateAsync(string operation, bool failGate, string? replacement)
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            using (Harness h = await Harness.CreateAsync(db, failGate).ConfigureAwait(false))
            using (CancellationTokenSource stop = new CancellationTokenSource(_Limit))
            {
                Mission parent = await h.CreateParentAsync(MissionStatusEnum.WorkProduced, true).ConfigureAwait(false);
                await h.ClaimAsync(parent).ConfigureAwait(false);
                parent = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                parent.ProcessId = null;
                await db.Driver.Missions.UpdateAsync(parent).ConfigureAwait(false);
                await h.EnableAsync(parent).ConfigureAwait(false);
                parent = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                await h.GrantAsync(parent, MissionAutomaticActionEnum.Verification).ConfigureAwait(false);
                Task? ownedCompletion = null;
                Mission? changed = null;
                Mission? protectedRow = null;
                try
                {
                    AssertEqual(1, await h.Service.StartInterruptedDefinitionOfDoneGateRerunsAsync(stop.Token).ConfigureAwait(false));
                    while (!File.Exists(h.Entered)) await Task.Delay(10, stop.Token).ConfigureAwait(false);
                    AssertTrue(h.Service.InFlightCompletionsForTests.TryGetValue(parent.Id,
                        out MissionService.CompletionGuardEntry? entry) && entry != null);
                    ownedCompletion = entry!.Gate;
                    AssertFalse(ownedCompletion.IsCompleted, "The real final gate shell command is still held at its release file.");
                    Mission running = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.WorkProduced, running.Status);
                    AssertTrue(File.ReadAllLines(h.Marker).Select(line => line.Trim()).SequenceEqual(new[] { "build" }));
                    if (operation == "grant")
                        await h.GrantAsync(running, MissionAutomaticActionEnum.DependentAdmission).ConfigureAwait(false);
                    else
                        await h.Service.UpdateAutomaticWorkPermissionAsync(Operator(), running.Id, new MissionAutomaticWorkPermissionRequest
                        {
                            Operation = MissionAutomaticPermissionOperationEnum.Revoke,
                            Action = MissionAutomaticActionEnum.Verification,
                            ExpectedStartedUtc = running.StartedUtc, ExpectedCommitHash = running.CommitHash,
                            Reason = "Withdraw verification after the final command began"
                        }).ConfigureAwait(false);
                    changed = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                    AssertTrue(changed.AutomaticWorkPermissionWriteRevision > running.AutomaticWorkPermissionWriteRevision);
                    if (replacement == "cancel")
                    {
                        MissionCancellationResult cancelled = await MissionCancellation.CancelAsync(db.Driver, changed,
                            MissionCancellation.OperatorCancelReason, null, stop.Token).ConfigureAwait(false);
                        AssertTrue(cancelled.Succeeded);
                        protectedRow = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                    }
                    else if (replacement == "later-attempt")
                    {
                        // An existing store operation represents a replacement writer; it does not fake completion.
                        Mission staleCompletion = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                        protectedRow = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                        protectedRow.Status = MissionStatusEnum.WorkProduced;
                        protectedRow.StartedUtc = protectedRow.StartedUtc!.Value.AddSeconds(1);
                        protectedRow.CommitHash = new string('b', 40);
                        protectedRow.ProcessId = null;
                        protectedRow.AgentOutput = "Later attempt output must survive";
                        await db.Driver.Missions.UpdateAsync(protectedRow).ConfigureAwait(false);
                        protectedRow = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                        staleCompletion.Status = MissionStatusEnum.Review;
                        AssertFalse(await db.Driver.Missions.TryUpdateCompletionAsync(staleCompletion, changed, stop.Token).ConfigureAwait(false),
                            "The completion CAS must refuse same-status replacement source/start even when permission raw/revision still match.");

                    }
                    File.WriteAllText(h.Release, "release own final command");
                    await ownedCompletion.WaitAsync(stop.Token).ConfigureAwait(false);
                    Mission after = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                    AssertEqual(changed.AutomaticWorkPermissionRawJson, after.AutomaticWorkPermissionRawJson);
                    AssertEqual(changed.AutomaticWorkPermissionWriteRevision, after.AutomaticWorkPermissionWriteRevision);
                    AssertEqual(0, h.Launches);
                    if (protectedRow != null)
                    {
                        AssertEqual(protectedRow.AdmissionWriteRevision, after.AdmissionWriteRevision, "Old completion must not increment a replacement/cancelled row.");
                        AssertEqual(protectedRow.Status, after.Status);
                        AssertEqual(protectedRow.StartedUtc, after.StartedUtc);
                        AssertEqual(protectedRow.CommitHash, after.CommitHash);
                        AssertEqual(protectedRow.ProcessId, after.ProcessId);
                        AssertEqual(protectedRow.AgentOutput, after.AgentOutput);
                        AssertEqual(protectedRow.FailureReason, after.FailureReason);
                        AssertNull(after.ReviewRequestedUtc);
                        return;
                    }
                    AssertEqual(changed.AdmissionWriteRevision + 1, after.AdmissionWriteRevision, "The verdict save increments admission_revision exactly once.");
                    AssertEqual(failGate ? MissionStatusEnum.Failed : MissionStatusEnum.Review, after.Status);
                    AssertEqual(changed.StartedUtc, after.StartedUtc); AssertEqual(changed.CommitHash, after.CommitHash);
                    AssertEqual(changed.RecoveryAttempts, after.RecoveryAttempts); AssertEqual(changed.LandingRetryCount, after.LandingRetryCount);
                    AssertTrue(File.ReadAllLines(h.Marker).Select(line => line.Trim()).SequenceEqual(new[] { "build", "test" }));
                    ArmadaEvent evaluation = (await db.Driver.Events.EnumerateByMissionAsync(parent.Id, 100).ConfigureAwait(false))
                        .Single(item => item.EventType == DefinitionOfDoneEvaluationRecord.EventType);
                    DefinitionOfDoneEvaluationRecord result = JsonSerializer.Deserialize<DefinitionOfDoneEvaluationRecord>(evaluation.Payload!)
                        ?? throw new InvalidOperationException("A real completed gate must retain its verdict.");
                    AssertEqual(failGate ? DefinitionOfDoneEvaluationOutcomeEnum.Failed : DefinitionOfDoneEvaluationOutcomeEnum.Passed, result.Outcome);
                    AssertEqual(changed.CommitHash, result.CommitHash);
                    if (failGate)
                    {
                        AssertEqual(7, result.ExitCode!.Value);
                        AssertFalse(after.FailureReason!.Contains("classification=Infra", StringComparison.Ordinal));
                    }
                    else
                    {
                        AssertNotNull(after.ReviewRequestedUtc); AssertNull(after.ReviewedUtc);
                        AssertNull(after.FailureReason);
                    }
                    if (operation == "grant") AssertNotNull(after.AutomaticWorkPermission!.DependentAdmission);
                    else AssertNull(after.AutomaticWorkPermission!.Verification);
                }
                finally
                {
                    File.WriteAllText(h.Release, "release own command on every path");
                    DefinitionOfDoneGateRuns.Cancel(parent.Id);
                    if (ownedCompletion == null && h.Service.InFlightCompletionsForTests.TryGetValue(parent.Id,
                        out MissionService.CompletionGuardEntry? entry) && entry != null) ownedCompletion = entry.Gate;
                    if (ownedCompletion != null) await ownedCompletion.WaitAsync(_Limit).ConfigureAwait(false);
                }
            }
        }

        private static AuthContext Operator() => AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "Test");

        private sealed class Harness : IDisposable
        {
            /// <summary>Database owned by the outer test.</summary>
            public TestDatabase Database { get; private set; } = null!;
            /// <summary>Settings limited to this fixture directory.</summary>
            public ArmadaSettings Settings { get; private set; } = null!;
            /// <summary>Existing git double with observable provisioning calls.</summary>
            public StubGitService Git { get; private set; } = null!;
            /// <summary>Vessel owned by this fixture.</summary>
            public Vessel Vessel { get; private set; } = null!;
            /// <summary>Captain whose launch is replaced by the existing callback.</summary>
            public Captain Captain { get; private set; } = null!;
            /// <summary>Initial service under test.</summary>
            public MissionService Service { get; private set; } = null!;
            /// <summary>Number of calls to the captain launch owner.</summary>
            public int Launches { get; private set; }
            /// <summary>File written only by actual gate shell commands.</summary>
            public string Marker => Path.Combine(_Root, "gate-markers.txt");
            public string Entered => Path.Combine(_Root, "unit-entered");
            public string Release => Path.Combine(_Root, "unit-release");


            private string _Root = "";
            private Voyage _Voyage = null!;
            private LoggingModule _Logging = null!;
            private ICaptainService _Captains = null!;
            private IDockService _Docks = null!;

            /// <summary>Create actual service owners and an isolated marker-command profile.</summary>
            public static async Task<Harness> CreateAsync(TestDatabase db, bool failGate = false)
            {
                Harness h = new Harness();
                h.Database = db;
                h._Root = Path.Combine(Path.GetTempPath(), "armada_permission_boundary_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(h._Root);
                string repository = Path.Combine(h._Root, "repository.git");
                string workingDirectory = Path.Combine(h._Root, "working");
                Directory.CreateDirectory(repository);
                Directory.CreateDirectory(workingDirectory);
                h._Logging = new LoggingModule();
                h._Logging.Settings.EnableConsole = false;
                h.Settings = new ArmadaSettings
                {
                    DataDirectory = h._Root, LogDirectory = Path.Combine(h._Root, "logs"),
                    DocksDirectory = Path.Combine(h._Root, "docks"), ReposDirectory = Path.Combine(h._Root, "repos")
                };
                h.Git = new StubGitService
                {
                    CreateWorktreeDirectories = true, RevisionCommitShaResult = new string('a', 40),
                    HeadCommitHashResult = new string('a', 40), ChangedFilesSinceResult = new[] { "src/Behavior.cs" }
                };
                h.Git.ExistingBranches.Add("permission-parent");
                h._Docks = new DockService(h._Logging, db.Driver, h.Settings, h.Git);
                h._Captains = new CaptainService(h._Logging, db.Driver, h.Settings, h.Git, h._Docks);
                h._Captains.OnLaunchAgent = (_, _, _) => { h.Launches++; return Task.FromResult(4747477); };
                h.Vessel = await db.Driver.Vessels.CreateAsync(new Vessel("permission-boundary", "https://github.com/test/permission.git")
                {
                    TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                    LocalPath = repository, WorkingDirectory = workingDirectory, DefaultBranch = "main"
                }).ConfigureAwait(false);
                h.Captain = await db.Driver.Captains.CreateAsync(new Captain("permission-boundary-captain")
                {
                    TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, State = CaptainStateEnum.Idle
                }).ConfigureAwait(false);
                h._Voyage = await db.Driver.Voyages.CreateAsync(new Voyage("Permission boundaries", "Owned test fixtures")
                {
                    TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId
                }).ConfigureAwait(false);
                await db.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                {
                    TenantId = Constants.DefaultTenantId, Name = "Permission marker commands", Scope = WorkflowProfileScopeEnum.Vessel,
                    VesselId = h.Vessel.Id, IsDefault = true, Active = true, GateCommandTimeoutSeconds = 20,
                    BuildCommand = "echo build >> \"" + h.Marker + "\"", UnitTestCommand = "echo entered > \"" + h.Entered + "\"; n=0; while [ ! -f \"" + h.Release + "\" ]; do n=$((n+1)); [ \"$n\" -lt 750 ] || exit 99; sleep 0.02; done; echo test >> \"" + h.Marker + "\"; exit "+(failGate ? "7" : "0")
                }).ConfigureAwait(false);
                h.Service = h.NewService();
                return h;
            }

            /// <summary>Construct another owner over the same durable rows.</summary>
            public MissionService NewService()
            {
                MissionService service = new MissionService(_Logging, Database.Driver, Settings, _Docks, _Captains,
                    git: Git, resourcePressureAdmission: TestResourcePressure.Unconstrained(Settings));
                service.DefinitionOfDone = new DefinitionOfDoneGate(new DefinitionOfDoneSettings
                {
                    Enabled = true, RunRestoreBeforeBuild = false
                }, Database.Driver, _Logging, gitService: Git);
                service.OnGetMissionOutput = _ => "[ARMADA:RESULT] COMPLETE\nImplemented the requested behavior.\n" + new string('x', 1200);
                return service;
            }

            /// <summary>Persist a producer with captured work and source identity.</summary>
            public async Task<Mission> CreateParentAsync(MissionStatusEnum status, bool review = false)
            {
                Mission created = await Database.Driver.Missions.CreateAsync(new Mission("Permission producer", "Implement the requested behavior")
                {
                    TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VesselId = Vessel.Id,
                    VoyageId = _Voyage.Id, StageOrder = 1, Persona = "Worker", Status = status,
                    StartedUtc = DateTime.UtcNow.AddMinutes(-5), CommitHash = new string('a', 40),
                    BranchName = "permission-parent", RequiresReview = review, RecoveryAttempts = 2, LandingRetryCount = 1,
                    DiffSnapshot = "diff --git a/src/Behavior.cs b/src/Behavior.cs\n--- a/src/Behavior.cs\n+++ b/src/Behavior.cs\n@@ -1 +1 @@\n-old\n+new"
                }).ConfigureAwait(false);
                return await ReadAsync(created.Id).ConfigureAwait(false);
            }

            /// <summary>Persist a dependent with or without an already prepared branch.</summary>
            public async Task<Mission> CreateChildAsync(Mission parent, bool prepared)
            {
                Mission created = await Database.Driver.Missions.CreateAsync(new Mission("Permission consumer", "Check the exact predecessor result")
                {
                    TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VesselId = Vessel.Id,
                    VoyageId = _Voyage.Id, DependsOnMissionId = parent.Id, StageOrder = 2, Persona = "TestEngineer",
                    Status = MissionStatusEnum.Pending, BranchName = prepared ? parent.BranchName : null
                }).ConfigureAwait(false);
                return await ReadAsync(created.Id).ConfigureAwait(false);
            }

            /// <summary>Represent the exiting captain and its owned dock without starting a process.</summary>
            public async Task<Captain> ClaimAsync(Mission parent)
            {
                string worktree = Path.Combine(_Root, "produced-worktree");
                Directory.CreateDirectory(worktree);
                Dock dock = await Database.Driver.Docks.CreateAsync(new Dock(Vessel.Id)
                {
                    TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                    CaptainId = Captain.Id, BranchName = parent.BranchName, WorktreePath = worktree
                }).ConfigureAwait(false);
                parent.CaptainId = Captain.Id;
                parent.DockId = dock.Id;
                parent.ProcessId = 4747477;
                await Database.Driver.Missions.UpdateAsync(parent).ConfigureAwait(false);
                Captain captain = (await Database.Driver.Captains.ReadAsync(Captain.Id).ConfigureAwait(false))!;
                captain.State = CaptainStateEnum.Working;
                captain.CurrentMissionId = parent.Id;
                captain.CurrentDockId = dock.Id;
                captain.ProcessId = 4747477;
                return await Database.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
            }

            /// <summary>Opt in through the authenticated public operator owner.</summary>
            public async Task EnableAsync(Mission mission)
            {
                await Service.UpdateAutomaticWorkPermissionAsync(Operator(), mission.Id, new MissionAutomaticWorkPermissionRequest
                {
                    Operation = MissionAutomaticPermissionOperationEnum.Enable, Reason = "Hold owned automatic work"
                }).ConfigureAwait(false);
            }

            /// <summary>Grant one current action through the public operator owner.</summary>
            public async Task GrantAsync(Mission mission, MissionAutomaticActionEnum action, MissionService? service = null)
            {
                await (service ?? Service).UpdateAutomaticWorkPermissionAsync(Operator(), mission.Id, new MissionAutomaticWorkPermissionRequest
                {
                    Operation = MissionAutomaticPermissionOperationEnum.Grant, Action = action,
                    ExpectedStartedUtc = mission.StartedUtc, ExpectedCommitHash = mission.CommitHash,
                    ExpiresUtc = DateTime.UtcNow.AddMinutes(10), Reason = "Admit only this current action"
                }).ConfigureAwait(false);
            }

            /// <summary>Read the current persisted mission including its permission revision.</summary>
            public async Task<Mission> ReadAsync(string id) => await Database.Driver.Missions.ReadAsync(id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The boundary fixture requires a persisted mission.");

            private static AuthContext Operator() => AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "Test");

            /// <summary>Delete only this fixture directory after its owned tasks settle.</summary>
            public void Dispose()
            {
                if (Directory.Exists(_Root)) Directory.Delete(_Root, true);
            }
        }
    }
}
