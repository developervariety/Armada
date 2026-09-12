namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
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

            await RunTest("IsEnvironmentalFailure separates provisioning faults from stage defects", () =>
            {
                AssertTrue(
                    AutonomousRecoveryOrchestrator.IsEnvironmentalFailure(
                        "stage_base_missing: this stage was provisioned on branch 'x' which lacks its dependency's commit"),
                    "A missing stage base is a provisioning fault no captain can repair.");
                AssertTrue(
                    AutonomousRecoveryOrchestrator.IsEnvironmentalFailure(
                        "start_from_ref_unverified: Armada could not prove the rescue base"),
                    "An unverified rescue base is a provisioning fault no captain can repair.");
                AssertTrue(
                    AutonomousRecoveryOrchestrator.IsEnvironmentalFailure(
                        "Check failed: ECULINK_PORT_ROOT environment variable is not set"),
                    "A missing environment variable cannot be fixed by re-running the brief.");
                AssertFalse(
                    AutonomousRecoveryOrchestrator.IsEnvironmentalFailure("Agent process exited with code 1"),
                    "An ordinary process failure is still rescuable.");
                AssertFalse(
                    AutonomousRecoveryOrchestrator.IsEnvironmentalFailure("Judge verdict: NEEDS_REVISION"),
                    "A substantive rejection of the work is still rescuable.");
            }).ConfigureAwait(false);

            await RunTest("An environmental failure is routed to the operator instead of rescued", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_env_fault", "usr_env_fault").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_env_fault", "usr_env_fault").ConfigureAwait(false);

                // A rescue re-runs the same brief in the same environment. The replacement captain
                // hits the identical provisioning fault, so the rescue burns the recovery budget a
                // genuine defect would have needed and changes nothing.
                Mission failed = await CreateFailedMissionAsync(
                    testDb,
                    vessel,
                    "stage_base_missing: this stage was provisioned on a branch that lacks its dependency's commit. "
                    + "This is a provisioning fault, not a defect in the stage's own work.").ConfigureAwait(false);
                failed.Persona = "Judge";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                List<Mission> vesselMissions = await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
                Mission? rescue = vesselMissions.FirstOrDefault(item => item.ParentMissionId == failed.Id);
                AssertTrue(rescue == null, "An environmental fault must not dispatch a rescue that cannot fix it.");

                AuthContext auth = AuthContext.Authenticated("ten_env_fault", "usr_env_fault", false, true, "UnitTest");
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = failed.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, incidentPage.Objects.Count, "The operator still needs an incident to act on.");
                AssertEqual(
                    IncidentSeverityEnum.High,
                    incidentPage.Objects[0].Severity,
                    "A fault that stops autonomous recovery is High: it waits for a human.");
            }).ConfigureAwait(false);

            await RunTest("Recoverable failed mission creates incident, runbook execution, and rescue mission", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.CommitHash = "1111111111111111111111111111111111111111";
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
                AssertEqual(failed.CommitHash, rescue.StartFromRef, "The rescue must start from the reviewed mission tip.");
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

            await RunTest("ReadOnlyJudgeFailure_PreservesModeAndPersistsRecommendedImplementation", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_readonly", "usr_auto_readonly").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_readonly", "usr_auto_readonly").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.Mode = MissionModeEnum.Research;
                failed.ReviewComment = "Recommended implementation: add coverage for the null-branch case.";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count,
                    "A failed read-only Judge must not dispatch an Implementation rescue.");

                List<JudgeFollowUp> followUps = await testDb.Driver.JudgeFollowUps.EnumeratePendingAsync(vessel.Id).ConfigureAwait(false);
                AssertEqual(1, followUps.Count, "The recommended implementation must remain a durable follow-up item.");
                AssertEqual(failed.Id, followUps[0].JudgeMissionId, "The follow-up must link to the failed Judge mission.");
                AssertContains("Recommended implementation", followUps[0].SuggestedFollowUps ?? String.Empty,
                    "The durable follow-up must retain the recommended implementation.");

                AuthContext auth = AuthContext.Authenticated("ten_auto_readonly", "usr_auto_readonly", false, true, "UnitTest");
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = failed.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, incidentPage.Objects.Count);
                AssertContains("read-only mode Research", incidentPage.Objects[0].RecoveryNotes ?? String.Empty,
                    "Recovery notes must state the preserved mode.");
                AssertContains("audit-only", incidentPage.Objects[0].RecoveryNotes ?? String.Empty,
                    "Recovery notes must state the preserved read-only scope.");

                List<ArmadaEvent> missionEvents = await testDb.Driver.Events.EnumerateByMissionAsync(failed.Id, 100).ConfigureAwait(false);
                ArmadaEvent? preservedEvent = missionEvents.FirstOrDefault(item =>
                    String.Equals(item.EventType, "autonomous_recovery.read_only_preserved", StringComparison.Ordinal));
                AssertTrue(preservedEvent != null, "Recovery must emit a read-only preservation event.");
                AssertContains("Research", preservedEvent!.Message, "The preservation event must state the mode.");
                AssertContains("audit-only", preservedEvent.Message, "The preservation event must state the scope.");
            }).ConfigureAwait(false);

            await RunTest("AuditJudgeFailure_PreservesAuditModeWithoutRecommendation", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_audit", "usr_auto_audit").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_audit", "usr_auto_audit").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: FAIL").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.Mode = MissionModeEnum.Audit;
                failed.ReviewComment = null;
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count,
                    "A failed Audit Judge must remain read-only even when it has no recommended implementation.");

                List<JudgeFollowUp> followUps = await testDb.Driver.JudgeFollowUps.EnumeratePendingAsync(vessel.Id).ConfigureAwait(false);
                AssertEqual(1, followUps.Count, "The failed Audit Judge must still produce a linked durable review record.");
                AssertEqual(failed.Id, followUps[0].JudgeMissionId, "The review record must link to the failed Audit Judge.");
                AssertTrue(String.IsNullOrWhiteSpace(followUps[0].SuggestedFollowUps),
                    "Recovery must not invent implementation advice when the Judge supplied none.");

                AuthContext auth = AuthContext.Authenticated("ten_auto_audit", "usr_auto_audit", false, true, "UnitTest");
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = failed.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, incidentPage.Objects.Count);
                AssertContains("read-only mode Audit", incidentPage.Objects[0].RecoveryNotes ?? String.Empty,
                    "Recovery notes must identify the preserved Audit mode.");
                AssertContains("audit-only", incidentPage.Objects[0].RecoveryNotes ?? String.Empty,
                    "Recovery notes must retain the audit-only scope.");

                EnumerationResult<RunbookExecution> executionPage = await runbooks.EnumerateExecutionsAsync(auth, new RunbookExecutionQuery
                {
                    IncidentId = incidentPage.Objects[0].Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, executionPage.Objects.Count);
                AssertEqual("Audit", executionPage.Objects[0].ParameterValues["missionMode"],
                    "The recovery execution must record the preserved Audit mode.");
                AssertContains("Audit", executionPage.Objects[0].Notes ?? String.Empty,
                    "The completed recovery execution must retain the preserved Audit mode in its notes.");
                AssertContains("audit-only", executionPage.Objects[0].Notes ?? String.Empty,
                    "The completed recovery execution must retain the read-only scope in its notes.");
            }).ConfigureAwait(false);

            await RunTest("ReadOnlyJudgeFailure_ReusesExistingDurableFollowUp", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_followup", "usr_auto_followup").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_followup", "usr_auto_followup").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.Mode = MissionModeEnum.Research;
                failed.ReviewComment = "Recommended implementation: preserve the explicit scope.";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                LoggingModule logging = new LoggingModule();
                JudgeFollowUp existing = await new JudgeFollowUpService(testDb.Driver, logging)
                    .CaptureAsync(failed, "NEEDS_REVISION", failed.ReviewComment).ConfigureAwait(false);
                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, logging);
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                List<JudgeFollowUp> followUps = await testDb.Driver.JudgeFollowUps.EnumeratePendingAsync(vessel.Id).ConfigureAwait(false);
                AssertEqual(1, followUps.Count, "Recovery must not duplicate an existing durable follow-up for the same Judge.");
                AssertEqual(existing.Id, followUps[0].Id, "Recovery must retain the original durable follow-up record.");
                AssertEqual(0, admiral.DispatchedMissions.Count, "A pre-existing follow-up must not widen read-only recovery into a rescue.");
            }).ConfigureAwait(false);

            await RunTest("ReadOnlyNonJudgeReviewers_BlockWithoutCreatingJudgeFollowUps", async () =>
            {
                foreach ((string Persona, MissionModeEnum Mode) scenario in new[]
                {
                    ("PortingReferenceAnalyst", MissionModeEnum.Research),
                    ("TestEngineer", MissionModeEnum.Audit)
                })
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    string suffix = scenario.Persona.ToLowerInvariant();
                    await EnsureTenantAndUserAsync(testDb, "ten_auto_" + suffix, "usr_auto_" + suffix).ConfigureAwait(false);

                    Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_" + suffix, "usr_auto_" + suffix).ConfigureAwait(false);
                    Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Review stage failed without a Judge verdict.").ConfigureAwait(false);
                    failed.Persona = scenario.Persona;
                    failed.Mode = scenario.Mode;
                    failed.ReviewComment = "A finding that must remain attached to the review stage.";
                    await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                    IncidentService incidents = new IncidentService(testDb.Driver);
                    RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                    await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                    AssertEqual(0, admiral.DispatchedMissions.Count,
                        scenario.Persona + " must remain read-only and must not dispatch a rescue.");
                    List<JudgeFollowUp> followUps = await testDb.Driver.JudgeFollowUps
                        .EnumeratePendingAsync(vessel.Id).ConfigureAwait(false);
                    AssertEqual(0, followUps.Count,
                        scenario.Persona + " must not be recorded as a Judge follow-up.");

                    AuthContext auth = AuthContext.Authenticated(
                        "ten_auto_" + suffix,
                        "usr_auto_" + suffix,
                        false,
                        true,
                        "UnitTest");
                    EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                    {
                        MissionId = failed.Id,
                        PageNumber = 1,
                        PageSize = 10
                    }).ConfigureAwait(false);
                    AssertEqual(1, incidentPage.Objects.Count);
                    AssertContains(scenario.Mode.ToString(), incidentPage.Objects[0].RecoveryNotes ?? String.Empty);
                    AssertContains("audit-only", incidentPage.Objects[0].RecoveryNotes ?? String.Empty);

                    EnumerationResult<RunbookExecution> executionPage = await runbooks.EnumerateExecutionsAsync(auth, new RunbookExecutionQuery
                    {
                        IncidentId = incidentPage.Objects[0].Id,
                        PageNumber = 1,
                        PageSize = 10
                    }).ConfigureAwait(false);
                    AssertEqual(1, executionPage.Objects.Count);
                    AssertEqual(scenario.Mode.ToString(), executionPage.Objects[0].ParameterValues["missionMode"]);
                    AssertContains("audit-only", executionPage.Objects[0].Notes ?? String.Empty);
                }
            }).ConfigureAwait(false);

            await RunTest("ExistingRecoveryRunbook_GainsMissionModeWithoutLosingContent", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_legacy_runbook", "usr_auto_legacy_runbook").ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated(
                    "ten_auto_legacy_runbook",
                    "usr_auto_legacy_runbook",
                    false,
                    true,
                    "UnitTest");

                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                Runbook legacy = await runbooks.CreateAsync(auth, new RunbookUpsertRequest
                {
                    FileName = "system/mission-recovery.md",
                    Title = "Existing Recovery Runbook",
                    Description = "Keep this description.",
                    Active = false,
                    Parameters = new List<RunbookParameter>
                    {
                        new RunbookParameter { Name = "missionId", Label = "Mission ID", Required = true },
                        new RunbookParameter
                        {
                            Name = "missionMode",
                            Label = "Existing Mode Label",
                            Description = "Keep this parameter description.",
                            DefaultValue = "Research",
                            Required = false
                        }
                    },
                    Steps = new List<RunbookStep>
                    {
                        new RunbookStep { Title = "Existing step", Instructions = "Keep these instructions." }
                    },
                    OverviewMarkdown = "Keep this overview."
                }).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_legacy_runbook", "usr_auto_legacy_runbook").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: FAIL").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.Mode = MissionModeEnum.Audit;
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);
                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                Runbook? migrated = await runbooks.ReadAsync(auth, legacy.Id).ConfigureAwait(false);
                AssertTrue(migrated != null, "The existing recovery runbook must remain readable.");
                AssertEqual("Existing Recovery Runbook", migrated!.Title);
                AssertEqual("Keep this description.", migrated.Description);
                AssertEqual(false, migrated.Active);
                AssertEqual("Keep this overview.", migrated.OverviewMarkdown);
                AssertEqual(1, migrated.Steps.Count);
                AssertEqual("Existing step", migrated.Steps[0].Title);
                AssertEqual("Keep these instructions.", migrated.Steps[0].Instructions);
                AssertTrue(migrated.Parameters.Any(item =>
                    String.Equals(item.Name, "missionMode", StringComparison.OrdinalIgnoreCase)
                    && item.Required),
                    "The existing recovery runbook must gain the required missionMode parameter.");
                RunbookParameter modeParameter = migrated.Parameters.Single(item =>
                    String.Equals(item.Name, "missionMode", StringComparison.OrdinalIgnoreCase));
                AssertEqual("Existing Mode Label", modeParameter.Label);
                AssertEqual("Keep this parameter description.", modeParameter.Description);
                AssertEqual("Research", modeParameter.DefaultValue);
            }).ConfigureAwait(false);

            await RunTest("Rescue start ref uses produced tip then original ref then same-vessel dependency", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_rescue_ref", "usr_rescue_ref").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_rescue_ref", "usr_rescue_ref").ConfigureAwait(false);
                Vessel otherVessel = new Vessel("Other Recovery Vessel", "file:///tmp/other-recovery.git")
                {
                    TenantId = "ten_rescue_ref",
                    UserId = "usr_rescue_ref",
                    LocalPath = "C:\\tmp\\other-recovery",
                    WorkingDirectory = "C:\\tmp\\other-recovery",
                    DefaultBranch = "main"
                };
                otherVessel = await testDb.Driver.Vessels.CreateAsync(otherVessel).ConfigureAwait(false);

                Mission reviewed = new Mission("Reviewed work", "Accepted implementation")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Persona = "Worker",
                    Status = MissionStatusEnum.Complete,
                    CommitHash = "2222222222222222222222222222222222222222"
                };
                reviewed = await testDb.Driver.Missions.CreateAsync(reviewed).ConfigureAwait(false);
                Mission sameVesselFailure = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                sameVesselFailure.Persona = "Judge";
                sameVesselFailure.CommitHash = null;
                sameVesselFailure.DependsOnMissionId = reviewed.Id;
                await testDb.Driver.Missions.UpdateAsync(sameVesselFailure).ConfigureAwait(false);

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                    testDb.Driver, admiral, new IncidentService(testDb.Driver),
                    new RunbookService(testDb.Driver, new LoggingModule()));

                Mission producedFailure = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                producedFailure.Persona = "Judge";
                producedFailure.CommitHash = "3333333333333333333333333333333333333333";
                producedFailure.StartFromRef = "4444444444444444444444444444444444444444";
                producedFailure.DependsOnMissionId = reviewed.Id;
                await testDb.Driver.Missions.UpdateAsync(producedFailure).ConfigureAwait(false);
                await orchestrator.HandleMissionOutcomeAsync(producedFailure, false).ConfigureAwait(false);
                AssertEqual(producedFailure.CommitHash,
                    admiral.DispatchedMissions.Single(item => item.ParentMissionId == producedFailure.Id).StartFromRef,
                    "a produced commit must take precedence over the original start ref and dependency commit");

                Mission originalRefFailure = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                originalRefFailure.Persona = "Judge";
                originalRefFailure.CommitHash = null;
                originalRefFailure.StartFromRef = "4444444444444444444444444444444444444444";
                originalRefFailure.DependsOnMissionId = reviewed.Id;
                await testDb.Driver.Missions.UpdateAsync(originalRefFailure).ConfigureAwait(false);
                await orchestrator.HandleMissionOutcomeAsync(originalRefFailure, false).ConfigureAwait(false);
                AssertEqual(originalRefFailure.StartFromRef,
                    admiral.DispatchedMissions.Single(item => item.ParentMissionId == originalRefFailure.Id).StartFromRef,
                    "the failed mission's original start ref must take precedence when it produced no commit");

                await orchestrator.HandleMissionOutcomeAsync(sameVesselFailure, false).ConfigureAwait(false);
                AssertEqual(reviewed.CommitHash,
                    admiral.DispatchedMissions.Single(item => item.ParentMissionId == sameVesselFailure.Id).StartFromRef,
                    "A same-vessel dependency supplies the reviewed tip when the Judge commit is absent.");

                Mission crossVesselFailure = await CreateFailedMissionAsync(testDb, otherVessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                crossVesselFailure.Persona = "Judge";
                crossVesselFailure.CommitHash = null;
                crossVesselFailure.DependsOnMissionId = reviewed.Id;
                await testDb.Driver.Missions.UpdateAsync(crossVesselFailure).ConfigureAwait(false);
                await orchestrator.HandleMissionOutcomeAsync(crossVesselFailure, false).ConfigureAwait(false);
                AssertFalse(admiral.DispatchedMissions.Any(item => item.ParentMissionId == crossVesselFailure.Id),
                    "A reviewer rescue must not start from the default branch when only a cross-vessel commit is available.");

                Mission reviewedWithoutCommit = new Mission("Reviewed work without captured tip", "Accepted implementation")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Persona = "Worker",
                    Status = MissionStatusEnum.Complete
                };
                reviewedWithoutCommit = await testDb.Driver.Missions.CreateAsync(reviewedWithoutCommit).ConfigureAwait(false);
                Mission missingTipFailure = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                missingTipFailure.Persona = "Judge";
                missingTipFailure.CommitHash = null;
                missingTipFailure.DependsOnMissionId = reviewedWithoutCommit.Id;
                await testDb.Driver.Missions.UpdateAsync(missingTipFailure).ConfigureAwait(false);

                await orchestrator.HandleMissionOutcomeAsync(missingTipFailure, false).ConfigureAwait(false);

                AssertFalse(admiral.DispatchedMissions.Any(item => item.ParentMissionId == missingTipFailure.Id),
                    "A reviewer rescue must not launch from the default branch when neither mission has a durable commit.");
                Mission? blocked = await testDb.Driver.Missions.ReadAsync(missingTipFailure.Id).ConfigureAwait(false);
                AssertTrue(blocked!.LastRecoveryActionUtc.HasValue, "The missing-tip failure must be marked handled instead of retried forever.");
            }).ConfigureAwait(false);

            await RunTest("Rescue dispatch attaches Build and UnitTest checks to the rescue voyage", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_checks", "usr_auto_checks").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_checks", "usr_auto_checks").ConfigureAwait(false);
                string workingDir = Path.Combine(Path.GetTempPath(), "armada_auto_checks_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDir);
                vessel.WorkingDirectory = workingDir;
                await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                WorkflowProfile profile = new WorkflowProfile
                {
                    TenantId = vessel.TenantId,
                    Name = "Auto Checks Profile",
                    Scope = WorkflowProfileScopeEnum.Vessel,
                    VesselId = vessel.Id,
                    BuildCommand = "true",
                    UnitTestCommand = "true",
                    IsDefault = true,
                    Active = true
                };
                await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                failed.Persona = "Judge";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                WorkflowProfileService profiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, profiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, profiles, readiness, logging);

                AutonomousRecoveryOrchestrator orchestrator = new AutonomousRecoveryOrchestrator(
                    testDb.Driver, admiral, incidents, runbooks, new ArmadaSettings(), logging,
                    null, null, null, null, null, null, checkRuns);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                // The rescue Judge's real-signal gate needs independent green Checks; the
                // orchestrator must attach Build + UnitTest to the rescue voyage at dispatch
                // (regression for the 231e760e incident where a recovery judge PASS was
                // rejected for having no Checks).
                List<Mission> vesselMissions = await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
                Mission? rescue = vesselMissions.FirstOrDefault(item => item.ParentMissionId == failed.Id);
                AssertTrue(rescue != null, "Expected a linked rescue mission.");
                AssertFalse(String.IsNullOrWhiteSpace(rescue!.VoyageId), "Rescue mission must belong to a rescue voyage");

                EnumerationResult<CheckRun> checkPage = await testDb.Driver.CheckRuns.EnumerateAsync(new CheckRunQuery
                {
                    VoyageId = rescue.VoyageId,
                    PageNumber = 1,
                    PageSize = 50
                }, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(checkPage.Objects.Any(c => c.Type == CheckRunTypeEnum.Build),
                    "A Build check must be attached to the rescue voyage");
                AssertTrue(checkPage.Objects.Any(c => c.Type == CheckRunTypeEnum.UnitTest),
                    "A UnitTest check must be attached to the rescue voyage");
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

            await RunTest("RealSignalGateRejection_NoRescueLoopOnGreenWork", async () =>
            {
                // A standalone Audit Judge whose PASS is rejected ONLY by the
                // real-signal gate (no green independent Checks attached) is an operator-attachment
                // problem, not a substantive rejection of the work. A rescue Judge on the same
                // branch re-reviews already-verified green work and cannot attach Checks itself, so
                // it must not be dispatched.
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_gate", "usr_auto_gate").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_gate", "usr_auto_gate").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel,
                    "Judge PASS rejected: no green independent Checks attached. Independent Checks are attached by the operator, not by captains. To complete the review without them, document an environmental exclusion with the [JUDGE-CHECK-EXCLUSION] marker in the review, or ask the operator to attach Build+UnitTest Checks.").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.AgentOutput = "review body\n[ARMADA:VERDICT] PASS";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count, "A real-signal-gate rejection must not spawn a rescue Judge on green work.");
            }).ConfigureAwait(false);

            // A Worker that fails its gate INSIDE a voyage has already cost that voyage its
            // TestEngineer and Judge (cancelled as blocked dependents). Its rescue must therefore
            // re-enter review, or the revision lands through LocalMerge with no reviewer ever
            // reading the final code. Measured live: a standalone rescue landed unreviewed.
            await RunTest("ReviseRetestRejudge_WorkerGateFailureInsideVoyage_ChainsReJudgeOntoTheRescue", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_wk", "usr_auto_wk").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_wk", "usr_auto_wk").ConfigureAwait(false);
                Voyage parent = await testDb.Driver.Voyages.CreateAsync(new Voyage("Parent voyage", "Tested pipeline")
                {
                    TenantId = "ten_auto_wk",
                    UserId = "usr_auto_wk",
                    Status = VoyageStatusEnum.InProgress
                }).ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "DoD gate failed: classification=TestFail; unit-test command exited 1").ConfigureAwait(false);
                failed.Persona = "Worker";
                failed.VoyageId = parent.Id;
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "Exactly one Worker rescue should be dispatched as the loop root.");
                Mission worker = admiral.DispatchedMissions[0];
                AssertEqual("Worker", worker.Persona, "The dispatched root must be a Worker rescue.");
                AssertTrue(!String.IsNullOrEmpty(worker.VoyageId), "A Worker that failed inside a voyage must be rescued inside a rescue voyage, never as a standalone mission.");
                AssertTrue(!String.Equals(worker.VoyageId, parent.Id, StringComparison.Ordinal), "The rescue runs in its own voyage, not in the halted parent.");

                List<Mission> loopMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(worker.VoyageId!).ConfigureAwait(false);
                Mission? judge = loopMissions.FirstOrDefault(item => String.Equals(item.Persona, "Judge", StringComparison.Ordinal));
                AssertTrue(judge != null, "A Judge stage must be chained onto the Worker rescue so the revision cannot land unreviewed.");
                AssertEqual(MissionStatusEnum.Pending, judge!.Status, "The Judge stage waits on the revision via the pipeline handoff.");
                AssertTrue(!String.IsNullOrEmpty(judge.DependsOnMissionId), "The Judge stage depends on the rescue chain.");
            }).ConfigureAwait(false);

            await RunTest("ReviseRetestRejudge_StandaloneWorkerFailure_KeepsAStandaloneRescue", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_sa", "usr_auto_sa").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_sa", "usr_auto_sa").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "DoD gate failed: classification=TestFail; unit-test command exited 1").ConfigureAwait(false);
                failed.Persona = "Worker";
                failed.VoyageId = null;
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "Exactly one rescue should be dispatched.");
                Mission worker = admiral.DispatchedMissions[0];
                AssertEqual("Worker", worker.Persona, "The rescue of a standalone Worker is a Worker.");
                AssertTrue(String.IsNullOrEmpty(worker.VoyageId), "A standalone mission never had review stages; its rescue stays standalone.");
            }).ConfigureAwait(false);

            await RunTest("ReviseRetestRejudge_JudgeFailure_ChainsReJudgeOntoWorkerRevisionBeforeLanding", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_loop", "usr_auto_loop").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_loop", "usr_auto_loop").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.CommitHash = new string('3', 40);
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
                AssertEqual(failed.CommitHash, worker.StartFromRef, "The Worker revision must start from the failed reviewer's captured tip.");
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
                AssertTrue(String.IsNullOrEmpty(judge.StartFromRef), "The downstream re-Judge inherits the rescue branch through its dependency, not a second start ref.");
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

            await RunTest("Sweep reconciles a Failed-voyage provider failure into one actionable incident", async () =>
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
                Voyage unrelatedVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Unrelated voyage")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Failed,
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-1)
                }).ConfigureAwait(false);
                Objective voyageOwner = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Title = "Provider failure voyage owner",
                    Status = ObjectiveStatusEnum.InProgress,
                    BacklogState = ObjectiveBacklogStateEnum.Dispatched,
                    VesselIds = new List<string> { vessel.Id },
                    VoyageIds = new List<string> { failedVoyage.Id }
                }).ConfigureAwait(false);
                Objective bystander = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Title = "Unrelated objective",
                    Status = ObjectiveStatusEnum.InProgress,
                    BacklogState = ObjectiveBacklogStateEnum.Dispatched,
                    VesselIds = new List<string> { vessel.Id },
                    VoyageIds = new List<string> { unrelatedVoyage.Id }
                }).ConfigureAwait(false);

                string failureReason = "Provider HTTP 400 forbidden: request is non-retryable.";
                Mission failedVoyageMission = await CreateFailedMissionAsync(testDb, vessel, failureReason).ConfigureAwait(false);
                failedVoyageMission.VoyageId = failedVoyage.Id;
                await testDb.Driver.Missions.UpdateAsync(failedVoyageMission).ConfigureAwait(false);
                Objective missionOwner = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Title = "Provider failure mission owner",
                    Status = ObjectiveStatusEnum.InProgress,
                    BacklogState = ObjectiveBacklogStateEnum.Dispatched,
                    VesselIds = new List<string> { vessel.Id },
                    MissionIds = new List<string> { failedVoyageMission.Id }
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count, "A non-retryable provider failure must not dispatch a rescue.");

                Mission? after = await testDb.Driver.Missions.ReadAsync(failedVoyageMission.Id).ConfigureAwait(false);
                AssertTrue(after!.LastRecoveryActionUtc.HasValue, "Failed-voyage candidate must reach failure policy processing.");
                AssertEqual(1, after.RecoveryAttempts, "Failure policy processing must consume the bounded terminal decision.");

                AuthContext auth = AuthContext.Authenticated("ten_sweep_failed_vyg", "usr_sweep_failed_vyg", false, true, "UnitTest");
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = failedVoyageMission.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, incidentPage.Objects.Count, "The operator inbox must show one actionable terminal failure.");
                AssertEqual(IncidentStatusEnum.Open, incidentPage.Objects[0].Status, "The terminal provider failure must remain open for operator action.");
                AssertEqual(IncidentSeverityEnum.High, incidentPage.Objects[0].Severity, "A blocked provider failure must be high severity.");
                AssertEqual(failedVoyageMission.Id, incidentPage.Objects[0].MissionId, "The incident must preserve mission evidence lineage.");
                AssertEqual(failedVoyage.Id, incidentPage.Objects[0].VoyageId, "The incident must preserve voyage evidence lineage.");
                AssertEqual(failureReason, incidentPage.Objects[0].RootCause, "The incident must preserve the provider diagnostic.");
                AssertContains("stopped before rescue dispatch", incidentPage.Objects[0].RecoveryNotes ?? "", "Recovery notes must explain why automatic recovery stopped.");

                string incidentId = incidentPage.Objects[0].Id;
                Objective? voyageOwnerAfter = await testDb.Driver.Objectives.ReadAsync(voyageOwner.Id).ConfigureAwait(false);
                Objective? missionOwnerAfter = await testDb.Driver.Objectives.ReadAsync(missionOwner.Id).ConfigureAwait(false);
                Objective? bystanderAfter = await testDb.Driver.Objectives.ReadAsync(bystander.Id).ConfigureAwait(false);
                AssertTrue(voyageOwnerAfter!.VoyageIds.Contains(failedVoyage.Id),
                    "incident linking must retain the failed voyage as dispatch evidence");
                AssertTrue(voyageOwnerAfter.IncidentIds.Contains(incidentId),
                    "an objective that owns the failed voyage must link the recovery incident");
                AssertTrue(missionOwnerAfter!.IncidentIds.Contains(incidentId),
                    "an objective that directly owns a standalone mission must link the recovery incident");
                AssertFalse(bystanderAfter!.IncidentIds.Contains(incidentId),
                    "an objective on the same vessel but outside the failed lineage must not link the incident");
                AssertEqual(ObjectiveStatusEnum.InProgress, voyageOwnerAfter.Status,
                    "an incident annotation must not rewrite objective lifecycle state");
                AssertEqual(ObjectiveBacklogStateEnum.Dispatched, voyageOwnerAfter.BacklogState,
                    "an incident annotation must not rewrite objective backlog state");
                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count, "A repeated sweep must remain idempotent and must not dispatch a rescue.");
                EnumerationResult<Incident> repeatedIncidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = failedVoyageMission.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, repeatedIncidentPage.Objects.Count, "A repeated sweep must not duplicate the inbox incident.");
                AssertEqual(incidentId, repeatedIncidentPage.Objects[0].Id, "A repeated sweep must retain the original incident record.");
                Objective? repeatedOwner = await testDb.Driver.Objectives.ReadAsync(voyageOwner.Id).ConfigureAwait(false);
                AssertEqual(1, repeatedOwner!.IncidentIds.Count(id => id == incidentId),
                    "a repeated sweep must not duplicate the objective incident link");
            }).ConfigureAwait(false);

            await RunTest("Sweep dispatches recovery for a recoverable Failed-voyage failure and links its objective", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_sweep_failed_recovery", "usr_sweep_failed_recovery").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_sweep_failed_recovery", "usr_sweep_failed_recovery").ConfigureAwait(false);
                Voyage failedVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Failed voyage")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Failed,
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-1),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
                }).ConfigureAwait(false);
                Objective owner = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Title = "Objective with recoverable failed voyage",
                    Status = ObjectiveStatusEnum.InProgress,
                    VesselIds = new List<string> { vessel.Id },
                    VoyageIds = new List<string> { failedVoyage.Id }
                }).ConfigureAwait(false);

                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                failed.Persona = "Worker";
                failed.VoyageId = failedVoyage.Id;
                failed.CommitHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "A recoverable failure in a Failed voyage must dispatch one rescue.");
                Mission rescue = admiral.DispatchedMissions[0];
                AssertEqual(failed.Id, rescue.ParentMissionId, "The rescue must preserve failed-mission lineage.");
                AssertEqual(failed.CommitHash, rescue.StartFromRef, "The rescue must start from the failed mission's recorded commit.");
                AssertTrue(!String.IsNullOrWhiteSpace(rescue.VoyageId), "The recovery must run in a dedicated rescue voyage.");
                AssertFalse(String.Equals(failedVoyage.Id, rescue.VoyageId, StringComparison.Ordinal), "The rescue must not reuse the terminal parent voyage.");

                Objective? ownerAfter = await testDb.Driver.Objectives.ReadAsync(owner.Id).ConfigureAwait(false);
                AssertTrue(ownerAfter!.VoyageIds.Contains(failedVoyage.Id), "The objective must retain the failed voyage as evidence.");
                AssertTrue(ownerAfter.VoyageIds.Contains(rescue.VoyageId!), "The objective must expose the active recovery voyage.");

                await orchestrator.SweepAsync().ConfigureAwait(false);
                AssertEqual(1, admiral.DispatchedMissions.Count, "A repeat sweep must not dispatch a duplicate rescue.");
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

            // The rescue-brief tests below were written as plain methods and never registered here, so
            // none of them ran. TestSuite has no reflection-based discovery: a test executes only when
            // RunTest is called for it. That included the cap test guarding the Azure content_filter
            // fix, which has therefore never proved anything since it was written.
            await RunTest("SanitizeOriginalDescriptionForRescue returns a short description verbatim",
                SanitizeOriginalDescriptionForRescue_ShortDescription_ReturnsVerbatim).ConfigureAwait(false);
            await RunTest("SanitizeOriginalDescriptionForRescue marks a null or empty description",
                SanitizeOriginalDescriptionForRescue_NullOrEmpty_ReturnsNoDescriptionMarker).ConfigureAwait(false);
            await RunTest("SanitizeOriginalDescriptionForRescue splits scope from diagnostics and truncates both",
                SanitizeOriginalDescriptionForRescue_LongDescriptionWithDiagnostics_TruncatesAndSplits).ConfigureAwait(false);
            await RunTest("SanitizeOriginalDescriptionForRescue truncates a scope-only description",
                SanitizeOriginalDescriptionForRescue_LongDescriptionWithoutDiagnostics_TruncatesScope).ConfigureAwait(false);
            // The objective scheduler counts running voyages through Objective.VoyageIds, for the
            // fleet ceiling and per vessel alike. A rescue voyage that is linked to nothing is
            // invisible to both, so the fleet runs one voyage more than its ceiling while the
            // rescued vessel reads idle.
            await RunTest("A rescue voyage is linked to every objective that owns the voyage it rescues", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_rescue_link", "usr_rescue_link").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_rescue_link", "usr_rescue_link").ConfigureAwait(false);
                Voyage parent = await testDb.Driver.Voyages.CreateAsync(new Voyage("parent voyage", "the voyage that failed")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Failed
                }).ConfigureAwait(false);
                Voyage unrelated = await testDb.Driver.Voyages.CreateAsync(new Voyage("other voyage", "someone else's work")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.InProgress
                }).ConfigureAwait(false);

                Objective owner = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Title = "the objective whose voyage failed",
                    Status = ObjectiveStatusEnum.InProgress,
                    VesselIds = new List<string> { vessel.Id },
                    VoyageIds = new List<string> { parent.Id }
                }).ConfigureAwait(false);
                Objective bystander = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Title = "an objective on another voyage",
                    Status = ObjectiveStatusEnum.InProgress,
                    VesselIds = new List<string> { vessel.Id },
                    VoyageIds = new List<string> { unrelated.Id }
                }).ConfigureAwait(false);

                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                failed.VoyageId = parent.Id;
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                Voyage rescue = await testDb.Driver.Voyages.CreateAsync(new Voyage("Rescue 1: the failed mission", "rescue")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.InProgress
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                int linked = await orchestrator.LinkRescueVoyageToObjectivesAsync(failed, rescue, CancellationToken.None).ConfigureAwait(false);
                AssertEqual(1, linked, "exactly the objective that owns the rescued voyage is linked");

                Objective? ownerAfter = await testDb.Driver.Objectives.ReadAsync(owner.Id).ConfigureAwait(false);
                AssertTrue(ownerAfter!.VoyageIds.Contains(rescue.Id), "the owner now carries the rescue voyage, so the scheduler counts it");
                AssertTrue(ownerAfter.VoyageIds.Contains(parent.Id), "the failed parent voyage stays linked as history");

                Objective? bystanderAfter = await testDb.Driver.Objectives.ReadAsync(bystander.Id).ConfigureAwait(false);
                AssertFalse(bystanderAfter!.VoyageIds.Contains(rescue.Id), "an objective on another voyage is untouched");

                int again = await orchestrator.LinkRescueVoyageToObjectivesAsync(failed, rescue, CancellationToken.None).ConfigureAwait(false);
                AssertEqual(0, again, "linking is idempotent");

                Mission orphan = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                orphan.VoyageId = null;
                await testDb.Driver.Missions.UpdateAsync(orphan).ConfigureAwait(false);
                AssertEqual(0, await orchestrator.LinkRescueVoyageToObjectivesAsync(orphan, rescue, CancellationToken.None).ConfigureAwait(false),
                    "a standalone mission with no voyage links nothing");
            }).ConfigureAwait(false);

            await RunTest("BuildRescueDescription keeps a large embedded failure log under the cap",
                BuildRescueDescription_LargeEmbeddedFailureLog_StaysUnderCap).ConfigureAwait(false);
            await RunTest("BuildRescueDescription reduces older handoff blocks so the newest survives",
                BuildRescueDescription_OlderHandoffBlocks_AreReducedSoTheNewestSurvives).ConfigureAwait(false);
            await RunTest("BuildRescueDescription keeps an over-cap Judge report's Follow-ups and Verdict whole",
                BuildRescueDescription_OverCapJudgeReport_KeepsTheVerdictAndFollowUpsWhole).ConfigureAwait(false);
            await RunTest("BuildRescueDescription embeds an under-cap Judge report whole",
                BuildRescueDescription_UnderCapJudgeReport_IsEmbeddedWhole).ConfigureAwait(false);
            await RunTest("TruncateReviewerFeedbackForBrief keeps the head-first cut for text without Judge sections",
                TruncateReviewerFeedbackForBrief_TextWithoutJudgeSections_KeepsTheHeadFirstCut).ConfigureAwait(false);
            await RunTest("TruncateReviewerFeedbackForBrief drops an over-budget narration preamble so the findings survive",
                TruncateReviewerFeedbackForBrief_NarrationPreambleOverBudget_KeepsTheFindingsNotTheChatter).ConfigureAwait(false);
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
                CommitHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
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
            public Task StopAllAgentProcessesAsync(CancellationToken token = default) => Task.CompletedTask;

            public Task HealthCheckAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
                => Task.CompletedTask;
        }

        /// <summary>
        /// Tests for the rescue-brief sanitizer. The old code embedded the prior
        /// mission's full description in the rescue prompt, which on DoD-gate
        /// failures is the entire build log. Azure OpenAI's content_filter rejects
        /// prompts that carry many repeated warning lines (BL0005, CS0618) packed
        /// into one block. The sanitizer must keep scope but trim diagnostics.
        /// </summary>
        public async Task SanitizeOriginalDescriptionForRescue_ShortDescription_ReturnsVerbatim()
        {
            string descriptionText = "scope: implement feature X." + "\n" + "files: src/Foo.cs";
            string sanitized = AutonomousRecoveryOrchestrator.SanitizeOriginalDescriptionForRescue(descriptionText);
            AssertEqual(descriptionText, sanitized,
                "Descriptions under the cap must be returned verbatim so a small brief is unchanged.");
            await Task.CompletedTask;
        }

        public async Task SanitizeOriginalDescriptionForRescue_NullOrEmpty_ReturnsNoDescriptionMarker()
        {
            string sanitizedNull = AutonomousRecoveryOrchestrator.SanitizeOriginalDescriptionForRescue(null);
            AssertEqual("(no description recorded)", sanitizedNull,
                "A null description must collapse to the explicit no-description marker.");
            string sanitizedEmpty = AutonomousRecoveryOrchestrator.SanitizeOriginalDescriptionForRescue(String.Empty);
            AssertEqual("(no description recorded)", sanitizedEmpty,
                "An empty description must collapse to the explicit no-description marker.");
            await Task.CompletedTask;
        }

        public async Task SanitizeOriginalDescriptionForRescue_LongDescriptionWithDiagnostics_TruncatesAndSplits()
        {
            // Simulate a failed TestEngineer mission whose description carries the full
            // DoD-gate build log. The scope block is short, the diagnostics block is long.
            string scope = "scope: add a 4-test coverage for the schema-v2 fixture." + "\n" + "files: TestAssets/dxp.json" + "\n";
            string diagnostics = "--- ACTIONABLE DIAGNOSTICS ---" + "\n" + new string('X', 30000) + "\n" + "--- OUTPUT TAIL ---" + "\n";
            string original = scope + diagnostics;

            string sanitized = AutonomousRecoveryOrchestrator.SanitizeOriginalDescriptionForRescue(original);

            AssertTrue(sanitized.Length <= AutonomousRecoveryOrchestrator._MaxRescueDescriptionChars + 2000,
                "Sanitized brief must not blow past the cap plus the diagnostics window. Actual length: " + sanitized.Length);
            AssertTrue(sanitized.Contains("ACTIONABLE DIAGNOSTICS truncated"),
                "Sanitized brief must mark the diagnostics truncation so the captain does not assume the log is complete.");
            AssertTrue(sanitized.Contains("scope: add a 4-test coverage"),
                "Sanitized brief must keep the head of the scope block where the recovery value lives.");
            AssertFalse(sanitized.Contains(new string('X', 30000)),
                "Sanitized brief must NOT contain the full diagnostics blob.");
            await Task.CompletedTask;
        }

        public async Task SanitizeOriginalDescriptionForRescue_LongDescriptionWithoutDiagnostics_TruncatesScope()
        {
            string scope = "scope line" + "\n" + new string('Y', 20000) + "\n" + "more scope";
            string sanitized = AutonomousRecoveryOrchestrator.SanitizeOriginalDescriptionForRescue(scope);

            AssertTrue(sanitized.Length < scope.Length,
                "A description without diagnostics must still be truncated to the cap.");
            AssertTrue(sanitized.Contains("truncated to"),
                "A scope-only truncation must mark itself so the captain reads the marker.");
            AssertFalse(sanitized.Contains(new string('Y', 20000)),
                "A scope-only truncation must not carry the full body.");
            await Task.CompletedTask;
        }

        public async Task BuildRescueDescription_LargeEmbeddedFailureLog_StaysUnderCap()
        {
            // Construct a Mission whose Description is the multi-page failure log the
            // M2 [Worker] brief carried. BuildRescueDescription must produce a result
            // that is comfortably below the rescue-brief cap.
            string scope = "title: add schema-v2 fixture" + "\n" + "files: TestAssets/dxp.json";
            string failureLog = "--- ACTIONABLE DIAGNOSTICS ---" + "\n" + new string('W', 18000) + "\n" + "--- OUTPUT TAIL ---" + "\n" + new string('Z', 10000);
            Mission failed = new Mission
            {
                Id = "msn_test_x_x",
                Title = "test mission",
                Status = MissionStatusEnum.Failed,
                FailureReason = "DoD gate failed: classification=Compile; build command exited 1",
                Description = scope + "\n" + failureLog,
                BranchName = "armada/test/msn_test",
            };
            Incident incident = new Incident
            {
                Id = "inc_test_x",
                Title = "test incident",
                Summary = "test summary",
                Status = IncidentStatusEnum.Open,
                Severity = IncidentSeverityEnum.High,
            };

            string brief = AutonomousRecoveryOrchestrator.BuildRescueDescription(failed, incident, 1);

            AssertTrue(brief.Length < AutonomousRecoveryOrchestrator._MaxRescueDescriptionChars + 2500,
                "Rescue brief must stay below the cap plus reasonable headroom for header text. Actual length: " + brief.Length);
            AssertTrue(brief.Contains("ACTIONABLE DIAGNOSTICS truncated"),
                "Rescue brief must mark the diagnostics truncation.");
            AssertFalse(brief.Contains(new string('W', 18000)),
                "Rescue brief must not carry the full diagnostics blob verbatim.");
            await Task.CompletedTask;
        }

        // A Judge report shaped the way the Judge persona is told to write it: findings first,
        // the actionable Suggested Follow-ups and Verdict last, then the standalone verdict line.
        private static string BuildJudgeReport(int completenessChars, int correctnessChars)
        {
            return "## Completeness" + "\n" + new string('c', completenessChars) + "\n\n"
                + "## Correctness" + "\n" + new string('r', correctnessChars) + "\n\n"
                + "## Tests" + "\n" + "Ran the suite in the foreground: 0 failed." + "\n\n"
                + "## Failure Modes" + "\n" + "Reviewed the absence paths." + "\n\n"
                + "## Suggested Follow-ups" + "\n" + "- src/Fixture.cs:12 -- record the sha256 of both copies." + "\n\n"
                + "## Verdict" + "\n" + "NEEDS_REVISION: the disclosure is missing; add the two hashes and the identity statement." + "\n\n"
                + "[ARMADA:VERDICT] NEEDS_REVISION";
        }

        public async Task BuildRescueDescription_OverCapJudgeReport_KeepsTheVerdictAndFollowUpsWhole()
        {
            // The report is four times the reviewer-feedback cap. A head-first cut would keep the
            // Completeness padding and drop every actionable line; the rescue brief must instead
            // carry the Suggested Follow-ups, the Verdict and the verdict line whole, and name the
            // sections it omitted.
            string report = BuildJudgeReport(4000, 4000);
            AssertTrue(report.Length > AutonomousRecoveryOrchestrator._MaxRescueReviewerFeedbackChars * 3,
                "Precondition: the report must be well over the cap. Actual length: " + report.Length);

            Mission failed = new Mission
            {
                Id = "msn_test_judge_over",
                Title = "test mission",
                Status = MissionStatusEnum.Failed,
                FailureReason = "Judge verdict: NEEDS_REVISION",
                Description = "title: decompile the update manager",
                ReviewComment = report,
            };
            Incident incident = new Incident
            {
                Id = "inc_test_judge_over",
                Title = "test incident",
                Summary = "test summary",
                Status = IncidentStatusEnum.Open,
                Severity = IncidentSeverityEnum.Medium,
            };

            string brief = AutonomousRecoveryOrchestrator.BuildRescueDescription(failed, incident, 1);

            AssertTrue(brief.Contains("## Suggested Follow-ups" + "\n" + "- src/Fixture.cs:12 -- record the sha256 of both copies."),
                "The Suggested Follow-ups section must survive whole.");
            AssertTrue(brief.Contains("## Verdict" + "\n" + "NEEDS_REVISION: the disclosure is missing; add the two hashes and the identity statement."),
                "The Verdict section must survive whole.");
            AssertTrue(brief.Contains("[ARMADA:VERDICT] NEEDS_REVISION"),
                "The standalone verdict line must survive.");
            AssertTrue(brief.Contains("## Completeness"),
                "The head of the report is still filled from the top.");
            AssertTrue(brief.Contains("reviewer feedback truncated") && brief.Contains("the sections Correctness, Tests, Failure Modes"),
                "The marker must sit where the middle was and name the omitted sections. Brief: " + brief);
            AssertFalse(brief.Contains(new string('r', 4000)),
                "The omitted Correctness padding must not be carried verbatim.");

            int feedbackStart = brief.IndexOf("Reviewer feedback to address:", StringComparison.Ordinal);
            int feedbackEnd = brief.IndexOf("Objective:", StringComparison.Ordinal);
            AssertTrue(feedbackStart > 0 && feedbackEnd > feedbackStart, "The brief keeps its section order.");
            int feedbackLength = feedbackEnd - feedbackStart;
            AssertTrue(feedbackLength <= AutonomousRecoveryOrchestrator._MaxRescueReviewerFeedbackChars + 400,
                "The embedded feedback must stay near the cap. Actual length: " + feedbackLength);
            await Task.CompletedTask;
        }

        public async Task BuildRescueDescription_UnderCapJudgeReport_IsEmbeddedWhole()
        {
            string report = BuildJudgeReport(120, 120);
            AssertTrue(report.Length < AutonomousRecoveryOrchestrator._MaxRescueReviewerFeedbackChars,
                "Precondition: the report must fit the cap.");

            Mission failed = new Mission
            {
                Id = "msn_test_judge_under",
                Title = "test mission",
                Status = MissionStatusEnum.Failed,
                FailureReason = "Judge verdict: NEEDS_REVISION",
                Description = "title: decompile the update manager",
                ReviewComment = report,
            };
            Incident incident = new Incident
            {
                Id = "inc_test_judge_under",
                Title = "test incident",
                Summary = "test summary",
                Status = IncidentStatusEnum.Open,
                Severity = IncidentSeverityEnum.Medium,
            };

            string brief = AutonomousRecoveryOrchestrator.BuildRescueDescription(failed, incident, 1);

            AssertTrue(brief.Contains(report), "An under-cap report is embedded verbatim.");
            AssertFalse(brief.Contains("reviewer feedback truncated"), "No truncation marker for an under-cap report.");
            await Task.CompletedTask;
        }

        // A Judge transcript that opens with more narration than the whole budget: the
        // narration is dropped first, so the findings sections survive instead of the
        // chatter. Measured live: 1,724 of 8,015 chars kept, every one of them preamble.
        public async Task TruncateReviewerFeedbackForBrief_NarrationPreambleOverBudget_KeepsTheFindingsNotTheChatter()
        {
            System.Text.StringBuilder preamble = new System.Text.StringBuilder();
            for (int i = 0; i < 60; i++)
                preamble.Append("I'll start by reading the mission instructions. Let me re-anchor and check the validation path (" + i + ").\n");
            string report = preamble.ToString() + BuildJudgeReport(600, 400);
            string brief = AutonomousRecoveryOrchestrator.TruncateReviewerFeedbackForBrief(report, 2000);

            AssertTrue(brief.Length <= 2400, "the brief must stay near the cap, was " + brief.Length);
            AssertTrue(!brief.Contains("I'll start by reading the mission instructions"), "narration before the first section must be dropped");
            AssertTrue(brief.Contains("reviewer narration before the first section omitted"), "the drop must be named in-band");
            AssertTrue(brief.Contains("## Completeness"), "the first findings section must survive");
            AssertTrue(brief.Contains("## Verdict"), "the Verdict section must survive whole");
            AssertTrue(brief.Contains("[ARMADA:VERDICT] NEEDS_REVISION"), "the verdict line must survive");
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public async Task TruncateReviewerFeedbackForBrief_TextWithoutJudgeSections_KeepsTheHeadFirstCut()
        {
            // A gate log or a free-form review has no Judge sections; its signal is at the top,
            // so the existing head-first cut and marker stay exactly as they were.
            string log = String.Join("\n", Enumerable.Range(0, 400).Select(i => "warning CS0618: line " + i));
            string cut = AutonomousRecoveryOrchestrator.TruncateReviewerFeedbackForBrief(log, AutonomousRecoveryOrchestrator._MaxRescueReviewerFeedbackChars);
            string expected = AutonomousRecoveryOrchestrator.TruncateForBrief(log, AutonomousRecoveryOrchestrator._MaxRescueReviewerFeedbackChars);

            AssertEqual(expected, cut, "Text without Judge sections uses the head-first cut unchanged.");
            AssertTrue(cut.StartsWith("warning CS0618: line 0", StringComparison.Ordinal), "The head is kept.");
            AssertTrue(cut.Contains("remainder in admiral log"), "The existing marker is kept.");
            await Task.CompletedTask;
        }

        public async Task BuildRescueDescription_OlderHandoffBlocks_AreReducedSoTheNewestSurvives()
        {
            // The rescue cap keeps the HEAD of the description, and handoff blocks sit at the end. An
            // oversized description therefore loses every prior-stage block, including the newest one --
            // the context the rescued mission was actually working from. Reducing the OLDER blocks first
            // buys back the room for the newest one to survive the cut.
            string baseBrief = "title: port the widget decoder\n" + new string('b', 1500);

            string olderBlock =
                "\n\n---\n" + MissionService.BuildHandoffMarker("msn_upstream_older") + "\n" +
                "## Prior Stage Output\nThe previous pipeline stage (Worker) completed mission older.\n" +
                "Branch: armada/example/older\n" +
                "### Diff from prior stage\n```diff\n" + new string('o', 6000) + "\n```\n";

            string newestBlock =
                "\n\n---\n" + MissionService.BuildHandoffMarker("msn_upstream_newest") + "\n" +
                "## Prior Stage Output\nThe previous pipeline stage (TestEngineer) completed mission newest.\n" +
                "Branch: armada/example/newest\n" +
                "NEWEST-STAGE-FACT the rescue must still see\n";

            Mission failed = new Mission
            {
                Id = "msn_test_handoff",
                Title = "test mission",
                Status = MissionStatusEnum.Failed,
                FailureReason = "captain runtime error",
                Description = baseBrief + olderBlock + newestBlock,
                BranchName = "armada/test/msn_test_handoff",
            };
            Incident incident = new Incident
            {
                Id = "inc_test_handoff",
                Title = "test incident",
                Summary = "test summary",
                Status = IncidentStatusEnum.Open,
                Severity = IncidentSeverityEnum.Medium,
            };

            string brief = AutonomousRecoveryOrchestrator.BuildRescueDescription(failed, incident, 1);

            AssertTrue(brief.Contains("NEWEST-STAGE-FACT the rescue must still see"),
                "The newest prior-stage block must survive into the rescue brief.");
            AssertFalse(brief.Contains(new string('o', 6000)),
                "The older prior-stage diff must not be carried into the rescue brief.");
            AssertTrue(brief.Contains("msn_upstream_older"),
                "The older stage must still be named, so the rescue can find its work if needed.");
            await Task.CompletedTask;
        }

    }
}
