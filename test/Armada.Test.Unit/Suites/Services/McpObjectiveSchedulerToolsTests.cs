namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
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
    /// Unit tests for McpObjectiveSchedulerTools: set/status handlers and auto-dispatchable marking.
    /// </summary>
    public class McpObjectiveSchedulerToolsTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "MCP Objective Scheduler Tools";

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        #endregion

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("ArmadaStatus_Scheduler_IsNonNullByDefault", () =>
            {
                ArmadaStatus status = new ArmadaStatus();
                AssertNotNull(status.Scheduler);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ArmadaStatus_Scheduler_SetterRejectsNull", () =>
            {
                ArmadaStatus status = new ArmadaStatus();
                status.Scheduler = null!;
                AssertNotNull(status.Scheduler);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("BuildStatus_FromScheduler_ReturnsCorrectValues", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, new ArmadaSettings
                {
                    AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                    {
                        Enabled = true,
                        Paused = false,
                        IntervalMinutes = 10,
                        MaxConcurrentVoyages = 3
                    }
                });

                ObjectiveSchedulerStatus status = McpObjectiveSchedulerTools.BuildStatus(scheduler);

                AssertTrue(status.Enabled, "Enabled should match settings.");
                AssertFalse(status.Paused, "Paused should match settings.");
                AssertEqual(10, status.IntervalMinutes, "IntervalMinutes should match settings.");
                AssertEqual(3, status.MaxConcurrentVoyages, "MaxConcurrentVoyages should match settings.");
                AssertNull(status.LastTickUtc, "LastTickUtc should be null before first sweep.");
            }).ConfigureAwait(false);

            await RunTest("ArmadaObjectiveSchedulerSet_TogglesEnabledAndPaused", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, new ArmadaSettings());
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, scheduler);

                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    enabled = true,
                    paused = true,
                    intervalMinutes = 15,
                    maxConcurrentVoyages = 5
                });

                object result = await handlers["armada_objective_scheduler_set"](args).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(result, _JsonOptions);

                AssertContains("\"enabled\":true", json);
                AssertContains("\"paused\":true", json);
                AssertContains("\"intervalMinutes\":15", json);
                AssertContains("\"maxConcurrentVoyages\":5", json);

                AssertTrue(scheduler.Enabled, "Scheduler.Enabled should be true.");
                AssertTrue(scheduler.Paused, "Scheduler.Paused should be true.");
                AssertEqual(15, scheduler.IntervalMinutes, "Scheduler.IntervalMinutes should be 15.");
                AssertEqual(5, scheduler.MaxConcurrentVoyages, "Scheduler.MaxConcurrentVoyages should be 5.");
            }).ConfigureAwait(false);

            await RunTest("ArmadaObjectiveSchedulerSet_WithNoArgs_ReturnsCurrentStatus", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ArmadaSettings settings = new ArmadaSettings();
                settings.AutonomousObjectiveScheduler.Enabled = false;
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, settings);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, scheduler);

                object result = await handlers["armada_objective_scheduler_set"](null).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(result, _JsonOptions);

                AssertContains("\"enabled\":false", json);
            }).ConfigureAwait(false);

            await RunTest("ArmadaObjectiveSchedulerStatus_ReflectsCurrentState", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ArmadaSettings settings = new ArmadaSettings();
                settings.AutonomousObjectiveScheduler.Enabled = true;
                settings.AutonomousObjectiveScheduler.IntervalMinutes = 30;
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, settings);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, scheduler);

                object result = await handlers["armada_objective_scheduler_status"](null).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(result, _JsonOptions);

                AssertContains("\"enabled\":true", json);
                AssertContains("\"intervalMinutes\":30", json);
            }).ConfigureAwait(false);

            await RunTest("ArmadaMarkObjectiveAutoDispatchable_SetsEnabledAndPersists", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated(
                    Constants.DefaultTenantId,
                    Constants.DefaultUserId,
                    false,
                    true,
                    "ApiKey");
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);

                Objective objective = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Test Objective"
                }).ConfigureAwait(false);
                AssertFalse(objective.AutoDispatchEnabled, "AutoDispatchEnabled should default to false.");

                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, new ArmadaSettings());
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, scheduler, objectives);

                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    objectiveId = objective.Id,
                    enabled = true
                });

                object result = await handlers["armada_mark_objective_auto_dispatchable"](args).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(result, _JsonOptions);

                AssertContains("\"autoDispatchEnabled\":true", json);
                AssertContains(objective.Id, json);

                Objective? updated = await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false);
                AssertNotNull(updated);
                AssertTrue(updated!.AutoDispatchEnabled, "AutoDispatchEnabled should be persisted in the database.");
            }).ConfigureAwait(false);

            await RunTest("ArmadaMarkObjectiveAutoDispatchable_MissingId_ReturnsError", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, new ArmadaSettings());
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, scheduler);

                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    enabled = true
                });

                object result = await handlers["armada_mark_objective_auto_dispatchable"](args).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(result, _JsonOptions);

                AssertContains("error", json.ToLowerInvariant());
            }).ConfigureAwait(false);

            await RunTest("OnGetSchedulerStatus_PopulatesArmadaStatusScheduler", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = CreateSettings();
                StubGitService git = new StubGitService();

                IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService);
                IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                AdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService);

                settings.AutonomousObjectiveScheduler.Enabled = true;
                settings.AutonomousObjectiveScheduler.IntervalMinutes = 20;
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, settings);
                admiral.OnGetSchedulerStatus = () => McpObjectiveSchedulerTools.BuildStatus(scheduler);

                ArmadaStatus status = await admiral.GetStatusAsync().ConfigureAwait(false);

                AssertNotNull(status.Scheduler);
                AssertTrue(status.Scheduler.Enabled, "Scheduler.Enabled should be true.");
                AssertEqual(20, status.Scheduler.IntervalMinutes, "Scheduler.IntervalMinutes should be 20.");
            }).ConfigureAwait(false);

            await RunTest("OnGetSchedulerStatus_WhenCallbackUnset_KeepsDefaultScheduler", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = CreateSettings();
                StubGitService git = new StubGitService();

                IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService);
                IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                AdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService);

                ArmadaStatus status = await admiral.GetStatusAsync().ConfigureAwait(false);

                AssertNotNull(status.Scheduler);
                AssertFalse(status.Scheduler.Enabled, "Default scheduler should remain disabled when callback is unset.");
                AssertEqual(25, status.Scheduler.IntervalMinutes, "Default interval should be 25 minutes.");
            }).ConfigureAwait(false);

            await RunTest("ArmadaObjectiveSchedulerSet_ClampsIntervalAndMaxConcurrent", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, new ArmadaSettings());
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, scheduler);

                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    intervalMinutes = 0,
                    maxConcurrentVoyages = 999
                });

                object result = await handlers["armada_objective_scheduler_set"](args).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(result, _JsonOptions);

                AssertContains("\"intervalMinutes\":1", json);
                AssertContains("\"maxConcurrentVoyages\":50", json);
                AssertEqual(1, scheduler.IntervalMinutes, "IntervalMinutes should clamp to minimum 1.");
                AssertEqual(50, scheduler.MaxConcurrentVoyages, "MaxConcurrentVoyages should clamp to maximum 50.");
            }).ConfigureAwait(false);

            await RunTest("ArmadaObjectiveSchedulerSet_PartialUpdate_LeavesOtherFieldsUnchanged", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ArmadaSettings settings = new ArmadaSettings();
                settings.AutonomousObjectiveScheduler.Enabled = true;
                settings.AutonomousObjectiveScheduler.IntervalMinutes = 40;
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, settings);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, scheduler);

                JsonElement args = JsonSerializer.SerializeToElement(new { intervalMinutes = 12 });
                object result = await handlers["armada_objective_scheduler_set"](args).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(result, _JsonOptions);

                AssertContains("\"enabled\":true", json);
                AssertContains("\"intervalMinutes\":12", json);
                AssertTrue(scheduler.Enabled, "Enabled should remain true when not specified.");
                AssertEqual(12, scheduler.IntervalMinutes, "IntervalMinutes should update.");
            }).ConfigureAwait(false);

            await RunTest("ArmadaObjectiveSchedulerSet_DisableAndResume", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ArmadaSettings settings = new ArmadaSettings();
                settings.AutonomousObjectiveScheduler.Enabled = true;
                settings.AutonomousObjectiveScheduler.Paused = true;
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, settings);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, scheduler);

                JsonElement args = JsonSerializer.SerializeToElement(new { enabled = false, paused = false });
                await handlers["armada_objective_scheduler_set"](args).ConfigureAwait(false);

                AssertFalse(scheduler.Enabled, "Scheduler should be disabled.");
                AssertFalse(scheduler.Paused, "Scheduler should be resumed.");
            }).ConfigureAwait(false);

            await RunTest("BuildStatus_ReflectsLastSkipReasonAfterDisabledSweep", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, new ArmadaSettings());

                await scheduler.SweepAsync().ConfigureAwait(false);

                ObjectiveSchedulerStatus status = McpObjectiveSchedulerTools.BuildStatus(scheduler);
                AssertEqual("disabled", status.LastSkipReason, "LastSkipReason should be 'disabled' when scheduler is off.");
                AssertNotNull(status.LastTickUtc, "LastTickUtc should be set after sweep attempt.");
            }).ConfigureAwait(false);

            await RunTest("ArmadaMarkObjectiveAutoDispatchable_SetsBlockersAndPersists", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated(
                    Constants.DefaultTenantId,
                    Constants.DefaultUserId,
                    false,
                    true,
                    "ApiKey");
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);

                Objective blocker = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Blocker Objective"
                }).ConfigureAwait(false);

                Objective objective = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Dependent Objective"
                }).ConfigureAwait(false);

                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, new ArmadaSettings());
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, scheduler, objectives);

                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    objectiveId = objective.Id,
                    enabled = true,
                    blockedByObjectiveIds = new[] { blocker.Id }
                });

                object result = await handlers["armada_mark_objective_auto_dispatchable"](args).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(result, _JsonOptions);

                AssertContains(blocker.Id, json);
                AssertContains("\"autoDispatchEnabled\":true", json);

                Objective? updated = await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false);
                AssertNotNull(updated);
                AssertTrue(updated!.AutoDispatchEnabled, "AutoDispatchEnabled should be persisted.");
                AssertEqual(1, updated.BlockedByObjectiveIds.Count, "Blocker count should be 1.");
                AssertEqual(blocker.Id, updated.BlockedByObjectiveIds[0], "Blocker ID should be persisted.");
            }).ConfigureAwait(false);

            await RunTest("ArmadaMarkObjectiveAutoDispatchable_OmittedBlockers_NoClobber", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated(
                    Constants.DefaultTenantId,
                    Constants.DefaultUserId,
                    false,
                    true,
                    "ApiKey");
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);

                Objective blocker = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Existing Blocker"
                }).ConfigureAwait(false);

                Objective objective = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Objective With Blockers",
                    BlockedByObjectiveIds = new List<string> { blocker.Id }
                }).ConfigureAwait(false);

                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, new ArmadaSettings());
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, scheduler, objectives);

                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    objectiveId = objective.Id,
                    enabled = true
                });

                await handlers["armada_mark_objective_auto_dispatchable"](args).ConfigureAwait(false);

                Objective? updated = await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false);
                AssertNotNull(updated);
                AssertEqual(1, updated!.BlockedByObjectiveIds.Count, "Omitted blockers should not clear existing list.");
                AssertEqual(blocker.Id, updated.BlockedByObjectiveIds[0], "Existing blocker should remain.");
            }).ConfigureAwait(false);

            await RunTest("ArmadaMarkObjectiveAutoDispatchable_InvalidObjectiveId_ReturnsError", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, new ArmadaSettings());
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, scheduler);

                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    objectiveId = "obj_does_not_exist",
                    enabled = true
                });

                object result = await handlers["armada_mark_objective_auto_dispatchable"](args).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(result, _JsonOptions);

                AssertContains("error", json.ToLowerInvariant());
            }).ConfigureAwait(false);

            await RunTest("McpToolRegistrar_RegisterAll_IncludesSchedulerToolsWhenProvided", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, new ArmadaSettings());
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();

                McpToolRegistrar.RegisterAll(
                    (name, _, _, handler) => { handlers[name] = handler; },
                    testDb.Driver,
                    new StubAdmiralService(),
                    objectiveService: objectives,
                    objectiveScheduler: scheduler);

                AssertTrue(handlers.ContainsKey("armada_objective_scheduler_status"), "Registrar should include scheduler status tool.");
                AssertTrue(handlers.ContainsKey("armada_objective_scheduler_set"), "Registrar should include scheduler set tool.");
                AssertTrue(handlers.ContainsKey("armada_mark_objective_auto_dispatchable"), "Registrar should include mark auto-dispatchable tool.");
            }).ConfigureAwait(false);

            await RunTest("McpToolRegistrar_RegisterAll_OmitsSchedulerToolsWithoutScheduler", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();

                McpToolRegistrar.RegisterAll(
                    (name, _, _, handler) => { handlers[name] = handler; },
                    testDb.Driver,
                    new StubAdmiralService(),
                    objectiveService: objectives);

                AssertFalse(handlers.ContainsKey("armada_objective_scheduler_status"), "Registrar should omit scheduler tools without scheduler.");
                AssertFalse(handlers.ContainsKey("armada_objective_scheduler_set"), "Registrar should omit scheduler set tool without scheduler.");
                AssertFalse(handlers.ContainsKey("armada_mark_objective_auto_dispatchable"), "Registrar should omit mark tool without scheduler.");
            }).ConfigureAwait(false);
        }

        #region Private-Methods

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_oss_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_oss_repos_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        private AutonomousObjectiveScheduler CreateScheduler(DatabaseDriver database, ArmadaSettings settings)
        {
            LoggingModule logging = CreateLogging();
            ObjectiveService objectives = new ObjectiveService(database);
            return new AutonomousObjectiveScheduler(
                database,
                objectives,
                new StubAdmiralService(),
                new StubSchedulerMergeQueueService(),
                settings,
                logging);
        }

        private static Dictionary<string, Func<JsonElement?, Task<object>>> RegisterHandlers(
            DatabaseDriver database,
            AutonomousObjectiveScheduler scheduler,
            ObjectiveService? objectiveService = null)
        {
            objectiveService = objectiveService ?? new ObjectiveService(database);
            Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
            McpObjectiveSchedulerTools.Register(
                (name, _, _, handler) => { handlers[name] = handler; },
                scheduler,
                database,
                objectiveService);
            return handlers;
        }

        #endregion

        #region Private-Types

        private sealed class StubAdmiralService : IAdmiralService
        {
            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => Task.FromResult<Pipeline?>(null);

            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
                => Task.FromResult(new ArmadaStatus());

            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
                => Task.CompletedTask;

            public Task RecallAllAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task HealthCheckAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
                => Task.CompletedTask;
        }

        private sealed class StubSchedulerMergeQueueService : IMergeQueueService
        {
            public Task<MergeEntry> EnqueueAsync(MergeEntry entry, CancellationToken token = default) => Task.FromResult(entry);
            public Task ProcessQueueAsync(CancellationToken token = default) => Task.CompletedTask;
            public Task CancelAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.CompletedTask;
            public Task<List<MergeEntry>> ListAsync(string? tenantId = null, CancellationToken token = default) => Task.FromResult(new List<MergeEntry>());
            public Task<MergeEntry?> ProcessSingleAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult<MergeEntry?>(null);
            public Task ProcessEntryByIdAsync(string entryId, CancellationToken token = default) => Task.CompletedTask;
            public Task<MergeEntry?> GetAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult<MergeEntry?>(null);
            public Task<bool> DeleteAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult(false);
            public Task<MergeQueuePurgeResult> DeleteMultipleAsync(List<string> entryIds, string? tenantId = null, CancellationToken token = default) => Task.FromResult(new MergeQueuePurgeResult());
            public Task<int> PurgeTerminalAsync(string? vesselId = null, MergeStatusEnum? status = null, string? tenantId = null, CancellationToken token = default) => Task.FromResult(0);
            public Task<int> ReconcilePullRequestEntriesAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<int> ReconcileLandingStateMachineAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<int> RecoverInFlightLandingsAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<bool> TryOpenPullRequestForRecoveryAsync(string mergeEntryId, CancellationToken token = default) => Task.FromResult(false);
            public Task<bool> HasActiveMergeEntryForMissionAsync(string missionId, CancellationToken token = default) => Task.FromResult(false);
            public Task<SafetyNetEnqueueResult> TrySafetyNetEnqueueAsync(Mission mission, Vessel vessel, string? unifiedDiff, IAutoLandEvaluator autoLandEvaluator, IConventionChecker conventionChecker, ICriticalTriggerEvaluator criticalTriggerEvaluator, CancellationToken token = default)
                => Task.FromResult(new SafetyNetEnqueueResult(SafetyNetEnqueueOutcomeEnum.Enqueued, null));
        }

        #endregion
    }
}
