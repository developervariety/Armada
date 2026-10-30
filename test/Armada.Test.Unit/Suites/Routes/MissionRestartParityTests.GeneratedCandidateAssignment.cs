namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.IO;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Test.Unit.Suites.Services;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    public partial class MissionRestartParityTests
    {
        private async Task RunGeneratedCandidateAssignmentTestAsync()
        {
            foreach (string modeSource in new[] { "old Checks refused", "vessel", "global", "voyage" })
            await RunTest("Generated candidate assignment and exact-source approval with " + modeSource, async () =>
            {
                bool oldChecksOnly = modeSource == "old Checks refused";
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    harness.Settings.LandingMode = modeSource == "global" ? LandingModeEnum.LocalMerge : LandingModeEnum.None;
                    string root = Path.Combine(harness.Settings.ReposDirectory, "generated-review-assignment");
                    Directory.CreateDirectory(root);
                    string source = Path.Combine(root, "source");
                    string bare = Path.Combine(root, "vessel.git");
                    GitServiceTests.SeedTemplateRepository(source);
                    string predecessor = (await RunGitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    string main = (await RunGitAsync(source, "symbolic-ref", "--short", "HEAD").ConfigureAwait(false)).Trim();
                    await RunGitAsync(source, "config", "user.name", "Armada tests").ConfigureAwait(false);
                    await RunGitAsync(source, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(source, "generated.txt"), "Combined authored input").ConfigureAwait(false);
                    await RunGitAsync(source, "add", "generated.txt").ConfigureAwait(false);
                    await RunGitAsync(source, "commit", "-m", "Regenerate combined artifact").ConfigureAwait(false);
                    string candidate = (await RunGitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    await File.WriteAllTextAsync(Path.Combine(source, "later.txt"), "Must not be discarded by candidate assignment").ConfigureAwait(false);
                    await RunGitAsync(source, "add", "later.txt").ConfigureAwait(false);
                    await RunGitAsync(source, "commit", "-m", "Advance beyond the candidate").ConfigureAwait(false);
                    string changedTip = (await RunGitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    await RunGitAsync(root, "clone", "--bare", source, bare).ConfigureAwait(false);
                    await RunGitAsync(bare, "update-ref", "refs/heads/review-candidate", candidate).ConfigureAwait(false);
                    await RunGitAsync(bare, "update-ref", "refs/heads/" + main, predecessor).ConfigureAwait(false);
                    await RunGitAsync(source, "reset", "--hard", predecessor).ConfigureAwait(false);
                    Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel("candidate-review", source)
                    {
                        TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                        LocalPath = bare, WorkingDirectory = source, DefaultBranch = main,
                        LandingMode = modeSource == "global" || modeSource == "voyage" ? null : LandingModeEnum.LocalMerge,
                        BranchCleanupPolicy = BranchCleanupPolicyEnum.None
                    }).ConfigureAwait(false);
                    Voyage voyage = await harness.Driver.Voyages.CreateAsync(new Voyage("Candidate source review")
                    { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, Status = VoyageStatusEnum.InProgress,
                        LandingMode = modeSource == "voyage" ? LandingModeEnum.LocalMerge : null }).ConfigureAwait(false);
                    Mission prior = await harness.Driver.Missions.CreateAsync(new Mission("Earlier reviewer")
                    {
                        TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                        VesselId = vessel.Id, VoyageId = voyage.Id, Status = MissionStatusEnum.Complete, Persona = "Worker",
                        BranchName = main, CommitHash = predecessor
                    }).ConfigureAwait(false);
                    Mission judge = await harness.Driver.Missions.CreateAsync(new Mission("Judge combined source")
                    {
                        TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VesselId = vessel.Id,
                        VoyageId = voyage.Id, Status = MissionStatusEnum.WorkProduced, Persona = "Judge", DependsOnMissionId = prior.Id,
                        BranchName = "review-candidate", CommitHash = predecessor, Mode = MissionModeEnum.Research,
                        AgentOutput = "[ARMADA:VERDICT] PASS\nOld review stays preserved"
                    }).ConfigureAwait(false);
                    judge = (await harness.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                    GeneratedArtifactReviewBinding binding = GeneratedArtifactReviewRule.NextBinding(judge,
                        new GeneratedArtifactLandingResult
                        { Outcome = "ReviewRequired", SourceCommit = predecessor, TargetBaseCommit = predecessor, CandidateCommit = candidate },
                        2, DateTime.UtcNow);
                    AssertTrue(await harness.Driver.Missions.TryWriteGeneratedArtifactReviewAsync(judge, binding, true).ConfigureAwait(false));
                    LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
                    GitService git = new GitService(logging);
                    DockService docks = new DockService(logging, harness.Driver, harness.Settings, git);
                    CaptainService captains = new CaptainService(logging, harness.Driver, harness.Settings, git, docks);
                    int launches = 0;
                    captains.OnLaunchAgent = (_, _, _) => { launches++; return Task.FromResult(Int32.MaxValue - 50); };
                    MissionService missions = new MissionService(logging, harness.Driver, harness.Settings, docks, captains,
                        git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(harness.Settings));
                    await harness.Driver.Captains.CreateAsync(new Captain("candidate-review")
                    {
                        TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                        Model = "claude-opus-5", Tier = CaptainTierEnum.Premium, AllowedPersonas = "[\"Judge\"]"
                    }).ConfigureAwait(false);
                    await RunGitAsync(bare, "update-ref", "refs/heads/review-candidate", changedTip).ConfigureAwait(false);
                    Mission changed = (await harness.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                    AssertFalse(await missions.TryAssignAsync(changed, vessel).ConfigureAwait(false));
                    AssertEqual(0, launches);
                    Mission held = (await harness.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                    AssertEqual(MissionStatusEnum.Pending, held.Status);
                    AssertEqual("generated_candidate_branch_changed", held.AssignmentHoldReason);
                    await RunGitAsync(bare, "update-ref", "refs/heads/review-candidate", candidate).ConfigureAwait(false);
                    AssertTrue(await missions.TryAssignAsync(held, vessel).ConfigureAwait(false), "The exact retained candidate must launch.");
                    Mission assigned = (await harness.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                    Dock dock = (await harness.Driver.Docks.ReadAsync(assigned.DockId!).ConfigureAwait(false))!;
                    AssertEqual(1, launches);
                    string instructions = await File.ReadAllTextAsync(Path.Combine(dock.WorktreePath!, "CLAUDE.md")).ConfigureAwait(false);
                    AssertContains("## Regenerated landing candidate", instructions);
                    AssertContains("Frozen candidate: `" + candidate + "`", instructions);
                    AssertContains("Earlier reviewed source: `" + predecessor + "`", instructions);

                    AssertEqual("review-candidate", assigned.BranchName);
                    AssertEqual(candidate, (await RunGitAsync(dock.WorktreePath!, "rev-parse", "HEAD").ConfigureAwait(false)).Trim());
                    AssertTrue(await git.TryIsAncestorAsync(dock.WorktreePath!, predecessor, "HEAD").ConfigureAwait(false) == true);
                    AssertEqual(prior.Id, assigned.DependsOnMissionId);
                    AssertEqual(0, assigned.RecoveryAttempts);
                    AssertEqual(0, assigned.LandingRetryCount);
                    AssertTrue(assigned.PreservedRunOutput!.Contains("Old review stays preserved", StringComparison.Ordinal));
                    Captain oldCallback = new Captain("earlier candidate review")
                    {
                        Id = assigned.CaptainId!, TenantId = Constants.DefaultTenantId,
                        CurrentMissionId = assigned.Id, CurrentDockId = "earlier-review-dock"
                    };
                    string? preserved = assigned.PreservedRunOutput;
                    await missions.HandleCompletionAsync(oldCallback, assigned.Id).ConfigureAwait(false);
                    Mission afterCallback = (await harness.Driver.Missions.ReadAsync(assigned.Id).ConfigureAwait(false))!;
                    AssertEqual(assigned.Status, afterCallback.Status,
                        "An earlier Judge callback cannot complete or fail the regenerated review launch.");
                    AssertEqual(assigned.DockId, afterCallback.DockId);
                    AssertEqual(candidate, afterCallback.CommitHash);
                    AssertEqual(preserved, afterCallback.PreservedRunOutput);
                    AssertEqual(0, afterCallback.RecoveryAttempts);
                    AssertEqual(0, afterCallback.LandingRetryCount);
                    WorkflowProfileService profiles = new WorkflowProfileService(harness.Driver, logging);
                    WorkflowProfile profile = await harness.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                    {
                        TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                        Name = "Generated review commands", Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id, IsDefault = true, BuildCommand = "git rev-parse HEAD",
                        UnitTestCommand = "git status --porcelain && git rev-parse HEAD"
                    }).ConfigureAwait(false);
                    CheckRunService checks = new CheckRunService(harness.Driver, profiles,
                        new VesselReadinessService(harness.Driver, profiles, logging), logging);
                    AuthContext auth = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId,
                        true, true, "UnitTest");
                    foreach (CheckRunTypeEnum type in new[] { CheckRunTypeEnum.Build, CheckRunTypeEnum.UnitTest })
                    {
                        CheckRun oldCheck = await checks.RunAsync(auth, new CheckRunRequest
                        {
                            VesselId = vessel.Id, VoyageId = voyage.Id, WorkflowProfileId = profile.Id, MissionId = judge.Id,
                            Type = type, CommitHash = predecessor, BranchName = main
                        }).ConfigureAwait(false);
                        AssertEqual(CheckRunStatusEnum.Passed, oldCheck.Status, oldCheck.Summary);
                        AssertEqual(predecessor, oldCheck.CommitHash);
                        AssertContains(predecessor, oldCheck.Output ?? String.Empty);
                    }
                    List<CheckRun> pending = new List<CheckRun>();
                    if (!oldChecksOnly)
                        foreach (CheckRunTypeEnum type in new[] { CheckRunTypeEnum.Build, CheckRunTypeEnum.UnitTest })
                            pending.Add(await harness.Driver.CheckRuns.CreateAsync(new CheckRun
                            {
                                TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                                VesselId = vessel.Id, VoyageId = voyage.Id, WorkflowProfileId = profile.Id, MissionId = judge.Id,
                                Type = type, Status = CheckRunStatusEnum.Pending, Source = CheckRunSourceEnum.Armada,
                                CommitHash = candidate, BranchName = assigned.BranchName,
                                Command = type == CheckRunTypeEnum.Build ? profile.BuildCommand : profile.UnitTestCommand,
                                Label = type.ToString()
                            }).ConfigureAwait(false));
                    Mission recorder = await harness.Driver.Missions.CreateAsync(new Mission("Retained terminal record")
                    {
                        TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                        VesselId = vessel.Id, VoyageId = voyage.Id, Persona = "Recorder",
                        Status = MissionStatusEnum.WorkProduced, Mode = MissionModeEnum.Research,
                        DependsOnMissionId = judge.Id, BranchName = "review-candidate",
                        CommitHash = candidate, StartFromRef = predecessor,
                        DiffSnapshot = await RunGitAsync(bare, "diff", predecessor, candidate).ConfigureAwait(false),
                        AgentOutput = "Earlier terminal record remains retained."
                    }).ConfigureAwait(false);
                    LandingService landing = new LandingService(logging, harness.Driver, harness.Settings, git);
                    landing.GeneratedArtifactPreparer = missions.PrepareGeneratedArtifactsForTargetAsync;
                    landing.SetIntegrationValidator(new IntegrationCandidateValidator(logging, harness.Driver,
                        harness.Settings, checks, profiles));
                    MergeQueueService queue = new MergeQueueService(logging, harness.Driver, harness.Settings,
                        git, new MergeFailureClassifier());
                    using (RemoteTriggerHttpClient remoteHttp = new RemoteTriggerHttpClient(logging))
                    {
                    Armada.Server.MissionLandingHandler handler = new Armada.Server.MissionLandingHandler(
                        logging, harness.Driver, harness.Settings, git, queue, landing,
                        new AutoLandEvaluator(), new ConventionChecker(), new CriticalTriggerEvaluator(),
                        new MessageTemplateService(logging), null, docks,
                        new RemoteTriggerService(null, remoteHttp, logging), null);
                    int retryCalls = 0;
                    landing.OnPerformLanding = handler.HandleMissionCompleteAsync;
                    missions.GeneratedArtifactLandingRetry = async (id, tenant, token) =>
                    {
                        retryCalls++;
                        AssertEqual(recorder.Id, id, "Only the existing terminal Recorder resumes.");
                        return await landing.RetryLandingAsync(id, tenant, token).ConfigureAwait(false);
                    };
                    string narrative = "The retained authored changes and the regenerated artifact were reviewed at the exact candidate. "
                        + "The source ancestry, negative controls and required command evidence support this verdict without a scope waiver. "
                        + "The current branch keeps the predecessor as an ancestor and names the retained candidate exactly. "
                        + "Earlier source Checks are historical evidence and cannot prove this candidate. Its current Build and UnitTest commands remain mandatory.";
                    missions.OnGetMissionOutput = _ => "## Completeness\n" + narrative + "\n## Correctness\n" + narrative
                        + "\n## Evidence\n" + narrative + "\n## Residual Risks\n" + narrative
                        + "\n## Verdict\nPASS\n[ARMADA:VERDICT] PASS";
                    Captain currentCaptain = (await harness.Driver.Captains.ReadAsync(assigned.CaptainId!).ConfigureAwait(false))!;
                    await missions.HandleCompletionAsync(currentCaptain, assigned.Id).ConfigureAwait(false);
                    Mission reviewed = (await harness.Driver.Missions.ReadAsync(assigned.Id).ConfigureAwait(false))!;
                    string reviewState = System.Text.Json.JsonSerializer.Serialize(new
                    { reviewed.Status, reviewed.Persona, reviewed.FailureReason, reviewed.HeldForOperatorReviewReason,
                        reviewed.CommitHash, reviewed.AgentOutput, reviewed.GeneratedArtifactReview });
                    AssertNull(reviewed.GeneratedArtifactReview!.ApprovedUtc, "Old-source Checks never approve this candidate.");
                    AssertEqual(0, retryCalls, "No retry runs before exact-candidate approval.");
                    AssertEqual(predecessor, (await RunGitAsync(bare, "rev-parse", main).ConfigureAwait(false)).Trim());
                    if (oldChecksOnly)
                    {
                        AssertTrue(JudgeCheckWaitHold.IsHeld(reviewed), "Old-source passing Checks leave the current PASS held: " + reviewState);
                        return;
                    }
                    AssertTrue(JudgeCheckWaitHold.IsHeld(reviewed), "The real Judge completion waits for its exact pending Checks: " + reviewState);
                    foreach (CheckRun item in pending)
                    {
                        CheckRun executed = await checks.RetryAsync(auth, item.Id).ConfigureAwait(false);
                        AssertEqual(item.Id, executed.Id, "The existing armed Check executes in place.");
                        AssertEqual(CheckRunStatusEnum.Passed, executed.Status, executed.Summary);
                        AssertEqual(candidate, executed.CommitHash);
                        AssertTrue(executed.StartedUtc.HasValue && executed.CompletedUtc.HasValue);
                        AssertNull(executed.SatisfiedBy, "Candidate proof is an actual command run.");
                        AssertContains(candidate, executed.Output ?? String.Empty);
                    }
                    AssertEqual(2, await new StaleCheckSupersessionService(harness.Driver, logging)
                        .SupersedeForVoyageAsync(voyage).ConfigureAwait(false),
                        "The normal orchestration pass retains and supersedes the two old-source Checks.");
                    int released = await missions.ReleaseJudgeCheckWaitHoldsAsync().ConfigureAwait(false);
                    Mission afterChecks = (await harness.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                    AssertEqual(1, released, System.Text.Json.JsonSerializer.Serialize(new
                    { afterChecks.Status, afterChecks.FailureReason, afterChecks.HeldForOperatorReviewReason, afterChecks.GeneratedArtifactReview }));
                    Mission approved = (await harness.Driver.Missions.ReadAsync(assigned.Id).ConfigureAwait(false))!;
                    AssertNotNull(approved.GeneratedArtifactReview!.ApprovedUtc,
                        "Only the real PASS plus current exact-source Checks approves publication.");
                    AssertEqual(candidate, approved.GeneratedArtifactReview.CandidateCommit);
                    AssertEqual(0, approved.RecoveryAttempts);
                    AssertEqual(0, approved.LandingRetryCount);
                    AssertTrue(approved.PreservedRunOutput!.Contains("Old review stays preserved", StringComparison.Ordinal));
                    AssertEqual(1, retryCalls, "Approval resumes the supported landing retry exactly once.");
                    Mission landedRecorder = (await harness.Driver.Missions.ReadAsync(recorder.Id).ConfigureAwait(false))!;
                    AssertEqual(MissionStatusEnum.Complete, landedRecorder.Status, landedRecorder.FailureReason);
                    AssertEqual(candidate, (await RunGitAsync(bare, "rev-parse", main).ConfigureAwait(false)).Trim());
                    AssertEqual(recorder.AgentOutput, landedRecorder.AgentOutput);
                    AssertEqual(0, landedRecorder.RecoveryAttempts);
                    AssertEqual(0, landedRecorder.LandingRetryCount);
                    }

                }
            }).ConfigureAwait(false);
        }
    }
}
