namespace Armada.Test.Unit.Suites.Recovery
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Unit tests for <see cref="MergeRecoveryHandler"/>: covers Redispatch, Surface,
    /// admiral-restart-throws rollback, AuditCriticalTrigger short-circuit, and the
    /// M3-pending RebaseCaptain guard. Uses a hand-rolled <see cref="RecordingAdmiralService"/>
    /// stub plus a real SQLite <see cref="TestDatabase"/>.
    /// </summary>
    public class MergeRecoveryHandlerTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Merge Recovery Handler";

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ArmadaSettings CreateSettings(int cap = 2)
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.MaxRecoveryAttempts = cap;
            return settings;
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("OnMergeFailed_RouterReturnsRedispatch_IncrementsAndCallsAdmiral", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    Mission mission = await SeedMissionAsync(testDb).ConfigureAwait(false);
                    MergeEntry entry = await SeedFailedEntryAsync(testDb, mission, MergeFailureClassEnum.StaleBase, 0, 5).ConfigureAwait(false);

                    StubRouter router = new StubRouter(new RecoveryAction.Redispatch());
                    RecordingAdmiralService admiral = new RecordingAdmiralService();
                    MergeRecoveryHandler handler = new MergeRecoveryHandler(logging, testDb.Driver, router, admiral, settings);

                    await handler.OnMergeFailedAsync(entry.Id).ConfigureAwait(false);

                    AssertEqual(1, admiral.RestartCalls.Count, "admiral.RestartMissionAsync should be invoked once");
                    AssertEqual(mission.Id, admiral.RestartCalls[0], "admiral should be called with the original mission id");

                    Mission? updatedMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(updatedMission, "mission should still exist");
                    AssertEqual(1, updatedMission!.RecoveryAttempts, "RecoveryAttempts should be incremented");
                    AssertNotNull(updatedMission.LastRecoveryActionUtc, "LastRecoveryActionUtc should be set");

                    MergeEntry? updatedEntry = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertNotNull(updatedEntry, "merge entry should still exist");
                    AssertEqual(MergeStatusEnum.Cancelled, updatedEntry!.Status, "redispatched entry should be Cancelled");
                    AssertEqual("recovery_redispatched", updatedEntry.TestOutput, "redispatch reason should be recorded");
                }
            });

            await RunTest("OnMergeFailed_RouterReturnsSurface_SetsAuditCriticalTrigger", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    Mission mission = await SeedMissionAsync(testDb).ConfigureAwait(false);
                    MergeEntry entry = await SeedFailedEntryAsync(testDb, mission, MergeFailureClassEnum.TestFailureBeforeMerge, 0, 0).ConfigureAwait(false);

                    StubRouter router = new StubRouter(new RecoveryAction.Surface("test_failure_before_merge"));
                    RecordingAdmiralService admiral = new RecordingAdmiralService();
                    MergeRecoveryHandler handler = new MergeRecoveryHandler(logging, testDb.Driver, router, admiral, settings);

                    await handler.OnMergeFailedAsync(entry.Id).ConfigureAwait(false);

                    AssertEqual(0, admiral.RestartCalls.Count, "Surface action must NOT call admiral");

                    MergeEntry? updatedEntry = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertNotNull(updatedEntry, "merge entry should still exist");
                    AssertEqual("test_failure_before_merge", updatedEntry!.AuditCriticalTrigger, "surface reason should be on AuditCriticalTrigger");
                    AssertEqual(MergeStatusEnum.Failed, updatedEntry.Status, "surface keeps entry status as Failed");
                    AssertNotNull(updatedEntry.AuditDeepNotes, "classifier summary should be appended to AuditDeepNotes");
                    AssertContains("recovery_surface", updatedEntry.AuditDeepNotes!, "AuditDeepNotes should include recovery_surface marker");

                    Mission? updatedMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(updatedMission, "mission should exist");
                    AssertEqual(0, updatedMission!.RecoveryAttempts, "RecoveryAttempts should NOT be incremented on Surface");
                }
            });

            await RunTest("OnMergeFailed_AdmiralRestartThrows_RollsBackAttempts", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    Mission mission = await SeedMissionAsync(testDb).ConfigureAwait(false);
                    MergeEntry entry = await SeedFailedEntryAsync(testDb, mission, MergeFailureClassEnum.StaleBase, 0, 5).ConfigureAwait(false);

                    StubRouter router = new StubRouter(new RecoveryAction.Redispatch());
                    RecordingAdmiralService admiral = new RecordingAdmiralService();
                    admiral.ThrowOnRestart = true;
                    MergeRecoveryHandler handler = new MergeRecoveryHandler(logging, testDb.Driver, router, admiral, settings);

                    await handler.OnMergeFailedAsync(entry.Id).ConfigureAwait(false);

                    Mission? updatedMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(updatedMission, "mission should exist");
                    AssertEqual(0, updatedMission!.RecoveryAttempts, "RecoveryAttempts must NOT remain incremented when admiral throws");

                    MergeEntry? updatedEntry = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertNotNull(updatedEntry, "merge entry should exist");
                    AssertEqual("recovery_dispatch_failed", updatedEntry!.AuditCriticalTrigger, "entry should surface as recovery_dispatch_failed when admiral throws");
                }
            });

            await RunTest("OnMergeFailed_EntryAlreadyHasAuditCriticalTrigger_NoOp", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    Mission mission = await SeedMissionAsync(testDb).ConfigureAwait(false);
                    MergeEntry entry = await SeedFailedEntryAsync(testDb, mission, MergeFailureClassEnum.StaleBase, 0, 5).ConfigureAwait(false);
                    entry.AuditCriticalTrigger = "pr_fallback_already_won";
                    await testDb.Driver.MergeEntries.UpdateAsync(entry).ConfigureAwait(false);

                    StubRouter router = new StubRouter(new RecoveryAction.Redispatch());
                    RecordingAdmiralService admiral = new RecordingAdmiralService();
                    MergeRecoveryHandler handler = new MergeRecoveryHandler(logging, testDb.Driver, router, admiral, settings);

                    await handler.OnMergeFailedAsync(entry.Id).ConfigureAwait(false);

                    AssertEqual(0, admiral.RestartCalls.Count, "admiral should not be called when AuditCriticalTrigger is already set");
                    AssertEqual(0, router.CallCount, "router should not be consulted when PR-fallback already won");

                    Mission? updatedMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(0, updatedMission!.RecoveryAttempts, "RecoveryAttempts should remain 0");

                    MergeEntry? updatedEntry = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertEqual("pr_fallback_already_won", updatedEntry!.AuditCriticalTrigger, "AuditCriticalTrigger should be untouched");
                }
            });

            await RunTest("OnMergeFailed_RouterReturnsRebaseCaptain_ThrowsNotImplementedInM3", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    Mission mission = await SeedMissionAsync(testDb).ConfigureAwait(false);
                    MergeEntry entry = await SeedFailedEntryAsync(testDb, mission, MergeFailureClassEnum.TextConflict, 200, 8).ConfigureAwait(false);

                    StubRouter router = new StubRouter(new RecoveryAction.RebaseCaptain());
                    RecordingAdmiralService admiral = new RecordingAdmiralService();
                    MergeRecoveryHandler handler = new MergeRecoveryHandler(logging, testDb.Driver, router, admiral, settings);

                    bool threw = false;
                    try
                    {
                        await handler.OnMergeFailedAsync(entry.Id).ConfigureAwait(false);
                    }
                    catch (NotImplementedException ex)
                    {
                        threw = true;
                        AssertContains("M4", ex.Message, "exception should reference M4 deferral");
                    }

                    AssertTrue(threw, "RebaseCaptain branch must throw NotImplementedException in M3");
                    AssertEqual(0, admiral.RestartCalls.Count, "admiral should not be invoked when router returns RebaseCaptain in M3");
                }
            });

            await RunTest("OnMergeFailed_NoMissionId_LogsAndReturns", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    MergeEntry entry = new MergeEntry("captain-no-mission", "main");
                    entry.Status = MergeStatusEnum.Failed;
                    entry.MergeFailureClass = MergeFailureClassEnum.StaleBase;
                    await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    StubRouter router = new StubRouter(new RecoveryAction.Redispatch());
                    RecordingAdmiralService admiral = new RecordingAdmiralService();
                    MergeRecoveryHandler handler = new MergeRecoveryHandler(logging, testDb.Driver, router, admiral, settings);

                    await handler.OnMergeFailedAsync(entry.Id).ConfigureAwait(false);

                    AssertEqual(0, admiral.RestartCalls.Count, "no mission means no admiral call");
                    AssertEqual(0, router.CallCount, "no mission means no router call");
                }
            });
        }

        private static async Task<Mission> SeedMissionAsync(TestDatabase db)
        {
            Fleet fleet = new Fleet("Recovery Fleet");
            await db.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

            Vessel vessel = new Vessel("Recovery Vessel", "https://github.com/test/recovery");
            vessel.FleetId = fleet.Id;
            await db.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Voyage voyage = new Voyage("Recovery Voyage", "test recovery handler");
            await db.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

            Mission mission = new Mission("Recovery Mission", "stale-base captain work");
            mission.VoyageId = voyage.Id;
            mission.VesselId = vessel.Id;
            mission.Status = MissionStatusEnum.Failed;
            return await db.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task<MergeEntry> SeedFailedEntryAsync(
            TestDatabase db,
            Mission mission,
            MergeFailureClassEnum failureClass,
            int diffLineCount,
            int conflictedFileCount)
        {
            MergeEntry entry = new MergeEntry("captain-branch-" + Guid.NewGuid().ToString("N").Substring(0, 8), "main");
            entry.MissionId = mission.Id;
            entry.VesselId = mission.VesselId;
            entry.Status = MergeStatusEnum.Failed;
            entry.MergeFailureClass = failureClass;
            entry.MergeFailureSummary = "test summary";
            entry.DiffLineCount = diffLineCount;

            if (conflictedFileCount > 0)
            {
                List<string> files = new List<string>();
                for (int i = 0; i < conflictedFileCount; i++) files.Add("file" + i + ".cs");
                entry.ConflictedFiles = System.Text.Json.JsonSerializer.Serialize(files);
            }

            return await db.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);
        }

        /// <summary>
        /// Hand-rolled <see cref="IRecoveryRouter"/> double that returns a fixed action and
        /// records every Route call.
        /// </summary>
        private sealed class StubRouter : IRecoveryRouter
        {
            private readonly RecoveryAction _Action;

            public int CallCount { get; private set; }

            public StubRouter(RecoveryAction action)
            {
                _Action = action;
            }

            public RecoveryAction Route(MergeFailureClassEnum failureClass, bool conflictTrivial, int recoveryAttempts)
            {
                CallCount++;
                return _Action;
            }
        }

        /// <summary>
        /// Hand-rolled <see cref="IAdmiralService"/> double; only RestartMissionAsync is
        /// exercised by these tests, every other surface throws.
        /// </summary>
        internal sealed class RecordingAdmiralService : IAdmiralService
        {
            public List<string> RestartCalls { get; } = new List<string>();
            public bool ThrowOnRestart { get; set; } = false;

            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public Task<Mission> RestartMissionAsync(string missionId, CancellationToken token = default)
            {
                RestartCalls.Add(missionId);
                if (ThrowOnRestart) throw new InvalidOperationException("simulated admiral failure");
                return Task.FromResult(new Mission { Id = missionId, Status = MissionStatusEnum.Pending });
            }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default) => throw new NotImplementedException();
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default) => throw new NotImplementedException();
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default) => throw new NotImplementedException();
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default) => throw new NotImplementedException();
            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default) => throw new NotImplementedException();
            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default) => Task.FromResult<Pipeline?>(null);
            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task RecallCaptainAsync(string captainId, CancellationToken token = default) => throw new NotImplementedException();
            public Task RecallAllAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task HealthCheckAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task CleanupStaleCaptainsAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default) => throw new NotImplementedException();
        }
    }
}
