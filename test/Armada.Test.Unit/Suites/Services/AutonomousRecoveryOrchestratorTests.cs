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
            await RunTest("IsClaudeThinkingBlockFailure ThinkingBlocks Returns True", () =>
            {
                AssertTrue(
                    AutonomousRecoveryOrchestrator.IsClaudeThinkingBlockFailure(ThinkingBlockFailure("thinking")),
                    "thinking block mutation failures should be detected.");
                AssertTrue(
                    AutonomousRecoveryOrchestrator.IsClaudeThinkingBlockFailure(ThinkingBlockFailure("redacted_thinking")),
                    "redacted_thinking block mutation failures should be detected.");
            }).ConfigureAwait(false);

            await RunTest("IsClaudeThinkingBlockFailure UnrelatedFailures Returns False", () =>
            {
                AssertFalse(
                    AutonomousRecoveryOrchestrator.IsClaudeThinkingBlockFailure("API Error: 400 invalid_request_error: model does not exist"),
                    "Unrelated API 400 failures should not match.");
                AssertFalse(
                    AutonomousRecoveryOrchestrator.IsClaudeThinkingBlockFailure("Agent process exited with code 1"),
                    "Ordinary process failures should not match.");
            }).ConfigureAwait(false);

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

            await RunTest("Claude thinking block failure disables extended thinking on rescue captain", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_claude_thinking", "usr_auto_claude_thinking").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_claude_thinking", "usr_auto_claude_thinking").ConfigureAwait(false);
                Captain failedCaptain = await CreateCaptainAsync(testDb, vessel, "failed-claude", AgentRuntimeEnum.ClaudeCode).ConfigureAwait(false);
                Captain rescueCaptain = await CreateCaptainAsync(testDb, vessel, "rescue-claude", AgentRuntimeEnum.ClaudeCode).ConfigureAwait(false);
                rescueCaptain.RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new CaptainOptions
                {
                    ReasoningEffort = "high"
                });
                await testDb.Driver.Captains.UpdateAsync(rescueCaptain).ConfigureAwait(false);

                Mission failed = await CreateFailedMissionAsync(testDb, vessel, ThinkingBlockFailure("thinking")).ConfigureAwait(false);
                failed.CaptainId = failedCaptain.Id;
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver)
                {
                    AssignedRescueCaptainId = rescueCaptain.Id
                };
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count);
                Captain? updatedRescueCaptain = await testDb.Driver.Captains.ReadAsync(rescueCaptain.Id).ConfigureAwait(false);
                AssertTrue(updatedRescueCaptain != null, "Expected rescue captain to remain readable.");
                AssertTrue(CaptainRuntimeOptions.GetDisableExtendedThinking(updatedRescueCaptain), "Expected disable-extended-thinking flag on rescue captain.");
                AssertEqual("high", CaptainRuntimeOptions.GetReasoningEffort(updatedRescueCaptain), "Existing reasoning effort should be preserved.");
            }).ConfigureAwait(false);

            await RunTest("Claude thinking block failure on non Claude captain leaves rescue option unchanged", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_codex_thinking", "usr_auto_codex_thinking").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_codex_thinking", "usr_auto_codex_thinking").ConfigureAwait(false);
                Captain failedCaptain = await CreateCaptainAsync(testDb, vessel, "failed-codex", AgentRuntimeEnum.Codex).ConfigureAwait(false);
                Captain rescueCaptain = await CreateCaptainAsync(testDb, vessel, "rescue-claude-negative", AgentRuntimeEnum.ClaudeCode).ConfigureAwait(false);

                Mission failed = await CreateFailedMissionAsync(testDb, vessel, ThinkingBlockFailure("redacted_thinking")).ConfigureAwait(false);
                failed.CaptainId = failedCaptain.Id;
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver)
                {
                    AssignedRescueCaptainId = rescueCaptain.Id
                };
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count);
                Captain? updatedRescueCaptain = await testDb.Driver.Captains.ReadAsync(rescueCaptain.Id).ConfigureAwait(false);
                AssertTrue(updatedRescueCaptain != null, "Expected rescue captain to remain readable.");
                AssertFalse(CaptainRuntimeOptions.GetDisableExtendedThinking(updatedRescueCaptain), "Non-Claude failed captains should not mutate the rescue captain option.");
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

            await RunTest("Cancelled-voyage failure marks handled, cancels rescue, and persists no suppression event", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_no_event", "usr_auto_no_event").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_no_event", "usr_auto_no_event").ConfigureAwait(false);
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

                AssertEqual(0, admiral.DispatchedMissions.Count, "Cancelled-voyage failures must not dispatch a rescue.");
                Mission? rescue = await testDb.Driver.Missions.ReadAsync(existingRescue.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Cancelled, rescue!.Status, "Active auto-rescue under a cancelled voyage should be cancelled.");
                Mission? original = await testDb.Driver.Missions.ReadAsync(failed.Id).ConfigureAwait(false);
                AssertTrue(original!.LastRecoveryActionUtc.HasValue, "Cancelled-voyage suppression should mark the mission recovery-handled.");
                AssertTrue(original.RecoveryAttempts >= 1, "Suppression should record a recovery attempt.");

                List<ArmadaEvent> missionEvents = await testDb.Driver.Events.EnumerateByMissionAsync(failed.Id, 100).ConfigureAwait(false);
                int suppressionEvents = missionEvents.Count(item => item.EventType == "autonomous_recovery.suppressed_cancelled_voyage");
                AssertEqual(0, suppressionEvents, "Suppression must not persist autonomous_recovery.suppressed_cancelled_voyage events.");
            }).ConfigureAwait(false);

            await RunTest("Repeated cancelled-voyage outcome does not re-suppress or duplicate recovery work", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_repeat", "usr_auto_repeat").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_repeat", "usr_auto_repeat").ConfigureAwait(false);
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

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);
                Mission? afterFirst = await testDb.Driver.Missions.ReadAsync(failed.Id).ConfigureAwait(false);
                int attemptsAfterFirst = afterFirst!.RecoveryAttempts;

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count, "Repeated suppression must never dispatch a rescue.");
                Mission? afterSecond = await testDb.Driver.Missions.ReadAsync(failed.Id).ConfigureAwait(false);
                AssertEqual(attemptsAfterFirst, afterSecond!.RecoveryAttempts, "Second pass must not re-mark the already-handled mission.");

                List<ArmadaEvent> missionEvents = await testDb.Driver.Events.EnumerateByMissionAsync(failed.Id, 100).ConfigureAwait(false);
                int suppressionEvents = missionEvents.Count(item => item.EventType == "autonomous_recovery.suppressed_cancelled_voyage");
                AssertEqual(0, suppressionEvents, "Repeated suppression must persist zero suppression events.");
            }).ConfigureAwait(false);

            await RunTest("Sweep excludes terminal-voyage candidates and still processes a non-terminal failure", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_sweep_filter", "usr_auto_sweep_filter").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_sweep_filter", "usr_auto_sweep_filter").ConfigureAwait(false);

                Voyage cancelledVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Cancelled voyage")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Cancelled,
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-1),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
                }).ConfigureAwait(false);
                Mission terminalFailed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                terminalFailed.VoyageId = cancelledVoyage.Id;
                await testDb.Driver.Missions.UpdateAsync(terminalFailed).ConfigureAwait(false);

                Voyage activeVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Active voyage")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.InProgress,
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
                }).ConfigureAwait(false);
                Mission liveFailed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                liveFailed.VoyageId = activeVoyage.Id;
                await testDb.Driver.Missions.UpdateAsync(liveFailed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "Only the non-terminal failure should be processed by the sweep.");
                AssertEqual(liveFailed.Id, admiral.DispatchedMissions[0].ParentMissionId, "Rescue should belong to the non-terminal failure.");

                Mission? terminalAfter = await testDb.Driver.Missions.ReadAsync(terminalFailed.Id).ConfigureAwait(false);
                AssertFalse(terminalAfter!.LastRecoveryActionUtc.HasValue, "Terminal-voyage candidate must be excluded from sweep selection.");

                Mission? liveAfter = await testDb.Driver.Missions.ReadAsync(liveFailed.Id).ConfigureAwait(false);
                AssertTrue(liveAfter!.LastRecoveryActionUtc.HasValue, "Non-terminal failure should be processed by the sweep.");
            }).ConfigureAwait(false);

            await RunTest("Sweep excludes a Complete-voyage failed candidate while processing a non-terminal failure", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_sweep_complete", "usr_auto_sweep_complete").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_sweep_complete", "usr_auto_sweep_complete").ConfigureAwait(false);

                Voyage completeVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Complete voyage")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Complete,
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-1),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
                }).ConfigureAwait(false);
                Mission completedVoyageFailed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                completedVoyageFailed.VoyageId = completeVoyage.Id;
                await testDb.Driver.Missions.UpdateAsync(completedVoyageFailed).ConfigureAwait(false);

                Voyage activeVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Active voyage")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.InProgress,
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
                }).ConfigureAwait(false);
                Mission liveFailed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                liveFailed.VoyageId = activeVoyage.Id;
                await testDb.Driver.Missions.UpdateAsync(liveFailed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "Complete-voyage failures must be excluded; only the non-terminal failure should dispatch.");
                AssertEqual(liveFailed.Id, admiral.DispatchedMissions[0].ParentMissionId, "Rescue should belong to the non-terminal failure.");

                Mission? completedAfter = await testDb.Driver.Missions.ReadAsync(completedVoyageFailed.Id).ConfigureAwait(false);
                AssertFalse(completedAfter!.LastRecoveryActionUtc.HasValue, "Complete-voyage candidate must be excluded from sweep selection.");

                Mission? liveAfter = await testDb.Driver.Missions.ReadAsync(liveFailed.Id).ConfigureAwait(false);
                AssertTrue(liveAfter!.LastRecoveryActionUtc.HasValue, "Non-terminal failure should be processed by the sweep.");
            }).ConfigureAwait(false);

            await RunTest("Sweep excludes a terminal-voyage LandingFailed candidate before policy application", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_sweep_landing", "usr_auto_sweep_landing").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_sweep_landing", "usr_auto_sweep_landing").ConfigureAwait(false);

                Voyage cancelledVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Cancelled voyage")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Cancelled,
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-1),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
                }).ConfigureAwait(false);
                Mission terminalLandingFailed = await CreateFailedMissionAsync(testDb, vessel, "Local merge failed with conflicts").ConfigureAwait(false);
                terminalLandingFailed.Status = MissionStatusEnum.LandingFailed;
                terminalLandingFailed.VoyageId = cancelledVoyage.Id;
                await testDb.Driver.Missions.UpdateAsync(terminalLandingFailed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count, "Terminal-voyage LandingFailed missions must not dispatch.");

                Mission? after = await testDb.Driver.Missions.ReadAsync(terminalLandingFailed.Id).ConfigureAwait(false);
                AssertFalse(after!.LastRecoveryActionUtc.HasValue, "Terminal-voyage LandingFailed candidate must be excluded before policy application.");

                AuthContext auth = AuthContext.Authenticated("ten_auto_sweep_landing", "usr_auto_sweep_landing", false, true, "UnitTest");
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = terminalLandingFailed.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(0, incidentPage.Objects.Count, "Excluded terminal-voyage candidate must not open an incident.");
            }).ConfigureAwait(false);

            await RunTest("Sweep processes a failed mission with no parent voyage", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_sweep_novoyage", "usr_auto_sweep_novoyage").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_sweep_novoyage", "usr_auto_sweep_novoyage").ConfigureAwait(false);
                // No VoyageId assigned -- the terminal-voyage filter must not exclude voyage-less candidates.
                Mission orphanFailed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "A voyage-less failure must still be processed by the sweep.");
                AssertEqual(orphanFailed.Id, admiral.DispatchedMissions[0].ParentMissionId, "Rescue should belong to the voyage-less failure.");

                Mission? after = await testDb.Driver.Missions.ReadAsync(orphanFailed.Id).ConfigureAwait(false);
                AssertTrue(after!.LastRecoveryActionUtc.HasValue, "Voyage-less failure should be marked processed.");
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

            await RunTest("Judge-stage failure inlines review feedback into Worker rescue brief", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.AgentOutput = "DISTINCTIVE_REVIEW_MARKER_42 the null guard on line 88 is missing.";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                List<Mission> vesselMissions = await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
                Mission? rescue = vesselMissions.FirstOrDefault(item => item.ParentMissionId == failed.Id);
                AssertTrue(rescue != null, "Expected a linked rescue mission.");
                AssertEqual("Worker", rescue!.Persona, "Judge-stage failures should dispatch Worker rescue missions.");
                AssertContains("Review feedback from failed Judge stage", rescue!.Description ?? "", "Rescue brief should carry the review feedback header.");
                AssertContains("address these findings with corrective code changes", rescue!.Description ?? "", "Rescue brief should instruct corrective changes.");
                AssertContains("DISTINCTIVE_REVIEW_MARKER_42", rescue!.Description ?? "", "Rescue brief should inline the parent review output.");
            }).ConfigureAwait(false);

            await RunTest("Worker-stage failure rescue omits the review feedback section", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                failed.Persona = "Worker";
                failed.AgentOutput = "build output that should not be inlined";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                List<Mission> vesselMissions = await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
                Mission? rescue = vesselMissions.FirstOrDefault(item => item.ParentMissionId == failed.Id);
                AssertTrue(rescue != null, "Expected a linked rescue mission.");
                AssertEqual("Worker", rescue!.Persona, "Worker-stage failures should keep the Worker persona.");
                AssertFalse((rescue!.Description ?? "").Contains("Review feedback from failed", StringComparison.Ordinal), "Non-review-stage rescue should not carry the review feedback section.");
            }).ConfigureAwait(false);

            await RunTest("Judge-stage failure with no agent output uses fallback review wording", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.AgentOutput = null;
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                List<Mission> vesselMissions = await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
                Mission? rescue = vesselMissions.FirstOrDefault(item => item.ParentMissionId == failed.Id);
                AssertTrue(rescue != null, "Expected a linked rescue mission even without agent output.");
                AssertEqual("Worker", rescue!.Persona, "Judge-stage failures should dispatch Worker rescue missions.");
                AssertContains("Review feedback from failed Judge stage", rescue!.Description ?? "", "Rescue brief should carry the review feedback header.");
                AssertContains("(no detailed review output recorded)", rescue!.Description ?? "", "Rescue brief should use the fallback wording when no agent output is recorded.");
            }).ConfigureAwait(false);

            await RunTest("TestEngineer-stage failure remaps to Worker and names the stage in the feedback header", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "TestEngineer verdict: coverage gap").ConfigureAwait(false);
                failed.Persona = "TestEngineer";
                failed.AgentOutput = "DISTINCTIVE_TE_MARKER_77 missing negative-path coverage for the cancellation branch.";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                List<Mission> vesselMissions = await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
                Mission? rescue = vesselMissions.FirstOrDefault(item => item.ParentMissionId == failed.Id);
                AssertTrue(rescue != null, "Expected a linked rescue mission.");
                AssertEqual("Worker", rescue!.Persona, "TestEngineer-stage failures should dispatch Worker rescue missions.");
                AssertContains("Review feedback from failed TestEngineer stage", rescue!.Description ?? "", "Rescue header should name the originating reviewer stage, not hardcode Judge.");
                AssertContains("DISTINCTIVE_TE_MARKER_77", rescue!.Description ?? "", "Rescue brief should inline the parent reviewer output.");
            }).ConfigureAwait(false);

            await RunTest("Suffix-based reviewer persona remaps to Worker and inlines feedback", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "SecurityReviewer verdict: rejected").ConfigureAwait(false);

                // "SecurityReviewer" is NOT in the explicit reviewer table; it matches only the *reviewer suffix branch.
                failed.Persona = "SecurityReviewer";
                failed.AgentOutput = "DISTINCTIVE_SUFFIX_MARKER_31 unsanitized tenant id reaches the query builder.";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                List<Mission> vesselMissions = await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
                Mission? rescue = vesselMissions.FirstOrDefault(item => item.ParentMissionId == failed.Id);
                AssertTrue(rescue != null, "Expected a linked rescue mission.");
                AssertEqual("Worker", rescue!.Persona, "Suffix-matched *reviewer personas should remap to Worker.");
                AssertContains("Review feedback from failed SecurityReviewer stage", rescue!.Description ?? "", "Rescue header should name the suffix-matched reviewer stage.");
                AssertContains("DISTINCTIVE_SUFFIX_MARKER_31", rescue!.Description ?? "", "Rescue brief should inline feedback for suffix-matched reviewers.");
            }).ConfigureAwait(false);

            await RunTest("Judge-stage failure with whitespace-only agent output uses fallback wording", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                failed.Persona = "Judge";

                // Whitespace-only (not null) output must still take the IsNullOrWhiteSpace fallback branch.
                failed.AgentOutput = "   \t  ";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                List<Mission> vesselMissions = await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
                Mission? rescue = vesselMissions.FirstOrDefault(item => item.ParentMissionId == failed.Id);
                AssertTrue(rescue != null, "Expected a linked rescue mission even with whitespace-only agent output.");
                AssertEqual("Worker", rescue!.Persona, "Judge-stage failures should dispatch Worker rescue missions.");
                AssertContains("(no detailed review output recorded)", rescue!.Description ?? "", "Whitespace-only output should take the same fallback path as null.");
            }).ConfigureAwait(false);

            await RunTest("Judge-stage failure truncates oversized review output to the feedback cap", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                failed.Persona = "Judge";

                // Head marker survives; tail marker sits well past the 6000-char cap and must be dropped.
                failed.AgentOutput = "REVIEW_HEAD_MARKER " + new string('x', 6200) + " REVIEW_TAIL_MARKER";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                List<Mission> vesselMissions = await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
                Mission? rescue = vesselMissions.FirstOrDefault(item => item.ParentMissionId == failed.Id);
                AssertTrue(rescue != null, "Expected a linked rescue mission.");
                AssertContains("Review feedback from failed Judge stage", rescue!.Description ?? "", "Rescue brief should carry the review feedback header.");
                AssertContains("REVIEW_HEAD_MARKER", rescue!.Description ?? "", "The beginning of oversized review output should survive truncation.");
                AssertFalse((rescue!.Description ?? "").Contains("REVIEW_TAIL_MARKER", StringComparison.Ordinal), "Review output beyond the 6000-char cap should be truncated away.");
                AssertContains("...", rescue!.Description ?? "", "Truncated review output should carry the ellipsis marker.");
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

        private static async Task<Captain> CreateCaptainAsync(TestDatabase testDb, Vessel vessel, string name, AgentRuntimeEnum runtime)
        {
            Captain captain = new Captain(name, runtime)
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId
            };

            return await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        private static string ThinkingBlockFailure(string blockName)
        {
            return "API Error: 400 {\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"messages.2.content.1: `" +
                blockName +
                "` blocks cannot be modified\"}}";
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

            public string? AssignedRescueCaptainId { get; set; }

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
                if (!String.IsNullOrWhiteSpace(AssignedRescueCaptainId))
                    mission.CaptainId = AssignedRescueCaptainId;

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
