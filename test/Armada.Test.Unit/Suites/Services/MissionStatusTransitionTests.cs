namespace Armada.Test.Unit.Suites.Services
{
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
                        await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
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
        }
    }
}
