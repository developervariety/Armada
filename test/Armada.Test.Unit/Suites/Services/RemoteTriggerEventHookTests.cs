namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Integration tests verifying that IRemoteTriggerService.FireDrainerAsync is called
    /// from MissionLandingHandler at the three qualifying event points (WorkProduced,
    /// MissionFailed, auto_land_skipped) and that FireCriticalAsync is called from
    /// McpAuditTools when verdict = Critical.
    /// Uses hand-rolled doubles; no mocking libraries.
    /// </summary>
    public class RemoteTriggerEventHookTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Remote Trigger Event Hooks";

        #region Doubles

        private sealed class RecordingRemoteTriggerService : IRemoteTriggerService
        {
            public List<(string vesselId, string text)> DrainerCalls { get; } = new List<(string, string)>();
            public List<string> CriticalCalls { get; } = new List<string>();

            public Task FireDrainerAsync(string vesselId, string text, CancellationToken token = default)
            {
                DrainerCalls.Add((vesselId, text));
                return Task.CompletedTask;
            }

            public Task FireCriticalAsync(string text, CancellationToken token = default)
            {
                CriticalCalls.Add(text);
                return Task.CompletedTask;
            }

            public AgentWakeSessionRegistration RegisterAgentWakeSession(AgentWakeSessionRegistration registration) => registration;
            public AgentWakeSessionRegistration? GetAgentWakeSession() => null;
        }

        private sealed class StubMergeQueueService : IMergeQueueService
        {
            public Task<MergeEntry> EnqueueAsync(MergeEntry entry, CancellationToken token = default) => Task.FromResult(entry);
            public Task ProcessQueueAsync(CancellationToken token = default) => Task.CompletedTask;
            public Task CancelAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.CompletedTask;
            public Task<List<MergeEntry>> ListAsync(string? tenantId = null, CancellationToken token = default) => Task.FromResult(new List<MergeEntry>());
            public Task<MergeEntry?> ProcessSingleAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult<MergeEntry?>(null);
            public Task ProcessEntryByIdAsync(string entryId, CancellationToken token = default) => Task.CompletedTask;
            public Task<MergeEntry?> GetAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult<MergeEntry?>(null);
            public Task<bool> DeleteAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult(false);
            public Task<MergeQueuePurgeResult> DeleteMultipleAsync(List<string> entryIds, string? tenantId = null, CancellationToken token = default)
                => Task.FromResult(new MergeQueuePurgeResult());
            public Task<int> PurgeTerminalAsync(string? vesselId = null, MergeStatusEnum? status = null, string? tenantId = null, CancellationToken token = default)
                => Task.FromResult(0);
            public Task<int> ReconcilePullRequestEntriesAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<bool> TryOpenPullRequestForRecoveryAsync(string mergeEntryId, CancellationToken token = default) => Task.FromResult(false);
        }

        private sealed class BenignConventionChecker : IConventionChecker
        {
            public ConventionCheckResult Check(string unifiedDiff)
                => new ConventionCheckResult { Passed = true };
        }

        private sealed class BenignCriticalTriggerEvaluator : ICriticalTriggerEvaluator
        {
            public CriticalTriggerResult Evaluate(string unifiedDiff, ConventionCheckResult conventionResult)
                => new CriticalTriggerResult { Fired = false };
        }

        #endregion

        #region Helpers

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_rt_test_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_rt_test_repos_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        private static async Task<(MissionLandingHandler handler, Mission mission, Dock dock, Vessel vessel)> CreateHandlerAsync(
            TestDatabase testDb,
            RecordingRemoteTriggerService recording,
            StubGitService git,
            LandingModeEnum? landingMode,
            List<string>? protectedPaths = null,
            string? autoLandPredicateJson = null,
            string? diffSnapshot = null)
        {
            LoggingModule logging = CreateLogging();
            ArmadaSettings settings = CreateSettings();

            Vessel vessel = new Vessel("test-vessel-" + Guid.NewGuid().ToString("N").Substring(0, 8), "https://github.com/test/repo.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_rt_bare_" + Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_rt_work_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            vessel.LandingMode = landingMode;
            vessel.ProtectedPaths = protectedPaths;
            vessel.AutoLandPredicate = autoLandPredicateJson;
            vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Captain captain = new Captain("test-captain-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            captain.State = CaptainStateEnum.Working;
            captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

            Dock dock = new Dock(vessel.Id);
            dock.CaptainId = captain.Id;
            dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_rt_wt_" + Guid.NewGuid().ToString("N"));
            dock.BranchName = "armada/test/msn_test_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            dock.Active = true;
            dock = await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

            Mission mission = new Mission("Test mission " + Guid.NewGuid().ToString("N").Substring(0, 8));
            mission.Status = MissionStatusEnum.WorkProduced;
            mission.CaptainId = captain.Id;
            mission.DockId = dock.Id;
            mission.VesselId = vessel.Id;
            mission.DiffSnapshot = diffSnapshot ?? string.Empty;
            mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

            IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
            IMessageTemplateService templateService = new MessageTemplateService(logging);

            MissionLandingHandler handler = new MissionLandingHandler(
                logging,
                testDb.Driver,
                settings,
                git,
                new StubMergeQueueService(),
                new AutoLandEvaluator(),
                new BenignConventionChecker(),
                new BenignCriticalTriggerEvaluator(),
                templateService,
                null,
                dockService,
                recording,
                null);

            return (handler, mission, dock, vessel);
        }

        #endregion

        /// <summary>Run all remote trigger event hook tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("MissionLandingHandler_WorkProduced_FiresDrainer", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RecordingRemoteTriggerService recording = new RecordingRemoteTriggerService();
                    StubGitService git = new StubGitService();

                    (MissionLandingHandler handler, Mission mission, Dock dock, Vessel vessel) =
                        await CreateHandlerAsync(testDb, recording, git, LandingModeEnum.MergeQueue).ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(recording.DrainerCalls.Count >= 1, "Expected at least one drainer fire for WorkProduced");
                    bool foundWorkProduced = false;
                    foreach ((string vid, string txt) in recording.DrainerCalls)
                    {
                        if (txt.Contains("WorkProduced") && vid == vessel.Id)
                        {
                            foundWorkProduced = true;
                            break;
                        }
                    }
                    AssertTrue(foundWorkProduced, "Expected drainer fire with 'WorkProduced' in text and matching vesselId");
                }
            });

            await RunTest("MissionLandingHandler_MissionFailed_FiresDrainer", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RecordingRemoteTriggerService recording = new RecordingRemoteTriggerService();
                    StubGitService git = new StubGitService();
                    List<string> protectedPaths = new List<string> { "CLAUDE.md" };
                    string diffSnapshot = "diff --git a/CLAUDE.md b/CLAUDE.md\n--- a/CLAUDE.md\n+++ b/CLAUDE.md\n+some change";

                    (MissionLandingHandler handler, Mission mission, Dock dock, Vessel vessel) =
                        await CreateHandlerAsync(testDb, recording, git, null, protectedPaths, null, diffSnapshot).ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                    AssertEqual(1, recording.DrainerCalls.Count, "Expected exactly one drainer fire for MissionFailed");
                    AssertTrue(recording.DrainerCalls[0].text.Contains("MissionFailed"), "Expected 'MissionFailed' in drainer fire text");
                    AssertEqual(vessel.Id, recording.DrainerCalls[0].vesselId, "Expected matching vesselId in drainer fire");
                }
            });

            await RunTest("MissionLandingHandler_AutoLandSkipped_FiresDrainer", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RecordingRemoteTriggerService recording = new RecordingRemoteTriggerService();
                    StubGitService git = new StubGitService();
                    // DiffResult returns a file matching DenyPaths so predicate evaluates to Fail
                    git.DiffResult = "+++ b/sensitive.denied\n+some change";
                    string autoLandJson = JsonSerializer.Serialize(new AutoLandPredicate
                    {
                        Enabled = true,
                        DenyPaths = new List<string> { "**/*.denied" }
                    });

                    (MissionLandingHandler handler, Mission mission, Dock dock, Vessel vessel) =
                        await CreateHandlerAsync(testDb, recording, git, LandingModeEnum.MergeQueue, null, autoLandJson).ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                    bool foundSkip = false;
                    foreach ((string vid, string txt) in recording.DrainerCalls)
                    {
                        if (txt.Contains("auto_land_skipped") && vid == vessel.Id)
                        {
                            foundSkip = true;
                            break;
                        }
                    }
                    AssertTrue(foundSkip, "Expected drainer fire with 'auto_land_skipped' in text");
                }
            });

            await RunTest("MissionLandingHandler_HeartbeatEvent_DoesNotFire", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RecordingRemoteTriggerService recording = new RecordingRemoteTriggerService();
                    StubGitService git = new StubGitService();

                    // No landing mode, no protected paths: code path goes to "no landing configured" branch
                    (MissionLandingHandler handler, Mission mission, Dock dock, Vessel vessel) =
                        await CreateHandlerAsync(testDb, recording, git, null).ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                    AssertEqual(0, recording.DrainerCalls.Count, "Expected no drainer fires for non-qualifying path");
                    AssertEqual(0, recording.CriticalCalls.Count, "Expected no critical fires for non-qualifying path");
                }
            });

            await RunTest("McpAuditTools_RecordVerdictCritical_FiresCritical", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RecordingRemoteTriggerService recording = new RecordingRemoteTriggerService();
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("rt-audit-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    MergeEntry entry = new MergeEntry("branch-rt-1", "main")
                    {
                        VesselId = vessel.Id,
                        Status = MergeStatusEnum.Landed,
                        AuditDeepPicked = true,
                        AuditDeepVerdict = "Pending"
                    };
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? recordHandler = null;
                    McpAuditTools.Register(
                        (name, _, _, h) => { if (name == "armada_record_audit_verdict") recordHandler = h; },
                        testDb.Driver,
                        recording);
                    AssertNotNull(recordHandler, "armada_record_audit_verdict handler should be registered");

                    // Critical verdict should fire FireCriticalAsync
                    JsonElement criticalArgs = JsonSerializer.SerializeToElement(new
                    {
                        entryId = entry.Id,
                        verdict = "Critical",
                        notes = "Suspicious pattern detected",
                        recommendedAction = "Roll back immediately"
                    });
                    await recordHandler!(criticalArgs).ConfigureAwait(false);

                    AssertEqual(1, recording.CriticalCalls.Count, "Expected one critical fire for Critical verdict");
                    AssertTrue(recording.CriticalCalls[0].Contains("Suspicious pattern detected"), "Expected notes in critical fire text");
                    AssertTrue(recording.CriticalCalls[0].Contains("Roll back immediately"), "Expected recommendedAction in critical fire text");
                    AssertEqual(0, recording.DrainerCalls.Count, "Expected no drainer fires from audit verdict");

                    // Pass verdict should not fire anything
                    RecordingRemoteTriggerService passRecording = new RecordingRemoteTriggerService();
                    Func<JsonElement?, Task<object>>? passRecordHandler = null;
                    McpAuditTools.Register(
                        (name, _, _, h) => { if (name == "armada_record_audit_verdict") passRecordHandler = h; },
                        testDb.Driver,
                        passRecording);

                    MergeEntry entry2 = new MergeEntry("branch-rt-2", "main")
                    {
                        VesselId = vessel.Id,
                        Status = MergeStatusEnum.Landed,
                        AuditDeepPicked = true,
                        AuditDeepVerdict = "Pending"
                    };
                    entry2 = await testDb.Driver.MergeEntries.CreateAsync(entry2).ConfigureAwait(false);

                    JsonElement passArgs = JsonSerializer.SerializeToElement(new
                    {
                        entryId = entry2.Id,
                        verdict = "Pass",
                        notes = "All good"
                    });
                    await passRecordHandler!(passArgs).ConfigureAwait(false);

                    AssertEqual(0, passRecording.CriticalCalls.Count, "Pass verdict should not fire critical");
                    AssertEqual(0, passRecording.DrainerCalls.Count, "Pass verdict should not fire drainer");
                }
            });

            await RunTest("MissionOutcomeWakeHandler_WillInvokeLanding_DoesNotFire", async () =>
            {
                RecordingRemoteTriggerService recording = new RecordingRemoteTriggerService();
                MissionOutcomeWakeHandler handler = new MissionOutcomeWakeHandler(recording, CreateLogging());
                Mission mission = new Mission("intermediate stage")
                {
                    Status = MissionStatusEnum.WorkProduced,
                    VesselId = "vsl_test"
                };

                await handler.HandleAsync(mission, willInvokeLandingHandler: true).ConfigureAwait(false);

                AssertEqual(0, recording.DrainerCalls.Count,
                    "Wake handler must stay silent when landing handler will run (avoids duplicate wake-ups).");
            });

            await RunTest("MissionOutcomeWakeHandler_IntermediateWorkProduced_FiresDrainer", async () =>
            {
                RecordingRemoteTriggerService recording = new RecordingRemoteTriggerService();
                MissionOutcomeWakeHandler handler = new MissionOutcomeWakeHandler(recording, CreateLogging());
                Mission mission = new Mission("intermediate stage")
                {
                    Status = MissionStatusEnum.WorkProduced,
                    VesselId = "vsl_intermediate"
                };

                await handler.HandleAsync(mission, willInvokeLandingHandler: false).ConfigureAwait(false);

                AssertEqual(1, recording.DrainerCalls.Count,
                    "Intermediate WorkProduced must fire a drainer when landing handler is bypassed.");
                AssertEqual("vsl_intermediate", recording.DrainerCalls[0].vesselId, "vesselId routed to FireDrainerAsync");
                AssertContains("WorkProduced", recording.DrainerCalls[0].text, "wake text identifies WorkProduced");
                AssertContains(mission.Id, recording.DrainerCalls[0].text, "wake text contains mission id");
            });

            await RunTest("MissionOutcomeWakeHandler_FailedStatus_FiresMissionFailed", async () =>
            {
                RecordingRemoteTriggerService recording = new RecordingRemoteTriggerService();
                MissionOutcomeWakeHandler handler = new MissionOutcomeWakeHandler(recording, CreateLogging());
                Mission mission = new Mission("doomed stage")
                {
                    Status = MissionStatusEnum.Failed,
                    VesselId = "vsl_failed",
                    FailureReason = "agent crashed"
                };

                await handler.HandleAsync(mission, willInvokeLandingHandler: false).ConfigureAwait(false);

                AssertEqual(1, recording.DrainerCalls.Count, "Terminal failure that bypasses landing handler must wake.");
                AssertContains("MissionFailed", recording.DrainerCalls[0].text, "wake text labels MissionFailed");
                AssertContains("agent crashed", recording.DrainerCalls[0].text, "wake text includes failure reason");
            });

            await RunTest("MissionOutcomeWakeHandler_NonOutcomeStatus_DoesNotFire", async () =>
            {
                RecordingRemoteTriggerService recording = new RecordingRemoteTriggerService();
                MissionOutcomeWakeHandler handler = new MissionOutcomeWakeHandler(recording, CreateLogging());
                Mission mission = new Mission("still working")
                {
                    Status = MissionStatusEnum.InProgress,
                    VesselId = "vsl_active"
                };

                await handler.HandleAsync(mission, willInvokeLandingHandler: false).ConfigureAwait(false);

                AssertEqual(0, recording.DrainerCalls.Count,
                    "Non-outcome statuses (heartbeats, in-progress) must not wake.");
            });

            await RunTest("MissionOutcomeWakeHandler_BuildWakeText_NullForHeartbeat", () =>
            {
                Mission missionInProgress = new Mission("in flight") { Status = MissionStatusEnum.InProgress };
                Mission missionPending = new Mission("queued") { Status = MissionStatusEnum.Pending };
                Mission missionAssigned = new Mission("assigned") { Status = MissionStatusEnum.Assigned };

                AssertNull(MissionOutcomeWakeHandler.BuildWakeText(missionInProgress), "InProgress must not produce wake text");
                AssertNull(MissionOutcomeWakeHandler.BuildWakeText(missionPending), "Pending must not produce wake text");
                AssertNull(MissionOutcomeWakeHandler.BuildWakeText(missionAssigned), "Assigned must not produce wake text");

                return Task.CompletedTask;
            });

            await RunTest("MissionService_OnMissionOutcome_FiresAfterHandleCompletion", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService);

                    List<MissionOutcomeCapture> outcomes = new List<MissionOutcomeCapture>();
                    missionService.OnMissionOutcome = (Mission mission, bool willInvokeLanding) =>
                    {
                        outcomes.Add(new MissionOutcomeCapture(mission.Id, mission.Status, willInvokeLanding));
                        return Task.CompletedTask;
                    };

                    Vessel vessel = new Vessel("outcome-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_outcome_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_outcome_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("outcome-captain") { State = CaptainStateEnum.Working };
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id)
                    {
                        CaptainId = captain.Id,
                        WorktreePath = Path.Combine(Path.GetTempPath(), "armada_outcome_wt_" + Guid.NewGuid().ToString("N")),
                        BranchName = "armada/test/msn_outcome",
                        Active = true
                    };
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    Mission mission = new Mission("outcome mission")
                    {
                        Status = MissionStatusEnum.InProgress,
                        CaptainId = captain.Id,
                        DockId = dock.Id,
                        VesselId = vessel.Id
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    captain.CurrentMissionId = mission.Id;
                    captain.CurrentDockId = dock.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    await missionService.HandleCompletionAsync(captain).ConfigureAwait(false);

                    AssertEqual(1, outcomes.Count, "OnMissionOutcome must fire exactly once after HandleCompletionAsync");
                    AssertEqual(mission.Id, outcomes[0].MissionId, "Outcome mission id matches");
                    AssertEqual(MissionStatusEnum.WorkProduced, outcomes[0].Status, "Status reported as WorkProduced after work emitted");
                }
            });

            await RunTest("MissionService_OnMissionOutcome_IntermediatePipelineStage_WillInvokeLandingFalse", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService);

                    RecordingRemoteTriggerService recording = new RecordingRemoteTriggerService();
                    MissionOutcomeWakeHandler outcomeWake = new MissionOutcomeWakeHandler(recording, logging);
                    missionService.OnMissionOutcome = (Mission mission, bool willInvokeLanding) =>
                        outcomeWake.HandleAsync(mission, willInvokeLanding);

                    Vessel vessel = new Vessel("pipeline-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_pipe_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_pipe_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Voyage voyage = new Voyage("pipeline voyage");
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Captain captain = new Captain("pipe-captain") { State = CaptainStateEnum.Working };
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id)
                    {
                        CaptainId = captain.Id,
                        WorktreePath = Path.Combine(Path.GetTempPath(), "armada_pipe_wt_" + Guid.NewGuid().ToString("N")),
                        BranchName = "armada/test/msn_stage1",
                        Active = true
                    };
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    Mission stage1 = new Mission("stage 1 (worker)")
                    {
                        Status = MissionStatusEnum.InProgress,
                        CaptainId = captain.Id,
                        DockId = dock.Id,
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id
                    };
                    await testDb.Driver.Missions.CreateAsync(stage1).ConfigureAwait(false);

                    Mission stage2 = new Mission("stage 2 (judge)")
                    {
                        Status = MissionStatusEnum.Pending,
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        DependsOnMissionId = stage1.Id
                    };
                    await testDb.Driver.Missions.CreateAsync(stage2).ConfigureAwait(false);

                    captain.CurrentMissionId = stage1.Id;
                    captain.CurrentDockId = dock.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    await missionService.HandleCompletionAsync(captain).ConfigureAwait(false);

                    AssertTrue(recording.DrainerCalls.Count >= 1,
                        "Intermediate pipeline stage WorkProduced must fire FireDrainerAsync via OnMissionOutcome.");
                    bool foundWake = false;
                    foreach ((string vid, string txt) in recording.DrainerCalls)
                    {
                        if (vid == vessel.Id && txt.Contains("WorkProduced") && txt.Contains(stage1.Id))
                        {
                            foundWake = true;
                            break;
                        }
                    }
                    AssertTrue(foundWake, "Drainer fire must reference vessel id and intermediate mission id with WorkProduced label.");
                }
            });

            await RunTest("AgentWakeProcessHost_NonZeroExit_InvokesOnExited", async () =>
            {
                LoggingModule logging = CreateLogging();
                AgentWakeProcessHost host = new AgentWakeProcessHost(logging);

                AgentWakeProcessRequest request = BuildExitProcessRequest(exitCode: 7);

                TaskCompletionSource exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool started = host.TryStart(request, () => exited.TrySetResult());

                AssertTrue(started, "AgentWakeProcessHost.TryStart should succeed for a real shell exit command.");

                Task winner = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false);
                AssertTrue(winner == exited.Task,
                    "onExited must be invoked for a non-zero-exit child so the diagnostic block runs.");
            });

            await RunTest("AgentWakeProcessHost_SuccessfulExit_InvokesOnExited", async () =>
            {
                LoggingModule logging = CreateLogging();
                AgentWakeProcessHost host = new AgentWakeProcessHost(logging);

                AgentWakeProcessRequest request = BuildExitProcessRequest(exitCode: 0);

                TaskCompletionSource exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool started = host.TryStart(request, () => exited.TrySetResult());

                AssertTrue(started, "AgentWakeProcessHost.TryStart should succeed for a normal exit command.");
                Task winner = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false);
                AssertTrue(winner == exited.Task,
                    "onExited must be invoked for a normal-exit child so the diagnostic block runs.");
            });
        }

        private sealed class MissionOutcomeCapture
        {
            public string MissionId { get; }
            public MissionStatusEnum Status { get; }
            public bool WillInvokeLanding { get; }

            public MissionOutcomeCapture(string missionId, MissionStatusEnum status, bool willInvokeLanding)
            {
                MissionId = missionId;
                Status = status;
                WillInvokeLanding = willInvokeLanding;
            }
        }

        private static AgentWakeProcessRequest BuildExitProcessRequest(int exitCode)
        {
            AgentWakeProcessRequest request = new AgentWakeProcessRequest();
            if (OperatingSystem.IsWindows())
            {
                request.Command = "cmd.exe";
                request.ArgumentList = new List<string> { "/c", "exit " + exitCode };
            }
            else
            {
                request.Command = "/bin/sh";
                request.ArgumentList = new List<string> { "-c", "exit " + exitCode };
            }
            request.StdinPayload = string.Empty;
            request.TimeoutSeconds = 10;
            return request;
        }
    }
}
