namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
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
        }
    }
}
