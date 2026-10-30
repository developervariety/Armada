namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for the eligibility and targeting of Checks armed at dispatch.
    /// </summary>
    /// <remarks>
    /// A voyage-armed Check exists to gate the voyage: a Judge PASS is rejected without a green
    /// independent Check. Waiting for the voyage to COMPLETE before running it is therefore a
    /// condition the record itself prevents, so these tests pin the earlier trigger -- work
    /// committed to a branch -- and pin that the record is pointed at that branch before it runs.
    /// </remarks>
    public class ArmedCheckEligibilityTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Armed Check Eligibility";

        private static AutomaticCheckRunOrchestrator BuildOrchestrator(
            TestDatabase testDb,
            DispatchHold? dispatchHold = null,
            Func<Armada.Core.Settings.DefinitionOfDoneSettings>? gateSettings = null,
            Func<DateTime>? clock = null)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
            VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
            CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);
            ReleaseService releases = new ReleaseService(testDb.Driver, workflowProfiles, logging);
            IncidentService incidents = new IncidentService(testDb.Driver);

            return new AutomaticCheckRunOrchestrator(testDb.Driver, checkRuns, releases, incidents, logging, dispatchHold, gateSettings, clock);
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb)
        {
            Vessel vessel = new Vessel("armed-check-vessel", "https://github.com/test/repo.git");
            vessel.DefaultBranch = "main";
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<CheckRun> ArmCheckAsync(TestDatabase testDb, Vessel vessel, Voyage voyage)
        {
            CheckRun run = new CheckRun
            {
                VesselId = vessel.Id,
                VoyageId = voyage.Id,
                Type = CheckRunTypeEnum.Build,
                Source = CheckRunSourceEnum.Armada,
                Status = CheckRunStatusEnum.Pending,
                Label = "Build (armed at dispatch)"
            };

            return await testDb.Driver.CheckRuns.CreateAsync(run).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateWorkMissionAsync(
            TestDatabase testDb, Vessel vessel, Voyage voyage, MissionStatusEnum status, string? branch, string? commit)
        {
            Mission mission = new Mission("[Worker] Do the work", "Do the work");
            mission.VesselId = vessel.Id;
            mission.VoyageId = voyage.Id;
            mission.Persona = "Worker";
            mission.Status = status;
            mission.BranchName = branch;
            mission.CommitHash = commit;
            return await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateStageMissionAsync(
            TestDatabase testDb, Vessel vessel, Voyage voyage, string persona, MissionStatusEnum status, string? branch, string? commit)
        {
            Mission mission = new Mission("[" + persona + "] Stage of the work", "Stage of the work");
            mission.VesselId = vessel.Id;
            mission.VoyageId = voyage.Id;
            mission.Persona = persona;
            mission.Status = status;
            mission.BranchName = branch;
            mission.CommitHash = commit;
            return await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task<(Mission Worker, Mission Judge, Mission Recorder)> CreateHeldReviewChainAsync(
            TestDatabase testDb, Vessel vessel, Voyage voyage)
        {
            Mission worker = await CreateStageMissionAsync(testDb, vessel, voyage, PersonaCatalog.Worker,
                MissionStatusEnum.WorkProduced, "armada/worker/msn-chain-one", "worker111").ConfigureAwait(false);
            Mission judge = await CreateDependentStageAsync(testDb, vessel, voyage, PersonaCatalog.Judge, worker).ConfigureAwait(false);
            judge.Status = MissionStatusEnum.WorkProduced;
            judge.HeldForOperatorReview = true;
            judge.HeldForOperatorReviewReason = JudgeRevisionCheckWaitHold.BuildReason(DateTime.UtcNow, worker.CommitHash, null);
            judge = await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);
            Mission recorder = await CreateDependentStageAsync(testDb, vessel, voyage, PersonaCatalog.Recorder, judge).ConfigureAwait(false);
            return (worker, judge, recorder);
        }

        private static async Task<Mission> CreateDependentStageAsync(
            TestDatabase testDb, Vessel vessel, Voyage voyage, string persona, Mission dependency)
        {
            Mission mission = new Mission("[" + persona + "] Stage of the work", "Stage of the work");
            mission.VesselId = vessel.Id;
            mission.VoyageId = voyage.Id;
            mission.Persona = persona;
            mission.Status = MissionStatusEnum.Pending;
            mission.DependsOnMissionId = dependency.Id;
            return await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        /// <summary>Run all armed-check eligibility tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("A pending armed check of an already cancelled voyage is discarded, not left Pending", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = new Voyage("ended-voyage");
                    voyage.Status = VoyageStatusEnum.Cancelled;
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    bool eligible = await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false);

                    CheckRun? after = await testDb.Driver.CheckRuns.ReadAsync(armed.Id).ConfigureAwait(false);
                    AssertFalse(eligible, "a check of an ended voyage never runs");
                    AssertEqual(CheckRunStatusEnum.Canceled, after!.Status, "a check of an ended voyage must not stay Pending and count as required");
                    AssertContains(VoyageCheckDiscard.VoyageCancelledReason, after.Summary ?? String.Empty, "the discard names its reason");
                    AssertContains(VoyageCheckDiscard.VoyageCancelledReason, after.Output ?? String.Empty, "the Output a reader opens names the reason too");
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByEntityAsync("check_run", armed.Id).ConfigureAwait(false);
                    ArmadaEvent? discardEvent = events.Find(evt => evt.EventType == VoyageCheckDiscard.DiscardedEventType);
                    AssertNotNull(discardEvent, "the discard records an event");
                    AssertContains(VoyageCheckDiscard.VoyageCancelledReason, discardEvent!.Message ?? String.Empty, "the event names the reason");
                }
            }).ConfigureAwait(false);

            await RunTest("An operator retry of a failed voyage's Check runs to completion and is not discarded while it waits for the host slot", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string workingDirectory = CreateWorkingDirectory();
                    IDisposable? hostSlot = null;
                    Task<CheckRun>? retry = null;
                    try
                    {
                        Vessel vessel = await CreateLiveDirectoryVesselAsync(testDb, workingDirectory).ConfigureAwait(false);
                        Voyage voyage = new Voyage("failed-voyage");
                        voyage.Status = VoyageStatusEnum.Failed;
                        voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                        CheckRun prior = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                        {
                            VesselId = vessel.Id,
                            VoyageId = voyage.Id,
                            Type = CheckRunTypeEnum.Build,
                            Source = CheckRunSourceEnum.Armada,
                            Status = CheckRunStatusEnum.Failed,
                            Command = "echo " + _DefaultBranchMarker,
                            WorkingDirectory = workingDirectory,
                            BranchName = "feature/operator-proof",
                            CommitHash = "abc123",
                            ExitCode = 1,
                            CompletedUtc = DateTime.UtcNow,
                            Label = "Build (armed at dispatch)"
                        }).ConfigureAwait(false);

                        LoggingModule logging = new LoggingModule();
                        logging.Settings.EnableConsole = false;
                        WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                        CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, new VesselReadinessService(testDb.Driver, workflowProfiles, logging), logging);

                        hostSlot = await HostWideCommandLock.AcquireAsync().ConfigureAwait(false);
                        retry = Task.Run(() => checkRuns.RetryAsync(BuildSystemAuth(), prior.Id));
                        CheckRun? waiting = null;
                        bool queued = await WaitUntilAsync(async () =>
                        {
                            EnumerationResult<CheckRun> pending = await testDb.Driver.CheckRuns.EnumerateAsync(new CheckRunQuery
                            {
                                VoyageId = voyage.Id,
                                Status = CheckRunStatusEnum.Pending,
                                PageNumber = 1,
                                PageSize = 10
                            }).ConfigureAwait(false);
                            waiting = pending.Objects.Find(run => run.SlotRequestedUtc.HasValue);
                            return waiting != null;
                        }).ConfigureAwait(false);
                        AssertTrue(queued, "the retry must reach the host slot queue");

                        // The sweep meets the retry as a Pending Check of an ended voyage.
                        AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                        AssertFalse(await orchestrator.IsEligibleAsync(waiting!, default).ConfigureAwait(false), "the sweep does not execute an operator's run a second time");
                        AssertEqual(0, await VoyageCheckDiscard.DiscardPendingAsync(testDb.Driver, voyage.Id, VoyageCheckDiscard.VoyageFailedReason).ConfigureAwait(false),
                            "ending the voyage again does not discard an operator's run");
                        CheckRun? afterSweep = await testDb.Driver.CheckRuns.ReadAsync(waiting!.Id).ConfigureAwait(false);
                        AssertEqual(CheckRunStatusEnum.Pending, afterSweep!.Status, "an operator's retry of an ended voyage's Check is not discarded");

                        hostSlot.Dispose();
                        hostSlot = null;
                        Task finished = await Task.WhenAny(retry, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
                        AssertTrue(ReferenceEquals(finished, retry), "the retry finishes once the host slot is free");
                        CheckRun result = await retry.ConfigureAwait(false);
                        AssertEqual(waiting.Id, result.Id, "the retry returns the record it created");
                        AssertEqual(CheckRunStatusEnum.Passed, result.Status, "the operator's retry runs to completion");
                        AssertFalse(CheckRunService.IsOperatorRun(result.Id), "the operator mark ends with the execution");
                    }
                    finally
                    {
                        hostSlot?.Dispose();
                        if (retry != null) await Task.WhenAny(retry, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
                        DeleteDirectory(workingDirectory);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Armed check is eligible once a stage commits work to a branch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("armed-voyage")).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                    await CreateWorkMissionAsync(
                        testDb, vessel, voyage, MissionStatusEnum.WorkProduced, "armada/worker/msn-1", "abc123").ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    bool eligible = await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false);

                    AssertTrue(eligible, "Work committed to a branch must make the armed check runnable before the voyage completes");
                }
            });

            await RunTest("An armed Build check waits while a definition-of-done gate of its voyage runs on the same vessel, and names the gate", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("gate-overlap-voyage")).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);
                    Mission work = await CreateWorkMissionAsync(
                        testDb, vessel, voyage, MissionStatusEnum.WorkProduced, "armada/worker/msn-overlap", "abc123").ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    AssertTrue(await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false), "with no gate running the armed check is eligible");

                    try
                    {
                        using (DefinitionOfDoneGateRun gate = DefinitionOfDoneGateRuns.Start(work.Id, CancellationToken.None))
                        {
                            AssertFalse(await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false),
                                "the check must not run the same suite beside the voyage's running gate");

                            await orchestrator.RunSweepAsync().ConfigureAwait(false);
                            List<ArmadaEvent> started = await testDb.Driver.Events.EnumerateByTypeAsync(AutomaticCheckRunOrchestrator.GateOverlapStartedEvent, 10).ConfigureAwait(false);
                            AssertEqual(1, started.Count, "the wait behind the gate is recorded");
                            AssertContains(armed.Id + " behind the gate of mission " + work.Id, started[0].Message, "the event names the check and the gate");

                            CheckRun? waiting = await testDb.Driver.CheckRuns.ReadAsync(armed.Id).ConfigureAwait(false);
                            AssertEqual(CheckRunStatusEnum.Pending, waiting!.Status, "the check stays pending while the gate runs");
                            CheckRunSlotWait? view = CheckRunSlotWait.Describe(waiting, new HostSlotSnapshot());
                            AssertContains("definition-of-done gate of mission " + work.Id, view!.QueueReason ?? String.Empty, "the check's view names the gate it waits for");
                        }

                        AssertTrue(await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false), "the check is eligible again once the gate ends");
                    }
                    finally
                    {
                        AutomaticCheckRunPlaces.PublishGateWaits(null);
                    }
                }
            });

            await RunTest("Armed check is not eligible while no stage has produced work", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("armed-voyage")).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                    await CreateWorkMissionAsync(
                        testDb, vessel, voyage, MissionStatusEnum.InProgress, null, null).ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    bool eligible = await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false);

                    AssertFalse(eligible, "A check with nothing committed to measure must not run");
                }
            });

            await RunTest("Armed check waits while a later stage can still commit on top of the work", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("staged-voyage")).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                    await CreateStageMissionAsync(testDb, vessel, voyage, PersonaCatalog.Worker,
                        MissionStatusEnum.WorkProduced, "armada/worker/msn-staged", "worker111").ConfigureAwait(false);
                    await CreateStageMissionAsync(testDb, vessel, voyage, PersonaCatalog.TestEngineer,
                        MissionStatusEnum.Pending, null, null).ConfigureAwait(false);
                    await CreateStageMissionAsync(testDb, vessel, voyage, PersonaCatalog.Judge,
                        MissionStatusEnum.Pending, null, null).ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    bool eligible = await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false);

                    AssertFalse(eligible, "a pending stage that can still commit would replace the measured commit, so the check must wait");
                }
            }).ConfigureAwait(false);

            await RunTest("Armed check runs while a later route chain waits on the held Judge", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("two-chain-voyage")).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);
                    (Mission worker, Mission judge, Mission recorder) = await CreateHeldReviewChainAsync(testDb, vessel, voyage).ConfigureAwait(false);
                    Mission laterWorker = await CreateDependentStageAsync(testDb, vessel, voyage, PersonaCatalog.Worker, recorder).ConfigureAwait(false);
                    await CreateDependentStageAsync(testDb, vessel, voyage, PersonaCatalog.TestEngineer, laterWorker).ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    AssertTrue(await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false),
                        "a chain that cannot start before the held Judge ends cannot commit before it, so the Judge's Checks run");
                    CheckRun stamped = await orchestrator.StampWorkUnderReviewAsync(armed, default).ConfigureAwait(false);
                    AssertEqual(worker.CommitHash, stamped.CommitHash, "the Check measures the commit the Judge reviewed");

                    judge.HeldForOperatorReview = false;
                    judge.HeldForOperatorReviewReason = null;
                    judge.Status = MissionStatusEnum.Complete;
                    await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);
                    AssertFalse(await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false),
                        "once the Judge releases its dependents the later chain can commit, so the check waits for it");
                }
            }).ConfigureAwait(false);

            await RunTest("Armed check measures a pending regenerated Judge candidate before its later chain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("candidate-review-voyage")).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);
                    string oldCommit = new string('a', 40);
                    string candidateCommit = new string('c', 40);
                    Mission worker = await CreateWorkMissionAsync(testDb, vessel, voyage, MissionStatusEnum.WorkProduced,
                        "candidate-review", oldCommit).ConfigureAwait(false);
                    Mission judge = await CreateStageMissionAsync(testDb, vessel, voyage, PersonaCatalog.Judge,
                        MissionStatusEnum.WorkProduced, worker.BranchName, oldCommit).ConfigureAwait(false);
                    judge.DependsOnMissionId = worker.Id;
                    await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);
                    Mission recorder = await CreateDependentStageAsync(testDb, vessel, voyage, PersonaCatalog.Recorder, judge).ConfigureAwait(false);
                    await CreateDependentStageAsync(testDb, vessel, voyage, PersonaCatalog.Worker, recorder).ConfigureAwait(false);
                    judge = (await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                    GeneratedArtifactReviewBinding binding = GeneratedArtifactReviewRule.NextBinding(judge,
                        new GeneratedArtifactLandingResult
                        { Outcome = "ReviewRequired", SourceCommit = oldCommit, TargetBaseCommit = new string('b', 40), CandidateCommit = candidateCommit },
                        2, DateTime.UtcNow);
                    AssertTrue(await testDb.Driver.Missions.TryWriteGeneratedArtifactReviewAsync(judge, binding, true).ConfigureAwait(false));
                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    foreach (MissionStatusEnum status in new[] { MissionStatusEnum.Pending, MissionStatusEnum.Assigned, MissionStatusEnum.InProgress, MissionStatusEnum.WorkProduced })
                    {
                        Mission current = (await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                        current.Status = status;
                        await testDb.Driver.Missions.UpdateAsync(current).ConfigureAwait(false);
                        AssertTrue(await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false),
                            "The later chain cannot commit before the candidate verdict.");
                        CheckRun stamped = await orchestrator.StampWorkUnderReviewAsync(armed, default).ConfigureAwait(false);
                        AssertEqual(candidateCommit, stamped.CommitHash, "The old source commit cannot satisfy candidate review.");
                        AssertEqual(worker.BranchName, stamped.BranchName);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Armed check still waits for a sibling stage that does not depend on the Judge", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("two-chain-voyage")).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);
                    (Mission worker, Mission judge, Mission recorder) = await CreateHeldReviewChainAsync(testDb, vessel, voyage).ConfigureAwait(false);
                    await CreateDependentStageAsync(testDb, vessel, voyage, PersonaCatalog.Worker, recorder).ConfigureAwait(false);
                    Mission sibling = await CreateDependentStageAsync(testDb, vessel, voyage, PersonaCatalog.Worker, worker).ConfigureAwait(false);
                    judge.StageOrder = 2;
                    sibling.StageOrder = 2;
                    await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);
                    await testDb.Driver.Missions.UpdateAsync(sibling).ConfigureAwait(false);
                    List<Mission> missions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertFalse(VoyageLanes.IsFanOut(VoyageLanes.Resolve(missions)),
                        "a stage in the Judge's parallel group keeps the voyage one lane, so the record measures its work");

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    AssertFalse(await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false),
                        "a stage beside the Judge can commit before the Judge ends, so the check waits for it");

                    try
                    {
                        await orchestrator.RunSweepAsync().ConfigureAwait(false);
                        CheckRun? waiting = await testDb.Driver.CheckRuns.ReadAsync(armed.Id).ConfigureAwait(false);
                        AssertEqual(CheckRunStatusEnum.Pending, waiting!.Status, "the check stays pending while the stage can still commit");
                        CheckRunSlotWait? view = CheckRunSlotWait.Describe(waiting, new HostSlotSnapshot());
                        AssertContains(sibling.Id, view!.QueueReason ?? String.Empty, "the check's view names the stage it waits for");
                        AssertFalse((view.Guidance ?? String.Empty).Contains("runs by itself", StringComparison.Ordinal),
                            "the guidance does not promise a start the check may never reach");
                    }
                    finally
                    {
                        AutomaticCheckRunPlaces.PublishWorkWaits(null);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Armed check waits while a running stage reports Testing, and runs once that stage has completed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("progress-status-voyage")).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                    await CreateStageMissionAsync(testDb, vessel, voyage, PersonaCatalog.Linter,
                        MissionStatusEnum.WorkProduced, "armada/linter/msn-progress", "linter111").ConfigureAwait(false);
                    Mission fixer = await CreateStageMissionAsync(testDb, vessel, voyage, PersonaCatalog.Worker,
                        MissionStatusEnum.Testing, null, null).ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    AssertFalse(await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false),
                        "a captain that reports Testing is still running and can still commit, so the check must wait");

                    fixer.Status = MissionStatusEnum.Review;
                    await testDb.Driver.Missions.UpdateAsync(fixer).ConfigureAwait(false);
                    AssertFalse(await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false),
                        "a captain that reports Review without a completion time is still running, so the check must wait");

                    fixer.BranchName = "armada/worker/msn-progress";
                    fixer.CommitHash = "worker222";
                    fixer.CompletedUtc = DateTime.UtcNow;
                    await testDb.Driver.Missions.UpdateAsync(fixer).ConfigureAwait(false);
                    AssertTrue(await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false),
                        "a stage in Review with a completion time has finished, so its commit is final and the check runs");
                }
            }).ConfigureAwait(false);

            await RunTest("Armed check becomes eligible when only the Judge stage remains", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("staged-voyage")).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                    await CreateStageMissionAsync(testDb, vessel, voyage, PersonaCatalog.Worker,
                        MissionStatusEnum.WorkProduced, "armada/worker/msn-staged", "worker111").ConfigureAwait(false);
                    Mission testStage = await CreateStageMissionAsync(testDb, vessel, voyage, PersonaCatalog.TestEngineer,
                        MissionStatusEnum.InProgress, null, null).ConfigureAwait(false);
                    await CreateStageMissionAsync(testDb, vessel, voyage, PersonaCatalog.Judge,
                        MissionStatusEnum.Pending, null, null).ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    AssertFalse(await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false),
                        "the check waits while the test stage is still running");

                    testStage.Status = MissionStatusEnum.WorkProduced;
                    testStage.BranchName = "armada/test-engineer/msn-staged";
                    testStage.CommitHash = "tests222";
                    await testDb.Driver.Missions.UpdateAsync(testStage).ConfigureAwait(false);

                    AssertTrue(await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false),
                        "with only the Judge left the check must run, or the Judge's hold on a missing green never ends");

                    CheckRun stamped = await orchestrator.StampWorkUnderReviewAsync(armed, default).ConfigureAwait(false);
                    AssertEqual("tests222", stamped.CommitHash, "the check measures the last committed stage, the commit the Judge reviews");
                }
            }).ConfigureAwait(false);

            // Each lane of a split voyage is final on its own: a lane whose stages are done is
            // reviewed by its own Judge while another lane is still working, and that Judge needs a
            // verdict on its lane's commit, not one deferred until every lane is finished.
            await RunTest("A lane's armed check runs while another lane is still working", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FanOutVoyageFixture fx = await FanOutVoyageFixture.CreateAsync(testDb, laneBWorkerStatus: MissionStatusEnum.InProgress).ConfigureAwait(false);
                    CheckRun laneA = await fx.AddVoyageCheckAsync(testDb, CheckRunTypeEnum.Build, CheckRunStatusEnum.Pending,
                        FanOutVoyageFixture.LaneABranch, FanOutVoyageFixture.LaneACommit).ConfigureAwait(false);
                    CheckRun laneB = await fx.AddVoyageCheckAsync(testDb, CheckRunTypeEnum.Build, CheckRunStatusEnum.Pending,
                        FanOutVoyageFixture.LaneBBranch, null).ConfigureAwait(false);
                    CheckRun shared = await fx.AddVoyageCheckAsync(testDb, CheckRunTypeEnum.UnitTest, CheckRunStatusEnum.Pending, null, null).ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    AssertTrue(await orchestrator.IsEligibleAsync(laneA, default).ConfigureAwait(false),
                        "lane A's work is final, so its check runs although lane B can still commit");
                    AssertFalse(await orchestrator.IsEligibleAsync(laneB, default).ConfigureAwait(false),
                        "lane B's Worker can still commit, so lane B's check waits");
                    AssertFalse(await orchestrator.IsEligibleAsync(shared, default).ConfigureAwait(false),
                        "an unstamped record shared by both lanes measures no lane, so it never runs");
                }
            }).ConfigureAwait(false);

            await RunTest("Armed check of a single committed and completed stage is eligible", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("single-stage-voyage")).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                    await CreateStageMissionAsync(testDb, vessel, voyage, PersonaCatalog.Worker,
                        MissionStatusEnum.Complete, "armada/worker/msn-single", "single333").ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    bool eligible = await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false);

                    AssertTrue(eligible, "a completed single stage is final work, so the check runs");
                }
            }).ConfigureAwait(false);

            await RunTest("A stage that cannot commit does not hold back an armed check", () =>
            {
                Mission judge = new Mission("[Judge] Review", "Review") { Persona = PersonaCatalog.Judge, Status = MissionStatusEnum.InProgress };
                Mission recorder = new Mission("[Recorder] Record", "Record") { Persona = PersonaCatalog.Recorder, Status = MissionStatusEnum.Pending };
                Mission research = new Mission("[Worker] Research", "Research") { Persona = PersonaCatalog.Worker, Status = MissionStatusEnum.Assigned, Mode = MissionModeEnum.Research };
                Mission linter = new Mission("[Linter] Lint", "Lint") { Persona = PersonaCatalog.Linter, Status = MissionStatusEnum.Assigned };
                Mission failedWorker = new Mission("[Worker] Failed", "Failed") { Persona = PersonaCatalog.Worker, Status = MissionStatusEnum.Failed };

                AssertFalse(StaleCheckSupersessionService.MayStillCommit(judge), "the Judge reviews and never commits");
                AssertFalse(StaleCheckSupersessionService.MayStillCommit(recorder), "a persona whose normal result is an empty diff never commits");
                AssertFalse(StaleCheckSupersessionService.MayStillCommit(research), "a read-only mission never commits");
                AssertFalse(StaleCheckSupersessionService.MayStillCommit(failedWorker), "an ended stage adds no further commit");
                AssertTrue(StaleCheckSupersessionService.MayStillCommit(linter), "an unfinished Linter can commit fixes");
            }).ConfigureAwait(false);

            await RunTest("Armed check on a cancelled voyage never becomes eligible", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = new Voyage("armed-voyage");
                    voyage.Status = VoyageStatusEnum.Cancelled;
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                    await CreateWorkMissionAsync(
                        testDb, vessel, voyage, MissionStatusEnum.WorkProduced, "armada/worker/msn-1", "abc123").ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    bool eligible = await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false);

                    AssertFalse(eligible, "A cancelled voyage has no gate left to feed");
                }
            });

            await RunTest("Armed check is pointed at the work branch before it runs", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("armed-voyage")).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                    await CreateWorkMissionAsync(
                        testDb, vessel, voyage, MissionStatusEnum.WorkProduced, "armada/worker/msn-1", "abc123").ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    CheckRun stamped = await orchestrator.StampWorkUnderReviewAsync(armed, default).ConfigureAwait(false);

                    AssertEqual("armada/worker/msn-1", stamped.BranchName, "An unstamped check would measure the default branch, not the work");
                    AssertEqual("abc123", stamped.CommitHash, "The commit under review must be recorded on the check");
                }
            });

            await RunTest("Sweep stamps the work branch onto an armed check before executing it", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // The vessel has no working directory, so execution fails immediately without
                    // running a command. Stamping happens BEFORE execution, so the persisted record
                    // still proves the sweep pointed the check at the work. Asserting the stamping
                    // METHOD alone would pass even if nothing ever called it.
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("armed-voyage")).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                    await CreateWorkMissionAsync(
                        testDb, vessel, voyage, MissionStatusEnum.WorkProduced, "armada/worker/msn-1", "abc123").ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    int executed = await orchestrator.RunSweepAsync(default).ConfigureAwait(false);

                    AssertEqual(1, executed, "The armed check should have been picked up by the sweep");

                    CheckRun? reloaded = await testDb.Driver.CheckRuns.ReadAsync(armed.Id).ConfigureAwait(false);
                    AssertNotNull(reloaded, "The armed check should remain readable");
                    AssertEqual("armada/worker/msn-1", reloaded!.BranchName, "The sweep must point the check at the work before running it");
                }
            });

            await RunTest("Sweep finishes eligible live-voyage checks under the dispatch hold and defers unrelated checks", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string workingDirectory = CreateWorkingDirectory();
                    try
                    {
                        Vessel voyageVessel = await CreateLiveDirectoryVesselAsync(testDb, workingDirectory).ConfigureAwait(false);
                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("held-voyage")).ConfigureAwait(false);
                        CheckRun armed = await ArmRunnableCheckAsync(testDb, voyageVessel, voyage).ConfigureAwait(false);
                        Mission producedWork = await CreateWorkMissionAsync(
                            testDb, voyageVessel, voyage, MissionStatusEnum.WorkProduced, "armada/worker/msn-held", "held123").ConfigureAwait(false);

                        Voyage finishingVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("finishing-held-voyage")).ConfigureAwait(false);
                        CheckRun finishingCheck = await ArmRunnableCheckAsync(testDb, voyageVessel, finishingVoyage).ConfigureAwait(false);
                        Mission finishingWork = await CreateWorkMissionAsync(
                            testDb, voyageVessel, finishingVoyage, MissionStatusEnum.InProgress, null, null).ConfigureAwait(false);

                        Vessel idleVessel = await CreateLiveDirectoryVesselAsync(testDb, workingDirectory, "armed-check-idle-vessel").ConfigureAwait(false);
                        CheckRun idleCheck = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                        {
                            VesselId = idleVessel.Id,
                            Type = CheckRunTypeEnum.Build,
                            Source = CheckRunSourceEnum.Armada,
                            Status = CheckRunStatusEnum.Pending,
                            Command = "echo " + _DefaultBranchMarker,
                            Label = "Build (idle-vessel check)"
                        }).ConfigureAwait(false);

                        DispatchHold hold = new DispatchHold();
                        AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb, hold);
                        AssertTrue(await orchestrator.IsEligibleAsync(idleCheck, default).ConfigureAwait(false),
                            "The idle-vessel check is otherwise eligible, so its deferral is due to the hold");
                        hold.Engage("Managed-vessel execution is paused.", "operator-session");

                        // A mission already running when the hold is engaged may finish and produce its
                        // branch during the hold. Its pre-armed voyage check must then be allowed to drain.
                        finishingWork.Status = MissionStatusEnum.WorkProduced;
                        finishingWork.BranchName = "armada/worker/msn-finishing-held";
                        finishingWork.CommitHash = "finishing123";
                        await testDb.Driver.Missions.UpdateAsync(finishingWork).ConfigureAwait(false);

                        AssertEqual(2, await orchestrator.RunSweepAsync(default).ConfigureAwait(false),
                            "Checks for committed work on existing voyages must run so those voyages can drain");

                        CheckRun? checkedVoyage = await testDb.Driver.CheckRuns.ReadAsync(armed.Id).ConfigureAwait(false);
                        AssertNotNull(checkedVoyage, "The voyage check should remain readable");
                        AssertEqual(CheckRunStatusEnum.Passed, checkedVoyage!.Status, "The eligible voyage check must finish under the hold");
                        AssertEqual(producedWork.BranchName, checkedVoyage.BranchName, "The check still measures the committed voyage work");
                        AssertEqual("held123", checkedVoyage.CommitHash, "The check remains pinned to that work's commit");

                        CheckRun? checkedFinishingVoyage = await testDb.Driver.CheckRuns.ReadAsync(finishingCheck.Id).ConfigureAwait(false);
                        AssertNotNull(checkedFinishingVoyage, "The check for work produced during the hold should remain readable");
                        AssertEqual(CheckRunStatusEnum.Passed, checkedFinishingVoyage!.Status, "The check for work produced during the hold must run");
                        AssertEqual(finishingWork.BranchName, checkedFinishingVoyage.BranchName, "The check is stamped at the new work branch");
                        AssertEqual("finishing123", checkedFinishingVoyage.CommitHash, "The check is stamped at the new work commit");

                        CheckRun? deferredIdle = await testDb.Driver.CheckRuns.ReadAsync(idleCheck.Id).ConfigureAwait(false);
                        AssertNotNull(deferredIdle, "The unrelated idle-vessel check should remain readable");
                        AssertEqual(CheckRunStatusEnum.Pending, deferredIdle!.Status, "The dispatch hold still defers unrelated vessel checks");
                        AssertNull(deferredIdle.StartedUtc, "An unrelated check must not start under the hold");

                        AssertEqual(0, await orchestrator.RunSweepAsync(default).ConfigureAwait(false), "A second sweep must not repeat a completed voyage check");

                        List<ArmadaEvent> deferred = await testDb.Driver.Events.EnumerateByTypeAsync(
                            AutomaticCheckRunOrchestrator.DeferredByDispatchHoldEvent, 50).ConfigureAwait(false);
                        AssertEqual(1, deferred.Count, "One event per hold engagement names why unrelated checks wait");
                        AssertContains("operator-session", deferred[0].Message ?? String.Empty, "The event names who holds dispatch");

                        hold.Clear();
                        AssertEqual(1, await orchestrator.RunSweepAsync(default).ConfigureAwait(false), "The unrelated check runs once the hold clears");
                    }
                    finally
                    {
                        DeleteDirectory(workingDirectory);
                    }
                }
            });

            await RunTest("A check armed while an earlier check waits for the host slot joins the slot queue without waiting for it", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string workingDirectory = CreateWorkingDirectory();
                    IDisposable? hostSlot = null;
                    try
                    {
                        Vessel vessel = await CreateLiveDirectoryVesselAsync(testDb, workingDirectory).ConfigureAwait(false);
                        Voyage voyage = new Voyage("landed-voyage");
                        voyage.Status = VoyageStatusEnum.Complete;
                        voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                        CheckRun first = await ArmRunnableCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                        // Another caller (a definition-of-done gate or a long suite) holds the host slot,
                        // so the first check waits in it for as long as the test decides.
                        hostSlot = await HostWideCommandLock.AcquireAsync().ConfigureAwait(false);
                        AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                        orchestrator.TriggerBackgroundSweep();
                        bool firstQueued = await WaitUntilAsync(async () =>
                            (await testDb.Driver.CheckRuns.ReadAsync(first.Id).ConfigureAwait(false))!.SlotRequestedUtc.HasValue).ConfigureAwait(false);
                        AssertTrue(firstQueued, "the first check must reach the host slot queue");

                        // Work moves on: a second check is armed while the first still waits.
                        CheckRun second = await ArmRunnableCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);
                        orchestrator.TriggerBackgroundSweep();
                        bool secondQueued = await WaitUntilAsync(async () =>
                            (await testDb.Driver.CheckRuns.ReadAsync(second.Id).ConfigureAwait(false))!.SlotRequestedUtc.HasValue).ConfigureAwait(false);

                        CheckRun? firstWhileHeld = await testDb.Driver.CheckRuns.ReadAsync(first.Id).ConfigureAwait(false);
                        AssertTrue(secondQueued, "a newly armed check must be started by the next sweep, not wait until the earlier check finishes");
                        AssertEqual(CheckRunStatusEnum.Pending, firstWhileHeld!.Status, "the host slot still serializes commands: nothing runs while another caller holds it");

                        hostSlot.Dispose();
                        hostSlot = null;
                        bool bothDone = await WaitUntilAsync(async () =>
                        {
                            CheckRun? a = await testDb.Driver.CheckRuns.ReadAsync(first.Id).ConfigureAwait(false);
                            CheckRun? b = await testDb.Driver.CheckRuns.ReadAsync(second.Id).ConfigureAwait(false);
                            return a!.Status == CheckRunStatusEnum.Passed && b!.Status == CheckRunStatusEnum.Passed;
                        }).ConfigureAwait(false);
                        AssertTrue(bothDone, "both checks run once the host slot is free");
                    }
                    finally
                    {
                        hostSlot?.Dispose();
                        DeleteDirectory(workingDirectory);
                    }
                }
            });

            await RunTest("The check runner's in-flight line names every slot holder and how many slots are in use", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);

                    // A grant time ahead of the clock reads as held for 0 s however long the assertion takes.
                    DateTime grantedUtc = DateTime.UtcNow.AddMinutes(5);
                    HostSlotSnapshot single = new HostSlotSnapshot { HolderKey = "chk_one", HolderDescription = "definition-of-done build", HolderSinceUtc = grantedUtc };
                    single.Holders.Add(new HostSlotHolder { Key = "chk_one", Description = "definition-of-done build", SinceUtc = single.HolderSinceUtc });
                    AssertEqual("0 check(s) running, 0 waiting for the host build slot; slot held by definition-of-done build for 0 s",
                        orchestrator.DescribeInFlight(single), "with one slot the line names its holder and no slot count");

                    HostSlotSnapshot several = new HostSlotSnapshot { SlotCount = 3, HolderKey = "chk_a", HolderDescription = "definition-of-done build", HolderSinceUtc = grantedUtc };
                    several.Holders.Add(new HostSlotHolder { Key = "chk_a", Description = "definition-of-done build", SinceUtc = several.HolderSinceUtc });
                    several.Holders.Add(new HostSlotHolder { Key = "chk_b", Description = "merge-queue test", SinceUtc = grantedUtc });
                    AssertEqual("0 check(s) running, 0 waiting for the host build slot; slot held by definition-of-done build for 0 s, merge-queue test for 0 s (2 of 3 slots in use)",
                        orchestrator.DescribeInFlight(several), "with several slots the line names every holder and the slots in use");

                    HostSlotSnapshot memoryHeld = new HostSlotSnapshot
                    {
                        MemoryFloorMb = 6144,
                        AvailableMemoryMb = 4100,
                        HeldForMemory = true,
                        MemoryWaitReason = "waiting for free memory: 4,100 MB available, floor 6,144 MB"
                    };
                    memoryHeld.Waiters.Add(new HostSlotWaiter { Position = 1, Key = "chk_waiting", Description = "check" });
                    AssertEqual("0 check(s) running, 0 waiting for the host build slot; 1 other request(s) queued for the slot; next request waiting for free memory: 4,100 MB available, floor 6,144 MB",
                        orchestrator.DescribeInFlight(memoryHeld), "a free slot held back for memory names the memory reason");
                }
            });

            await RunTest("A sweep records an event when the host command slot holds its next request for free memory", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    HostWideCommandLock.SetMemoryReader(() => 1000);
                    HostWideCommandLock.SetMemoryFloorMb(6144);
                    Task<IDisposable> held = HostWideCommandLock.AcquireAsync("chk_memory_held", "check chk_memory_held (Build) for a vessel", cancel.Token);

                    try
                    {
                        AssertTrue(HostWideCommandLock.Snapshot().HeldForMemory, "the request is held for memory before the sweep");

                        await orchestrator.RunSweepAsync().ConfigureAwait(false);

                        List<ArmadaEvent> started = await testDb.Driver.Events.EnumerateByTypeAsync("check.memory_wait_started", 10).ConfigureAwait(false);
                        AssertEqual(1, started.Count, "a memory hold seen by the sweep is recorded, so the waiting queue does not read as idle");
                        AssertContains("1,000 MB available, floor 6,144 MB", started[0].Message, "the event names the memory reading and the floor");
                        AssertContains("chk_memory_held", started[0].Message, "the event names the waiting request");
                    }
                    finally
                    {
                        cancel.Cancel();
                        try { (await held.ConfigureAwait(false)).Dispose(); } catch (OperationCanceledException) { }
                        HostWideCommandLock.SetMemoryFloorMb(0);
                        HostWideCommandLock.SetMemoryReader(() => null);
                    }
                }
            });

            await RunTest("A memory hold is reported at each interval and records its end", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DateTime now = new DateTime(2026, 1, 5, 6, 0, 0, DateTimeKind.Utc);
                    Armada.Core.Settings.DefinitionOfDoneSettings settings = new Armada.Core.Settings.DefinitionOfDoneSettings
                    {
                        HostSlotWaitReportMinutes = 5
                    };
                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(
                        testDb,
                        gateSettings: () => settings,
                        clock: () => now);

                    DateTime since = now;
                    HostSlotSnapshot held = new HostSlotSnapshot
                    {
                        MemoryFloorMb = 6144,
                        AvailableMemoryMb = 4100,
                        HeldForMemory = true,
                        MemoryHeldSinceUtc = since,
                        MemoryWaitReason = "waiting for free memory: 4,100 MB available, floor 6,144 MB"
                    };
                    held.Waiters.Add(new HostSlotWaiter { Position = 1, Key = "msn_gate", Description = "definition-of-done gate for mission msn_gate" });
                    held.Waiters.Add(new HostSlotWaiter { Position = 2, Key = "chk_waiting", Description = "check chk_waiting (Build) for a vessel" });

                    async Task<int> CountAsync(string eventType) =>
                        (await testDb.Driver.Events.EnumerateByTypeAsync(eventType, 50).ConfigureAwait(false)).Count;

                    await orchestrator.ReportWaitsAsync(held, CancellationToken.None).ConfigureAwait(false);
                    now = since.AddMinutes(2);
                    await orchestrator.ReportWaitsAsync(held, CancellationToken.None).ConfigureAwait(false);
                    AssertEqual(1, await CountAsync(AutomaticCheckRunOrchestrator.MemoryWaitStartedEvent).ConfigureAwait(false), "the hold is recorded once when it starts");
                    AssertEqual(0, await CountAsync(AutomaticCheckRunOrchestrator.MemoryWaitContinuingEvent).ConfigureAwait(false), "no repeat inside the report interval");

                    now = since.AddMinutes(5);
                    await orchestrator.ReportWaitsAsync(held, CancellationToken.None).ConfigureAwait(false);
                    AssertEqual(1, await CountAsync(AutomaticCheckRunOrchestrator.MemoryWaitContinuingEvent).ConfigureAwait(false), "the hold is reported again after the interval");

                    now = since.AddMinutes(12);
                    await orchestrator.ReportWaitsAsync(new HostSlotSnapshot(), CancellationToken.None).ConfigureAwait(false);
                    List<ArmadaEvent> ended = await testDb.Driver.Events.EnumerateByTypeAsync(AutomaticCheckRunOrchestrator.MemoryWaitEndedEvent, 10).ConfigureAwait(false);
                    AssertEqual(1, ended.Count, "the end of the hold is recorded");
                    AssertContains("lasted 12 min", ended[0].Message, "the end names how long the hold lasted");
                }
            });

            await RunTest("Every busy host command slot with requests waiting is reported with its holders and the waiting order", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DateTime now = new DateTime(2026, 1, 5, 7, 0, 0, DateTimeKind.Utc);
                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(
                        testDb,
                        clock: () => now);

                    DateTime since = now;
                    HostSlotSnapshot busy = new HostSlotSnapshot { SlotCount = 1, HolderKey = "msn_gate", HolderDescription = "definition-of-done gate for mission msn_gate", HolderSinceUtc = since.AddMinutes(-3) };
                    busy.Holders.Add(new HostSlotHolder { Key = "msn_gate", Description = "definition-of-done gate for mission msn_gate", SinceUtc = since.AddMinutes(-3) });
                    busy.Waiters.Add(new HostSlotWaiter { Position = 1, Key = "chk_first", Description = "check chk_first (Build) for a vessel" });
                    busy.Waiters.Add(new HostSlotWaiter { Position = 2, Key = "chk_second", Description = "check chk_second (UnitTest) for a vessel" });

                    await orchestrator.ReportWaitsAsync(busy, CancellationToken.None).ConfigureAwait(false);
                    List<ArmadaEvent> started = await testDb.Driver.Events.EnumerateByTypeAsync(AutomaticCheckRunOrchestrator.SlotBusyStartedEvent, 10).ConfigureAwait(false);
                    AssertEqual(1, started.Count, "a busy slot with waiters is recorded when it is first seen");
                    AssertContains("1 slot(s) held by definition-of-done gate for mission msn_gate since 2026-01-05 06:57:00Z", started[0].Message, "the event names the holder and since when it holds the slot");
                    AssertContains("1. check chk_first (Build) for a vessel; 2. check chk_second (UnitTest) for a vessel", started[0].Message, "the event names every waiting request in order");
                    AssertEqual(0, (await testDb.Driver.Events.EnumerateByTypeAsync(AutomaticCheckRunOrchestrator.MemoryWaitStartedEvent, 10).ConfigureAwait(false)).Count,
                        "a busy slot is not reported as a memory hold");

                    now = since.AddMinutes(11);
                    HostSlotSnapshot free = new HostSlotSnapshot { SlotCount = 2, HolderKey = "msn_gate", HolderDescription = "definition-of-done gate for mission msn_gate", HolderSinceUtc = since };
                    free.Holders.Add(new HostSlotHolder { Key = "msn_gate", Description = "definition-of-done gate for mission msn_gate", SinceUtc = since });
                    await orchestrator.ReportWaitsAsync(free, CancellationToken.None).ConfigureAwait(false);
                    AssertEqual(1, (await testDb.Driver.Events.EnumerateByTypeAsync(AutomaticCheckRunOrchestrator.SlotBusyEndedEvent, 10).ConfigureAwait(false)).Count,
                        "a free slot ends the busy wait");
                }
            });

            await RunTest("Sweep finds an eligible check behind two hundred ineligible checks", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("starved-armed-voyage")).ConfigureAwait(false);
                    CheckRun armed = new CheckRun
                    {
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        Type = CheckRunTypeEnum.Build,
                        Source = CheckRunSourceEnum.Armada,
                        Status = CheckRunStatusEnum.Pending,
                        Label = "Old eligible check",
                        CreatedUtc = DateTime.UtcNow.AddHours(-4)
                    };
                    armed = await testDb.Driver.CheckRuns.CreateAsync(armed).ConfigureAwait(false);

                    await CreateWorkMissionAsync(
                        testDb, vessel, voyage, MissionStatusEnum.WorkProduced, "armada/worker/msn-old", "old123").ConfigureAwait(false);

                    DateTime newerBaseUtc = DateTime.UtcNow.AddHours(-3);
                    for (int index = 0; index < 200; index++)
                    {
                        CheckRun ineligible = new CheckRun
                        {
                            VesselId = vessel.Id,
                            DeploymentId = "dpl_not-ready-" + index,
                            Type = CheckRunTypeEnum.Build,
                            Source = CheckRunSourceEnum.Armada,
                            Status = CheckRunStatusEnum.Pending,
                            Label = "Newer ineligible check " + index,
                            CreatedUtc = newerBaseUtc.AddMilliseconds(index)
                        };
                        await testDb.Driver.CheckRuns.CreateAsync(ineligible).ConfigureAwait(false);
                    }

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    int executed = await orchestrator.RunSweepAsync(default).ConfigureAwait(false);

                    AssertEqual(1, executed, "An older eligible check must not be hidden by a full page of newer ineligible checks");
                    CheckRun? reloaded = await testDb.Driver.CheckRuns.ReadAsync(armed.Id).ConfigureAwait(false);
                    AssertNotNull(reloaded, "The older eligible check should remain readable");
                    AssertEqual("armada/worker/msn-old", reloaded!.BranchName, "The sweep must execute and stamp the older eligible check");
                }
            });

            await RunTest("Armed check on a completed voyage keeps measuring the default branch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = new Voyage("armed-voyage");
                    voyage.Status = VoyageStatusEnum.Complete;
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                    await CreateWorkMissionAsync(
                        testDb, vessel, voyage, MissionStatusEnum.Complete, "armada/worker/msn-1", "abc123").ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    CheckRun stamped = await orchestrator.StampWorkUnderReviewAsync(armed, default).ConfigureAwait(false);

                    AssertNull(stamped.BranchName, "Work on a completed voyage is on the default branch, which is the correct subject");
                }
            });

            await RunTest("An armed check whose voyage fails before stamping is cancelled, not run against the default branch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string workingDirectory = CreateWorkingDirectory();
                    try
                    {
                        Vessel vessel = await CreateLiveDirectoryVesselAsync(testDb, workingDirectory).ConfigureAwait(false);
                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("window-voyage")).ConfigureAwait(false);
                        CheckRun armed = await ArmRunnableCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);
                        Mission work = await CreateWorkMissionAsync(
                            testDb, vessel, voyage, MissionStatusEnum.WorkProduced, "armada/worker/msn-1", "abc123").ConfigureAwait(false);

                        AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                        AssertTrue(await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false), "the record is eligible while the voyage has work");

                        // The voyage fails between the eligibility read and the stamping read.
                        voyage.Status = VoyageStatusEnum.Failed;
                        await testDb.Driver.Voyages.UpdateAsync(voyage).ConfigureAwait(false);
                        work.Status = MissionStatusEnum.Failed;
                        await testDb.Driver.Missions.UpdateAsync(work).ConfigureAwait(false);

                        CheckRun result = await orchestrator.ExecutePendingAsync(BuildSystemAuth(), armed, default).ConfigureAwait(false);

                        AssertEqual(CheckRunStatusEnum.Canceled, result.Status, "an unstamped record of an ended voyage must not execute");
                        AssertFalse((result.Output ?? String.Empty).Contains(_DefaultBranchMarker), "the default branch must not be measured");
                        AssertContains("no branch or commit", result.Summary ?? String.Empty, "the cancel names the missing stamp");
                    }
                    finally
                    {
                        DeleteDirectory(workingDirectory);
                    }
                }
            });

            await RunTest("An unstamped armed check of a live voyage whose work disappeared is left Pending", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string workingDirectory = CreateWorkingDirectory();
                    try
                    {
                        Vessel vessel = await CreateLiveDirectoryVesselAsync(testDb, workingDirectory).ConfigureAwait(false);
                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("live-voyage")).ConfigureAwait(false);
                        CheckRun armed = await ArmRunnableCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);
                        Mission work = await CreateWorkMissionAsync(
                            testDb, vessel, voyage, MissionStatusEnum.WorkProduced, "armada/worker/msn-1", "abc123").ConfigureAwait(false);

                        AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                        AssertTrue(await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false), "the record is eligible while the voyage has work");

                        work.Status = MissionStatusEnum.Failed;
                        await testDb.Driver.Missions.UpdateAsync(work).ConfigureAwait(false);

                        CheckRun result = await orchestrator.ExecutePendingAsync(BuildSystemAuth(), armed, default).ConfigureAwait(false);

                        AssertEqual(CheckRunStatusEnum.Pending, result.Status, "a live voyage's unstamped record waits for a stage to commit");
                        AssertNull(result.StartedUtc, "the waiting record must not be started");
                        AssertFalse((result.Output ?? String.Empty).Contains(_DefaultBranchMarker), "the default branch must not be measured");
                    }
                    finally
                    {
                        DeleteDirectory(workingDirectory);
                    }
                }
            });

            await RunTest("An unstamped armed check of a completed voyage still executes", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string workingDirectory = CreateWorkingDirectory();
                    try
                    {
                        Vessel vessel = await CreateLiveDirectoryVesselAsync(testDb, workingDirectory).ConfigureAwait(false);
                        Voyage voyage = new Voyage("complete-voyage");
                        voyage.Status = VoyageStatusEnum.Complete;
                        voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                        CheckRun armed = await ArmRunnableCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                        AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                        CheckRun result = await orchestrator.ExecutePendingAsync(BuildSystemAuth(), armed, default).ConfigureAwait(false);

                        AssertEqual(CheckRunStatusEnum.Passed, result.Status, "a completed voyage's work is on the default branch");
                        AssertContains(_DefaultBranchMarker, result.Output ?? String.Empty, "the command must have run");
                    }
                    finally
                    {
                        DeleteDirectory(workingDirectory);
                    }
                }
            });
        }

        private const string _DefaultBranchMarker = "measured-live-directory";

        /// <summary>
        /// Poll a condition until it holds or the deadline passes. The deadline only bounds a failing
        /// run; a passing run returns as soon as the condition holds.
        /// </summary>
        private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (await condition().ConfigureAwait(false)) return true;
                await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
            }

            return await condition().ConfigureAwait(false);
        }

        private static AuthContext BuildSystemAuth()
        {
            return AuthContext.Authenticated(
                Armada.Core.Constants.DefaultTenantId,
                Armada.Core.Constants.DefaultUserId,
                isAdmin: true,
                isTenantAdmin: true,
                authMethod: "System",
                principalDisplay: "Automated checks");
        }

        private static string CreateWorkingDirectory()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada-iso-armed-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  could not delete test directory " + path + ": " + ex.Message);
            }
        }

        private static async Task<Vessel> CreateLiveDirectoryVesselAsync(TestDatabase testDb, string workingDirectory, string name = "armed-check-live-vessel")
        {
            Vessel vessel = new Vessel
            {
                Name = name,
                RepoUrl = String.Empty,
                LocalPath = String.Empty,
                WorkingDirectory = workingDirectory,
                DefaultBranch = "main"
            };
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<CheckRun> ArmRunnableCheckAsync(TestDatabase testDb, Vessel vessel, Voyage voyage)
        {
            CheckRun run = new CheckRun
            {
                VesselId = vessel.Id,
                VoyageId = voyage.Id,
                Type = CheckRunTypeEnum.Build,
                Source = CheckRunSourceEnum.Armada,
                Status = CheckRunStatusEnum.Pending,
                Command = "echo " + _DefaultBranchMarker,
                Label = "Build (armed at dispatch)"
            };

            return await testDb.Driver.CheckRuns.CreateAsync(run).ConfigureAwait(false);
        }
    }
}
