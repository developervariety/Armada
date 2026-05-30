namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Unit coverage for Armada-native autonomous recovery records and dispatch policy.
    /// </summary>
    public class AutonomousRecoveryOrchestratorTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Autonomous Recovery Orchestrator";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Recoverable failed mission creates incident, runbook execution, and rescue mission", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                failed.Persona = "Judge";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                Mission? original = await testDb.Driver.Missions.ReadAsync(failed.Id).ConfigureAwait(false);
                AssertTrue(original != null, "Expected original mission to remain readable.");
                AssertEqual(1, original!.RecoveryAttempts);
                AssertTrue(original.LastRecoveryActionUtc.HasValue, "Expected recovery timestamp on original mission.");

                List<Mission> vesselMissions = await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
                Mission? rescue = vesselMissions.FirstOrDefault(item => item.ParentMissionId == failed.Id);
                AssertTrue(rescue != null, "Expected a linked rescue mission.");
                AssertEqual("Worker", rescue!.Persona, "Reviewer-stage failures should dispatch Worker rescue missions.");
                AssertContains("Autonomous rescue", rescue!.Description ?? "", "Rescue mission should carry recovery context.");

                AuthContext auth = AuthContext.Authenticated("ten_auto_recovery", "usr_auto_recovery", false, true, "UnitTest");
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = failed.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, incidentPage.Objects.Count);
                AssertEqual(IncidentSeverityEnum.Medium, incidentPage.Objects[0].Severity);

                EnumerationResult<RunbookExecution> executionPage = await runbooks.EnumerateExecutionsAsync(auth, new RunbookExecutionQuery
                {
                    IncidentId = incidentPage.Objects[0].Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, executionPage.Objects.Count);
                AssertEqual(RunbookExecutionStatusEnum.Completed, executionPage.Objects[0].Status);
            }).ConfigureAwait(false);

            await RunTest("Serious failure opens incident without dispatching rescue", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_block", "usr_auto_block").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_block", "usr_auto_block").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Review denied: missing tests").ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                Mission? original = await testDb.Driver.Missions.ReadAsync(failed.Id).ConfigureAwait(false);
                AssertTrue(original != null, "Expected original mission.");
                AssertEqual(1, original!.RecoveryAttempts);
                AssertTrue(original.LastRecoveryActionUtc.HasValue, "Expected blocked policy to be recorded.");
                AssertEqual(0, admiral.DispatchedMissions.Count);

                AuthContext auth = AuthContext.Authenticated("ten_auto_block", "usr_auto_block", false, true, "UnitTest");
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = failed.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, incidentPage.Objects.Count);
                AssertEqual(IncidentSeverityEnum.High, incidentPage.Objects[0].Severity);
                AssertContains("human review", incidentPage.Objects[0].RecoveryNotes ?? "", "Incident should explain why rescue was blocked.");
            }).ConfigureAwait(false);

            await RunTest("Landing failure opens incident without generic rescue dispatch", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_landing", "usr_auto_landing").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_landing", "usr_auto_landing").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Local merge failed with conflicts").ConfigureAwait(false);
                failed.Status = MissionStatusEnum.LandingFailed;
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count);
                Mission? original = await testDb.Driver.Missions.ReadAsync(failed.Id).ConfigureAwait(false);
                AssertTrue(original != null, "Expected original mission.");
                AssertTrue(original!.LastRecoveryActionUtc.HasValue, "Expected landing failure policy to be recorded.");

                AuthContext auth = AuthContext.Authenticated("ten_auto_landing", "usr_auto_landing", false, true, "UnitTest");
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = failed.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, incidentPage.Objects.Count);
                AssertContains("landing", incidentPage.Objects[0].RecoveryNotes ?? "", "Incident should preserve landing ownership reason.");
            }).ConfigureAwait(false);

            await RunTest("Cancelled parent voyage suppresses failed-mission rescue and cancels active rescue", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_cancelled_voyage", "usr_auto_cancelled_voyage").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_cancelled_voyage", "usr_auto_cancelled_voyage").ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Cancelled voyage")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Cancelled,
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-1),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
                }).ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge failed after voyage cancellation").ConfigureAwait(false);
                failed.VoyageId = voyage.Id;
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);
                Mission existingRescue = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    ParentMissionId = failed.Id,
                    Title = "Rescue 1: Failed mission",
                    Description = "Autonomous rescue mission. <!-- ARMADA:AUTO-RESCUE -->",
                    Status = MissionStatusEnum.Pending
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count);
                Mission? rescue = await testDb.Driver.Missions.ReadAsync(existingRescue.Id).ConfigureAwait(false);
                AssertTrue(rescue != null, "Expected existing rescue to remain readable.");
                AssertEqual(MissionStatusEnum.Cancelled, rescue!.Status);
                Mission? original = await testDb.Driver.Missions.ReadAsync(failed.Id).ConfigureAwait(false);
                AssertTrue(original != null, "Expected original mission.");
                AssertTrue(original!.LastRecoveryActionUtc.HasValue, "Cancelled-voyage suppression should be recorded.");
            }).ConfigureAwait(false);

            await RunTest("Cancelled-voyage cancel path cancels only matching auto-rescue and leaves unrelated vessel missions untouched", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_scope", "usr_auto_scope").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_scope", "usr_auto_scope").ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Cancelled voyage")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Cancelled,
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-1),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
                }).ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge failed after voyage cancellation").ConfigureAwait(false);
                failed.VoyageId = voyage.Id;
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                Mission matchingRescue = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    ParentMissionId = failed.Id,
                    Title = "Rescue 1: Failed mission",
                    Description = "Autonomous rescue mission. <!-- ARMADA:AUTO-RESCUE -->",
                    Status = MissionStatusEnum.Pending
                }).ConfigureAwait(false);

                // Same vessel + same parent, but NOT an auto-rescue (no marker) -- must be left alone.
                Mission childNonRescue = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    ParentMissionId = failed.Id,
                    Title = "Follow-up work",
                    Description = "Regular child mission, not a rescue.",
                    Status = MissionStatusEnum.InProgress
                }).ConfigureAwait(false);

                // Unrelated mission on the same vessel -- must be left alone.
                Mission unrelated = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Title = "Unrelated mission",
                    Description = "Independent work on the same vessel.",
                    Status = MissionStatusEnum.InProgress
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                Mission? cancelled = await testDb.Driver.Missions.ReadAsync(matchingRescue.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Cancelled, cancelled!.Status, "Matching auto-rescue should be cancelled.");

                Mission? child = await testDb.Driver.Missions.ReadAsync(childNonRescue.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.InProgress, child!.Status, "Non-rescue child of the failed mission must not be cancelled.");

                Mission? other = await testDb.Driver.Missions.ReadAsync(unrelated.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.InProgress, other!.Status, "Unrelated vessel mission must not be cancelled.");
            }).ConfigureAwait(false);

            await RunTest("Sweep sends one bounded Mail nudge to quiet live captain", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_nudge", "usr_auto_nudge").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_nudge", "usr_auto_nudge").ConfigureAwait(false);
                Mission mission = new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Title = "Quiet mission",
                    Description = "Mission with no recent output",
                    Status = MissionStatusEnum.InProgress,
                    StartedUtc = DateTime.UtcNow.AddMinutes(-30),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-30)
                };
                mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                Captain captain = new Captain
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Name = "quiet-captain",
                    State = CaptainStateEnum.Working,
                    CurrentMissionId = mission.Id,
                    LastHeartbeatUtc = DateTime.UtcNow.AddMinutes(-10)
                };
                captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                ArmadaSettings settings = new ArmadaSettings
                {
                    StallThresholdMinutes = 10,
                    AutonomousRecovery = new AutonomousRecoverySettings
                    {
                        SendStallMailNudges = true,
                        DispatchRescueMissions = true,
                        StallMailNudgeThresholdRatio = 0.5,
                        StallMailNudgeCooldownMinutes = 30
                    }
                };
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    new IncidentService(testDb.Driver),
                    new RunbookService(testDb.Driver, new LoggingModule()),
                    settings);

                await orchestrator.SweepAsync().ConfigureAwait(false);
                await orchestrator.SweepAsync().ConfigureAwait(false);

                EnumerationResult<Signal> signals = await testDb.Driver.Signals.EnumerateAsync(vessel.TenantId!, new EnumerationQuery
                {
                    SignalType = SignalTypeEnum.Mail.ToString(),
                    ToCaptainId = captain.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);

                AssertEqual(1, signals.Objects.Count);
                AssertContains("ARMADA_AUTO_NUDGE", signals.Objects[0].Payload ?? "", "Expected autonomous nudge marker.");
            }).ConfigureAwait(false);
        }

        private static AutonomousRecoveryOrchestrator CreateOrchestrator(
            DatabaseDriver database,
            RecordingAdmiralService admiral,
            IncidentService incidents,
            RunbookService runbooks,
            ArmadaSettings? settings = null)
        {
            return new AutonomousRecoveryOrchestrator(
                database,
                admiral,
                incidents,
                runbooks,
                settings ?? new ArmadaSettings(),
                new LoggingModule());
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb, string tenantId, string userId)
        {
            Vessel vessel = new Vessel
            {
                TenantId = tenantId,
                UserId = userId,
                Name = "Auto Recovery Vessel",
                RepoUrl = "file:///tmp/auto-recovery.git",
                LocalPath = "C:\\tmp\\auto-recovery",
                WorkingDirectory = "C:\\tmp\\auto-recovery",
                DefaultBranch = "main"
            };
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateFailedMissionAsync(TestDatabase testDb, Vessel vessel, string failureReason)
        {
            Mission mission = new Mission
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                VesselId = vessel.Id,
                Title = "Failed mission",
                Description = "Original mission description",
                Status = MissionStatusEnum.Failed,
                FailureReason = failureReason,
                CompletedUtc = DateTime.UtcNow.AddMinutes(-1),
                LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
            };

            return await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task EnsureTenantAndUserAsync(TestDatabase testDb, string tenantId, string userId)
        {
            TenantMetadata? existingTenant = await testDb.Driver.Tenants.ReadAsync(tenantId).ConfigureAwait(false);
            if (existingTenant == null)
            {
                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata
                {
                    Id = tenantId,
                    Name = tenantId
                }).ConfigureAwait(false);
            }

            UserMaster? existingUser = await testDb.Driver.Users.ReadByIdAsync(userId).ConfigureAwait(false);
            if (existingUser == null)
            {
                await testDb.Driver.Users.CreateAsync(new UserMaster
                {
                    Id = userId,
                    TenantId = tenantId,
                    Email = userId + "@armada.test",
                    PasswordSha256 = UserMaster.ComputePasswordHash("password"),
                    IsTenantAdmin = true
                }).ConfigureAwait(false);
            }
        }

        private sealed class RecordingAdmiralService : IAdmiralService
        {
            private readonly DatabaseDriver _Database;

            public RecordingAdmiralService(DatabaseDriver database)
            {
                _Database = database;
            }

            public List<Mission> DispatchedMissions { get; } = new List<Mission>();

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

            public async Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
            {
                Mission created = await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                DispatchedMissions.Add(created);
                return created;
            }

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
    }
}
