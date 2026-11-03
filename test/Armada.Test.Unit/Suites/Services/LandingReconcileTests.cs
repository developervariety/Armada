namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
    using System.Reflection;
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using StubGitService = global::Test.Shared.Infrastructure.StubGitService;
    using SyslogLogging;

    /// <summary>
    /// Tests the landing sweep that proposes LandingFailed missions whose work is already on the default branch, the
    /// Needs You item it raises, and the confirmation that completes the mission.
    /// </summary>
    public class LandingReconcileTests : TestSuite
    {
        private const string TargetRef = "refs/remotes/origin/main";

        /// <inheritdoc />
        public override string Name => "Landing Reconcile";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Sweep proposes a mission whose commit is on the default branch once and leaves it LandingFailed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await CreateVoyageAsync(testDb).ConfigureAwait(false);
                    Mission mission = await CreateLandingFailedAsync(testDb, vessel, voyage, "Already landed", "merge conflicts in: src/Foo.cs").ConfigureAwait(false);
                    StubGitService git = new StubGitService();
                    git.AncestorResults[mission.CommitHash + "|" + TargetRef] = true;
                    LandingReconcileSweep sweep = CreateSweep(testDb, git);

                    AssertEqual(1, await sweep.SweepAsync().ConfigureAwait(false), "the first sweep proposes the mission");
                    AssertEqual(0, await sweep.SweepAsync().ConfigureAwait(false), "the second sweep proposes nothing new");

                    List<ArmadaEvent> proposals = await ProposalsAsync(testDb, mission).ConfigureAwait(false);
                    AssertEqual(1, proposals.Count, "one proposal per mission");
                    LandingReconcileEvidence evidence = JsonSerializer.Deserialize<LandingReconcileEvidence>(proposals[0].Payload!)!;
                    AssertEqual(LandingEvidenceKindEnum.CommitAncestry, evidence.EvidenceKind, "ancestry is the proof");
                    AssertEqual(mission.CommitHash!, evidence.EvidenceCommit, "the proof names the mission commit");
                    AssertEqual("origin/main", evidence.TargetRef, "the proof names the ref it read");

                    Mission? after = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.LandingFailed, after!.Status, "the sweep never completes the mission");

                    List<InboxItem> items = await new InboxService(testDb.Driver, CreateLogging()).GetInboxAsync().ConfigureAwait(false);
                    List<InboxItem> forMission = items.Where(item => item.EntityId == mission.Id).ToList();
                    AssertEqual(1, forMission.Count, "one Needs You item per mission");
                    AssertEqual(InboxService.LandingReconcileKind, forMission[0].Kind, "the item asks for confirmation");
                    AssertContains(mission.CommitHash!.Substring(0, 12), forMission[0].Detail, "the item carries the evidence");
                    AssertContains("merge conflicts in: src/Foo.cs", forMission[0].Detail, "the item keeps the failure reason");
                }
            });

            await RunTest("Sweep proposes on the exact mission trailer when the commit is not an ancestor", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Mission mission = await CreateLandingFailedAsync(testDb, vessel, null, "Squash landed", "landing failed").ConfigureAwait(false);
                    StubGitService git = new StubGitService();
                    git.AncestorResults[mission.CommitHash + "|" + TargetRef] = false;
                    string trailerCommit = new String('c', 40);
                    git.TrailerCommits[mission.Id] = trailerCommit;

                    AssertEqual(1, await CreateSweep(testDb, git).SweepAsync().ConfigureAwait(false), "the trailer is proof");

                    List<ArmadaEvent> proposals = await ProposalsAsync(testDb, mission).ConfigureAwait(false);
                    LandingReconcileEvidence evidence = JsonSerializer.Deserialize<LandingReconcileEvidence>(proposals[0].Payload!)!;
                    AssertEqual(LandingEvidenceKindEnum.MissionTrailer, evidence.EvidenceKind, "the trailer is the proof kind");
                    AssertEqual(trailerCommit, evidence.EvidenceCommit, "the proof names the trailer commit");
                    AssertTrue(git.TrailerSearchCalls.Any(call => call.Contains(LandingReconcileProof.MissionIdTrailerKey + ":" + mission.Id)),
                        "the search used the Armada-Mission-Id trailer key and the exact mission id");
                }
            });

            await RunTest("Sweep skips landing refusals, a stage start commit and work with no proof", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string logDirectory = Path.Combine(Path.GetTempPath(), "armada_reconcile_logs_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                        StubGitService git = new StubGitService { IsAncestorResult = true };

                        Mission evidenceRefusal = await CreateLandingFailedAsync(testDb, vessel, null, "Evidence refusal",
                            LandingEvidence.RefusalPrefix + ": the change could not be read").ConfigureAwait(false);
                        Mission boundaryRefusal = await CreateLandingFailedAsync(testDb, vessel, null, "Boundary refusal",
                            LandingRefusalReasons.DockBoundaryPrefix + " for vessel 'v': a finding").ConfigureAwait(false);
                        Mission protectedPath = await CreateLandingFailedAsync(testDb, vessel, null, "Protected path",
                            ProtectedPathsValidator.FormatFailureReason("CLAUDE.md", "v")).ConfigureAwait(false);
                        Mission startCommit = await CreateLandingFailedAsync(testDb, vessel, null, "Unchanged checkout", "landing failed").ConfigureAwait(false);
                        await MissionStageStartCommitResolver.WriteStageStartRecordAsync(logDirectory, startCommit.Id, startCommit.CommitHash!).ConfigureAwait(false);
                        Mission unproven = await CreateLandingFailedAsync(testDb, vessel, null, "No proof", "landing failed").ConfigureAwait(false);
                        git.AncestorResults[unproven.CommitHash + "|" + TargetRef] = false;
                        Mission proven = await CreateLandingFailedAsync(testDb, vessel, null, "Proven", "landing failed").ConfigureAwait(false);

                        LandingReconcileSweep sweep = new LandingReconcileSweep(testDb.Driver, git, CreateLogging(), logDirectory);
                        AssertEqual(1, await sweep.SweepAsync().ConfigureAwait(false), "only the proven mission is proposed");

                        foreach (Mission skipped in new[] { evidenceRefusal, boundaryRefusal, protectedPath, startCommit, unproven })
                            AssertEqual(0, (await ProposalsAsync(testDb, skipped).ConfigureAwait(false)).Count, skipped.Title + " is not proposed");
                        AssertEqual(1, (await ProposalsAsync(testDb, proven).ConfigureAwait(false)).Count, "the proven mission is proposed");
                    }
                    finally
                    {
                        if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory, true);
                    }
                }
            });

            await RunTest("Failed merges whose work is on the target branch show as delivered context; unproven ones stay Critical", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    StubGitService git = new StubGitService();
                    MergeEntry delivered = await CreateFailedMergeAsync(testDb, vessel, git, true).ConfigureAwait(false);
                    MergeEntry undelivered = await CreateFailedMergeAsync(testDb, vessel, git, false).ConfigureAwait(false);
                    MergeEntry unknown = await CreateFailedMergeAsync(testDb, vessel, git, null).ConfigureAwait(false);
                    LandingReconcileSweep sweep = CreateSweep(testDb, git);

                    await sweep.SweepAsync().ConfigureAwait(false);
                    await sweep.SweepAsync().ConfigureAwait(false);

                    MergeEntry? deliveredAfter = await testDb.Driver.MergeEntries.ReadAsync(delivered.Id).ConfigureAwait(false);
                    AssertEqual(MergeStatusEnum.Failed, deliveredAfter!.Status, "the failed outcome is kept");
                    AssertNotNull(deliveredAfter.DeliveryEvidence, "the proof is stored on the entry");
                    Mission deliveredMission = (await testDb.Driver.Missions.ReadAsync(delivered.MissionId!).ConfigureAwait(false))!;
                    AssertEqual(deliveredMission.CommitHash!, deliveredAfter.DeliveryEvidence!.EvidenceCommit, "the proof names the mission commit");
                    AssertEqual("origin/main", deliveredAfter.DeliveryEvidence.TargetRef, "the proof names the target it read");
                    foreach (MergeEntry open in new[] { undelivered, unknown })
                        AssertNull((await testDb.Driver.MergeEntries.ReadAsync(open.Id).ConfigureAwait(false))!.DeliveryEvidence, "no proof is stored for an unproven entry");

                    List<ArmadaEvent> proven = (await testDb.Driver.Events.EnumerateByEntityAsync("merge_entry", delivered.Id, 50).ConfigureAwait(false))
                        .Where(item => item.EventType == LandingReconcileSweep.MergeDeliveryProvenEventType).ToList();
                    AssertEqual(1, proven.Count, "the proof is recorded once across sweeps");

                    List<InboxItem> items = await new InboxService(testDb.Driver, CreateLogging()).GetInboxAsync().ConfigureAwait(false);
                    InboxItem deliveredItem = items.Single(item => item.EntityId == delivered.Id);
                    AssertEqual(InboxService.MergeFailedDeliveredKind, deliveredItem.Kind, "the delivered failure is context");
                    AssertEqual(InboxSeverityEnum.Info, deliveredItem.Severity, "the delivered failure is not critical");
                    AssertContains(deliveredMission.CommitHash!.Substring(0, 12), deliveredItem.Detail, "the item carries the evidence");
                    foreach (MergeEntry open in new[] { undelivered, unknown })
                    {
                        InboxItem openItem = items.Single(item => item.EntityId == open.Id);
                        AssertEqual("merge_failed", openItem.Kind, "an unproven failure keeps its kind");
                        AssertEqual(InboxSeverityEnum.Critical, openItem.Severity, "an unproven failure stays critical");
                    }
                }
            });

            await RunTest("Sweep fetches each vessel once per sweep and rotates through bounded pages", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    StubGitService git = new StubGitService { IsAncestorResult = true };
                    List<Mission> missions = new List<Mission>();
                    for (int index = 0; index < 3; index++)
                    {
                        missions.Add(await CreateLandingFailedAsync(testDb, vessel, null, "Mission " + index, "landing failed").ConfigureAwait(false));
                        await Task.Delay(5).ConfigureAwait(false);
                    }

                    LandingReconcileSweep sweep = CreateSweep(testDb, git);
                    sweep.MaxPerCycle = 2;

                    AssertEqual(2, await sweep.SweepAsync().ConfigureAwait(false), "the first sweep reads one page of two");
                    AssertEqual(1, git.FetchCalls.Count, "the vessel is fetched once for two missions");
                    AssertEqual(1, await sweep.SweepAsync().ConfigureAwait(false), "the second sweep reads the next page");
                    AssertEqual(2, git.FetchCalls.Count, "each sweep fetches the vessel once");
                    AssertEqual(0, await sweep.SweepAsync().ConfigureAwait(false), "the third sweep wraps to the first page, already proposed");
                    foreach (Mission mission in missions)
                        AssertEqual(1, (await ProposalsAsync(testDb, mission).ConfigureAwait(false)).Count, mission.Title + " is proposed once");
                }
            });

            await RunTest("A LandingFailed mission cannot complete without a proposal", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Mission mission = await CreateLandingFailedAsync(testDb, vessel, null, "Not proposed", "landing failed").ConfigureAwait(false);
                    StubGitService git = new StubGitService { IsAncestorResult = true };

                    MissionStatusTransitionResult result = await CreateTransitions(testDb, git).TransitionAsync(mission, MissionStatusEnum.Complete).ConfigureAwait(false);

                    AssertEqual(MissionStatusTransitionOutcomeEnum.InvalidTransition, result.Outcome, "LandingFailed to Complete stays invalid without a proposal");
                    Mission? after = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.LandingFailed, after!.Status, "the mission is unchanged");
                }
            });

            await RunTest("Confirming a proposal completes the mission, its voyage and closes its incident", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await CreateVoyageAsync(testDb).ConfigureAwait(false);
                    Mission mission = await CreateLandingFailedAsync(testDb, vessel, voyage, "Landed elsewhere", "landing failed").ConfigureAwait(false);
                    StubGitService git = new StubGitService();
                    git.AncestorResults[mission.CommitHash + "|" + TargetRef] = true;
                    AuthContext auth = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, true, "UnitTest");
                    IncidentService incidents = new IncidentService(testDb.Driver);
                    Incident incident = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                    {
                        Title = "Landing failed",
                        MissionId = mission.Id,
                        VesselId = vessel.Id
                    }).ConfigureAwait(false);
                    await CreateSweep(testDb, git).SweepAsync().ConfigureAwait(false);

                    MissionStatusTransitionResult result = await CreateTransitions(testDb, git).TransitionAsync(mission, MissionStatusEnum.Complete).ConfigureAwait(false);

                    AssertEqual(MissionStatusTransitionOutcomeEnum.Applied, result.Outcome, "the confirmation applies: " + result.Message);
                    Mission? after = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Complete, after!.Status, "the mission is Complete");
                    AssertNull(after.FailureReason, "the failure reason is cleared");
                    AssertNotNull(after.CompletedUtc, "the completion time is recorded");
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id, 50).ConfigureAwait(false);
                    ArmadaEvent? reconciled = events.FirstOrDefault(item => item.EventType == LandingReconcileProposals.ReconciledEventType);
                    AssertNotNull(reconciled, "the confirmation is recorded");
                    LandingReconcileEvidence recorded = JsonSerializer.Deserialize<LandingReconcileEvidence>(reconciled!.Payload!)!;
                    AssertEqual("landing failed", recorded.FailureReason, "the event keeps the reason the landing failed");
                    Voyage? voyageAfter = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.Complete, voyageAfter!.Status, "voyage completion runs after the confirmation");
                    Incident? incidentAfter = await incidents.ReadAsync(auth, incident.Id).ConfigureAwait(false);
                    AssertEqual(IncidentStatusEnum.Closed, incidentAfter!.Status, "the mission's incident is closed");
                    List<InboxItem> items = await new InboxService(testDb.Driver, CreateLogging()).GetInboxAsync().ConfigureAwait(false);
                    AssertFalse(items.Any(item => item.EntityId == mission.Id), "the Needs You item is gone");
                }
            });

            await RunTest("Confirming refuses when the proof is gone or a Check failed and leaves the mission LandingFailed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Mission mission = await CreateLandingFailedAsync(testDb, vessel, null, "Proof changes", "landing failed").ConfigureAwait(false);
                    StubGitService git = new StubGitService();
                    git.AncestorResults[mission.CommitHash + "|" + TargetRef] = true;
                    await CreateSweep(testDb, git).SweepAsync().ConfigureAwait(false);
                    MissionStatusTransitionService transitions = CreateTransitions(testDb, git);

                    git.AncestorResults[mission.CommitHash + "|" + TargetRef] = false;
                    MissionStatusTransitionResult gone = await transitions.TransitionAsync(mission, MissionStatusEnum.Complete).ConfigureAwait(false);
                    AssertEqual(MissionStatusTransitionOutcomeEnum.Refused, gone.Outcome, "a proposal whose proof no longer holds is refused");
                    AssertContains("landing_reconcile_proof_unavailable", gone.Message, "the refusal names the missing proof");

                    git.AncestorResults[mission.CommitHash + "|" + TargetRef] = true;
                    await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                    {
                        MissionId = mission.Id,
                        Status = CheckRunStatusEnum.Failed,
                        Command = "dotnet test",
                        StartedUtc = DateTime.UtcNow,
                        CommitHash = mission.CommitHash
                    }).ConfigureAwait(false);
                    MissionStatusTransitionResult failedCheck = await transitions.TransitionAsync(mission, MissionStatusEnum.Complete).ConfigureAwait(false);
                    AssertEqual(MissionStatusTransitionOutcomeEnum.Refused, failedCheck.Outcome, "the manual completion gates still apply");
                    AssertContains("manual_completion_failed_check", failedCheck.Message, "the refusal names the failed Check");

                    Mission? after = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.LandingFailed, after!.Status, "a refusal never changes the mission");
                }
            });

            await RunTest("Trailer search matches the exact Armada-Mission-Id trailer and not a prose mention", async () =>
            {
                string repository = Path.Combine(Path.GetTempPath(), "armada_trailer_repo_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(repository);
                try
                {
                    await RunGitAsync(repository, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(repository, "config", "user.email", "test@example.test").ConfigureAwait(false);
                    await RunGitAsync(repository, "config", "user.name", "Test").ConfigureAwait(false);
                    await CommitAsync(repository, "a.txt", "mentions msn_prose in prose only").ConfigureAwait(false);
                    await CommitAsync(repository, "b.txt", "carries the trailer\n\nArmada-Mission-Id: msn_exact").ConfigureAwait(false);
                    string exactCommit = (await RunGitAsync(repository, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    await CommitAsync(repository, "c.txt", "longer id\n\nArmada-Mission-Id: msn_exact_longer").ConfigureAwait(false);

                    LoggingModule logging = CreateLogging();
                    GitService git = new GitService(logging);
                    IReadOnlyList<string> exact = await git.FindCommitsByTrailerAsync(
                        repository, "refs/heads/main", LandingReconcileProof.MissionIdTrailerKey, "msn_exact", null, 1).ConfigureAwait(false);
                    AssertEqual(1, exact.Count, "the exact trailer matches one commit");
                    AssertEqual(exactCommit, exact[0], "the match is the trailer commit, not the longer id");
                    AssertEqual(0, (await git.FindCommitsByTrailerAsync(
                        repository, "refs/heads/main", LandingReconcileProof.MissionIdTrailerKey, "msn_prose", null, 1).ConfigureAwait(false)).Count,
                        "a prose mention is not a trailer");
                    AssertEqual(0, (await git.FindCommitsByTrailerAsync(
                        repository, "refs/heads/main", LandingReconcileProof.MissionIdTrailerKey, "msn_exact", DateTime.UtcNow.AddDays(1), 1).ConfigureAwait(false)).Count,
                        "the creation bound excludes commits older than the mission");
                    AssertEqual(0, (await git.FindCommitsByTrailerAsync(
                        repository, "refs/remotes/origin/missing", LandingReconcileProof.MissionIdTrailerKey, "msn_exact", null, 1).ConfigureAwait(false)).Count,
                        "a ref that does not resolve finds nothing");
                }
                finally
                {
                    if (Directory.Exists(repository))
                    {
                        foreach (string file in Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories))
                            File.SetAttributes(file, FileAttributes.Normal);
                        Directory.Delete(repository, true);
                    }
                }
            });

            await RunTest("An operator records delivery on a failed entry with no mission and the entry keeps its failure", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    StubGitService git = new StubGitService();
                    string commit = new String('a', 40);
                    MergeEntry entry = await CreateMissionlessFailedMergeAsync(testDb, vessel, git, commit, true).ConfigureAwait(false);

                    MergeDeliveryRecordResult result = await new MergeDeliveryRecorder(testDb.Driver, git)
                        .RecordAsync(entry.Id, commit, OperatorCaller(" Example operator ", "crd_example", "usr_example"), "inc_example").ConfigureAwait(false);

                    AssertTrue(result.Recorded, "the proven delivery is recorded: " + result.Message);
                    AssertEqual(1, git.FetchCalls.Count(path => path == vessel.LocalPath), "the vessel is fetched before the proof is read");
                    MergeEntry after = (await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false))!;
                    AssertEqual(MergeStatusEnum.Failed, after.Status, "the failed outcome is kept");
                    AssertNotNull(after.DeliveryEvidence, "the proof is stored on the entry");
                    LandingReconcileEvidence evidence = after.DeliveryEvidence!;
                    AssertEqual(commit, evidence.EvidenceCommit, "the proof names the recorded commit");
                    AssertEqual(LandingEvidenceKindEnum.CommitAncestry, evidence.EvidenceKind, "ancestry is the proof");
                    AssertEqual("origin/main", evidence.TargetRef, "the proof names the target it read");
                    AssertEqual(vessel.Id, evidence.VesselId, "the proof names the vessel");
                    AssertEqual(String.Empty, evidence.MissionId, "an entry with no mission records no mission");
                    AssertEqual("Example operator", evidence.RecordedBy, "the recorder is the authenticated caller");
                    AssertEqual("inc_example", evidence.IncidentId, "the incident the operator named is kept");

                    List<ArmadaEvent> recorded = (await testDb.Driver.Events.EnumerateByEntityAsync("merge_entry", entry.Id, 50).ConfigureAwait(false))
                        .Where(item => item.EventType == MergeDeliveryRecorder.RecordedEventType).ToList();
                    AssertEqual(1, recorded.Count, "one event records the delivery");

                    List<InboxItem> items = await new InboxService(testDb.Driver, CreateLogging()).GetInboxAsync().ConfigureAwait(false);
                    InboxItem item = items.Single(candidate => candidate.EntityId == entry.Id);
                    AssertEqual(InboxService.MergeFailedDeliveredKind, item.Kind, "the recorded failure is context");
                    AssertEqual(InboxSeverityEnum.Info, item.Severity, "the recorded failure is not critical");
                    AssertContains("Recorded by Example operator", item.Detail, "the item names the operator who recorded the proof");
                }
            });

            await RunTest("Recording delivery refuses each ineligible or unproven entry by name and leaves it unchanged", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    StubGitService git = new StubGitService();
                    string commit = new String('a', 40);
                    AuthContext caller = OperatorCaller("Example operator", null, "usr_example");
                    AuthContext nameless = OperatorCaller(null, null, String.Empty);

                    MergeEntry queued = await CreateMissionlessFailedMergeAsync(testDb, vessel, git, commit, true).ConfigureAwait(false);
                    queued.Status = MergeStatusEnum.Queued;
                    await testDb.Driver.MergeEntries.UpdateAsync(queued).ConfigureAwait(false);

                    MergeEntry alreadyRecorded = await CreateMissionlessFailedMergeAsync(testDb, vessel, git, commit, true).ConfigureAwait(false);
                    alreadyRecorded.DeliveryEvidence = new LandingReconcileEvidence
                    {
                        VesselId = vessel.Id,
                        EvidenceCommit = new String('e', 40),
                        TargetRef = "origin/main",
                        RecordedBy = "Earlier operator"
                    };
                    await testDb.Driver.MergeEntries.UpdateAsync(alreadyRecorded).ConfigureAwait(false);

                    MergeEntry missionBound = await CreateMissionlessFailedMergeAsync(testDb, vessel, git, commit, true).ConfigureAwait(false);
                    missionBound.MissionId = "msn_example";
                    await testDb.Driver.MergeEntries.UpdateAsync(missionBound).ConfigureAwait(false);

                    MergeEntry noVessel = await CreateMissionlessFailedMergeAsync(testDb, vessel, git, commit, true).ConfigureAwait(false);
                    noVessel.VesselId = null;
                    await testDb.Driver.MergeEntries.UpdateAsync(noVessel).ConfigureAwait(false);

                    MergeEntry noIdentity = await CreateMissionlessFailedMergeAsync(testDb, vessel, git, commit, true).ConfigureAwait(false);
                    string offTargetCommit = new String('b', 40);
                    MergeEntry offTarget = await CreateMissionlessFailedMergeAsync(testDb, vessel, git, offTargetCommit, false).ConfigureAwait(false);
                    MergeEntry movedBranch = await CreateMissionlessFailedMergeAsync(testDb, vessel, git, commit, true).ConfigureAwait(false);
                    git.RevisionCommitShas[vessel.LocalPath + "|refs/heads/" + movedBranch.BranchName] = new String('d', 40);

                    (string Label, string EntryId, string Commit, AuthContext Caller, string Code)[] cases = new[]
                    {
                        ("unknown entry", "mrg_missing_example", commit, caller, MergeDeliveryRecorder.NotFoundCode),
                        ("entry that is not Failed", queued.Id, commit, caller, MergeDeliveryRecorder.NotFailedCode),
                        ("entry that already carries proof", alreadyRecorded.Id, commit, caller, MergeDeliveryRecorder.AlreadyRecordedCode),
                        ("entry bound to a mission", missionBound.Id, commit, caller, MergeDeliveryRecorder.MissionBoundCode),
                        ("entry with no vessel", noVessel.Id, commit, caller, MergeDeliveryRecorder.RepositoryUnavailableCode),
                        ("caller with no identity", noIdentity.Id, commit, nameless, MergeDeliveryRecorder.NoCallerIdentityCode),
                        ("commit not on the target branch", offTarget.Id, offTargetCommit, caller, MergeDeliveryRecorder.NotOnTargetCode),
                        ("branch tip that is not the commit", movedBranch.Id, commit, caller, MergeDeliveryRecorder.BranchTipDiffersCode)
                    };

                    MergeDeliveryRecorder recorder = new MergeDeliveryRecorder(testDb.Driver, git);
                    HashSet<string> codes = new HashSet<string>(StringComparer.Ordinal);
                    foreach ((string label, string entryId, string caseCommit, AuthContext caseCaller, string code) in cases)
                    {
                        MergeEntry? before = await testDb.Driver.MergeEntries.ReadAsync(entryId).ConfigureAwait(false);
                        MergeDeliveryRecordResult result = await recorder.RecordAsync(entryId, caseCommit, caseCaller, null).ConfigureAwait(false);

                        AssertFalse(result.Recorded, label + ": refused");
                        AssertEqual(code, result.Code, label + ": the refusal names its reason: " + result.Message);
                        codes.Add(result.Code!);
                        MergeEntry? after = await testDb.Driver.MergeEntries.ReadAsync(entryId).ConfigureAwait(false);
                        AssertEqual(before == null, after == null, label + ": the entry is neither created nor deleted");
                        if (before != null)
                        {
                            AssertEqual(before.Status, after!.Status, label + ": the status is unchanged");
                            AssertEqual(JsonSerializer.Serialize(before.DeliveryEvidence), JsonSerializer.Serialize(after.DeliveryEvidence), label + ": the proof is unchanged");
                            int recordedEvents = (await testDb.Driver.Events.EnumerateByEntityAsync("merge_entry", entryId, 50).ConfigureAwait(false))
                                .Count(item => item.EventType == MergeDeliveryRecorder.RecordedEventType);
                            AssertEqual(0, recordedEvents, label + ": a refusal writes no event");
                        }
                    }

                    AssertEqual(cases.Length, codes.Count, "every refusal has its own reason");
                }
            });

            await RunTest("A cancelled mission's failed entry whose branch is gone records delivery by ancestry or by patch-id and the mission is unchanged", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    StubGitService git = new StubGitService();
                    AuthContext caller = OperatorCaller("Example operator", null, "usr_example");
                    MergeDeliveryRecorder recorder = new MergeDeliveryRecorder(testDb.Driver, git);

                    // Ancestry: the mission's own commit is on the target branch.
                    string landed = new String('a', 40);
                    (Mission ancestryMission, MergeEntry ancestryEntry) = await CreateCancelledMissionMergeAsync(testDb, vessel, git, landed, MissionStatusEnum.Cancelled).ConfigureAwait(false);
                    git.AncestorResults[landed + "|" + TargetRef] = true;

                    // Patch-id: the work reached the target branch as a different commit with the same patch.
                    string work = new String('b', 40);
                    string picked = new String('c', 40);
                    (Mission patchMission, MergeEntry patchEntry) = await CreateCancelledMissionMergeAsync(testDb, vessel, git, work, MissionStatusEnum.Cancelled).ConfigureAwait(false);
                    git.AncestorResults[work + "|" + TargetRef] = false;
                    git.AncestorResults[picked + "|" + TargetRef] = true;
                    git.PatchIds[work] = "patch-same";
                    git.PatchIds[picked] = "patch-same";

                    foreach ((string label, Mission mission, MergeEntry entry, string commit, LandingEvidenceKindEnum kind) in new[]
                    {
                        ("ancestry", ancestryMission, ancestryEntry, landed, LandingEvidenceKindEnum.CommitAncestry),
                        ("patch-id", patchMission, patchEntry, picked, LandingEvidenceKindEnum.PatchId)
                    })
                    {
                        MergeDeliveryRecordResult result = await recorder.RecordAsync(entry.Id, commit, caller, null, kind).ConfigureAwait(false);

                        AssertTrue(result.Recorded, label + ": the proven delivery is recorded: " + result.Message);
                        MergeEntry after = (await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false))!;
                        AssertEqual(MergeStatusEnum.Failed, after.Status, label + ": the failed outcome is kept");
                        LandingReconcileEvidence evidence = after.DeliveryEvidence!;
                        AssertEqual(kind, evidence.EvidenceKind, label + ": the proof kind is the one proven");
                        AssertEqual(commit, evidence.EvidenceCommit, label + ": the proof names the commit on the target branch");
                        AssertEqual(mission.Id, evidence.MissionId, label + ": the proof names the entry's mission");
                        AssertEqual(mission.CommitHash, evidence.SourceCommit, label + ": the source of the work comes from the mission record");
                        AssertEqual("Example operator", evidence.RecordedBy, label + ": the recorder is the authenticated caller");
                        Mission missionAfter = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                        AssertEqual(MissionStatusEnum.Cancelled, missionAfter.Status, label + ": the mission is not changed");
                    }

                    MergeEntry patched = (await testDb.Driver.MergeEntries.ReadAsync(patchEntry.Id).ConfigureAwait(false))!;
                    AssertContains("same patch", patched.DeliveryEvidence!.Describe(), "the patch-id proof reads as a patch match");
                }
            });

            await RunTest("Recording delivery on a mission's entry or by patch-id refuses each unproven case by name and leaves the entry and mission unchanged", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    StubGitService git = new StubGitService();
                    AuthContext caller = OperatorCaller("Example operator", null, "usr_example");
                    string onTarget = new String('a', 40);
                    git.AncestorResults[onTarget + "|" + TargetRef] = true;
                    git.PatchIds[onTarget] = "patch-target";

                    // The work's patch differs from the commit on the target branch.
                    string otherWork = new String('b', 40);
                    git.PatchIds[otherWork] = "patch-other";
                    (Mission mismatchMission, MergeEntry mismatch) = await CreateCancelledMissionMergeAsync(testDb, vessel, git, otherWork, MissionStatusEnum.Cancelled).ConfigureAwait(false);

                    // The mission recorded no commit, so there is no work to compare; the request's commit is not a source.
                    (Mission sourcelessMission, MergeEntry sourceless) = await CreateCancelledMissionMergeAsync(testDb, vessel, git, null, MissionStatusEnum.Cancelled).ConfigureAwait(false);

                    // The mission is not Cancelled, so the landing sweep still owns its proof.
                    (Mission failedMission, MergeEntry notCancelled) = await CreateCancelledMissionMergeAsync(testDb, vessel, git, onTarget, MissionStatusEnum.Failed).ConfigureAwait(false);

                    // The mission's branch still resolves, so the work may still change.
                    (Mission branchMission, MergeEntry branchPresent) = await CreateCancelledMissionMergeAsync(testDb, vessel, git, onTarget, MissionStatusEnum.Cancelled).ConfigureAwait(false);
                    git.RevisionCommitShas[vessel.LocalPath + "|refs/heads/" + branchPresent.BranchName] = onTarget;

                    // An entry with no mission whose branch is gone has no work to compare a patch with.
                    MergeEntry missionlessGone = await CreateMissionlessFailedMergeAsync(testDb, vessel, git, onTarget, true).ConfigureAwait(false);
                    git.RevisionCommitShas[vessel.LocalPath + "|refs/heads/" + missionlessGone.BranchName] = null;

                    // Git fails while it reads the branch: that is not "branch gone", for a mission's entry or one with none.
                    (Mission unreadableMission, MergeEntry unreadable) = await CreateCancelledMissionMergeAsync(testDb, vessel, git, onTarget, MissionStatusEnum.Cancelled).ConfigureAwait(false);
                    git.BranchPresence[vessel.LocalPath + "|" + unreadable.BranchName] = null;
                    MergeEntry missionlessUnreadable = await CreateMissionlessFailedMergeAsync(testDb, vessel, git, onTarget, true).ConfigureAwait(false);
                    git.BranchPresence[vessel.LocalPath + "|" + missionlessUnreadable.BranchName] = null;

                    // The branch exists but its tip cannot be resolved.
                    MergeEntry tipUnreadable = await CreateMissionlessFailedMergeAsync(testDb, vessel, git, onTarget, true).ConfigureAwait(false);
                    git.BranchPresence[vessel.LocalPath + "|" + tipUnreadable.BranchName] = true;
                    git.RevisionCommitShas[vessel.LocalPath + "|refs/heads/" + tipUnreadable.BranchName] = null;

                    (string Label, MergeEntry Entry, Mission? Mission, LandingEvidenceKindEnum Kind, string Code)[] cases = new (string, MergeEntry, Mission?, LandingEvidenceKindEnum, string)[]
                    {
                        ("patch-id that differs from the work", mismatch, mismatchMission, LandingEvidenceKindEnum.PatchId, MergeDeliveryRecorder.PatchIdMismatchCode),
                        ("mission with no recorded commit", sourceless, sourcelessMission, LandingEvidenceKindEnum.PatchId, MergeDeliveryRecorder.SourceCommitUnavailableCode),
                        ("mission that is not Cancelled", notCancelled, failedMission, LandingEvidenceKindEnum.CommitAncestry, MergeDeliveryRecorder.MissionBoundCode),
                        ("mission whose branch is present", branchPresent, branchMission, LandingEvidenceKindEnum.CommitAncestry, MergeDeliveryRecorder.MissionBoundCode),
                        ("entry with no mission and no branch", missionlessGone, null, LandingEvidenceKindEnum.PatchId, MergeDeliveryRecorder.SourceCommitUnavailableCode),
                        ("proof kind the recorder does not read", missionlessGone, null, LandingEvidenceKindEnum.MissionTrailer, MergeDeliveryRecorder.ProofKindUnsupportedCode),
                        ("mission whose branch cannot be read", unreadable, unreadableMission, LandingEvidenceKindEnum.CommitAncestry, MergeDeliveryRecorder.RepositoryUnavailableCode),
                        ("entry with no mission whose branch cannot be read", missionlessUnreadable, null, LandingEvidenceKindEnum.CommitAncestry, MergeDeliveryRecorder.RepositoryUnavailableCode),
                        ("branch whose tip cannot be read", tipUnreadable, null, LandingEvidenceKindEnum.CommitAncestry, MergeDeliveryRecorder.RepositoryUnavailableCode)
                    };

                    MergeDeliveryRecorder recorder = new MergeDeliveryRecorder(testDb.Driver, git);
                    foreach ((string label, MergeEntry entry, Mission? mission, LandingEvidenceKindEnum kind, string code) in cases)
                    {
                        MergeEntry before = (await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false))!;
                        MergeDeliveryRecordResult result = await recorder.RecordAsync(entry.Id, onTarget, caller, null, kind).ConfigureAwait(false);

                        AssertFalse(result.Recorded, label + ": refused");
                        AssertEqual(code, result.Code, label + ": the refusal names its reason: " + result.Message);
                        MergeEntry after = (await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false))!;
                        AssertEqual(before.Status, after.Status, label + ": the status is unchanged");
                        AssertNull(after.DeliveryEvidence, label + ": no proof is stored");
                        int recordedEvents = (await testDb.Driver.Events.EnumerateByEntityAsync("merge_entry", entry.Id, 50).ConfigureAwait(false))
                            .Count(item => item.EventType == MergeDeliveryRecorder.RecordedEventType);
                        AssertEqual(0, recordedEvents, label + ": a refusal writes no event");
                        if (mission != null)
                        {
                            Mission missionAfter = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                            AssertEqual(mission.Status, missionAfter.Status, label + ": the mission is not changed");
                        }
                    }
                }
            });

            await RunTest("An entry with no mission whose branch moved records delivery when the commit on the target carries the branch tip's patch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    StubGitService git = new StubGitService();
                    string picked = new String('a', 40);
                    string tip = new String('d', 40);
                    MergeEntry entry = await CreateMissionlessFailedMergeAsync(testDb, vessel, git, picked, true).ConfigureAwait(false);
                    git.RevisionCommitShas[vessel.LocalPath + "|refs/heads/" + entry.BranchName] = tip;
                    git.PatchIds[picked] = "patch-same";
                    git.PatchIds[tip] = "patch-same";
                    MergeDeliveryRecorder recorder = new MergeDeliveryRecorder(testDb.Driver, git);
                    AuthContext caller = OperatorCaller("Example operator", null, "usr_example");

                    MergeDeliveryRecordResult byAncestry = await recorder.RecordAsync(entry.Id, picked, caller, null, LandingEvidenceKindEnum.CommitAncestry).ConfigureAwait(false);
                    AssertEqual(MergeDeliveryRecorder.BranchTipDiffersCode, byAncestry.Code, "ancestry still needs the branch tip to be the commit: " + byAncestry.Message);

                    MergeDeliveryRecordResult byPatch = await recorder.RecordAsync(entry.Id, picked, caller, null, LandingEvidenceKindEnum.PatchId).ConfigureAwait(false);
                    AssertTrue(byPatch.Recorded, "the same patch on the target proves the delivery: " + byPatch.Message);
                    LandingReconcileEvidence evidence = (await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false))!.DeliveryEvidence!;
                    AssertEqual(LandingEvidenceKindEnum.PatchId, evidence.EvidenceKind, "patch-id is the proof");
                    AssertEqual(picked, evidence.EvidenceCommit, "the proof names the commit on the target branch");
                    AssertEqual(tip, evidence.SourceCommit, "the source is the branch tip the repository resolves");
                }
            });

            await RunTest("REST and MCP record delivery under the caller's own identity and ignore a recorder in the request", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(harness.Database).ConfigureAwait(false);
                    foreach ((string surface, string expected) in new[] { ("REST", "API Key"), ("MCP", "Test operator") })
                    {
                        string commit = Guid.NewGuid().ToString("N") + "00000000";
                        MergeEntry entry = await CreateMissionlessFailedMergeAsync(harness.Database, vessel, harness.Git, commit, true).ConfigureAwait(false);
                        SurfaceReply reply = surface == "REST"
                            ? await harness.RestAsync(System.Net.Http.HttpMethod.Post, "/api/v1/merge-queue/" + entry.Id + "/delivery-evidence",
                                new { commit = commit, incidentId = "inc_example", recordedBy = "Someone else" }).ConfigureAwait(false)
                            : await harness.McpAsync("armada_record_merge_delivery",
                                new { entryId = entry.Id, commit = commit, incidentId = "inc_example", recordedBy = "Someone else" }).ConfigureAwait(false);

                        AssertFalse(reply.Refused, surface + ": the proven delivery is recorded: " + reply);
                        MergeEntry after = (await harness.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false))!;
                        AssertEqual(expected, after.DeliveryEvidence?.RecordedBy, surface + ": the recorder comes from the credential, never the request");
                        AssertEqual("inc_example", after.DeliveryEvidence?.IncidentId, surface + ": the incident is kept");

                        string picked = Guid.NewGuid().ToString("N") + "00000000";
                        string tip = Guid.NewGuid().ToString("N") + "00000000";
                        MergeEntry moved = await CreateMissionlessFailedMergeAsync(harness.Database, vessel, harness.Git, picked, true).ConfigureAwait(false);
                        harness.Git.RevisionCommitShas[vessel.LocalPath + "|refs/heads/" + moved.BranchName] = tip;
                        harness.Git.PatchIds[picked] = "patch-" + surface;
                        harness.Git.PatchIds[tip] = "patch-" + surface;
                        SurfaceReply patchReply = surface == "REST"
                            ? await harness.RestAsync(System.Net.Http.HttpMethod.Post, "/api/v1/merge-queue/" + moved.Id + "/delivery-evidence",
                                new { commit = picked, proofKind = "PatchId" }).ConfigureAwait(false)
                            : await harness.McpAsync("armada_record_merge_delivery",
                                new { entryId = moved.Id, commit = picked, proofKind = "PatchId" }).ConfigureAwait(false);
                        AssertFalse(patchReply.Refused, surface + ": the proof kind in the request reaches the recorder: " + patchReply);
                        MergeEntry movedAfter = (await harness.Driver.MergeEntries.ReadAsync(moved.Id).ConfigureAwait(false))!;
                        AssertEqual(LandingEvidenceKindEnum.PatchId, movedAfter.DeliveryEvidence?.EvidenceKind, surface + ": the patch-id proof is stored");
                    }

                    SurfaceReply restRefusal = await harness.RestAsync(System.Net.Http.HttpMethod.Post, "/api/v1/merge-queue/mrg_missing_example/delivery-evidence",
                        new { commit = new String('a', 40) }).ConfigureAwait(false);
                    AssertEqual(404, restRefusal.Status, "REST: an unknown entry is not found: " + restRefusal);
                    AssertContains(MergeDeliveryRecorder.NotFoundCode, restRefusal.Body, "REST: the refusal names its reason");
                    SurfaceReply mcpRefusal = await harness.McpAsync("armada_record_merge_delivery",
                        new { entryId = "mrg_missing_example", commit = new String('a', 40) }).ConfigureAwait(false);
                    AssertTrue(mcpRefusal.Refused, "MCP: an unknown entry is refused: " + mcpRefusal);
                    AssertContains(MergeDeliveryRecorder.NotFoundCode, mcpRefusal.Body, "MCP: the refusal names its reason");

                    AuthContext missionCaller = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "Bearer");
                    missionCaller.MissionId = "msn_example";
                    AssertFalse(Armada.Server.Mcp.McpToolAccessPolicy.IsAllowed(missionCaller, "armada_record_merge_delivery"),
                        "a mission caller cannot record delivery, even when its owner is an administrator");
                }
            });
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static LandingReconcileSweep CreateSweep(TestDatabase testDb, IGitService git)
        {
            return new LandingReconcileSweep(testDb.Driver, git, CreateLogging());
        }

        private static MissionStatusTransitionService CreateTransitions(TestDatabase testDb, IGitService git)
        {
            return new MissionStatusTransitionService(
                testDb.Driver,
                DispatchProxy.Create<IAdmiralService, EmptyServiceProxy>(),
                DispatchProxy.Create<IMissionService, EmptyServiceProxy>(),
                git,
                (Mission mission, CancellationToken token) => Task.FromResult(false),
                (Mission mission, Dock dock) => Task.CompletedTask,
                (string eventType, string message, string? entityType, string? entityId, string? captainId, string? missionId, string? vesselId, string? voyageId) => Task.CompletedTask,
                CreateLogging());
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb)
        {
            Vessel vessel = new Vessel("reconcile-vessel-" + Guid.NewGuid().ToString("N").Substring(0, 6), "https://example.test/reconcile.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_reconcile_bare_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<Voyage> CreateVoyageAsync(TestDatabase testDb)
        {
            return await testDb.Driver.Voyages.CreateAsync(new Voyage("Reconcile voyage")
            {
                Status = VoyageStatusEnum.InProgress
            }).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateLandingFailedAsync(TestDatabase testDb, Vessel vessel, Voyage? voyage, string title, string failureReason)
        {
            Mission mission = new Mission(title)
            {
                Status = MissionStatusEnum.LandingFailed,
                VesselId = vessel.Id,
                VoyageId = voyage?.Id,
                FailureReason = failureReason,
                CommitHash = Guid.NewGuid().ToString("N") + "00000000"
            };
            return await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task<MergeEntry> CreateFailedMergeAsync(TestDatabase testDb, Vessel vessel, StubGitService git, bool? onTarget)
        {
            Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Merged " + Guid.NewGuid().ToString("N").Substring(0, 6))
            {
                Status = MissionStatusEnum.Failed,
                VesselId = vessel.Id,
                FailureReason = "merge failed",
                CommitHash = Guid.NewGuid().ToString("N") + "00000000"
            }).ConfigureAwait(false);
            git.AncestorResults[mission.CommitHash + "|" + TargetRef] = onTarget;
            MergeEntry entry = new MergeEntry("armada/captain/" + mission.Id, "main")
            {
                MissionId = mission.Id,
                VesselId = vessel.Id,
                Status = MergeStatusEnum.Failed
            };
            return await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);
        }

        private static AuthContext OperatorCaller(string? principalDisplay, string? credentialId, string userId)
        {
            return AuthContext.Authenticated(Constants.DefaultTenantId, userId, true, true, "Test", credentialId, principalDisplay);
        }

        private static async Task<MergeEntry> CreateMissionlessFailedMergeAsync(TestDatabase testDb, Vessel vessel, StubGitService git, string commit, bool? onTarget)
        {
            MergeEntry entry = new MergeEntry("feature/example-" + Guid.NewGuid().ToString("N").Substring(0, 6), "main")
            {
                VesselId = vessel.Id,
                Status = MergeStatusEnum.Failed
            };
            git.AncestorResults[commit + "|" + TargetRef] = onTarget;
            git.RevisionCommitShas[vessel.LocalPath + "|refs/heads/" + entry.BranchName] = commit;
            return await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);
        }

        private static async Task<(Mission Mission, MergeEntry Entry)> CreateCancelledMissionMergeAsync(
            TestDatabase testDb, Vessel vessel, StubGitService git, string? missionCommit, MissionStatusEnum status)
        {
            Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Cancelled " + Guid.NewGuid().ToString("N").Substring(0, 6))
            {
                Status = status,
                VesselId = vessel.Id,
                CommitHash = missionCommit
            }).ConfigureAwait(false);
            MergeEntry entry = new MergeEntry("armada/captain/" + mission.Id, "main")
            {
                MissionId = mission.Id,
                VesselId = vessel.Id,
                Status = MergeStatusEnum.Failed
            };
            git.RevisionCommitShas[vessel.LocalPath + "|refs/heads/" + entry.BranchName] = null;
            return (mission, await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false));
        }

        private static async Task<List<ArmadaEvent>> ProposalsAsync(TestDatabase testDb, Mission mission)
        {
            List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id, 100).ConfigureAwait(false);
            return events.Where(item => item.EventType == LandingReconcileProposals.ProposedEventType).ToList();
        }

        private static async Task CommitAsync(string repository, string file, string message)
        {
            await File.WriteAllTextAsync(Path.Combine(repository, file), message).ConfigureAwait(false);
            await RunGitAsync(repository, "add", file).ConfigureAwait(false);
            await RunGitAsync(repository, "commit", "-m", message).ConfigureAwait(false);
        }

        private static async Task<string> RunGitAsync(string repository, params string[] arguments)
        {
            ProcessStartInfo start = new ProcessStartInfo("git")
            {
                WorkingDirectory = repository,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using (Process process = Process.Start(start)!)
            {
                string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException("git " + String.Join(" ", arguments) + " failed: " + error);
                return output;
            }
        }

        /// <summary>
        /// Stand-in for a collaborator whose members the confirmation reads but does not need: every member returns the
        /// default value, so an unset hook reads as null.
        /// </summary>
        public class EmptyServiceProxy : DispatchProxy
        {
            /// <inheritdoc />
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                Type? returnType = targetMethod?.ReturnType;
                if (returnType == null || returnType == typeof(void)) return null;
                if (returnType == typeof(Task)) return Task.CompletedTask;
                return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
            }
        }
    }
}
