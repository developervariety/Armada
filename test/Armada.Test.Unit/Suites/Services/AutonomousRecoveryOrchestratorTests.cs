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

            await RunTest("ReviewerFeedback_JudgeStageFailure_InlinedIntoWorkerRescueBrief", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_feedback", "usr_auto_feedback").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_feedback", "usr_auto_feedback").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.ReviewComment = "The fix is missing a regression test for the null-branch case; add coverage before resubmitting.";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "Reviewer-stage failure should dispatch one rescue.");
                Mission rescue = admiral.DispatchedMissions[0];
                AssertEqual("Worker", rescue.Persona, "Reviewer-stage failures must dispatch a Worker rescue.");
                AssertContains("Reviewer feedback to address:", rescue.Description ?? "", "Rescue brief must label the inlined reviewer feedback.");
                AssertContains(failed.ReviewComment!, rescue.Description ?? "", "Rescue brief must inline the parent's review feedback verbatim.");
            }).ConfigureAwait(false);

            await RunTest("ReviseRetestRejudge_JudgeFailure_ChainsReJudgeOntoWorkerRevisionBeforeLanding", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_loop", "usr_auto_loop").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_loop", "usr_auto_loop").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.ReviewComment = "Add a regression test for the null-branch case before resubmitting.";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "Exactly one Worker revision should be dispatched as the loop root.");
                Mission worker = admiral.DispatchedMissions[0];
                AssertEqual("Worker", worker.Persona, "The dispatched root must be a Worker revision.");
                AssertEqual(failed.Id, worker.ParentMissionId, "The Worker revision should link back to the failed reviewer mission.");
                AssertTrue(!String.IsNullOrEmpty(worker.VoyageId), "The Worker revision must run inside a dedicated rescue voyage so handoff can chain stages.");
                AssertEqual(1, worker.RecoveryAttempts, "The Worker revision should carry the recovery budget forward to bound the loop.");

                List<Mission> loopMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(worker.VoyageId!).ConfigureAwait(false);
                Mission? judge = loopMissions.FirstOrDefault(item =>
                    String.Equals(item.Persona, "Judge", StringComparison.Ordinal) &&
                    item.DependsOnMissionId == worker.Id);
                AssertTrue(judge != null, "A re-Judge stage must be chained onto the Worker revision before it can land.");
                AssertEqual(MissionStatusEnum.Pending, judge!.Status, "The re-Judge stage should wait on the revision via the pipeline handoff.");
                AssertContains("ARMADA:AUTO-RESCUE", judge.Description ?? "", "The re-Judge stage should be marked as autonomous rescue work.");
                AssertEqual(1, judge.RecoveryAttempts, "The re-Judge stage should also carry the recovery budget so a repeat rejection is bounded.");
                AssertTrue(String.IsNullOrEmpty(judge.ParentMissionId), "The re-Judge stage is a pipeline dependent, not a direct rescue of the original failure.");
            }).ConfigureAwait(false);

            await RunTest("ReviseRetestRejudge_BudgetExhausted_OpensHighIncidentWithoutDispatch", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_exhausted", "usr_auto_exhausted").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_exhausted", "usr_auto_exhausted").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.ReviewComment = "Still missing negative-path coverage.";
                // A re-Judge stage that fails again arrives with the recovery budget already spent
                // (default MaxMissionRecoveryAttempts = 1). The bounded loop must stop here.
                failed.RecoveryAttempts = 1;
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count, "An exhausted recovery budget must not dispatch another revision.");

                AuthContext auth = AuthContext.Authenticated("ten_auto_exhausted", "usr_auto_exhausted", false, true, "UnitTest");
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = failed.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, incidentPage.Objects.Count, "Exhaustion should leave exactly one incident open.");
                AssertEqual(IncidentSeverityEnum.High, incidentPage.Objects[0].Severity, "The incident should escalate to High only after the bounded loop is exhausted.");
            }).ConfigureAwait(false);

            await RunTest("FindSuspectNoOpRescues_CompleteRescueAtTargetHead_FlaggedAndAdvancedExcluded", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_backfill", "usr_auto_backfill").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_backfill", "usr_auto_backfill").ConfigureAwait(false);
                Mission parent = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);

                string targetHead = "1111111111111111111111111111111111111111";

                Mission suspect = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    ParentMissionId = parent.Id,
                    Title = "Rescue 1: Failed mission",
                    Description = "Autonomous rescue mission. <!-- ARMADA:AUTO-RESCUE -->",
                    Status = MissionStatusEnum.Complete,
                    CommitHash = targetHead
                }).ConfigureAwait(false);

                Mission advanced = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    ParentMissionId = parent.Id,
                    Title = "Rescue 2: Failed mission",
                    Description = "Autonomous rescue mission. <!-- ARMADA:AUTO-RESCUE -->",
                    Status = MissionStatusEnum.Complete,
                    CommitHash = "2222222222222222222222222222222222222222"
                }).ConfigureAwait(false);

                // Non-rescue mission at the same head must be excluded (no auto-rescue marker).
                Mission nonRescue = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Title = "Regular mission",
                    Description = "Ordinary work, not a rescue.",
                    Status = MissionStatusEnum.Complete,
                    CommitHash = targetHead
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                List<Mission> suspects = await orchestrator.FindSuspectNoOpRescueMissionsAsync(vessel.Id, targetHead).ConfigureAwait(false);

                AssertEqual(1, suspects.Count, "Only the Complete rescue whose commit equals the target head should be flagged.");
                AssertEqual(suspect.Id, suspects[0].Id, "The flagged suspect should be the no-op rescue.");
                AssertFalse(suspects.Any(item => item.Id == advanced.Id), "A rescue that advanced the branch must be excluded.");
                AssertFalse(suspects.Any(item => item.Id == nonRescue.Id), "A non-rescue mission must be excluded.");
            }).ConfigureAwait(false);

            // Guard 1 negative path: when the failed reviewer mission carries no review
            // feedback, the rescue brief must not emit an empty "Reviewer feedback" section.
            await RunTest("ReviewerFeedback_NoReviewComment_FeedbackSectionOmittedFromRescueBrief", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_nofeedback", "usr_auto_nofeedback").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_nofeedback", "usr_auto_nofeedback").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                failed.Persona = "Judge";
                // ReviewComment intentionally left null -- the inlined-feedback block must be skipped.
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "A recoverable reviewer-stage failure should dispatch one rescue.");
                Mission rescue = admiral.DispatchedMissions[0];
                AssertFalse((rescue.Description ?? "").Contains("Reviewer feedback to address:", StringComparison.Ordinal),
                    "With no ReviewComment, the rescue brief must not include the reviewer-feedback section.");
            }).ConfigureAwait(false);

            // Guard 4 edge: the backfill detector keys on the rescue marker (the stamp
            // BuildRescueDescription writes), so a marker-only rescue with no ParentMissionId
            // must still be flagged.
            await RunTest("FindSuspectNoOpRescues_MarkerOnlyRescueNoParentLink_Flagged", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_markeronly", "usr_auto_markeronly").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_markeronly", "usr_auto_markeronly").ConfigureAwait(false);
                string targetHead = "3333333333333333333333333333333333333333";

                Mission markerOnly = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Title = "Rescue 1: Failed mission",
                    Description = "Autonomous rescue mission. <!-- ARMADA:AUTO-RESCUE -->",
                    Status = MissionStatusEnum.Complete,
                    CommitHash = targetHead
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                List<Mission> suspects = await orchestrator.FindSuspectNoOpRescueMissionsAsync(vessel.Id, targetHead).ConfigureAwait(false);

                AssertEqual(1, suspects.Count, "A marker-stamped rescue with no parent link must still be flagged.");
                AssertEqual(markerOnly.Id, suspects[0].Id, "The flagged suspect should be the marker-only rescue.");
            }).ConfigureAwait(false);

            // Guard 4 edge: only Complete rescues are false-positive landings; a rescue that
            // ended Failed at the same head must not be reported for operator review.
            await RunTest("FindSuspectNoOpRescues_FailedRescueAtTargetHead_Excluded", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_failedrescue", "usr_auto_failedrescue").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_failedrescue", "usr_auto_failedrescue").ConfigureAwait(false);
                string targetHead = "4444444444444444444444444444444444444444";

                Mission failedRescue = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Title = "Rescue 1: Failed mission",
                    Description = "Autonomous rescue mission. <!-- ARMADA:AUTO-RESCUE -->",
                    Status = MissionStatusEnum.Failed,
                    CommitHash = targetHead
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                List<Mission> suspects = await orchestrator.FindSuspectNoOpRescueMissionsAsync(vessel.Id, targetHead).ConfigureAwait(false);

                AssertFalse(suspects.Any(item => item.Id == failedRescue.Id), "A Failed rescue must not be flagged as a false-positive landing.");
            }).ConfigureAwait(false);

            // Guard 4 guard clauses: blank inputs short-circuit to an empty result rather
            // than enumerating the vessel.
            await RunTest("FindSuspectNoOpRescues_BlankVesselOrTargetHead_ReturnsEmpty", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_blankargs", "usr_auto_blankargs").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_blankargs", "usr_auto_blankargs").ConfigureAwait(false);
                string targetHead = "5555555555555555555555555555555555555555";

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                List<Mission> blankVessel = await orchestrator.FindSuspectNoOpRescueMissionsAsync("   ", targetHead).ConfigureAwait(false);
                AssertEqual(0, blankVessel.Count, "A blank vessel id must yield no suspects.");

                List<Mission> blankHead = await orchestrator.FindSuspectNoOpRescueMissionsAsync(vessel.Id, "   ").ConfigureAwait(false);
                AssertEqual(0, blankHead.Count, "A blank target head must yield no suspects.");
            }).ConfigureAwait(false);

            // Guard 4 edge: commit-hash comparison is case-insensitive and trims surrounding
            // whitespace on the supplied head, so cosmetic differences still match.
            await RunTest("FindSuspectNoOpRescues_CommitHashCaseAndWhitespaceDiffer_StillFlagged", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_casehead", "usr_auto_casehead").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_casehead", "usr_auto_casehead").ConfigureAwait(false);

                Mission suspect = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Title = "Rescue 1: Failed mission",
                    Description = "Autonomous rescue mission. <!-- ARMADA:AUTO-RESCUE -->",
                    Status = MissionStatusEnum.Complete,
                    CommitHash = "ABCDEF1234567890ABCDEF1234567890ABCDEF12"
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                // Same hash, different case, plus surrounding whitespace the detector must trim.
                List<Mission> suspects = await orchestrator.FindSuspectNoOpRescueMissionsAsync(
                    vessel.Id, "  abcdef1234567890abcdef1234567890abcdef12  ").ConfigureAwait(false);

                AssertEqual(1, suspects.Count, "Case and whitespace differences must not hide a no-op rescue.");
                AssertEqual(suspect.Id, suspects[0].Id, "The flagged suspect should be the case-folded match.");
            }).ConfigureAwait(false);

            await RunTest("Sweep excludes auto-rescue candidate with ParentMissionId set", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_sweep_rescue_excl", "usr_sweep_rescue_excl").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_sweep_rescue_excl", "usr_sweep_rescue_excl").ConfigureAwait(false);

                // Plain failed mission -- the one that spawned the rescue; should still be processed.
                Mission originalFailed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);

                // Auto-rescue child: has ParentMissionId set (the marker used by the sweep exclusion),
                // and also carries the rescue description marker so Classify would block it.
                Mission rescueFailed = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    ParentMissionId = originalFailed.Id,
                    Title = "Rescue 1: Failed mission",
                    Description = "<!-- ARMADA:AUTO-RESCUE -->\nAutonomous rescue attempt 1 for failed mission " + originalFailed.Id + ".",
                    Status = MissionStatusEnum.Failed,
                    FailureReason = "Agent process exited with code 1",
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-2),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-2)
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                // The auto-rescue must be excluded from sweep processing.
                Mission? rescueAfter = await testDb.Driver.Missions.ReadAsync(rescueFailed.Id).ConfigureAwait(false);
                AssertFalse(rescueAfter!.LastRecoveryActionUtc.HasValue, "Auto-rescue with ParentMissionId must be excluded from sweep processing.");

                AuthContext auth = AuthContext.Authenticated("ten_sweep_rescue_excl", "usr_sweep_rescue_excl", false, true, "UnitTest");
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = rescueFailed.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(0, incidentPage.Objects.Count, "No incident must be created for the excluded auto-rescue.");

                // The original failed mission (no ParentMissionId) must still be processed.
                Mission? originalAfter = await testDb.Driver.Missions.ReadAsync(originalFailed.Id).ConfigureAwait(false);
                AssertTrue(originalAfter!.LastRecoveryActionUtc.HasValue, "Original failed mission without ParentMissionId must still be processed by the sweep.");
            }).ConfigureAwait(false);

            await RunTest("Sweep excludes voyage-less failed mission older than max age setting", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_sweep_aged_excl", "usr_sweep_aged_excl").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_sweep_aged_excl", "usr_sweep_aged_excl").ConfigureAwait(false);

                // Voyage-less mission older than the configured max age.
                Mission agedFailed = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Title = "Aged voyage-less failure",
                    Description = "Old non-rescue voyage-less failure",
                    Status = MissionStatusEnum.Failed,
                    FailureReason = "Agent process exited with code 1",
                    CompletedUtc = DateTime.UtcNow.AddHours(-3),
                    LastUpdateUtc = DateTime.UtcNow.AddHours(-3)
                }).ConfigureAwait(false);

                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousRecovery = new AutonomousRecoverySettings
                    {
                        RecoverySweepMaxFailedMissionAgeHours = 1
                    }
                };
                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks, settings);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count, "Aged voyage-less failed mission must not be processed by the sweep.");

                Mission? after = await testDb.Driver.Missions.ReadAsync(agedFailed.Id).ConfigureAwait(false);
                AssertFalse(after!.LastRecoveryActionUtc.HasValue, "Aged voyage-less candidate must be excluded from sweep selection.");
            }).ConfigureAwait(false);

            await RunTest("Sweep excludes a Failed-voyage failed candidate before policy application", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_sweep_failed_vyg", "usr_sweep_failed_vyg").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_sweep_failed_vyg", "usr_sweep_failed_vyg").ConfigureAwait(false);

                Voyage failedVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Failed voyage")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Failed,
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-1),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
                }).ConfigureAwait(false);

                Mission failedVoyageMission = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                failedVoyageMission.VoyageId = failedVoyage.Id;
                await testDb.Driver.Missions.UpdateAsync(failedVoyageMission).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count, "Failed-voyage failures must not be processed by the sweep.");

                Mission? after = await testDb.Driver.Missions.ReadAsync(failedVoyageMission.Id).ConfigureAwait(false);
                AssertFalse(after!.LastRecoveryActionUtc.HasValue, "Failed-voyage candidate must be excluded before policy application.");

                AuthContext auth = AuthContext.Authenticated("ten_sweep_failed_vyg", "usr_sweep_failed_vyg", false, true, "UnitTest");
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = failedVoyageMission.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(0, incidentPage.Objects.Count, "Excluded Failed-voyage candidate must not open an incident.");
            }).ConfigureAwait(false);

            await RunTest("PolicyBlock skips incident and closes existing for rescue_produced_no_commits auto-rescue", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_rescue_no_commits", "usr_rescue_no_commits").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_rescue_no_commits", "usr_rescue_no_commits").ConfigureAwait(false);

                Mission originalFailed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);

                // Auto-rescue mission deliberately failed with rescue_produced_no_commits.
                Mission noOpRescue = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    ParentMissionId = originalFailed.Id,
                    Title = "Rescue 1: Failed mission",
                    Description = "<!-- ARMADA:AUTO-RESCUE -->\nAutonomous rescue attempt 1 for failed mission " + originalFailed.Id + ".",
                    Status = MissionStatusEnum.Failed,
                    FailureReason = "rescue_produced_no_commits",
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-2),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-2)
                }).ConfigureAwait(false);

                // Pre-seed an open incident that the guard must close.
                AuthContext auth = AuthContext.Authenticated("ten_rescue_no_commits", "usr_rescue_no_commits", false, true, "UnitTest");
                IncidentService incidents = new IncidentService(testDb.Driver);
                Incident existingIncident = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                {
                    Title = "Pre-existing incident for rescue",
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.High,
                    MissionId = noOpRescue.Id
                }).ConfigureAwait(false);

                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(noOpRescue, false).ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count, "No rescue must be dispatched for a rescue_produced_no_commits auto-rescue.");

                // The pre-existing incident must be closed.
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = noOpRescue.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertTrue(incidentPage.Objects.All(item => item.Status == IncidentStatusEnum.Closed),
                    "Existing open incident for rescue_produced_no_commits must be closed by the guard.");

                // Policy must be marked blocked so subsequent sweeps short-circuit via IsAlreadyHandledAsync.
                Mission? rescueAfter = await testDb.Driver.Missions.ReadAsync(noOpRescue.Id).ConfigureAwait(false);
                AssertTrue(rescueAfter!.LastRecoveryActionUtc.HasValue, "Policy-blocked marker must be set to prevent repeated processing.");
            }).ConfigureAwait(false);

            await RunTest("Sweep still processes a recent voyage-less non-rescue failure despite age setting", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_sweep_recent_ok", "usr_sweep_recent_ok").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_sweep_recent_ok", "usr_sweep_recent_ok").ConfigureAwait(false);

                // Recent voyage-less failure: within the age window, no ParentMissionId. Must be processed.
                Mission recentFailed = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Title = "Recent recoverable failure",
                    Description = "Plain mission that failed recently",
                    Status = MissionStatusEnum.Failed,
                    FailureReason = "Agent process exited with code 1",
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-30),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-30)
                }).ConfigureAwait(false);

                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousRecovery = new AutonomousRecoverySettings
                    {
                        RecoverySweepMaxFailedMissionAgeHours = 2
                    }
                };
                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks, settings);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "A recent voyage-less non-rescue failure must still be processed by the sweep.");
                AssertEqual(recentFailed.Id, admiral.DispatchedMissions[0].ParentMissionId, "The dispatched rescue must target the recent failure.");

                Mission? after = await testDb.Driver.Missions.ReadAsync(recentFailed.Id).ConfigureAwait(false);
                AssertTrue(after!.LastRecoveryActionUtc.HasValue, "Recent voyage-less failure must be marked processed.");
            }).ConfigureAwait(false);

            // Pins the house-style clamp on the new age-gate setting: the getter must default
            // to 6, clamp negatives up to 0 (the documented gate-disabled sentinel), clamp
            // above the 168-hour (7-day) ceiling, and preserve in-range values verbatim.
            await RunTest("RecoverySweepMaxFailedMissionAgeHours clamps to [0,168] and defaults to 6", () =>
            {
                AutonomousRecoverySettings settings = new AutonomousRecoverySettings();
                AssertEqual(6, settings.RecoverySweepMaxFailedMissionAgeHours, "Default age gate must be 6 hours.");

                settings.RecoverySweepMaxFailedMissionAgeHours = -5;
                AssertEqual(0, settings.RecoverySweepMaxFailedMissionAgeHours, "A negative age must clamp up to 0 (gate disabled).");

                settings.RecoverySweepMaxFailedMissionAgeHours = 1000;
                AssertEqual(168, settings.RecoverySweepMaxFailedMissionAgeHours, "An out-of-range age must clamp down to the 168-hour ceiling.");

                settings.RecoverySweepMaxFailedMissionAgeHours = 0;
                AssertEqual(0, settings.RecoverySweepMaxFailedMissionAgeHours, "Zero must be accepted as the gate-disabled sentinel.");

                settings.RecoverySweepMaxFailedMissionAgeHours = 24;
                AssertEqual(24, settings.RecoverySweepMaxFailedMissionAgeHours, "An in-range value must be preserved verbatim.");
            }).ConfigureAwait(false);

            // No over-exclusion when the age gate is disabled (= 0): a voyage-less failure older
            // than any positive window must STILL be processed, exercising the > 0 false branch
            // of the sweep's age guard.
            await RunTest("Sweep processes aged voyage-less failure when age gate is disabled", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_sweep_gate_off", "usr_sweep_gate_off").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_sweep_gate_off", "usr_sweep_gate_off").ConfigureAwait(false);

                // Voyage-less failure 10 hours old -- older than the default 6h window, but the
                // gate is disabled so age must not exclude it.
                Mission agedFailed = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Title = "Aged voyage-less failure, gate disabled",
                    Description = "Old non-rescue voyage-less failure",
                    Status = MissionStatusEnum.Failed,
                    FailureReason = "Agent process exited with code 1",
                    CompletedUtc = DateTime.UtcNow.AddHours(-10),
                    LastUpdateUtc = DateTime.UtcNow.AddHours(-10)
                }).ConfigureAwait(false);

                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousRecovery = new AutonomousRecoverySettings
                    {
                        RecoverySweepMaxFailedMissionAgeHours = 0
                    }
                };
                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks, settings);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "A disabled age gate must not exclude an aged voyage-less failure.");
                AssertEqual(agedFailed.Id, admiral.DispatchedMissions[0].ParentMissionId, "The dispatched rescue must target the aged failure.");

                Mission? after = await testDb.Driver.Missions.ReadAsync(agedFailed.Id).ConfigureAwait(false);
                AssertTrue(after!.LastRecoveryActionUtc.HasValue, "Aged voyage-less failure must be processed when the gate is disabled.");
            }).ConfigureAwait(false);

            // Age fallback: when CompletedUtc is absent the sweep measures age by LastUpdateUtc
            // (the CompletedUtc ?? LastUpdateUtc branch). The persistence layer always stamps
            // LastUpdateUtc to "now" on write, so a null-CompletedUtc Failed candidate is treated
            // as fresh and must be processed -- the coalesce must not throw or mis-read null as old.
            await RunTest("Sweep age gate falls back to LastUpdateUtc when CompletedUtc is null", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_sweep_nocompleted", "usr_sweep_nocompleted").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_sweep_nocompleted", "usr_sweep_nocompleted").ConfigureAwait(false);

                // CompletedUtc deliberately left null; the age guard must coalesce to LastUpdateUtc
                // (stamped to now by the driver), keeping this fresh candidate eligible.
                Mission noCompleted = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Title = "Voyage-less failure without CompletedUtc",
                    Description = "Non-rescue voyage-less failure, no completion timestamp",
                    Status = MissionStatusEnum.Failed,
                    FailureReason = "Agent process exited with code 1"
                }).ConfigureAwait(false);
                AssertFalse(noCompleted.CompletedUtc.HasValue, "Pre-condition: the candidate must have no CompletedUtc.");

                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousRecovery = new AutonomousRecoverySettings
                    {
                        RecoverySweepMaxFailedMissionAgeHours = 1
                    }
                };
                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks, settings);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "A null-CompletedUtc candidate must coalesce to a fresh LastUpdateUtc and still be processed.");
                AssertEqual(noCompleted.Id, admiral.DispatchedMissions[0].ParentMissionId, "The dispatched rescue must target the null-CompletedUtc failure.");

                Mission? after = await testDb.Driver.Missions.ReadAsync(noCompleted.Id).ConfigureAwait(false);
                AssertTrue(after!.LastRecoveryActionUtc.HasValue, "Age fallback to LastUpdateUtc must keep the fresh candidate eligible.");
            }).ConfigureAwait(false);

            // No over-suppression: the incident-suppression guard is gated on IsAutoRescueMission.
            // A plain (non-rescue) mission whose FailureReason coincidentally equals
            // "rescue_produced_no_commits" must NOT be suppressed -- it still recovers normally.
            await RunTest("Sweep still recovers a non-rescue mission whose reason is rescue_produced_no_commits", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_sweep_nocommits_plain", "usr_sweep_nocommits_plain").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_sweep_nocommits_plain", "usr_sweep_nocommits_plain").ConfigureAwait(false);

                // Plain mission: no auto-rescue marker, no "Rescue:" title, no ParentMissionId.
                // The no-commits reason alone must not trip the auto-rescue suppression guard.
                Mission plainFailed = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Title = "Ordinary failed mission",
                    Description = "Not an autonomous rescue, just an ordinary failure.",
                    Status = MissionStatusEnum.Failed,
                    FailureReason = "rescue_produced_no_commits",
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-5),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-5)
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "A non-rescue mission must recover normally regardless of the coincidental reason string.");
                AssertEqual(plainFailed.Id, admiral.DispatchedMissions[0].ParentMissionId, "The dispatched rescue must target the ordinary failure.");

                // The suppression guard (which closes incidents) must NOT have fired; a normal
                // recovery incident must exist for the non-rescue failure.
                AuthContext auth = AuthContext.Authenticated("ten_sweep_nocommits_plain", "usr_sweep_nocommits_plain", false, true, "UnitTest");
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = plainFailed.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, incidentPage.Objects.Count, "A non-rescue failure must still open a recovery incident (no over-suppression).");
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
