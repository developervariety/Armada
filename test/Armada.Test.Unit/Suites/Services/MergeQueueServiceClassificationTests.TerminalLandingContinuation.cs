namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    public partial class MergeQueueServiceClassificationTests
    {
        private sealed class TerminalCase
        {
            public PreparingQueue Prepared { get; set; } = null!;
            public Mission Worker { get; set; } = null!;
            public Mission Judge { get; set; } = null!;
            public Voyage Voyage { get; set; } = null!;
            public string BareDir { get; set; } = String.Empty;
            public string Branch { get; set; } = String.Empty;
            public string Source { get; set; } = String.Empty;
            public string MainBefore { get; set; } = String.Empty;
        }

        private async Task RunTerminalLandingContinuationTestsAsync()
        {
            await RunTest("A terminal landing continuation reopens only the passed Judge of a Failed voyage and assignment admits it", async () =>
            {
                string rootDir = NewTerminalRoot();
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        TerminalCase c = await CreateTerminalCaseAsync(testDb, rootDir).ConfigureAwait(false);

                        TerminalLandingContinuationResult result = await c.Prepared.Missions.ContinueTerminalLandingAsync(c.Judge.Id, "operator-a").ConfigureAwait(false);

                        AssertTrue(result.Granted, "the landing-only failure is granted: " + result.Reason);
                        Mission judge = (await testDb.Driver.Missions.ReadAsync(c.Judge.Id).ConfigureAwait(false))!;
                        GeneratedArtifactReviewBinding binding = judge.GeneratedArtifactReview!;
                        AssertNotNull(binding.TerminalContinuation, "the grant is stored in the Judge's binding");
                        AssertEqual("operator-a", binding.TerminalContinuation!.GrantedBy);
                        AssertEqual(c.Voyage.Id, binding.TerminalContinuation.VoyageId);
                        AssertEqual(1, binding.TerminalContinuation.Predecessors.Count, "the reconciled Worker is named");
                        AssertEqual(c.Worker.Id, binding.TerminalContinuation.Predecessors[0].MissionId);
                        AssertEqual(0, binding.TerminalContinuation.AdmittedAttempt, "nothing is admitted before assignment");
                        AssertEqual(MissionStatusEnum.Pending, judge.Status, "the Judge waits to rerun");
                        AssertEqual(binding.CandidateCommit, judge.CommitHash, "the Judge reruns at the candidate");
                        AssertEqual(c.Source, binding.SourceCommit, "the binding keeps the reviewed commit");
                        AssertNull(binding.ApprovedUtc, "no approval before a new Judge PASS");
                        AssertEqual(c.Judge.RecoveryAttempts, judge.RecoveryAttempts, "recovery attempts are kept");
                        AssertEqual(c.Judge.LandingRetryCount, judge.LandingRetryCount, "landing retries are kept");
                        AssertContains("terminal review", judge.PreservedRunOutput ?? String.Empty, "the earlier Judge output is preserved");
                        Mission worker = (await testDb.Driver.Missions.ReadAsync(c.Worker.Id).ConfigureAwait(false))!;
                        AssertEqual(MissionStatusEnum.Failed, worker.Status, "the predecessor keeps its Failed status");
                        AssertEqual(TerminalVoyageMissionRule.ReasonWorkUnlanded, worker.ReconciledReason, "the reconciliation marker is kept");
                        AssertEqual(VoyageStatusEnum.Failed, (await testDb.Driver.Voyages.ReadAsync(c.Voyage.Id).ConfigureAwait(false))!.Status,
                            "the voyage stays Failed as history");
                        AssertEqual("generated_candidate_review_pending", GeneratedArtifactReviewRule.PublicationRefusal(judge, c.MainBefore, binding.CandidateCommit),
                            "no publication before the new review");
                        AssertEqual(c.MainBefore, await RunGitCaptureAsync(c.BareDir, "rev-parse", "main").ConfigureAwait(false), "the target does not move");
                        AssertEqual(1, (await testDb.Driver.Events.EnumerateByTypeAsync(MissionService.TerminalContinuationGrantedEventType).ConfigureAwait(false)).Count,
                            "the grant writes one event");

                        await c.Prepared.Missions.TryAssignAsync(judge, c.Prepared.Vessel).ConfigureAwait(false);

                        Mission assigned = (await testDb.Driver.Missions.ReadAsync(c.Judge.Id).ConfigureAwait(false))!;
                        AssertNotEqual(MissionStatusEnum.Cancelled, assigned.Status, "the terminal-voyage rule does not cancel the granted Judge: " + assigned.FailureReason);
                        AssertNotEqual(MissionAssignmentHold.DependencyUnfinished, assigned.AssignmentHoldReason, "the named Failed predecessor does not hold it");
                        AssertEqual(1, assigned.GeneratedArtifactReview!.TerminalContinuation!.AdmittedAttempt, "the admission is recorded for this candidate");

                        Mission sibling = await testDb.Driver.Missions.CreateAsync(new Mission("Sibling without a grant", "Document the work")
                        {
                            TenantId = Armada.Core.Constants.DefaultTenantId, VesselId = c.Prepared.Vessel.Id, VoyageId = c.Voyage.Id,
                            Persona = "Worker", Status = MissionStatusEnum.Pending, BranchName = c.Branch
                        }).ConfigureAwait(false);
                        await c.Prepared.Missions.TryAssignAsync(sibling, c.Prepared.Vessel).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Cancelled, (await testDb.Driver.Missions.ReadAsync(sibling.Id).ConfigureAwait(false))!.Status,
                            "the grant admits only its own Judge; another Pending mission of the Failed voyage is still cancelled");
                    }
                }
                finally { DeleteTerminalRoot(rootDir); }
            }).ConfigureAwait(false);

            await RunTest("The admiral dispatch sweep reaches a granted Judge of a Failed voyage and admits it", async () =>
            {
                string rootDir = NewTerminalRoot();
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        TerminalCase c = await CreateTerminalCaseAsync(testDb, rootDir).ConfigureAwait(false);
                        AssertTrue((await c.Prepared.Missions.ContinueTerminalLandingAsync(c.Judge.Id, "operator-a").ConfigureAwait(false)).Granted);
                        await testDb.Driver.Captains.CreateAsync(new Captain("terminal-continuation-captain") { State = CaptainStateEnum.Idle }).ConfigureAwait(false);
                        SyslogLogging.LoggingModule logging = CreateLogging();
                        GitService git = new GitService(logging);
                        Armada.Core.Services.Interfaces.IDockService docks = new DockService(logging, testDb.Driver, c.Prepared.Settings, git);
                        Armada.Core.Services.Interfaces.ICaptainService captains = new CaptainService(logging, testDb.Driver, c.Prepared.Settings, git, docks);
                        captains.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                        Armada.Core.Services.Interfaces.IAdmiralService admiral = new AdmiralService(logging, testDb.Driver, c.Prepared.Settings, captains,
                            c.Prepared.Missions, new VoyageService(logging, testDb.Driver), docks);
                        admiral.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);

                        await admiral.HealthCheckAsync().ConfigureAwait(false);
                        await Task.Delay(200).ConfigureAwait(false);

                        Mission after = (await testDb.Driver.Missions.ReadAsync(c.Judge.Id).ConfigureAwait(false))!;
                        AssertNotEqual(MissionStatusEnum.Cancelled, after.Status, "the sweep does not cancel the granted Judge: " + after.FailureReason);
                        AssertEqual(1, after.GeneratedArtifactReview!.TerminalContinuation!.AdmittedAttempt,
                            "the sweep reached assignment for a mission of a Failed voyage (hold: " + after.AssignmentHoldReason + ")");
                        AssertEqual(VoyageStatusEnum.Failed, (await testDb.Driver.Voyages.ReadAsync(c.Voyage.Id).ConfigureAwait(false))!.Status,
                            "the voyage stays Failed");
                    }
                }
                finally { DeleteTerminalRoot(rootDir); }
            }).ConfigureAwait(false);

            await RunTest("A terminal landing continuation is refused by name and changes nothing when the failure is not landing-only", async () =>
            {
                List<(string Name, Func<TestDatabase, TerminalCase, Task> Arrange, string Reason)> cases = new List<(string, Func<TestDatabase, TerminalCase, Task>, string)>
                {
                    ("genuine upstream failure", async (db, c) => { c.Worker.ReconciledReason = null; c.Worker.ReconciledUtc = null; await db.Driver.Missions.UpdateAsync(c.Worker).ConfigureAwait(false); },
                        "terminal_continuation_upstream_failure:"),
                    ("predecessor commit not contained", async (db, c) => { c.Worker.CommitHash = c.MainBefore; await db.Driver.Missions.UpdateAsync(c.Worker).ConfigureAwait(false); },
                        "terminal_continuation_predecessor_not_contained:"),
                    ("operator hold", async (db, c) => { c.Judge.HeldForOperatorReview = true; c.Judge.HeldForOperatorReviewReason = "operator"; await db.Driver.Missions.UpdateAsync(c.Judge).ConfigureAwait(false); },
                        "terminal_continuation_operator_hold"),
                    ("dispatch hold", (db, c) => db.Driver.DispatchHolds.SaveAsync(new DispatchHoldSnapshot { Reason = "maintenance", SetBy = "operator-a" }),
                        "terminal_continuation_dispatch_hold"),
                    ("stale source", async (db, c) => { c.Judge.CommitHash = c.MainBefore; await db.Driver.Missions.UpdateAsync(c.Judge).ConfigureAwait(false); },
                        "terminal_continuation_source_changed"),
                    ("cancelled voyage", async (db, c) => { c.Voyage.Status = VoyageStatusEnum.Cancelled; await db.Driver.Voyages.UpdateAsync(c.Voyage).ConfigureAwait(false); },
                        "terminal_continuation_voyage_cancelled"),
                    ("no recorded PASS", async (db, c) => { c.Judge.AgentOutput = "terminal review\n[ARMADA:VERDICT] FAIL"; await db.Driver.Missions.UpdateAsync(c.Judge).ConfigureAwait(false); },
                        "terminal_continuation_no_pass")
                };
                foreach ((string name, Func<TestDatabase, TerminalCase, Task> arrange, string reason) in cases)
                {
                    string rootDir = NewTerminalRoot();
                    try
                    {
                        using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                        {
                            TerminalCase c = await CreateTerminalCaseAsync(testDb, rootDir).ConfigureAwait(false);
                            await arrange(testDb, c).ConfigureAwait(false);
                            Mission before = (await testDb.Driver.Missions.ReadAsync(c.Judge.Id).ConfigureAwait(false))!;

                            TerminalLandingContinuationResult result = await c.Prepared.Missions.ContinueTerminalLandingAsync(c.Judge.Id, "operator-a").ConfigureAwait(false);

                            AssertFalse(result.Granted, name + ": refused");
                            AssertStartsWith(reason, result.Reason ?? String.Empty, name + ": the reason names the refusal");
                            Mission after = (await testDb.Driver.Missions.ReadAsync(c.Judge.Id).ConfigureAwait(false))!;
                            AssertEqual(before.Status, after.Status, name + ": the Judge status is unchanged");
                            AssertEqual(before.CommitHash, after.CommitHash, name + ": the Judge commit is unchanged");
                            AssertNull(after.GeneratedArtifactReview, name + ": no grant or binding is written");
                            AssertEqual(c.Source, await RunGitCaptureAsync(c.BareDir, "rev-parse", c.Branch).ConfigureAwait(false),
                                name + ": the branch is not rewritten");
                            AssertEqual(0, (await testDb.Driver.Events.EnumerateByTypeAsync(MissionService.TerminalContinuationGrantedEventType).ConfigureAwait(false)).Count,
                                name + ": no grant event");
                        }
                    }
                    finally { DeleteTerminalRoot(rootDir); }
                }
            }).ConfigureAwait(false);

            await RunTest("A terminal landing continuation cannot be granted twice or admit a launched candidate again", async () =>
            {
                string rootDir = NewTerminalRoot();
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        TerminalCase c = await CreateTerminalCaseAsync(testDb, rootDir).ConfigureAwait(false);
                        AssertTrue((await c.Prepared.Missions.ContinueTerminalLandingAsync(c.Judge.Id, "operator-a").ConfigureAwait(false)).Granted);

                        TerminalLandingContinuationResult second = await c.Prepared.Missions.ContinueTerminalLandingAsync(c.Judge.Id, "operator-b").ConfigureAwait(false);
                        AssertFalse(second.Granted, "a second grant is refused");
                        AssertEqual("terminal_continuation_already_granted", second.Reason);

                        Mission judge = (await testDb.Driver.Missions.ReadAsync(c.Judge.Id).ConfigureAwait(false))!;
                        await c.Prepared.Missions.TryAssignAsync(judge, c.Prepared.Vessel).ConfigureAwait(false);
                        judge = (await testDb.Driver.Missions.ReadAsync(c.Judge.Id).ConfigureAwait(false))!;
                        AssertEqual(1, judge.GeneratedArtifactReview!.TerminalContinuation!.AdmittedAttempt);

                        // The admitted candidate ran once and came back to Pending at the same candidate.
                        judge.Status = MissionStatusEnum.Pending;
                        judge.StartedUtc = DateTime.UtcNow.AddMinutes(-5);
                        judge.CaptainId = null;
                        judge.ProcessId = null;
                        await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);
                        judge = (await testDb.Driver.Missions.ReadAsync(c.Judge.Id).ConfigureAwait(false))!;

                        await c.Prepared.Missions.TryAssignAsync(judge, c.Prepared.Vessel).ConfigureAwait(false);

                        Mission replayed = (await testDb.Driver.Missions.ReadAsync(c.Judge.Id).ConfigureAwait(false))!;
                        AssertEqual(MissionStatusEnum.Pending, replayed.Status, "a replay is held, not cancelled or launched");
                        AssertEqual("terminal_continuation_replayed", replayed.AssignmentHoldReason, "the replay is named");
                        AssertEqual(1, replayed.GeneratedArtifactReview!.TerminalContinuation!.AdmittedAttempt, "the admission is not repeated");
                    }
                }
                finally { DeleteTerminalRoot(rootDir); }
            }).ConfigureAwait(false);

            await RunTest("A predecessor that changes after the grant holds the Judge by name instead of admitting it", async () =>
            {
                foreach (string change in new[] { "status", "commit" })
                {
                    string rootDir = NewTerminalRoot();
                    try
                    {
                        using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                        {
                            TerminalCase c = await CreateTerminalCaseAsync(testDb, rootDir).ConfigureAwait(false);
                            AssertTrue((await c.Prepared.Missions.ContinueTerminalLandingAsync(c.Judge.Id, "operator-a").ConfigureAwait(false)).Granted);
                            Mission worker = (await testDb.Driver.Missions.ReadAsync(c.Worker.Id).ConfigureAwait(false))!;
                            if (change == "status") worker.Status = MissionStatusEnum.Cancelled;
                            else worker.CommitHash = c.MainBefore;
                            await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);
                            Mission judge = (await testDb.Driver.Missions.ReadAsync(c.Judge.Id).ConfigureAwait(false))!;

                            await c.Prepared.Missions.TryAssignAsync(judge, c.Prepared.Vessel).ConfigureAwait(false);

                            Mission after = (await testDb.Driver.Missions.ReadAsync(c.Judge.Id).ConfigureAwait(false))!;
                            AssertEqual(MissionStatusEnum.Pending, after.Status, change + ": held, not cancelled or launched");
                            AssertEqual("terminal_continuation_predecessor_changed", after.AssignmentHoldReason, change + ": the reason is named");
                            AssertEqual(c.Worker.Id, after.AssignmentHoldEntityId, change + ": the changed predecessor is named");
                            AssertEqual(0, after.GeneratedArtifactReview!.TerminalContinuation!.AdmittedAttempt, change + ": nothing is admitted");
                        }
                    }
                    finally { DeleteTerminalRoot(rootDir); }
                }
            }).ConfigureAwait(false);

            await RunTest("A Pending Judge in a Failed voyage without a grant is still cancelled by the terminal-voyage rule", async () =>
            {
                string rootDir = NewTerminalRoot();
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        TerminalCase c = await CreateTerminalCaseAsync(testDb, rootDir).ConfigureAwait(false);
                        c.Judge.Status = MissionStatusEnum.Pending;
                        await testDb.Driver.Missions.UpdateAsync(c.Judge).ConfigureAwait(false);
                        Mission judge = (await testDb.Driver.Missions.ReadAsync(c.Judge.Id).ConfigureAwait(false))!;

                        await c.Prepared.Missions.TryAssignAsync(judge, c.Prepared.Vessel).ConfigureAwait(false);

                        AssertEqual(MissionStatusEnum.Cancelled, (await testDb.Driver.Missions.ReadAsync(c.Judge.Id).ConfigureAwait(false))!.Status,
                            "only a granted Judge passes the terminal-voyage rule");
                    }
                }
                finally { DeleteTerminalRoot(rootDir); }
            }).ConfigureAwait(false);

            foreach (bool granted in new[] { true, false })
            await RunTest("A merge-queue landing of an approved Judge candidate " + (granted
                ? "writes the terminal continuation landed event" : "without a grant writes no continuation event"), async () =>
            {
                string rootDir = NewTerminalRoot();
                try
                {
                    GeneratedManifestRepos repos = await CreateGeneratedManifestReposAsync(rootDir, false, "b.txt").ConfigureAwait(false);
                    string branch = repos.Branches[0];
                    string source = await RunGitCaptureAsync(repos.BareDir, "rev-parse", branch).ConfigureAwait(false);
                    string mainBefore = await RunGitCaptureAsync(repos.BareDir, "rev-parse", "main").ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        PreparingQueue prepared = await CreatePreparingQueueAsync(testDb, repos.BareDir).ConfigureAwait(false);
                        RoutedStages stages = await CreateRoutedStagesAsync(testDb, prepared.Vessel, branch, source, "queue-landed").ConfigureAwait(false);
                        await testDb.Driver.Missions.DeleteAsync(stages.Recorder.Id).ConfigureAwait(false);
                        stages.Entry.MissionId = stages.Judge.Id;
                        await testDb.Driver.MergeEntries.UpdateAsync(stages.Entry).ConfigureAwait(false);

                        Mission judge = (await testDb.Driver.Missions.ReadAsync(stages.Judge.Id).ConfigureAwait(false))!;
                        judge.Status = MissionStatusEnum.WorkProduced;
                        await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);
                        judge = (await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                        GeneratedArtifactReviewBinding requested = new GeneratedArtifactReviewBinding
                        {
                            SourceCommit = source, TargetBaseCommit = mainBefore, CandidateCommit = source, PreparationAttempt = 1,
                            RequestedUtc = DateTime.UtcNow,
                            TerminalContinuation = granted ? new TerminalLandingContinuation
                            {
                                GrantedBy = "operator-a", GrantedUtc = DateTime.UtcNow, VoyageId = judge.VoyageId!, AdmittedAttempt = 1, AdmittedUtc = DateTime.UtcNow
                            } : null
                        };
                        AssertTrue(await testDb.Driver.Missions.TryWriteGeneratedArtifactReviewAsync(judge, requested, true).ConfigureAwait(false));
                        judge = (await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                        judge.Status = MissionStatusEnum.WorkProduced;
                        await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);
                        judge = (await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                        GeneratedArtifactReviewBinding approved = System.Text.Json.JsonSerializer.Deserialize<GeneratedArtifactReviewBinding>(
                            System.Text.Json.JsonSerializer.Serialize(judge.GeneratedArtifactReview))!;
                        approved.ApprovedUtc = DateTime.UtcNow;
                        AssertTrue(await testDb.Driver.Missions.TryWriteGeneratedArtifactReviewAsync(judge, approved, false).ConfigureAwait(false),
                            "Fixture approval isolates the queue landing event, not a real Judge verdict.");

                        await prepared.Queue.ProcessEntryByIdAsync(stages.Entry.Id).ConfigureAwait(false);

                        MergeEntry entry = (await testDb.Driver.MergeEntries.ReadAsync(stages.Entry.Id).ConfigureAwait(false))!;
                        AssertEqual(MergeStatusEnum.Landed, entry.Status, entry.TestOutput + entry.MergeFailureSummary);
                        AssertEqual(MissionStatusEnum.Complete, (await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!.Status);
                        List<ArmadaEvent> landed = await testDb.Driver.Events.EnumerateByTypeAsync(MissionService.TerminalContinuationLandedEventType).ConfigureAwait(false);
                        AssertEqual(granted ? 1 : 0, landed.Count, "one event only for a granted Judge");
                        if (granted)
                        {
                            AssertEqual(judge.Id, landed[0].MissionId);
                            AssertContains(source, landed[0].Payload ?? String.Empty, "the event names the landed candidate");
                        }
                    }
                }
                finally { DeleteTerminalRoot(rootDir); }
            }).ConfigureAwait(false);

            await RunTest("A granted Judge of a Failed voyage lands its candidate only after a real PASS and Checks at that candidate, and the voyage stays Failed", async () =>
            {
                string rootDir = NewTerminalRoot();
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        TerminalCase c = await CreateTerminalCaseAsync(testDb, rootDir).ConfigureAwait(false);
                        Vessel vessel = (await testDb.Driver.Vessels.ReadAsync(c.Prepared.Vessel.Id).ConfigureAwait(false))!;
                        vessel.LandingMode = LandingModeEnum.LocalMerge;
                        vessel.WorkingDirectory = Path.Combine(rootDir, "source");
                        vessel = await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);
                        AssertTrue((await c.Prepared.Missions.ContinueTerminalLandingAsync(c.Judge.Id, "operator-a").ConfigureAwait(false)).Granted);
                        string candidate = (await testDb.Driver.Missions.ReadAsync(c.Judge.Id).ConfigureAwait(false))!.GeneratedArtifactReview!.CandidateCommit;
                        AssertNotEqual(c.Source, candidate, "the grant prepared a regenerated candidate");

                        SyslogLogging.LoggingModule logging = CreateLogging();
                        GitService git = new GitService(logging);
                        DockService docks = new DockService(logging, testDb.Driver, c.Prepared.Settings, git);
                        int launches = 0;
                        c.Prepared.Captains.OnLaunchAgent = (_, _, _) => { launches++; return Task.FromResult(Int32.MaxValue - 60); };
                        await testDb.Driver.Captains.CreateAsync(new Captain("terminal-full-flow")
                        {
                            TenantId = Armada.Core.Constants.DefaultTenantId, AllowedPersonas = "[\"Judge\"]"
                        }).ConfigureAwait(false);

                        Mission pending = (await testDb.Driver.Missions.ReadAsync(c.Judge.Id).ConfigureAwait(false))!;
                        AssertTrue(await c.Prepared.Missions.TryAssignAsync(pending, vessel).ConfigureAwait(false), "the granted Judge launches");
                        Mission running = (await testDb.Driver.Missions.ReadAsync(c.Judge.Id).ConfigureAwait(false))!;
                        AssertEqual(1, launches);
                        AssertEqual(candidate, running.CommitHash, "the Judge reviews the regenerated candidate");

                        WorkflowProfileService profiles = new WorkflowProfileService(testDb.Driver, logging);
                        WorkflowProfile profile = await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                        {
                            TenantId = Armada.Core.Constants.DefaultTenantId, UserId = Armada.Core.Constants.DefaultUserId,
                            Name = "Terminal continuation commands", Scope = WorkflowProfileScopeEnum.Vessel, VesselId = vessel.Id,
                            IsDefault = true, BuildCommand = "git rev-parse HEAD", UnitTestCommand = "git status --porcelain && git rev-parse HEAD"
                        }).ConfigureAwait(false);
                        List<CheckRun> armed = new List<CheckRun>();
                        foreach (CheckRunTypeEnum type in new[] { CheckRunTypeEnum.Build, CheckRunTypeEnum.UnitTest })
                            armed.Add(await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                            {
                                TenantId = Armada.Core.Constants.DefaultTenantId, UserId = Armada.Core.Constants.DefaultUserId,
                                VesselId = vessel.Id, VoyageId = c.Voyage.Id, WorkflowProfileId = profile.Id, MissionId = c.Judge.Id,
                                Type = type, Status = CheckRunStatusEnum.Pending, Source = CheckRunSourceEnum.Armada,
                                CommitHash = candidate, BranchName = running.BranchName,
                                Command = type == CheckRunTypeEnum.Build ? profile.BuildCommand : profile.UnitTestCommand, Label = type.ToString()
                            }).ConfigureAwait(false));

                        CheckRunService checks = new CheckRunService(testDb.Driver, profiles, new VesselReadinessService(testDb.Driver, profiles, logging), logging);
                        LandingService landing = new LandingService(logging, testDb.Driver, c.Prepared.Settings, git);
                        landing.GeneratedArtifactPreparer = c.Prepared.Missions.PrepareGeneratedArtifactsForTargetAsync;
                        using (RemoteTriggerHttpClient remoteHttp = new RemoteTriggerHttpClient(logging))
                        {
                            Armada.Server.MissionLandingHandler handler = new Armada.Server.MissionLandingHandler(
                                logging, testDb.Driver, c.Prepared.Settings, git, c.Prepared.Queue, landing,
                                new AutoLandEvaluator(), new ConventionChecker(), new CriticalTriggerEvaluator(),
                                new MessageTemplateService(logging), null, docks, new RemoteTriggerService(null, remoteHttp, logging), null);
                            landing.OnPerformLanding = handler.HandleMissionCompleteAsync;
                            c.Prepared.Missions.OnMissionComplete = handler.HandleMissionCompleteAsync;
                            c.Prepared.Missions.OnCaptureDiff = handler.HandleCaptureDiffAsync;
                            string narrative = "The regenerated manifest and the authored change were reviewed at the exact candidate. "
                                + "The candidate keeps the reconciled Worker commit as an ancestor and changes only the declared artifact beyond it. "
                                + "Earlier Checks at the source commit are history and cannot prove this candidate; its Build and UnitTest commands remain required.";
                            c.Prepared.Missions.OnGetMissionOutput = _ => "## Completeness\n" + narrative + "\n## Correctness\n" + narrative
                                + "\n## Tests\n" + narrative + "\n## Failure Modes\n" + narrative + "\n## Verdict\nPASS\n[ARMADA:VERDICT] PASS";

                            Captain captain = (await testDb.Driver.Captains.ReadAsync(running.CaptainId!).ConfigureAwait(false))!;
                            await c.Prepared.Missions.HandleCompletionAsync(captain, c.Judge.Id).ConfigureAwait(false);

                            Mission reviewed = (await testDb.Driver.Missions.ReadAsync(c.Judge.Id).ConfigureAwait(false))!;
                            string state = System.Text.Json.JsonSerializer.Serialize(new
                            { reviewed.Status, reviewed.FailureReason, reviewed.HeldForOperatorReviewReason, reviewed.GeneratedArtifactReview });
                            AssertTrue(JudgeCheckWaitHold.IsHeld(reviewed), "the real PASS waits for its Checks at the candidate: " + state);
                            AssertNull(reviewed.GeneratedArtifactReview!.ApprovedUtc, "no approval before the Checks pass");
                            AssertEqual(c.MainBefore, await RunGitCaptureAsync(c.BareDir, "rev-parse", "main").ConfigureAwait(false), "nothing lands before the Checks pass");

                            Armada.Core.Models.AuthContext auth = Armada.Core.Models.AuthContext.Authenticated(
                                Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId, true, true, "UnitTest");
                            foreach (CheckRun check in armed)
                            {
                                CheckRun executed = await checks.RetryAsync(auth, check.Id).ConfigureAwait(false);
                                AssertEqual(CheckRunStatusEnum.Passed, executed.Status, executed.Summary);
                                AssertEqual(candidate, executed.CommitHash);
                                AssertContains(candidate, executed.Output ?? String.Empty, "the Check ran at the candidate");
                            }
                            AssertEqual(1, await c.Prepared.Missions.ReleaseJudgeCheckWaitHoldsAsync().ConfigureAwait(false), "the held PASS is released");

                            Mission landed = (await testDb.Driver.Missions.ReadAsync(c.Judge.Id).ConfigureAwait(false))!;
                            AssertNotNull(landed.GeneratedArtifactReview!.ApprovedUtc, "the real PASS and the candidate Checks approve it");
                            AssertEqual(MissionStatusEnum.Complete, landed.Status, landed.FailureReason);
                            AssertEqual(candidate, await RunGitCaptureAsync(c.BareDir, "rev-parse", "main").ConfigureAwait(false),
                                "the target moves onto exactly the candidate (main before " + c.MainBefore + ", diff snapshot length " + (landed.DiffSnapshot?.Length ?? 0) + ")");
                            List<ArmadaEvent> landedEvents = await testDb.Driver.Events.EnumerateByTypeAsync(MissionService.TerminalContinuationLandedEventType).ConfigureAwait(false);
                            AssertEqual(1, landedEvents.Count, "the direct landing writes one continuation landed event");
                            AssertContains(candidate, landedEvents[0].Payload ?? String.Empty);
                            AssertEqual(VoyageStatusEnum.Failed, (await testDb.Driver.Voyages.ReadAsync(c.Voyage.Id).ConfigureAwait(false))!.Status, "the voyage stays Failed as history");
                            Mission worker = (await testDb.Driver.Missions.ReadAsync(c.Worker.Id).ConfigureAwait(false))!;
                            AssertEqual(MissionStatusEnum.Failed, worker.Status, "the reconciled Worker keeps its Failed status");
                        }
                    }
                }
                finally { DeleteTerminalRoot(rootDir); }
            }).ConfigureAwait(false);

            await RunTest("A re-prepared candidate after target drift keeps the grant and can be admitted once more", async () =>
            {
                Mission judge = new Mission("Judge drift", "Review") { Persona = "Judge", Status = MissionStatusEnum.WorkProduced, CommitHash = "candidate-1" };
                TerminalLandingContinuation grant = new TerminalLandingContinuation
                {
                    GrantedBy = "operator-a", VoyageId = "vyg_example", AdmittedAttempt = 1, AdmittedUtc = DateTime.UtcNow,
                    Predecessors = new List<TerminalLandingContinuationPredecessor> { new TerminalLandingContinuationPredecessor { MissionId = "msn_worker", Status = "Failed", CommitHash = "source" } }
                };
                typeof(Mission).GetProperty(nameof(Mission.GeneratedArtifactReview))!.SetValue(judge, new GeneratedArtifactReviewBinding
                {
                    SourceCommit = "source", TargetBaseCommit = "target-1", CandidateCommit = "candidate-1", PreparationAttempt = 1,
                    RequestedUtc = DateTime.UtcNow, ApprovedUtc = DateTime.UtcNow, TerminalContinuation = grant
                });

                GeneratedArtifactReviewBinding next = GeneratedArtifactReviewRule.NextBinding(judge, new GeneratedArtifactLandingResult
                {
                    Outcome = "ReviewRequired", SourceCommit = "candidate-1", TargetBaseCommit = "target-2", CandidateCommit = "candidate-2"
                }, 2, DateTime.UtcNow);

                AssertEqual(2, next.PreparationAttempt);
                AssertNull(next.ApprovedUtc, "drift needs a new review");
                AssertNotNull(next.TerminalContinuation, "the grant follows the Judge to the new candidate");
                AssertEqual("operator-a", next.TerminalContinuation!.GrantedBy);
                AssertTrue(next.TerminalContinuation.AdmittedAttempt < next.PreparationAttempt, "the new candidate can be admitted once");
            }).ConfigureAwait(false);
        }

        private static string NewTerminalRoot()
        {
            string rootDir = Path.Combine(Path.GetTempPath(), "armada_terminal_continuation_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootDir);
            return rootDir;
        }

        private static void DeleteTerminalRoot(string rootDir)
        {
            try { Directory.Delete(rootDir, true); }
            catch (Exception ex) { Console.WriteLine("Test cleanup failed: " + ex.Message); }
        }

        /// <summary>
        /// A Failed voyage whose Worker was reconciled Failed with unlanded work and whose passed Judge failed to land
        /// because the target regenerated the declared manifest. The Judge is the voyage's last stage.
        /// </summary>
        private async Task<TerminalCase> CreateTerminalCaseAsync(TestDatabase testDb, string rootDir)
        {
            GeneratedManifestRepos repos = await CreateGeneratedManifestReposAsync(rootDir, true, "b.txt").ConfigureAwait(false);
            string branch = repos.Branches[0];
            string source = await RunGitCaptureAsync(repos.BareDir, "rev-parse", branch).ConfigureAwait(false);
            string mainBefore = await RunGitCaptureAsync(repos.BareDir, "rev-parse", "main").ConfigureAwait(false);
            PreparingQueue prepared = await CreatePreparingQueueAsync(testDb, repos.BareDir).ConfigureAwait(false);
            RoutedStages stages = await CreateRoutedStagesAsync(testDb, prepared.Vessel, branch, source, "terminal").ConfigureAwait(false);
            await testDb.Driver.MergeEntries.DeleteAsync(stages.Entry.Id).ConfigureAwait(false);
            await testDb.Driver.Missions.DeleteAsync(stages.Recorder.Id).ConfigureAwait(false);

            Voyage voyage = (await testDb.Driver.Voyages.ReadAsync(stages.Worker.VoyageId!).ConfigureAwait(false))!;
            voyage.Status = VoyageStatusEnum.Failed;
            await testDb.Driver.Voyages.UpdateAsync(voyage).ConfigureAwait(false);

            Mission worker = stages.Worker;
            worker.Status = MissionStatusEnum.Failed;
            TerminalVoyageMissionRule.RecordReconciledOutcome(worker, VoyageStatusEnum.Failed, TerminalVoyageMissionRule.ReasonWorkUnlanded, DateTime.UtcNow);
            await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);

            Mission judge = stages.Judge;
            judge.Status = MissionStatusEnum.LandingFailed;
            judge.AgentOutput = "terminal review\n[ARMADA:VERDICT] PASS";
            judge.FailureReason = "Landing conflict confined to " + GeneratedManifest;
            judge.RecoveryAttempts = 1;
            judge.LandingRetryCount = 1;
            await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);

            return new TerminalCase
            {
                Prepared = prepared,
                Worker = (await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!,
                Judge = (await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!,
                Voyage = (await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false))!,
                BareDir = repos.BareDir, Branch = branch, Source = source, MainBefore = mainBefore
            };
        }
    }
}
