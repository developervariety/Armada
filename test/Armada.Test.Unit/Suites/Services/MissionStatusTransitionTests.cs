namespace Armada.Test.Unit.Suites.Services
{
    using System.Linq;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;
    using SyslogLogging;

    /// <summary>
    /// Tests for mission status transitions through the landing pipeline:
    /// InProgress -> WorkProduced -> Complete (success) or LandingFailed (failure).
    /// </summary>
    public class MissionStatusTransitionTests : TestSuite
    {
        public override string Name => "Mission Status Transitions";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        private async Task<TestEntitiesResult> CreateTestEntitiesAsync(
            SqliteDatabaseDriver db)
        {
            // Create a vessel (fleet is optional)
            Vessel vessel = new Vessel("test-vessel", "https://github.com/test/repo.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            // Create a captain
            Captain captain = new Captain("test-captain");
            captain.State = CaptainStateEnum.Working;
            await db.Captains.CreateAsync(captain).ConfigureAwait(false);

            // Create a dock
            Dock dock = new Dock(vessel.Id);
            dock.CaptainId = captain.Id;
            dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_test_wt_" + Guid.NewGuid().ToString("N"));
            dock.BranchName = "armada/test-captain/msn_test123";
            dock.Active = true;
            await db.Docks.CreateAsync(dock).ConfigureAwait(false);

            // Create a mission in InProgress state
            Mission mission = new Mission("Test mission");
            mission.Status = MissionStatusEnum.InProgress;
            mission.CaptainId = captain.Id;
            mission.DockId = dock.Id;
            mission.VesselId = vessel.Id;
            await db.Missions.CreateAsync(mission).ConfigureAwait(false);

            // Wire up captain
            captain.CurrentMissionId = mission.Id;
            captain.CurrentDockId = dock.Id;
            await db.Captains.UpdateAsync(captain).ConfigureAwait(false);

            return new TestEntitiesResult(captain, mission, dock);
        }

        protected override async Task RunTestsAsync()
        {
            // === MissionStatusEnum Value Tests ===

            await RunTest("All expected statuses defined", () =>
            {
                string[] expected = new[]
                {
                    "Pending", "Assigned", "InProgress", "WorkProduced", "PullRequestOpen",
                    "Testing", "Review", "Complete", "Failed", "LandingFailed", "Cancelled"
                };

                string[] actual = Enum.GetNames(typeof(MissionStatusEnum));
                AssertEqual(expected.Length, actual.Length, "Enum value count");

                foreach (string name in expected)
                {
                    Assert(Enum.TryParse<MissionStatusEnum>(name, out _), "Missing enum value: " + name);
                }
            });

            // === HandleCompletionAsync Tests (InProgress -> WorkProduced) ===

            // A skipped definition-of-done result carries Passed=true so that completion policy
            // accepts it. The activity log must still say the gate was skipped: "validation passed"
            // reads later as a build and test run that never happened.
            await RunTest("HandleCompletion records a skipped DoD gate as skipped, not passed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_dod_skip_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                        ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                        MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                        missionService.DefinitionOfDone = new DefinitionOfDoneGate(
                            new DefinitionOfDoneSettings { Enabled = false }, testDb.Driver, logging);

                        TestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver);
                        await missionService.HandleCompletionAsync(entities.Captain);

                        string logPath = Path.Combine(settings.LogDirectory, "missions", entities.Mission.Id + ".log");
                        AssertTrue(File.Exists(logPath), "Completion should write the mission activity log");
                        string activity = await File.ReadAllTextAsync(logPath);
                        AssertTrue(activity.Contains("validation started: definition-of-done gate", StringComparison.Ordinal),
                            "The gate should have been evaluated. Activity: " + activity);
                        AssertTrue(activity.Contains("validation skipped: DoD gate is disabled", StringComparison.Ordinal),
                            "A skipped gate must be reported as skipped. Activity: " + activity);
                        AssertFalse(activity.Contains("validation passed: definition-of-done gate", StringComparison.Ordinal),
                            "A skipped gate must not be reported as passed. Activity: " + activity);

                        Mission? updated = await testDb.Driver.Missions.ReadAsync(entities.Mission.Id);
                        AssertNotNull(updated, "Mission should exist after completion");
                        AssertEqual(MissionStatusEnum.WorkProduced, updated!.Status, "A skipped gate must not change completion policy");
                    }
                    finally
                    {
                        if (Directory.Exists(settings.LogDirectory)) Directory.Delete(settings.LogDirectory, true);
                    }
                }
            });

            await RunTest("HandleCompletion preserves the typed DoD failure class on the attempt fact", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_dod_failure_" + Guid.NewGuid().ToString("N"));
                    string? worktreePath = null;
                    try
                    {
                        IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                        ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                        MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git,
                            resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                        missionService.DefinitionOfDone = new DefinitionOfDoneGate(
                            new DefinitionOfDoneSettings
                            {
                                Enabled = true,
                                AppliedPersonas = new List<string> { "Worker" },
                                RunRestoreBeforeBuild = false,
                                RerunFailingClassesOnce = false
                            },
                            testDb.Driver, logging);

                        TestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver);
                        worktreePath = entities.Dock.WorktreePath!;
                        Directory.CreateDirectory(worktreePath);
                        Mission mission = entities.Mission;
                        mission.Persona = "Worker";
                        mission.DiffSnapshot = "diff --git a/src/Example.cs b/src/Example.cs\n+class Example {}";
                        await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
                        await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                        {
                            Name = "DoD Failure Profile",
                            Scope = WorkflowProfileScopeEnum.Vessel,
                            VesselId = mission.VesselId,
                            BuildCommand = OperatingSystem.IsWindows()
                                ? "echo Parser.cs(12,5): error CS0103: identifier x not found& exit 1"
                                : "echo 'Parser.cs(12,5): error CS0103: identifier x not found'; exit 1",
                            UnitTestCommand = "echo ok",
                            IsDefault = true,
                            Active = true
                        }).ConfigureAwait(false);

                        await missionService.HandleCompletionAsync(entities.Captain).ConfigureAwait(false);

                        Mission? failed = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertNotNull(failed, "Mission exists after its DoD result");
                        AssertEqual(MissionStatusEnum.Failed, failed!.Status, "A failed build still fails the mission");
                        ProductionFactPage<MissionAttemptFact> facts = await testDb.Driver.MissionAttemptFacts.EnumerateAsync(
                            new ProductionFactQuery { FromUtc = DateTime.UtcNow.AddMinutes(-1), ToUtc = DateTime.UtcNow.AddMinutes(1) }).ConfigureAwait(false);
                        MissionAttemptFact? failedFact = facts.Items.FirstOrDefault(item => item.MissionId == mission.Id
                            && item.FactType == MissionAttemptFactTypeEnum.Failed);
                        AssertNotNull(failedFact, "The DoD failure emits an attempt fact");
                        AssertEqual("dod_compile", failedFact!.ReasonCode,
                            "The structured DoD failure class is preserved without reading its diagnostic text");
                    }
                    finally
                    {
                        if (worktreePath != null && Directory.Exists(worktreePath)) Directory.Delete(worktreePath, true);
                        if (Directory.Exists(settings.LogDirectory)) Directory.Delete(settings.LogDirectory, true);
                    }
                }
            });

            // A gate runs the vessel's full build and test command under the host-wide slot. A mission cancelled
            // while its gate runs must stop that command and free the slot promptly, not hold it for the whole run.
            await RunTest("Cancelling a mission stops its running DoD gate, frees the host slot and records the cancel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_dod_cancel_" + Guid.NewGuid().ToString("N"));
                    Task? completion = null;
                    string? worktreePath = null;
                    try
                    {
                        IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                        ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                        MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                        missionService.DefinitionOfDone = new DefinitionOfDoneGate(
                            new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false }, testDb.Driver, logging);

                        TestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver);
                        worktreePath = entities.Dock.WorktreePath!;
                        Directory.CreateDirectory(worktreePath);
                        Mission mission = entities.Mission;
                        mission.Persona = "Worker";
                        await testDb.Driver.Missions.UpdateAsync(mission);

                        // The build command outlasts the bound below, so only a stopped gate finishes inside it.
                        string longBuild = OperatingSystem.IsWindows() ? "ping -n 21 127.0.0.1 >nul" : "sleep 20";
                        WorkflowProfile profile = await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                        {
                            Name = "Cancel Gate Profile",
                            Scope = WorkflowProfileScopeEnum.Vessel,
                            VesselId = mission.VesselId,
                            BuildCommand = longBuild,
                            UnitTestCommand = "echo ok",
                            IsDefault = true,
                            Active = true
                        });

                        completion = missionService.HandleCompletionAsync(entities.Captain);

                        // The gate leases the dock for its whole run, so a held lease means the gate is running.
                        DateTime startDeadline = DateTime.UtcNow.AddSeconds(15);
                        while (DateTime.UtcNow < startDeadline && !completion.IsCompleted && !DockLeaseRegistry.IsHeld(entities.Dock.Id))
                        {
                            await Task.Delay(25);
                        }

                        AssertTrue(DockLeaseRegistry.IsHeld(entities.Dock.Id), "The gate must be running before the cancel");
                        await Task.Delay(300);

                        Mission produced = (await testDb.Driver.Missions.ReadAsync(mission.Id))!;
                        AssertEqual(MissionStatusEnum.WorkProduced, produced.Status, "The gate runs after WorkProduced is written");
                        MissionCancellationResult cancel = await MissionCancellation.CancelAsync(
                            testDb.Driver, produced, MissionCancellation.OperatorCancelReason, null);
                        AssertTrue(cancel.Succeeded, "A produced mission can be cancelled");

                        Task finished = await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(10)));
                        AssertTrue(finished == completion, "Cancelling the mission must stop its gate well before the build command ends");
                        await completion;

                        using (CancellationTokenSource slotWait = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                        {
                            using (IDisposable slot = await HostWideCommandLock.AcquireAsync(slotWait.Token))
                            {
                                AssertNotNull(slot, "The host-wide command slot is free once the gate stops");
                            }
                        }

                        Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id);
                        AssertEqual(MissionStatusEnum.Cancelled, stored!.Status, "The completion handler must not overwrite the cancel");

                        string logPath = Path.Combine(settings.LogDirectory, "missions", mission.Id + ".log");
                        string activity = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : String.Empty;
                        AssertContains("validation cancelled: definition-of-done gate", activity,
                            "The activity log records that the gate was cancelled");
                        AssertFalse(activity.Contains("validation passed: definition-of-done gate", StringComparison.Ordinal),
                            "A cancelled gate is not reported as passed. Activity: " + activity);
                        AssertFalse(activity.Contains("validation failed", StringComparison.Ordinal),
                            "A cancelled gate is not reported as failed. Activity: " + activity);

                        EnumerationResult<ArmadaEvent> evaluations = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery
                        {
                            MissionId = mission.Id,
                            EventType = "mission.definition_of_done_evaluated",
                            PageNumber = 1,
                            PageSize = 10
                        });
                        AssertEqual(1, evaluations.Objects.Count, "One evaluation is recorded for the cancelled gate");
                        AssertContains("\"Outcome\":\"Cancelled\"", evaluations.Objects[0].Payload ?? "",
                            "The recorded evaluation names the cancel");

                        // A voyage can end after its captain exits, while the produced mission's gate
                        // waits behind another suite. Keep that outcome, but never run obsolete work.
                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Queued gate voyage")
                        {
                            Status = VoyageStatusEnum.InProgress
                        });
                        produced = await testDb.Driver.Missions.CreateAsync(new Mission("Produced work waiting for its gate", "Run the configured verification.")
                        {
                            Status = MissionStatusEnum.WorkProduced,
                            VoyageId = voyage.Id,
                            VesselId = mission.VesselId,
                            DockId = entities.Dock.Id,
                            Persona = "Worker"
                        });
                        profile.BuildCommand = "echo ran > voyage-gate-started.txt";
                        await testDb.Driver.WorkflowProfiles.UpdateAsync(profile);
                        string commandMarker = Path.Combine(worktreePath, "voyage-gate-started.txt");
                        using DefinitionOfDoneGateRun unrelated = DefinitionOfDoneGateRuns.Start(
                            "unrelated-" + Guid.NewGuid().ToString("N"), CancellationToken.None);
                        using CancellationTokenSource heldWait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        using IDisposable held = await HostWideCommandLock.AcquireAloneAsync(
                            "voyage-cancel-fixture", "Hold the host before cancellation", heldWait.Token);
                        Task<DefinitionOfDoneResult>? queued = null;
                        try
                        {
                            queued = missionService.DefinitionOfDone.EvaluateAsync(produced, entities.Dock);
                            AssertTrue(DefinitionOfDoneGateRuns.IsRunning(produced.Id), "The queued evaluation is registered");
                            DateTime queueDeadline = DateTime.UtcNow.AddSeconds(5);
                            while (!HostWideCommandLock.Snapshot().Waiters.Any(waiter => waiter.Key == produced.Id)
                                && !queued.IsCompleted && DateTime.UtcNow < queueDeadline)
                                await Task.Delay(10);
                            AssertTrue(HostWideCommandLock.Snapshot().Waiters.Any(waiter => waiter.Key == produced.Id),
                                "The real gate must reach the host queue before voyage cancellation");
                            await VoyageCancellation.CancelVoyageAsync(testDb.Driver, voyage, "operator cancel");
                            AssertFalse(unrelated.Token.IsCancellationRequested, "Another mission's gate keeps running");
                            AssertEqual(MissionStatusEnum.WorkProduced,
                                (await testDb.Driver.Missions.ReadAsync(produced.Id))!.Status,
                                "Voyage cancellation preserves produced work for recovery");

                            foreach (bool lateRegistration in new[] { false, true })
                            {
                                if (lateRegistration)
                                    queued = missionService.DefinitionOfDone.EvaluateAsync(produced, entities.Dock);
                                Task ended = await Task.WhenAny(queued, Task.Delay(TimeSpan.FromSeconds(3)));
                                AssertTrue(ended == queued, lateRegistration
                                    ? "A gate registered after voyage cancellation must stop before waiting"
                                    : "Voyage cancellation must remove the queued gate before the held slot is released");
                                bool cancelled = false;
                                try { await queued; }
                                catch (DefinitionOfDoneGateCancelledException) { cancelled = true; }
                                AssertTrue(cancelled, "Obsolete work has a cancellation outcome, never pass or fail");
                                AssertFalse(File.Exists(commandMarker), "The queued command must never start");
                                AssertFalse(DefinitionOfDoneGateRuns.IsRunning(produced.Id), "The cancelled evaluation leaves the registry");
                                AssertFalse(HostWideCommandLock.Snapshot().Waiters.Any(waiter => waiter.Key == produced.Id),
                                    "The cancelled evaluation leaves the host queue while its holder remains");
                            }
                        }
                        finally
                        {
                            DefinitionOfDoneGateRuns.Cancel(produced.Id);
                            if (queued != null)
                                await Task.WhenAny(queued, Task.Delay(TimeSpan.FromSeconds(5)));
                        }

                    }
                    finally
                    {
                        if (completion != null)
                        {
                            await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(30)));
                        }

                        if (worktreePath != null && Directory.Exists(worktreePath)) Directory.Delete(worktreePath, true);
                        if (Directory.Exists(settings.LogDirectory)) Directory.Delete(settings.LogDirectory, true);
                    }
                }
            });

            // Regression: a mission retried after a failed attempt kept the earlier attempt's
            // FailureReason forever. The requeue paths leave that text in place on purpose, so a
            // Pending mission shows why it is being retried -- but nothing cleared it when a later
            // attempt succeeded. Observed on msn_msfamlyg: Status WorkProduced, a real CommitHash,
            // RecoveryAttempts 1, and FailureReason still reading "403 Daily spend limit reached"
            // from the attempt that was superseded. Anyone reading FailureReason without checking
            // Status concludes a mission that worked had failed.
            await RunTest("HandleCompletion clears a superseded FailureReason from an earlier attempt", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    TestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver);
                    Captain captain = entities.Captain;
                    Mission mission = entities.Mission;

                    mission.FailureReason =
                        "Failed to authenticate. API Error: 403 Daily spend limit of $2000.00 reached for this user.";
                    await testDb.Driver.Missions.UpdateAsync(mission);

                    await missionService.HandleCompletionAsync(captain);

                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id);
                    AssertNotNull(updated, "Mission should exist after completion");
                    AssertEqual(MissionStatusEnum.WorkProduced, updated!.Status, "Status should be WorkProduced");
                    AssertNull(updated.FailureReason,
                        "A mission that reached WorkProduced must not still report why an earlier attempt failed");
                }
            });

            await RunTest("HandleCompletion holds a host-fault Timeout once, keeps the dock, and does not cancel the next stage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    await using HostFaultScene scene = await HostFaultScene.CreateAsync(testDb.Driver, HangCommand()).ConfigureAwait(false);
                    try
                    {
                        await scene.Service.HandleCompletionAsync(scene.Captain).ConfigureAwait(false);

                        Mission held = (await testDb.Driver.Missions.ReadAsync(scene.Mission.Id).ConfigureAwait(false))!;
                        AssertEqual(MissionStatusEnum.WorkProduced, held.Status, "A host-fault Timeout must not fail the mission on the first attempt");
                        AssertTrue(held.HeldForOperatorReview, "The first host-fault Timeout holds the produced work for a gate-only re-run");
                        AssertStartsWith("definition_of_done_host_fault:", held.HeldForOperatorReviewReason ?? "",
                            "The hold names the host-fault Timeout");
                        AssertNull(held.FailureReason, "The first host-fault Timeout records no mission failure");

                        Dock dock = (await testDb.Driver.Docks.ReadAsync(scene.Dock.Id).ConfigureAwait(false))!;
                        AssertTrue(dock.Active, "The dock stays until the re-run ends");

                        Mission dependent = (await testDb.Driver.Missions.ReadAsync(scene.Dependent.Id).ConfigureAwait(false))!;
                        AssertEqual(MissionStatusEnum.Pending, dependent.Status, "The next stage is held, not cancelled");

                        Captain captain = (await testDb.Driver.Captains.ReadAsync(scene.Captain.Id).ConfigureAwait(false))!;
                        AssertEqual(CaptainStateEnum.Idle, captain.State, "The captain is released, not restarted");
                        AssertNull(captain.CurrentMissionId, "No new mission is assigned to the captain");

                        EnumerationResult<ArmadaEvent> evaluations = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery
                        {
                            MissionId = scene.Mission.Id,
                            EventType = DefinitionOfDoneEvaluationRecord.EventType,
                            PageNumber = 1,
                            PageSize = 10
                        }).ConfigureAwait(false);
                        AssertTrue(evaluations.Objects.Count >= 1, "The first attempt is recorded");
                        AssertContains("\"FailureClass\":\"Timeout\"", evaluations.Objects[0].Payload ?? "",
                            "The recorded evaluation names the Timeout");

                        EnumerationResult<ArmadaEvent> holds = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery
                        {
                            MissionId = scene.Mission.Id,
                            EventType = "mission.definition_of_done_host_fault_hold",
                            PageNumber = 1,
                            PageSize = 10
                        }).ConfigureAwait(false);
                        AssertEqual(1, holds.Objects.Count, "The hold records its reason and load reading");
                    }
                    finally
                    {
                        await scene.CleanupAsync().ConfigureAwait(false);
                    }
                }
            });

            await RunTest("A host-fault hold that passes on the gate re-run continues the pipeline", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    await using HostFaultScene scene = await HostFaultScene.CreateAsync(testDb.Driver, HangCommand()).ConfigureAwait(false);
                    try
                    {
                        await scene.Service.HandleCompletionAsync(scene.Captain).ConfigureAwait(false);
                        Mission held = (await testDb.Driver.Missions.ReadAsync(scene.Mission.Id).ConfigureAwait(false))!;
                        AssertStartsWith("definition_of_done_host_fault:", held.HeldForOperatorReviewReason ?? "", "The first host-fault holds for a re-run");

                        scene.Profile.BuildCommand = "exit 0";
                        scene.Profile.UnitTestCommand = "exit 0";
                        await testDb.Driver.WorkflowProfiles.UpdateAsync(scene.Profile).ConfigureAwait(false);

                        DefinitionOfDoneEvaluationOutcomeEnum? outcome = await scene.Service.RerunInterruptedDefinitionOfDoneGateAsync(
                            scene.Mission.Id, CancellationToken.None).ConfigureAwait(false);
                        AssertEqual(DefinitionOfDoneEvaluationOutcomeEnum.Passed, outcome, "The re-run passes");

                        Mission released = (await testDb.Driver.Missions.ReadAsync(scene.Mission.Id).ConfigureAwait(false))!;
                        AssertFalse(released.HeldForOperatorReview, "A passing re-run releases the hold");
                        Mission dependent = (await testDb.Driver.Missions.ReadAsync(scene.Dependent.Id).ConfigureAwait(false))!;
                        AssertContains(MissionService.BuildHandoffMarker(scene.Mission.Id), dependent.Description ?? "",
                            "The released work is handed to the next stage");
                    }
                    finally
                    {
                        await scene.CleanupAsync().ConfigureAwait(false);
                    }
                }
            });

            await RunTest("A second host-fault Timeout on the gate re-run fails the mission and cancels the next stage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    await using HostFaultScene scene = await HostFaultScene.CreateAsync(testDb.Driver, HangCommand()).ConfigureAwait(false);
                    try
                    {
                        await scene.Service.HandleCompletionAsync(scene.Captain).ConfigureAwait(false);
                        Mission held = (await testDb.Driver.Missions.ReadAsync(scene.Mission.Id).ConfigureAwait(false))!;
                        AssertStartsWith("definition_of_done_host_fault:", held.HeldForOperatorReviewReason ?? "", "The first host-fault holds for a re-run");

                        DefinitionOfDoneEvaluationOutcomeEnum? outcome = await scene.Service.RerunInterruptedDefinitionOfDoneGateAsync(
                            scene.Mission.Id, CancellationToken.None).ConfigureAwait(false);
                        AssertEqual(DefinitionOfDoneEvaluationOutcomeEnum.Failed, outcome, "The second host-fault fails");

                        Mission failed = (await testDb.Driver.Missions.ReadAsync(scene.Mission.Id).ConfigureAwait(false))!;
                        AssertEqual(MissionStatusEnum.Failed, failed.Status, "A second host-fault takes the existing failure path");
                        AssertFalse(failed.HeldForOperatorReview, "The failed mission is no longer held");
                        AssertStartsWith("DoD gate failed", failed.FailureReason ?? "", "The failure carries the gate's reason");

                        Mission dependent = (await testDb.Driver.Missions.ReadAsync(scene.Dependent.Id).ConfigureAwait(false))!;
                        AssertEqual(MissionStatusEnum.Cancelled, dependent.Status, "The next stage is cancelled only after the re-run fails");
                    }
                    finally
                    {
                        await scene.CleanupAsync().ConfigureAwait(false);
                    }
                }
            });

            await RunTest("HandleCompletion fails a HostLoadTimeout immediately because the gate already used its re-run", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DateTime now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    HostLoadMonitor overloaded = new HostLoadMonitor(
                        () => 100.0,
                        () => 1,
                        clock: () => now,
                        delay: (interval, _) =>
                        {
                            now = now.Add(interval);
                            return Task.CompletedTask;
                        },
                        longLoadReader: () => 100.0);
                    await using HostFaultScene scene = await HostFaultScene.CreateAsync(testDb.Driver, HangCommand(), overloaded).ConfigureAwait(false);
                    try
                    {
                        await scene.Service.HandleCompletionAsync(scene.Captain).ConfigureAwait(false);

                        Mission failed = (await testDb.Driver.Missions.ReadAsync(scene.Mission.Id).ConfigureAwait(false))!;
                        AssertEqual(MissionStatusEnum.Failed, failed.Status, "A host-load time limit the gate already re-ran fails at once");
                        AssertFalse(failed.HeldForOperatorReview, "The inline re-run consumed the one retry");
                        AssertStartsWith("DoD gate failed", failed.FailureReason ?? "", "The failure carries the gate's reason");

                        Mission dependent = (await testDb.Driver.Missions.ReadAsync(scene.Dependent.Id).ConfigureAwait(false))!;
                        AssertEqual(MissionStatusEnum.Cancelled, dependent.Status, "The next stage is cancelled with the failed mission");
                    }
                    finally
                    {
                        await scene.CleanupAsync().ConfigureAwait(false);
                    }
                }
            });
        }

        private static string HangCommand()
        {
            return OperatingSystem.IsWindows()
                ? "echo run >> gate-runs.log & ping -n 999 127.0.0.1 >nul"
                : "echo run >> gate-runs.log; sleep 999";
        }

        /// <summary>
        /// A gate whose command timeout is shorter than the production floor, so a host-fault Timeout
        /// can be proven without waiting out 30 seconds. Unknown host load is the default, so a loaded
        /// test host never sends the timeout into the gate's own load wait.
        /// </summary>
        private sealed class FastTimeoutGate : DefinitionOfDoneGate
        {
            private readonly TimeSpan _Timeout;

            public FastTimeoutGate(
                DefinitionOfDoneSettings settings,
                DatabaseDriver database,
                LoggingModule logging,
                TimeSpan timeout,
                HostLoadMonitor? hostLoad = null)
                : base(settings, database, logging, null, null, null, hostLoad ?? new HostLoadMonitor(() => null))
            {
                _Timeout = timeout;
            }

            protected override TimeSpan ResolveCommandTimeout(int configuredSeconds)
            {
                return _Timeout;
            }
        }

        private sealed class HostFaultScene : IAsyncDisposable
        {
            public MissionService Service = null!;
            public Captain Captain = null!;
            public Mission Mission = null!;
            public Mission Dependent = null!;
            public Dock Dock = null!;
            public WorkflowProfile Profile = null!;
            public ArmadaSettings Settings = null!;

            public static async Task<HostFaultScene> CreateAsync(
                SqliteDatabaseDriver db,
                string buildCommand,
                HostLoadMonitor? hostLoad = null)
            {
                HostFaultScene scene = new HostFaultScene();
                StubGitService git = new StubGitService();
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                scene.Settings = new ArmadaSettings();
                scene.Settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                scene.Settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                scene.Settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_host_fault_" + Guid.NewGuid().ToString("N"));

                IDockService dockService = new DockService(logging, db, scene.Settings, git);
                ICaptainService captainService = new CaptainService(logging, db, scene.Settings, git, dockService);
                scene.Service = new MissionService(logging, db, scene.Settings, dockService, captainService, git: git,
                    resourcePressureAdmission: TestResourcePressure.Unconstrained(scene.Settings));
                scene.Service.DefinitionOfDone = new FastTimeoutGate(
                    new DefinitionOfDoneSettings
                    {
                        Enabled = true,
                        AppliedPersonas = new List<string> { "Worker" },
                        RunRestoreBeforeBuild = false,
                        RerunFailingClassesOnce = false
                    },
                    db,
                    logging,
                    TimeSpan.FromSeconds(1),
                    hostLoad);

                Vessel vessel = new Vessel("host-fault-vessel", "https://github.com/test/repo.git");
                vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
                vessel.DefaultBranch = "main";
                await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                Captain captain = new Captain("host-fault-captain");
                captain.State = CaptainStateEnum.Working;
                captain = await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                Dock dock = new Dock(vessel.Id);
                dock.CaptainId = captain.Id;
                dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_test_wt_" + Guid.NewGuid().ToString("N"));
                dock.BranchName = "armada/host-fault-captain/msn_hostfault";
                dock.Active = true;
                dock = await db.Docks.CreateAsync(dock).ConfigureAwait(false);
                Directory.CreateDirectory(dock.WorktreePath!);

                Voyage voyage = await db.Voyages.CreateAsync(new Voyage("host-fault voyage") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);

                Mission mission = new Mission("Host-fault worker");
                mission.Status = MissionStatusEnum.InProgress;
                mission.CaptainId = captain.Id;
                mission.DockId = dock.Id;
                mission.VesselId = vessel.Id;
                mission.VoyageId = voyage.Id;
                mission.Persona = "Worker";
                mission.DiffSnapshot = "diff --git a/src/Example.cs b/src/Example.cs\n+class Example {}";
                mission = await db.Missions.CreateAsync(mission).ConfigureAwait(false);

                captain.CurrentMissionId = mission.Id;
                captain.CurrentDockId = dock.Id;
                await db.Captains.UpdateAsync(captain).ConfigureAwait(false);

                Mission dependent = new Mission("Next stage", "Original next-stage brief.");
                dependent.Status = MissionStatusEnum.Pending;
                dependent.VoyageId = voyage.Id;
                dependent.VesselId = vessel.Id;
                dependent.DependsOnMissionId = mission.Id;
                dependent.Persona = "TestEngineer";
                dependent.AssignmentState = MissionAssignmentStateEnum.WaitingForDependency;
                dependent = await db.Missions.CreateAsync(dependent).ConfigureAwait(false);

                scene.Profile = await db.WorkflowProfiles.CreateAsync(new WorkflowProfile
                {
                    Name = "Host Fault Profile",
                    Scope = WorkflowProfileScopeEnum.Vessel,
                    VesselId = vessel.Id,
                    BuildCommand = buildCommand,
                    UnitTestCommand = "echo ok",
                    IsDefault = true,
                    Active = true
                }).ConfigureAwait(false);

                scene.Captain = captain;
                scene.Mission = mission;
                scene.Dependent = dependent;
                scene.Dock = dock;
                return scene;
            }

            public Task CleanupAsync()
            {
                if (Dock.WorktreePath != null && Directory.Exists(Dock.WorktreePath)) Directory.Delete(Dock.WorktreePath, true);
                if (Settings.LogDirectory != null && Directory.Exists(Settings.LogDirectory)) Directory.Delete(Settings.LogDirectory, true);
                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync()
            {
                await CleanupAsync().ConfigureAwait(false);
            }
        }
    }
}
