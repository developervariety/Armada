namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
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
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;
    using StubGitService = global::Test.Shared.Infrastructure.StubGitService;

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
            await RunTest("A rescue of an objective whose non-goals forbid test edits runs no TestEngineer stage, unless the failure asks for it", () =>
            {
                List<PipelineStage> chain = new List<PipelineStage>
                {
                    new PipelineStage(3, "TestEngineer"),
                    new PipelineStage(4, "Linter"),
                    new PipelineStage(5, "Judge")
                };
                Objective reportOnly = new Objective
                {
                    Title = "Write the parity report",
                    NonGoals = new List<string> { "Do not add, edit, or commit tests." }
                };
                Mission failed = new Mission("Write the parity report") { Persona = "Judge", FailureReason = "Judge NEEDS_REVISION: the report omits two rows." };

                AutonomousRecoveryOrchestrator.CarriedStageSkip? skip =
                    AutonomousRecoveryOrchestrator.ApplyNoTestObjectiveSkip(failed, "Worker", chain, reportOnly);
                AssertNotNull(skip, "a report-only objective drops the TestEngineer from the rescue chain");
                AssertEqual("Linter,Judge", String.Join(",", skip!.RemainingStages.Select(stage => stage.PersonaName)),
                    "the rest of the chain runs in order");
                AssertEqual(AutonomousRecoveryOrchestrator.NoTestObjectiveSkipReason, skip.Request.Reason, "the skip records why");

                Mission askedForTests = new Mission("Write the parity report") { Persona = "Judge", FailureReason = "No TestEngineer ran on this change." };
                AssertNull(AutonomousRecoveryOrchestrator.ApplyNoTestObjectiveSkip(askedForTests, "Worker", chain, reportOnly),
                    "a failure that names the TestEngineer keeps that stage");

                Objective withTests = new Objective { Title = "Fix the parser", NonGoals = new List<string> { "Do not change the public API." } };
                AssertNull(AutonomousRecoveryOrchestrator.ApplyNoTestObjectiveSkip(failed, "Worker", chain, withTests),
                    "an objective that allows test edits keeps the TestEngineer");
                AssertNull(AutonomousRecoveryOrchestrator.ApplyNoTestObjectiveSkip(failed, "Worker", chain, null),
                    "a rescue with no owning objective keeps the TestEngineer");
                return Task.CompletedTask;
            });

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
                        "Check failed: EXAMPLE_PORT_ROOT environment variable is not set"),
                    "A missing environment variable cannot be fixed by re-running the brief.");
                AssertFalse(
                    AutonomousRecoveryOrchestrator.IsEnvironmentalFailure("Agent process exited with code 1"),
                    "An ordinary process failure is still rescuable.");
                AssertFalse(
                    AutonomousRecoveryOrchestrator.IsEnvironmentalFailure("Judge verdict: NEEDS_REVISION"),
                    "A substantive rejection of the work is still rescuable.");
            }).ConfigureAwait(false);

            await RunTest("IsPolicyRefusalFailure blocks a rescue only for a stopped captain refusal", () =>
            {
                AssertTrue(
                    AutonomousRecoveryOrchestrator.IsPolicyRefusalFailure(
                        PolicyRefusalContinuationService.StoppedReasonPrefix + "ProviderSafeguardBlock on ClaudeCode: flagged. The mission already had its one continuation."),
                    "A stopped refusal would repeat the blocked path, so it is not rescued.");
                AssertTrue(
                    AutonomousRecoveryOrchestrator.IsPolicyRefusalFailure(
                        PolicyRefusalContinuationService.StoppedReasonPrefix + "DeclaredRefusal on Codex: out of scope [recoverable work preserved on mission branch x]"),
                    "The terminal path's preserved-work suffix does not hide the refusal.");
                AssertFalse(
                    AutonomousRecoveryOrchestrator.IsPolicyRefusalFailure(
                        PolicyRefusalContinuationService.ContinuationReasonPrefix + "DeclaredRefusal on Codex: out of scope"),
                    "A pending continuation is not a stopped refusal.");
                AssertFalse(
                    AutonomousRecoveryOrchestrator.IsPolicyRefusalFailure("Judge verdict: NEEDS_REVISION (the captain wrote policy_refusal: in a quote)"),
                    "Only a failure reason that opens with the stop prefix matches.");
                AssertFalse(AutonomousRecoveryOrchestrator.IsPolicyRefusalFailure(null), "No reason is not a refusal.");
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

            // The gate writes its failure class into the reason. Infra and Timeout name the host, not
            // the work: a rescue re-runs the same commands on the same host and fails the same way.
            await RunTest("Gate failure class Infra from missing workflow commands blocks the rescue and names the class", async () =>
            {
                string reason = BuildGateFailureReason("Infra", "missing-commands", -1,
                    "No BuildCommand or UnitTestCommand is configured on the vessel's workflow profile. " +
                    "Add a workflow profile for this vessel, or add '[DOD:DOC-ONLY]' to the mission description to opt out of in-dock verification.");
                string summary = await AssertGateFailureBlocksRescueAsync("ten_gate_infra", reason).ConfigureAwait(false);
                AssertTrue(summary.Contains("Infra", StringComparison.Ordinal), "The blocked policy must name the gate class. Summary: " + summary);
            }).ConfigureAwait(false);

            await RunTest("Gate failure class Timeout blocks the rescue and names the class", async () =>
            {
                string reason = BuildGateFailureReason("Timeout", "unit-test", -1,
                    "unit-test command timed out after 1800 seconds.");
                string summary = await AssertGateFailureBlocksRescueAsync("ten_gate_timeout", reason).ConfigureAwait(false);
                AssertTrue(summary.Contains("Timeout", StringComparison.Ordinal), "The blocked policy must name the gate class. Summary: " + summary);
            }).ConfigureAwait(false);

            // A time limit that still read as host load on the gate's own re-run, which held the host alone, is the
            // host's load, not the work's: one rescue carries the work through a fresh gate. A plain Timeout stays
            // blocked (the test above), and a rescue that times out again stops at the auto-rescue block.
            await RunTest("A gate Timeout that still read as host load on its re-run dispatches one rescue", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_gate_load_timeout", "usr_gate_load_timeout").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_gate_load_timeout", "usr_gate_load_timeout").ConfigureAwait(false);
                DefinitionOfDoneResult gateResult = DefinitionOfDoneResult.Fail("unit-test", -1,
                    "unit-test command timed out after 1200 seconds. The command was still writing output at the limit.\n"
                    + "      Request finished HTTP/1.1 GET http://localhost:5000/api/example - 200",
                    DefinitionOfDoneFailureClassEnum.Timeout);
                gateResult.TimedOut = true;
                gateResult.TimedOutWhileProgressing = true;
                gateResult.HostLoadTimeout = true;
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, MissionService.BuildDodFailureReason(gateResult)).ConfigureAwait(false);
                failed.Persona = "Worker";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);
                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "A host-load time limit is carried through one fresh gate. Reason: " + failed.FailureReason);
                AssertEqual(failed.Id, admiral.DispatchedMissions[0].ParentMissionId, "The rescue is for the timed-out mission.");
                EnumerationResult<ArmadaEvent> blocked = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery
                {
                    MissionId = failed.Id,
                    EventType = "autonomous_recovery.blocked",
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(0, blocked.Objects.Count, "The host-load time limit is not refused as gate_class_not_rescuable.");

                Mission rescueFailed = await CreateRescueMissionAsync(testDb, vessel, failed.Id, MissionService.BuildDodFailureReason(gateResult)).ConfigureAwait(false);
                await orchestrator.HandleMissionOutcomeAsync(rescueFailed, false).ConfigureAwait(false);
                AssertEqual(1, admiral.DispatchedMissions.Count, "A rescue that times out the same way is not rescued again.");
            }).ConfigureAwait(false);

            await RunTest("Gate failure on a missing .NET runtime or testhost exit blocks the rescue", async () =>
            {
                DefinitionOfDoneFailureClassifier classifier = new DefinitionOfDoneFailureClassifier();
                string runtimeReason = BuildGateFailureReason(
                    classifier.Classify("unit-test", 150, DefinitionOfDoneFailureClassifierTests.MissingRuntimeInstallOutput).ToString(),
                    "unit-test", 150, DefinitionOfDoneFailureClassifierTests.MissingRuntimeInstallOutput);
                await AssertGateFailureBlocksRescueAsync("ten_gate_runtime", runtimeReason).ConfigureAwait(false);

                string testhostReason = BuildGateFailureReason(
                    classifier.Classify("unit-test", 1, DefinitionOfDoneFailureClassifierTests.TesthostExitOutput).ToString(),
                    "unit-test", 1, DefinitionOfDoneFailureClassifierTests.TesthostExitOutput);
                await AssertGateFailureBlocksRescueAsync("ten_gate_testhost", testhostReason).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("Gate failure class Compile still dispatches a rescue", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_gate_compile", "usr_gate_compile").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_gate_compile", "usr_gate_compile").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel,
                    BuildGateFailureReason("Compile", "build", 1, "Parser.cs(12,5): error CS0103: The name 'x' does not exist in the current context")).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "A compile failure is the work's own defect; a rescue can fix it.");
            }).ConfigureAwait(false);

            // The gate runs a consumer's suite only after this vessel's own build and tests pass, so a
            // TestFail on the consumer's suite is a failure in the consumer's repository. A rescue in this
            // vessel cannot fix it; the same failing tests in this vessel's own suite are still rescued.
            await RunTest("Gate failure on a consumer's test suite blocks the producer rescue and names the consumer and its tests", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_gate_consumer", "usr_gate_consumer").ConfigureAwait(false);
                Vessel producer = await CreateVesselAsync(testDb, "ten_gate_consumer", "usr_gate_consumer").ConfigureAwait(false);
                Vessel consumer = await testDb.Driver.Vessels.CreateAsync(new Vessel
                {
                    TenantId = "ten_gate_consumer",
                    UserId = "usr_gate_consumer",
                    Name = "ExampleConsumer",
                    RepoUrl = "file:///tmp/example-consumer.git",
                    LocalPath = "C:\\tmp\\example-consumer",
                    WorkingDirectory = "C:\\tmp\\example-consumer",
                    DefaultBranch = "main"
                }).ConfigureAwait(false);
                await SeedEligibleRecoveryRosterAsync(testDb, producer).ConfigureAwait(false);

                string consumerReason = BuildGateFailureReason("TestFail", "consumer_tests_failed: ExampleConsumer", 1,
                    "  Failed Consumer.Data.RowCitesPastEndOfFile [3 ms]\nFailed: 1, Passed: 640");
                Mission consumerFailed = await CreateFailedMissionAsync(testDb, producer, consumerReason).ConfigureAwait(false);
                consumerFailed.Persona = "Worker";
                await testDb.Driver.Missions.UpdateAsync(consumerFailed).ConfigureAwait(false);
                await WriteTestFailEvaluationAsync(testDb, consumerFailed.Id, false, "Consumer.Data.RowCitesPastEndOfFile").ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(consumerFailed, false).ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count, "A failure in a consumer's tests must not dispatch a rescue in the producer.");
                AuthContext auth = AuthContext.Authenticated("ten_gate_consumer", "usr_gate_consumer", false, true, "UnitTest");
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = consumerFailed.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, incidentPage.Objects.Count, "The operator needs an incident to act on.");
                Incident incident = incidentPage.Objects[0];
                string summary = incident.Summary ?? String.Empty;
                string notes = incident.RecoveryNotes ?? String.Empty;
                AssertContains("Policy: block", summary, "The incident records the blocked policy.");
                AssertContains(AutonomousRecoveryOrchestrator.ConsumerTestFailureReason, summary, "The incident carries the stable consumer-failure reason.");
                AssertContains("ExampleConsumer", notes, "The incident names the consumer vessel.");
                AssertContains(consumer.Id, notes, "The incident names the consumer vessel's id.");
                AssertContains("Consumer.Data.RowCitesPastEndOfFile", notes, "The incident names the failing consumer test.");

                EnumerationResult<ArmadaEvent> blocked = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery
                {
                    MissionId = consumerFailed.Id,
                    EventType = "autonomous_recovery.blocked",
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, blocked.Objects.Count, "A blocked event records the consumer-failure decision.");
                AssertContains(AutonomousRecoveryOrchestrator.ConsumerTestFailureReason + ":", blocked.Objects[0].Message ?? String.Empty,
                    "The event carries the stable consumer-failure reason.");

                string ownReason = BuildGateFailureReason("TestFail", "unit-test", 1,
                    "  Failed Consumer.Data.RowCitesPastEndOfFile [3 ms]\nFailed: 1, Passed: 640");
                Mission ownFailed = await CreateFailedMissionAsync(testDb, producer, ownReason).ConfigureAwait(false);
                ownFailed.Persona = "Worker";
                await testDb.Driver.Missions.UpdateAsync(ownFailed).ConfigureAwait(false);
                await WriteTestFailEvaluationAsync(testDb, ownFailed.Id, false, "Consumer.Data.RowCitesPastEndOfFile").ConfigureAwait(false);

                await orchestrator.HandleMissionOutcomeAsync(ownFailed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "A failure in the producer's own suite is still rescued.");
                AssertEqual(ownFailed.Id, admiral.DispatchedMissions[0].ParentMissionId, "The rescue is for the producer's own failure.");
            }).ConfigureAwait(false);

            // A test failure keeps the rescue whatever the failing tests are named or assert: the
            // marker rules read the gate's own head line, never the output of the work under test.
            await RunTest("Gate failure class TestFail with authorization, quota or approval words in the test output still dispatches a rescue", async () =>
            {
                string[] outputs =
                {
                    "  Failed Api.Routes.DeleteVessel_AsViewer [12 ms]\n  Expected: 200 OK, Actual: 403 Forbidden\nFailed: 1, Passed: 812",
                    "  Failed Api.Auth.RejectsMissingToken [4 ms]\n  Assert.Equal() Failure: expected Unauthorized, actual OK\nFailed: 1, Passed: 90",
                    "  Failed Billing.QuotaTests.RejectsOverQuotaUpload [7 ms]\n  Expected quota error, got none\nFailed: 1, Passed: 44",
                    "  Failed Workflow.ApprovalTests.RequiresApprovalBeforeMerge [3 ms]\nFailed: 1, Passed: 31",
                    "  Failed Http.ClientTests.BacksOffOnRateLimit [9 ms]\n  Expected retry after rate limit\nFailed: 1, Passed: 12",
                    "  Failed Config.SettingsTests.ReadsEnvironmentVariable [2 ms]\n  environment variable ARMADA_EXAMPLE was null\nFailed: 1, Passed: 7"
                };

                for (int i = 0; i < outputs.Length; i++)
                {
                    string reason = BuildGateFailureReason("TestFail", "unit-test", 1, outputs[i]);
                    string tenantId = "ten_gate_words" + i;
                    string userId = "usr_gate_words" + i;
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                    Vessel vessel = await CreateVesselAsync(testDb, tenantId, userId).ConfigureAwait(false);
                    Mission failed = await CreateFailedMissionAsync(testDb, vessel, reason).ConfigureAwait(false);

                    IncidentService incidents = new IncidentService(testDb.Driver);
                    RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                    await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

                    await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                    AssertEqual(1, admiral.DispatchedMissions.Count, "A test failure keeps its rescue whatever its test output names. Output: " + outputs[i]);
                    AssertEqual(
                        "DoD gate failed: classification=TestFail; unit-test command exited 1",
                        AutonomousRecoveryOrchestrator.FailureMarkerText(reason),
                        "The marker rules must read only the gate head line of a TestFail reason.");
                }
            }).ConfigureAwait(false);

            await RunTest("FailureMarkerText reads a non-gate reason, and an Infra gate reason, whole", () =>
            {
                string quota = "Captain stopped: provider quota exhausted\nretry later";
                AssertEqual(quota, AutonomousRecoveryOrchestrator.FailureMarkerText(quota), "A reason that is not a gate test failure is read whole.");
                string infra = BuildGateFailureReason("Infra", "unit-test", 1, "error NU1301: Unable to load the service index for source");
                AssertEqual(infra, AutonomousRecoveryOrchestrator.FailureMarkerText(infra), "An Infra gate reason is read whole.");
                string refused = BuildGateFailureReason("TestFail", "unit-test", 1, "Failed X.Y [1 ms]");
                AssertFalse(AutonomousRecoveryOrchestrator.FailureMarkerText(refused).Contains("Failed X.Y", StringComparison.Ordinal), "A TestFail reason drops the output tail.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Judge failure rescue routes Worker below reviewer tier", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_loop", "usr_auto_loop").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_loop", "usr_auto_loop").ConfigureAwait(false);
                await testDb.Driver.Captains.CreateAsync(new Captain("standard-recovery-worker")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Tier = CaptainTierEnum.Standard,
                    AllowedPersonas = "[\"Worker\"]"
                }).ConfigureAwait(false);
                await SeedEligibleRecoveryCaptainsAsync(testDb, vessel, "Judge").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.PreferredModel = "high";
                failed.CommitHash = new string('3', 40);
                failed.ReviewComment = "Add a regression test for the null-branch case before resubmitting.";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                ArmadaSettings settings = StandardRecoveryTierSettings();
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                    testDb.Driver,
                    admiral,
                    new IncidentService(testDb.Driver),
                    new RunbookService(testDb.Driver, new LoggingModule()),
                    settings);
                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                Mission worker = admiral.DispatchedMissions.Single();
                AssertEqual("mid", worker.PreferredModel ?? "", "A Judge's high tier must not make the Worker unassignable.");
                List<Mission> recovery = await testDb.Driver.Missions.EnumerateByVoyageAsync(worker.VoyageId!).ConfigureAwait(false);
                Mission judge = recovery.Single(item => item.Persona == "Judge");
                AssertEqual("mid", judge.PreferredModel ?? "", "A chained Judge without an explicit stage tier uses its own Standard minimum.");
            }).ConfigureAwait(false);

            await RunTest("Recovery pipeline tiers do not inherit the failed mission tier", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_tier_recovery", "usr_tier_recovery").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_tier_recovery", "usr_tier_recovery").ConfigureAwait(false);
                await testDb.Driver.Captains.CreateAsync(new Captain("standard-tier-recovery-worker")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Tier = CaptainTierEnum.Standard,
                    AllowedPersonas = "[\"Worker\"]"
                }).ConfigureAwait(false);
                Pipeline pipeline = new Pipeline("RecoveryTierRouting")
                {
                    TenantId = vessel.TenantId,
                    Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "TestEngineer"),
                        new PipelineStage(3, "Judge")
                    }
                };
                pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);
                await SeedEligibleRecoveryCaptainsAsync(testDb, vessel, "TestEngineer", "Judge").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.PreferredModel = "high";
                failed.VoyageId = (await testDb.Driver.Voyages.CreateAsync(new Voyage("Parent recovery tier")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Failed
                }).ConfigureAwait(false)).Id;
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);
                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Title = "Recovery tier owner",
                    Status = ObjectiveStatusEnum.InProgress,
                    VesselIds = new List<string> { vessel.Id },
                    VoyageIds = new List<string> { failed.VoyageId! },
                    SuggestedPipelineId = pipeline.Id
                }).ConfigureAwait(false);

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                    testDb.Driver,
                    admiral,
                    new IncidentService(testDb.Driver),
                    new RunbookService(testDb.Driver, new LoggingModule()),
                    StandardRecoveryTierSettings());
                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                Mission worker = admiral.DispatchedMissions.Single();
                List<Mission> recovery = await testDb.Driver.Missions.EnumerateByVoyageAsync(worker.VoyageId!).ConfigureAwait(false);
                AssertEqual("mid", worker.PreferredModel ?? "", "The Worker must use its eligible Standard tier.");
                AssertTrue(recovery.Where(item => item.Persona == "TestEngineer" || item.Persona == "Judge")
                    .All(item => item.PreferredModel == "mid"), "Downstream stages must use their own persona tier floors.");
            }).ConfigureAwait(false);

            await RunTest("Impossible high Worker rescue refuses before any rescue record and retries after configuration", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_rescue_root_floor", "usr_rescue_root_floor").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_rescue_root_floor", "usr_rescue_root_floor").ConfigureAwait(false);
                Captain standardWorker = await testDb.Driver.Captains.CreateAsync(new Captain("standard-rescue-worker")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Model = "gpt-5.6-luna",
                    Tier = CaptainTierEnum.Standard,
                    State = CaptainStateEnum.Idle,
                    AllowedPersonas = "[\"Worker\"]"
                }).ConfigureAwait(false);
                await SeedEligibleRecoveryCaptainsAsync(testDb, vessel, "TestEngineer", "Judge").ConfigureAwait(false);
                Pipeline pipeline = await testDb.Driver.Pipelines.CreateAsync(new Pipeline("HighWorkerRecovery")
                {
                    TenantId = vessel.TenantId,
                    Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker") { PreferredModel = "high" },
                        new PipelineStage(2, "TestEngineer"),
                        new PipelineStage(3, "Judge")
                    }
                }).ConfigureAwait(false);
                Voyage parentVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Parent root rescue")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Failed
                }).ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.VoyageId = parentVoyage.Id;
                failed.PreferredModel = "high";
                failed.CommitHash = new string('6', 40);
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);
                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Title = "Root-role recovery owner",
                    Status = ObjectiveStatusEnum.InProgress,
                    VesselIds = new List<string> { vessel.Id },
                    VoyageIds = new List<string> { parentVoyage.Id },
                    SuggestedPipelineId = pipeline.Id
                }).ConfigureAwait(false);

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                    testDb.Driver,
                    admiral,
                    new IncidentService(testDb.Driver),
                    new RunbookService(testDb.Driver, new LoggingModule()),
                    StandardRecoveryTierSettings());
                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);
                AssertEqual(0, admiral.DispatchedMissions.Count,
                    "A Standard-only Worker must not dispatch a high-floor rescue.");
                AssertEqual(1, (await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false)).Count,
                    "The root-role refusal must happen before a rescue voyage is created.");
                AssertEqual(0, (await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false))
                    .Count(item => item.ParentMissionId == failed.Id),
                    "The root-role refusal must happen before a rescue mission is created.");
                AssertEqual(0, (await testDb.Driver.Missions.ReadAsync(failed.Id).ConfigureAwait(false))!.RecoveryAttempts,
                    "A root-role refusal must not spend a recovery attempt.");

                standardWorker.Tier = CaptainTierEnum.Premium;
                await testDb.Driver.Captains.UpdateAsync(standardWorker).ConfigureAwait(false);
                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);
                AssertEqual(1, admiral.DispatchedMissions.Count,
                    "The same rescue must proceed after its Worker becomes eligible.");
                AssertEqual("high", admiral.DispatchedMissions.Single().PreferredModel ?? "",
                    "The rescue keeps its high Worker floor.");
                List<Mission> recoveryStages = await testDb.Driver.Missions
                    .EnumerateByVoyageAsync(admiral.DispatchedMissions.Single().VoyageId!).ConfigureAwait(false);
                AssertTrue(recoveryStages.Any(item => item.Persona == "TestEngineer"),
                    "The configured TestEngineer remains in the rescue graph.");
                AssertTrue(recoveryStages.Any(item => item.Persona == "Judge"),
                    "The configured Judge remains in the rescue graph.");
                AssertEqual(1, (await testDb.Driver.Missions.ReadAsync(failed.Id).ConfigureAwait(false))!.RecoveryAttempts,
                    "A feasible rescue spends one recovery attempt.");
            }).ConfigureAwait(false);

            await RunTest("Rescue refuses an impossible downstream TestEngineer before creating any rescue records", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_rescue_floor", "usr_rescue_floor").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_rescue_floor", "usr_rescue_floor").ConfigureAwait(false);
                Captain premiumWorker = await testDb.Driver.Captains.CreateAsync(new Captain("premium-rescue-worker")
                {
                    TenantId = vessel.TenantId,
                    Model = "gpt-5.6-luna",
                    Tier = CaptainTierEnum.Standard,
                    State = CaptainStateEnum.Idle,
                    AllowedPersonas = "[\"Worker\"]"
                }).ConfigureAwait(false);
                await SeedEligibleRecoveryCaptainsAsync(testDb, vessel, "Judge").ConfigureAwait(false);
                Pipeline pipeline = await testDb.Driver.Pipelines.CreateAsync(new Pipeline("HighWorkerRecovery")
                {
                    TenantId = vessel.TenantId,
                    Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker") { PreferredModel = "high" },
                        new PipelineStage(2, "TestEngineer"),
                        new PipelineStage(3, "Judge")
                    }
                }).ConfigureAwait(false);
                Voyage parentVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Parent rescue")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Failed
                }).ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.VoyageId = parentVoyage.Id;
                failed.PreferredModel = "high";
                failed.CommitHash = new string('4', 40);
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);
                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Title = "High Worker recovery owner",
                    Status = ObjectiveStatusEnum.InProgress,
                    VesselIds = new List<string> { vessel.Id },
                    VoyageIds = new List<string> { parentVoyage.Id },
                    SuggestedPipelineId = pipeline.Id
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                    testDb.Driver,
                    admiral,
                    incidents,
                    new RunbookService(testDb.Driver, new LoggingModule()),
                    StandardRecoveryTierSettings());
                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                List<Mission> vesselMissions = await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
                AssertEqual(0, vesselMissions.Count(item => item.ParentMissionId == failed.Id),
                    "An impossible downstream stage must not create a rescue mission.");
                AssertEqual(1, (await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false)).Count,
                    "An impossible downstream stage must be rejected before the rescue voyage is created.");
                Mission? refreshedFailure = await testDb.Driver.Missions.ReadAsync(failed.Id).ConfigureAwait(false);
                AssertEqual(failed.RecoveryAttempts, refreshedFailure!.RecoveryAttempts,
                    "A configuration refusal must not spend a recovery attempt.");

                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");
                List<Incident> active = await incidents.EnumerateActiveAsync(auth, new IncidentQuery
                {
                    MissionId = failed.Id
                }).ConfigureAwait(false);
                AssertTrue(active.Any(item => (item.RecoveryNotes ?? String.Empty).Contains("rescue_unassignable_by_construction", StringComparison.Ordinal)),
                    "The existing incident must record why rescue was refused.");

                // Make the Worker eligible, then prove the missing downstream TestEngineer still blocks all writes.
                premiumWorker.Tier = CaptainTierEnum.Premium;
                await testDb.Driver.Captains.UpdateAsync(premiumWorker).ConfigureAwait(false);
                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);
                AssertEqual(0, (await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false))
                    .Count(item => item.ParentMissionId == failed.Id),
                    "The missing TestEngineer must still prevent a rescue mission after Worker becomes eligible.");
                AssertEqual(1, (await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false)).Count,
                    "The missing TestEngineer must still prevent a rescue voyage after Worker becomes eligible.");
                AssertEqual(0, (await testDb.Driver.Missions.ReadAsync(failed.Id).ConfigureAwait(false))!.RecoveryAttempts,
                    "The downstream refusal must leave the recovery budget unchanged.");

                await SeedEligibleRecoveryCaptainsAsync(testDb, vessel, "TestEngineer").ConfigureAwait(false);
                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);
                AssertEqual(1, admiral.DispatchedMissions.Count,
                    "An eligible downstream TestEngineer must allow the rescue after the roster changes.");
                AssertEqual("high", admiral.DispatchedMissions[0].PreferredModel ?? "",
                    "A matched rescue keeps its Premium Worker floor.");
                List<Mission> recoveryStages = await testDb.Driver.Missions
                    .EnumerateByVoyageAsync(admiral.DispatchedMissions[0].VoyageId!).ConfigureAwait(false);
                AssertTrue(recoveryStages.Any(item => item.Persona == "TestEngineer"),
                    "The newly eligible TestEngineer must be created as part of the rescue graph.");
                AssertTrue(recoveryStages.Any(item => item.Persona == "Judge"),
                    "The already eligible Judge must remain in the rescue graph.");
                AssertEqual(1, (await testDb.Driver.Missions.ReadAsync(failed.Id).ConfigureAwait(false))!.RecoveryAttempts,
                    "A dispatched matched rescue spends one recovery attempt.");
            }).ConfigureAwait(false);

            await RunTest("AreComparableIdenticalTestSets: only complete, non-empty, identical sets compare equal", () =>
            {
                AutonomousRecoveryOrchestrator.StoredFailedTestSet ab = FailedTestSet(false, "A", "B");
                AutonomousRecoveryOrchestrator.StoredFailedTestSet abAgain = FailedTestSet(false, "A", "B");
                AutonomousRecoveryOrchestrator.StoredFailedTestSet ba = FailedTestSet(false, "B", "A");
                AutonomousRecoveryOrchestrator.StoredFailedTestSet abc = FailedTestSet(false, "A", "B", "C");
                AutonomousRecoveryOrchestrator.StoredFailedTestSet empty = FailedTestSet(false);
                AutonomousRecoveryOrchestrator.StoredFailedTestSet abOverflow = FailedTestSet(true, "A", "B");

                AssertTrue(AutonomousRecoveryOrchestrator.AreComparableIdenticalTestSets(ab, abAgain), "The same complete set is a repeat.");
                AssertTrue(AutonomousRecoveryOrchestrator.AreComparableIdenticalTestSets(ab, ba), "The same failing tests in a different order (parallel-runner output) are still a repeat.");
                AssertFalse(AutonomousRecoveryOrchestrator.AreComparableIdenticalTestSets(ab, abc), "A different length is not identical.");
                AssertFalse(AutonomousRecoveryOrchestrator.AreComparableIdenticalTestSets(empty, empty), "Two empty sets prove nothing and are not a repeat.");
                AssertFalse(AutonomousRecoveryOrchestrator.AreComparableIdenticalTestSets(abOverflow, abAgain), "An overflowed set is not comparable.");
                AssertFalse(AutonomousRecoveryOrchestrator.AreComparableIdenticalTestSets(ab, abOverflow), "An overflowed set is not comparable on either side.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A rescue that repeated its parent's identical complete test set is blocked and names the tests", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_repeat", "usr_repeat").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_repeat", "usr_repeat").ConfigureAwait(false);

                Mission parent = await CreateFailedMissionAsync(testDb, vessel,
                    BuildGateFailureReason("TestFail", "unit-test", 1, "Failed X.Y.StaleTest [1 ms]")).ConfigureAwait(false);
                await WriteTestFailEvaluationAsync(testDb, parent.Id, false, "X.Y.StaleTest", "X.Y.OtherStale").ConfigureAwait(false);

                Mission rescue = await CreateRescueMissionAsync(testDb, vessel, parent.Id,
                    BuildGateFailureReason("TestFail", "unit-test", 1, "Failed X.Y.StaleTest [1 ms]")).ConfigureAwait(false);
                await WriteTestFailEvaluationAsync(testDb, rescue.Id, false, "X.Y.StaleTest", "X.Y.OtherStale").ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks, TwoAttemptSettings());

                await orchestrator.HandleMissionOutcomeAsync(rescue, false).ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count, "An identical repeat must not dispatch a further rescue.");

                AuthContext auth = AuthContext.Authenticated("ten_repeat", "usr_repeat", false, true, "UnitTest");
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = rescue.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, incidentPage.Objects.Count, "The operator still needs an incident to act on.");
                string notes = incidentPage.Objects[0].RecoveryNotes ?? String.Empty;
                AssertTrue(notes.Contains("Repeated identical test failure", StringComparison.Ordinal), "The incident must name the repeat. Notes: " + notes);
                AssertTrue(notes.Contains("X.Y.StaleTest", StringComparison.Ordinal), "The incident must name the repeated tests. Notes: " + notes);

                EnumerationResult<ArmadaEvent> blocked = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery
                {
                    MissionId = rescue.Id,
                    EventType = "autonomous_recovery.blocked",
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, blocked.Objects.Count, "A blocked event records the decision.");
                AssertTrue((blocked.Objects[0].Message ?? String.Empty).Contains("repeated_identical_test_failure", StringComparison.Ordinal),
                    "The blocked reason must be the specific repeated-failure reason. Message: " + blocked.Objects[0].Message);
            }).ConfigureAwait(false);

            await RunTest("A rescue whose test set differs from its parent keeps today's behaviour, not the repeat block", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_differ", "usr_differ").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_differ", "usr_differ").ConfigureAwait(false);

                Mission parent = await CreateFailedMissionAsync(testDb, vessel,
                    BuildGateFailureReason("TestFail", "unit-test", 1, "Failed X.Y.FirstTest [1 ms]")).ConfigureAwait(false);
                await WriteTestFailEvaluationAsync(testDb, parent.Id, false, "X.Y.FirstTest").ConfigureAwait(false);

                Mission rescue = await CreateRescueMissionAsync(testDb, vessel, parent.Id,
                    BuildGateFailureReason("TestFail", "unit-test", 1, "Failed X.Y.DifferentTest [1 ms]")).ConfigureAwait(false);
                await WriteTestFailEvaluationAsync(testDb, rescue.Id, false, "X.Y.DifferentTest").ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks, TwoAttemptSettings());

                await orchestrator.HandleMissionOutcomeAsync(rescue, false).ConfigureAwait(false);

                EnumerationResult<ArmadaEvent> blocked = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery
                {
                    MissionId = rescue.Id,
                    EventType = "autonomous_recovery.blocked",
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, blocked.Objects.Count, "A differing-set rescue still blocks on the generic auto-rescue rule.");
                AssertFalse((blocked.Objects[0].Message ?? String.Empty).Contains("repeated_identical_test_failure", StringComparison.Ordinal),
                    "A different failing set is not a repeated identical failure. Message: " + blocked.Objects[0].Message);
            }).ConfigureAwait(false);

            await RunTest("An overflowed rescue set is not comparable, so it is not a repeated identical failure", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_overflow", "usr_overflow").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_overflow", "usr_overflow").ConfigureAwait(false);

                Mission parent = await CreateFailedMissionAsync(testDb, vessel,
                    BuildGateFailureReason("TestFail", "unit-test", 1, "Failed X.Y.StaleTest [1 ms]")).ConfigureAwait(false);
                await WriteTestFailEvaluationAsync(testDb, parent.Id, true, "X.Y.StaleTest").ConfigureAwait(false);

                Mission rescue = await CreateRescueMissionAsync(testDb, vessel, parent.Id,
                    BuildGateFailureReason("TestFail", "unit-test", 1, "Failed X.Y.StaleTest [1 ms]")).ConfigureAwait(false);
                await WriteTestFailEvaluationAsync(testDb, rescue.Id, true, "X.Y.StaleTest").ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks, TwoAttemptSettings());

                await orchestrator.HandleMissionOutcomeAsync(rescue, false).ConfigureAwait(false);

                EnumerationResult<ArmadaEvent> blocked = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery
                {
                    MissionId = rescue.Id,
                    EventType = "autonomous_recovery.blocked",
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, blocked.Objects.Count, "The rescue still blocks.");
                AssertFalse((blocked.Objects[0].Message ?? String.Empty).Contains("repeated_identical_test_failure", StringComparison.Ordinal),
                    "An overflowed set cannot prove a repeat. Message: " + blocked.Objects[0].Message);
            }).ConfigureAwait(false);

            await RunTest("Recovery budget exhaustion still wins over the repeated-failure guard", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_budget", "usr_budget").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_budget", "usr_budget").ConfigureAwait(false);

                Mission parent = await CreateFailedMissionAsync(testDb, vessel,
                    BuildGateFailureReason("TestFail", "unit-test", 1, "Failed X.Y.StaleTest [1 ms]")).ConfigureAwait(false);
                await WriteTestFailEvaluationAsync(testDb, parent.Id, false, "X.Y.StaleTest").ConfigureAwait(false);

                Mission rescue = await CreateRescueMissionAsync(testDb, vessel, parent.Id,
                    BuildGateFailureReason("TestFail", "unit-test", 1, "Failed X.Y.StaleTest [1 ms]")).ConfigureAwait(false);
                rescue.RecoveryAttempts = 2; // At the default max: budget is exhausted before the guard runs.
                await testDb.Driver.Missions.UpdateAsync(rescue).ConfigureAwait(false);
                await WriteTestFailEvaluationAsync(testDb, rescue.Id, false, "X.Y.StaleTest").ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks, TwoAttemptSettings());

                await orchestrator.HandleMissionOutcomeAsync(rescue, false).ConfigureAwait(false);

                EnumerationResult<ArmadaEvent> blocked = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery
                {
                    MissionId = rescue.Id,
                    EventType = "autonomous_recovery.blocked",
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, blocked.Objects.Count, "The rescue still blocks.");
                AssertTrue((blocked.Objects[0].Message ?? String.Empty).Contains("budget", StringComparison.Ordinal),
                    "The budget hard-block wins over the repeated-failure guard. Message: " + blocked.Objects[0].Message);
                AssertFalse((blocked.Objects[0].Message ?? String.Empty).Contains("repeated_identical_test_failure", StringComparison.Ordinal),
                    "The guard must not override a hard-block. Message: " + blocked.Objects[0].Message);
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

                await SeedEligibleRecoveryCaptainsAsync(testDb, vessel, "Worker", "Judge").ConfigureAwait(false);
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
                AssertContains(DispatchContextPackStager.NotStagedLinePrefix, rescue.Description ?? "",
                    "A rescue with no context pack service names that no pack was staged.");

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

            await RunTest("A rescue stages its own context pack at the rescue start ref", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_recovery", "usr_auto_recovery").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.CommitHash = "2222222222222222222222222222222222222222";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);
                RecordingCodeIndexService codeIndex = new RecordingCodeIndexService();
                codeIndex.PackResponse = new ContextPackResponse();
                codeIndex.PackResponse.PrestagedFiles.Add(PrestagedFile.FromContent(DispatchContextPackStager.ContextPackDestPath, "# Context Pack\n\nGoal pack content"));
                orchestrator.ContextPackStager = new DispatchContextPackStager(testDb.Driver, codeIndex, new ArmadaSettings(), null);

                await SeedEligibleRecoveryCaptainsAsync(testDb, vessel, "Worker", "Judge").ConfigureAwait(false);
                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                List<Mission> vesselMissions = await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
                Mission? rescue = vesselMissions.FirstOrDefault(item => item.ParentMissionId == failed.Id);
                AssertTrue(rescue != null, "Expected a linked rescue mission.");
                AssertEqual(1, codeIndex.ContextPackRequests.Count, "The rescue builds one pack.");
                AssertEqual(failed.CommitHash, codeIndex.ContextPackRequests[0].StartRef, "The pack is built at the rescue start ref.");
                AssertTrue((rescue!.PrestagedFiles ?? new List<PrestagedFile>()).Any(file => file.DestPath == DispatchContextPackStager.ContextPackDestPath),
                    "The rescue carries the staged context pack.");
                AssertFalse((rescue.Description ?? "").Contains(DispatchContextPackStager.NotStagedLinePrefix, StringComparison.Ordinal),
                    "A staged pack adds no not-staged line.");
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

            await RunTest("ReadOnlyJudgeFollowUpCapture failure is contained and cancellation propagates", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_capture_seam", "usr_auto_capture_seam").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_capture_seam", "usr_auto_capture_seam").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.Mode = MissionModeEnum.Research;
                failed.ReviewComment = "Retain this recommendation.";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                RunbookService runbooks = new RunbookService(testDb.Driver, logging);
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator contained = new AutonomousRecoveryOrchestrator(
                    testDb.Driver, admiral, incidents, runbooks, new ArmadaSettings(), logging,
                    null, null, null, null, null, null, null,
                    (_, _, _, _) => throw new InvalidOperationException("injected capture failure"));

                await contained.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated("ten_auto_capture_seam", "usr_auto_capture_seam", false, true, "UnitTest");
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = failed.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, incidentPage.Objects.Count, "A capture write fault must not stop recovery bookkeeping");

                using CancellationTokenSource cts = new CancellationTokenSource();
                Mission cancelled = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                cancelled.Persona = "Judge";
                cancelled.Mode = MissionModeEnum.Research;
                cancelled.ReviewComment = "Cancellation seam.";
                await testDb.Driver.Missions.UpdateAsync(cancelled).ConfigureAwait(false);
                AutonomousRecoveryOrchestrator cancelling = new AutonomousRecoveryOrchestrator(
                    testDb.Driver, admiral, incidents, runbooks, new ArmadaSettings(), logging,
                    null, null, null, null, null, null, null,
                    (_, _, _, _) =>
                    {
                        cts.Cancel();
                        throw new OperationCanceledException(cts.Token);
                    });
                System.Reflection.MethodInfo captureMethod = typeof(AutonomousRecoveryOrchestrator).GetMethod(
                    "EnsureReadOnlyJudgeFollowUpAsync",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                await AssertThrowsAsync<OperationCanceledException>(
                    async () => await ((Task)captureMethod.Invoke(cancelling, new object[] { cancelled, cts.Token })!)
                        .ConfigureAwait(false),
                    "Requested cancellation must escape the capture seam").ConfigureAwait(false);
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
                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);
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

                await SeedEligibleRecoveryCaptainsAsync(testDb, vessel, "Worker", "Judge").ConfigureAwait(false);
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

            await RunTest("Recovery reuses an older open incident hidden behind newer closed incidents", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_hidden", "usr_auto_hidden").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_hidden", "usr_auto_hidden").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Review denied: missing tests").ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated("ten_auto_hidden", "usr_auto_hidden", false, true, "UnitTest");
                Incident open = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                {
                    Title = "Older open incident",
                    Status = IncidentStatusEnum.Open,
                    MissionId = failed.Id,
                    VesselId = vessel.Id
                }).ConfigureAwait(false);
                await Task.Delay(20).ConfigureAwait(false);
                for (int i = 0; i < 25; i++)
                {
                    await incidents.CreateAsync(auth, new IncidentUpsertRequest
                    {
                        Title = "Newer closed incident " + i,
                        Status = IncidentStatusEnum.Closed,
                        RootCause = "Closed by an operator",
                        MissionId = failed.Id,
                        VesselId = vessel.Id
                    }).ConfigureAwait(false);
                }

                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver), incidents, new RunbookService(testDb.Driver, new LoggingModule()));
                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                List<Incident> active = (await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = failed.Id,
                    ExcludeTerminal = true,
                    PageNumber = 1,
                    PageSize = 100
                }).ConfigureAwait(false)).Objects;
                AssertEqual(1, active.Count, "Recovery must not open a duplicate while an older incident is still open.");
                AssertEqual(open.Id, active[0].Id, "Recovery updates the existing open incident.");
                AssertContains("human review", active[0].RecoveryNotes ?? "", "The existing incident receives the recovery note.");
            }).ConfigureAwait(false);

            await RunTest("Rescue dispatch records voyage.dispatched for the rescue voyage once its missions exist", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_rescue_event", "usr_rescue_event").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_rescue_event", "usr_rescue_event").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                failed.Persona = "Judge";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                AutonomousRecoveryOrchestrator orchestrator = new AutonomousRecoveryOrchestrator(
                    testDb.Driver, new RecordingAdmiralService(testDb.Driver), new IncidentService(testDb.Driver),
                    new RunbookService(testDb.Driver, logging), new ArmadaSettings(), logging);

                await SeedEligibleRecoveryCaptainsAsync(testDb, vessel, "Worker", "Judge").ConfigureAwait(false);
                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                List<Mission> vesselMissions = await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
                Mission? rescue = vesselMissions.FirstOrDefault(item => item.ParentMissionId == failed.Id);
                AssertTrue(rescue != null, "Expected a linked rescue mission.");
                AssertFalse(String.IsNullOrWhiteSpace(rescue!.VoyageId), "Rescue mission must belong to a rescue voyage");
                int rescueMissionCount = vesselMissions.Count(item => item.VoyageId == rescue.VoyageId);

                List<ArmadaEvent> dispatched = (await testDb.Driver.Events.EnumerateByTypeAsync(VoyageDispatchedEvent.EventType).ConfigureAwait(false))
                    .Where(evt => evt.VoyageId == rescue.VoyageId)
                    .ToList();
                AssertEqual(1, dispatched.Count, "The rescue voyage is announced once");
                AssertEqual(vessel.Id, dispatched[0].VesselId, "The event names the rescued vessel");
                AssertEqual(vessel.TenantId, dispatched[0].TenantId, "The event takes the rescue voyage's owner");
                AssertContains("(" + rescueMissionCount + " mission", dispatched[0].Message, "The event counts every mission the rescue created");
            }).ConfigureAwait(false);

            await RunTest("Rescue on a full sibling lane writes no voyage and records the deferral", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_rescue_full", "usr_rescue_full").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_rescue_full", "usr_rescue_full").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                failed.Persona = "Judge";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver)
                {
                    CapacityRefusal = new FleetCapacityAdmissionException(
                        "sibling_lane_capacity_reached", "Sibling lane capacity is full for vessel " + vessel.Id + ": 4 active work unit(s), limit 4.",
                        4, 4, vessel.Id, new List<string> { vessel.Id })
                };
                AutonomousRecoveryOrchestrator orchestrator = new AutonomousRecoveryOrchestrator(
                    testDb.Driver, admiral, new IncidentService(testDb.Driver),
                    new RunbookService(testDb.Driver, logging), new ArmadaSettings(), logging);

                await SeedEligibleRecoveryCaptainsAsync(testDb, vessel, "Worker", "Judge").ConfigureAwait(false);
                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);
                await orchestrator.SweepAsync().ConfigureAwait(false);
                await orchestrator.SweepAsync().ConfigureAwait(false);

                List<Voyage> voyages = await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false);
                AssertEqual(0, voyages.Count, "a rescue refused for capacity writes no voyage, not even a cancelled one; found: "
                    + String.Join(", ", voyages.Select(v => v.Title + "/" + v.Status)));
                List<Mission> rescues = (await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false))
                    .Where(m => m.ParentMissionId == failed.Id).ToList();
                AssertEqual(0, rescues.Count, "no rescue mission is created while the lane is full");
                AssertEqual(0, admiral.DispatchedMissions.Count, "nothing is dispatched while the lane is full");
                List<ArmadaEvent> deferred = await testDb.Driver.Events.EnumerateByTypeAsync("autonomous_recovery.capacity_deferred").ConfigureAwait(false);
                AssertEqual(1, deferred.Count, "three refusals for the same full lane record the deferral once");
                AssertContains("Sibling lane capacity is full", deferred[0].Message, "the deferral names the full lane");
                AuthContext auth = AuthContext.Authenticated("ten_rescue_full", "usr_rescue_full", false, true, "UnitTest");
                Incident incident = (await new IncidentService(testDb.Driver).EnumerateActiveAsync(auth, new IncidentQuery { MissionId = failed.Id }).ConfigureAwait(false)).Single();
                AssertContains("rescue_capacity_deferred: sibling_lane_capacity_reached", incident.RecoveryNotes ?? "", "the incident names the capacity deferral");
                AssertEqual(1, (incident.RecoveryNotes ?? "").Split("rescue_capacity_deferred").Length - 1, "the incident carries the deferral once");
                AssertEqual(0, (await testDb.Driver.Missions.ReadAsync(failed.Id).ConfigureAwait(false))!.RecoveryAttempts, "a capacity deferral spends no recovery attempt");

                admiral.CapacityRefusal = new FleetCapacityAdmissionException(
                    "sibling_lane_capacity_reached", "Sibling lane capacity is full for vessel " + vessel.Id + ": 5 active work unit(s), limit 5.",
                    5, 5, vessel.Id, new List<string> { vessel.Id });
                await orchestrator.SweepAsync().ConfigureAwait(false);
                deferred = await testDb.Driver.Events.EnumerateByTypeAsync("autonomous_recovery.capacity_deferred").ConfigureAwait(false);
                AssertEqual(2, deferred.Count, "a refusal with a changed limit is recorded again");
            }).ConfigureAwait(false);

            await RunTest("An incident an operator mitigated with a written cause stops the rescue; a lifecycle mitigation does not", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_mitigated", "usr_auto_mitigated").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_mitigated", "usr_auto_mitigated").ConfigureAwait(false);
                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

                Mission mitigatedByOperator = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                Mission mitigatedAutomatically = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                DispatchHold hold = new DispatchHold();
                AutonomousRecoveryOrchestrator orchestrator = new AutonomousRecoveryOrchestrator(
                    testDb.Driver, admiral, incidents, runbooks, new ArmadaSettings(), new LoggingModule(),
                    null, null, null, null, null, null, null, null, hold);

                // The held sweep opens each failure's incident without dispatching a rescue.
                hold.Engage("redeploy window", "session-hold");
                await orchestrator.SweepAsync().ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");
                Incident operatorIncident = (await incidents.EnumerateActiveAsync(auth, new IncidentQuery { MissionId = mitigatedByOperator.Id }).ConfigureAwait(false)).Single();
                await incidents.UpdateAsync(auth, operatorIncident.Id, new IncidentUpsertRequest
                {
                    Status = IncidentStatusEnum.Mitigated,
                    RootCause = "The stage branch was still checked out in the previous stage's dock.",
                    RecoveryNotes = "Operator mitigated: restart the stage once the earlier dock is released."
                }).ConfigureAwait(false);
                Incident automaticIncident = (await incidents.EnumerateActiveAsync(auth, new IncidentQuery { MissionId = mitigatedAutomatically.Id }).ConfigureAwait(false)).Single();
                await incidents.UpdateAutomaticallyAsync(auth, automaticIncident.Id, new IncidentUpsertRequest
                {
                    Status = IncidentStatusEnum.Mitigated
                }).ConfigureAwait(false);

                hold.Clear();
                await orchestrator.SweepAsync().ConfigureAwait(false);
                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "only the failure whose incident the lifecycle mitigated is rescued");
                AssertEqual(mitigatedAutomatically.Id, admiral.DispatchedMissions[0].ParentMissionId, "the rescue belongs to the automatically mitigated failure");
                AssertEqual(0, (await testDb.Driver.Missions.ReadAsync(mitigatedByOperator.Id).ConfigureAwait(false))!.RecoveryAttempts,
                    "recovery spends no budget on a failure an operator mitigated");
                List<ArmadaEvent> skipped = await testDb.Driver.Events.EnumerateByTypeAsync("autonomous_recovery.skipped_mitigated_incident").ConfigureAwait(false);
                AssertEqual(1, skipped.Count, "two sweeps record the skip once");
                AssertContains(operatorIncident.Id, skipped[0].Message, "the skip names the mitigated incident");
            }).ConfigureAwait(false);

            await RunTest("Rescue refused for capacity after its voyage is written removes the empty voyage", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_rescue_late", "usr_rescue_late").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_rescue_late", "usr_rescue_late").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                failed.Persona = "Judge";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver)
                {
                    // Capacity fills between the check and the admission.
                    ThrowOnDispatch = new FleetCapacityAdmissionException(
                        "sibling_lane_capacity_reached", "Sibling lane capacity is full for vessel " + vessel.Id + ": 4 active work unit(s), limit 4.",
                        4, 4, vessel.Id, new List<string> { vessel.Id })
                };
                AutonomousRecoveryOrchestrator orchestrator = new AutonomousRecoveryOrchestrator(
                    testDb.Driver, admiral, new IncidentService(testDb.Driver),
                    new RunbookService(testDb.Driver, logging), new ArmadaSettings(), logging);

                await SeedEligibleRecoveryCaptainsAsync(testDb, vessel, "Worker", "Judge").ConfigureAwait(false);
                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);
                await orchestrator.SweepAsync().ConfigureAwait(false);

                List<Voyage> voyages = await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false);
                AssertEqual(0, voyages.Count, "the rescue voyage that never received a mission is removed, not left cancelled; found: "
                    + String.Join(", ", voyages.Select(v => v.Title + "/" + v.Status)));
            }).ConfigureAwait(false);

            await RunTest("Cancelled voyage closes every active incident of the failed mission beyond one page", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_many_open", "usr_auto_many_open").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_many_open", "usr_auto_many_open").ConfigureAwait(false);
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
                AuthContext auth = AuthContext.Authenticated("ten_auto_many_open", "usr_auto_many_open", false, true, "UnitTest");
                for (int i = 0; i < 30; i++)
                {
                    await incidents.CreateAsync(auth, new IncidentUpsertRequest
                    {
                        Title = "Open incident " + i,
                        Status = IncidentStatusEnum.Open,
                        MissionId = failed.Id,
                        VesselId = vessel.Id
                    }).ConfigureAwait(false);
                }

                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver), incidents, new RunbookService(testDb.Driver, new LoggingModule()));
                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                List<Incident> active = (await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = failed.Id,
                    ExcludeTerminal = true,
                    PageNumber = 1,
                    PageSize = 100
                }).ConfigureAwait(false)).Objects;
                AssertEqual(0, active.Count, "Every active incident of a cancelled-voyage mission is closed, not only the first page.");
                List<Incident> closedIncidents = (await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = failed.Id,
                    Status = IncidentStatusEnum.Closed,
                    PageNumber = 1,
                    PageSize = 100
                }).ConfigureAwait(false)).Objects;
                AssertEqual(30, closedIncidents.Count);
                AssertTrue(closedIncidents.All(item => item.ClosedAutomatically && item.RootCauseWrittenBy == null),
                    "Recovery records each close as automatic, with no person-written cause.");
            }).ConfigureAwait(false);

            await RunTest("Recovery policy gates are released once no application holds them", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_gate", "usr_auto_gate").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_gate", "usr_auto_gate").ConfigureAwait(false);
                Mission first = await CreateFailedMissionAsync(testDb, vessel, "Review denied: missing tests").ConfigureAwait(false);
                Mission second = await CreateFailedMissionAsync(testDb, vessel, "Review denied: missing tests").ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver), incidents, new RunbookService(testDb.Driver, new LoggingModule()));
                await Task.WhenAll(
                    orchestrator.HandleMissionOutcomeAsync(first, false),
                    orchestrator.HandleMissionOutcomeAsync(first, false),
                    orchestrator.HandleMissionOutcomeAsync(second, false)).ConfigureAwait(false);

                AssertEqual(0, orchestrator.MissionLockCount, "No per-mission gate remains after every policy application finished.");
                AssertEqual(1, await CountIncidentsAsync(incidents, vessel, first.Id).ConfigureAwait(false),
                    "Concurrent applications for one mission stay serialized.");
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

            await RunTest("Sweep does not let handled failures consume the recovery cap", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_sweep_handled", "usr_auto_sweep_handled").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_sweep_handled", "usr_auto_sweep_handled").ConfigureAwait(false);
                DateTime oldest = DateTime.UtcNow.AddMinutes(-20);

                for (int idx = 0; idx < 11; idx++)
                {
                    Mission handled = await CreateFailedMissionAsync(testDb, vessel, "Previously handled failure " + idx).ConfigureAwait(false);
                    handled.RecoveryAttempts = 1;
                    handled.LastRecoveryActionUtc = oldest.AddSeconds(idx);
                    handled.CompletedUtc = oldest.AddSeconds(idx);
                    handled.LastUpdateUtc = oldest.AddSeconds(idx);
                    await testDb.Driver.Missions.UpdateAsync(handled).ConfigureAwait(false);
                }

                Mission unhandled = await CreateFailedMissionAsync(testDb, vessel, "New report-only failure").ConfigureAwait(false);
                unhandled.Persona = "Judge";
                unhandled.Mode = MissionModeEnum.Research;
                unhandled.CompletedUtc = oldest.AddMinutes(1);
                unhandled.LastUpdateUtc = oldest.AddMinutes(1);
                await testDb.Driver.Missions.UpdateAsync(unhandled).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                Mission? after = await testDb.Driver.Missions.ReadAsync(unhandled.Id).ConfigureAwait(false);
                AssertTrue(after!.LastRecoveryActionUtc.HasValue, "Handled failures ahead of the candidate must not starve it.");
                AssertEqual(1, after.RecoveryAttempts, "The unhandled read-only failure should receive one bounded policy action.");
                AssertEqual(0, admiral.DispatchedMissions.Count, "Research failures must preserve read-only mode and never dispatch a rescue.");

                AuthContext auth = AuthContext.Authenticated("ten_auto_sweep_handled", "usr_auto_sweep_handled", false, true, "UnitTest");
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = unhandled.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, incidentPage.Objects.Count, "The unhandled failure must receive its incident in the same sweep.");
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

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "Only the non-terminal failure should be processed by the sweep.");
                AssertEqual(liveFailed.Id, admiral.DispatchedMissions[0].ParentMissionId, "Rescue should belong to the non-terminal failure.");

                Mission? terminalAfter = await testDb.Driver.Missions.ReadAsync(terminalFailed.Id).ConfigureAwait(false);
                AssertFalse(terminalAfter!.LastRecoveryActionUtc.HasValue, "Terminal-voyage candidate must be excluded from sweep selection.");

                Mission? liveAfter = await testDb.Driver.Missions.ReadAsync(liveFailed.Id).ConfigureAwait(false);
                AssertTrue(liveAfter!.LastRecoveryActionUtc.HasValue, "Non-terminal failure should be processed by the sweep.");
            }).ConfigureAwait(false);

            await RunTest("Rescue carries the current linked objective contract and full parent output pointer", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                const string tenantId = "ten_rescue_current_objective";
                const string userId = "usr_rescue_current_objective";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, tenantId, userId).ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                failed.Persona = "Worker";
                failed.Description = "Old mission scope and old acceptance criterion.";
                failed = await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Current objective revision",
                    Description = "Use the current objective scope during rescue.",
                    Status = ObjectiveStatusEnum.InProgress,
                    MissionIds = new List<string> { failed.Id },
                    AcceptanceCriteria = new List<string>
                    {
                        "The rescue uses the current objective revision, not the saved mission scope."
                    }
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                    testDb.Driver,
                    admiral,
                    incidents,
                    new RunbookService(testDb.Driver, new LoggingModule()));

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "A recoverable failure with a linked objective still dispatches one rescue.");
                string rescueDescription = admiral.DispatchedMissions[0].Description ?? String.Empty;
                AssertContains("<!-- armada-objective-brief:" + objective.Id + " -->", rescueDescription,
                    "The rescue carries a canonical brief for the current linked objective.");
                AssertContains(objective.AcceptanceCriteria[0], rescueDescription,
                    "The rescue carries the current objective criterion even when the saved mission brief is older.");
                AssertContains(objective.Description!, rescueDescription,
                    "The rescue carries the current objective scope.");
                AssertContains("mission-output:" + failed.Id, rescueDescription,
                    "The rescue keeps the complete failed-mission output pointer.");
            }).ConfigureAwait(false);

            await RunTest("Rescue resolves the brief-marked child objective over a stale objective frame", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                const string tenantId = "ten_rescue_child_objective";
                const string userId = "usr_rescue_child_objective";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, tenantId, userId).ConfigureAwait(false);
                Voyage parentVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Objective rescue parent", "")
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Status = VoyageStatusEnum.InProgress
                }).ConfigureAwait(false);
                Pipeline stalePipeline = await testDb.Driver.Pipelines.CreateAsync(new Pipeline("Stale umbrella recovery pipeline")
                {
                    TenantId = tenantId,
                    Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge")
                    }
                }).ConfigureAwait(false);

                Objective stale = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Stale umbrella objective",
                    Description = "This parent frame is older than the child brief.",
                    Status = ObjectiveStatusEnum.InProgress,
                    SuggestedPipelineId = stalePipeline.Id,
                    VoyageIds = new List<string> { parentVoyage.Id },
                    AcceptanceCriteria = new List<string> { "The stale umbrella criterion is no longer the active contract." }
                }).ConfigureAwait(false);

                Objective child = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Current child objective",
                    Description = "Use the specific child scope.",
                    Status = ObjectiveStatusEnum.InProgress,
                    ParentObjectiveId = stale.Id,
                    VoyageIds = new List<string> { parentVoyage.Id },
                    AcceptanceCriteria = new List<string> { "The child criterion is the active contract." }
                }).ConfigureAwait(false);
                parentVoyage.Description = ObjectiveBriefRenderer.Render(child);
                await testDb.Driver.Voyages.UpdateAsync(parentVoyage).ConfigureAwait(false);

                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                failed.Persona = "Worker";
                failed.VoyageId = parentVoyage.Id;
                failed.Description = ObjectiveBriefRenderer.Render(stale);
                failed = await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                    testDb.Driver,
                    admiral,
                    new IncidentService(testDb.Driver),
                    new RunbookService(testDb.Driver, new LoggingModule()));

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "A recoverable stage failure creates its rescue Worker.");
                string rescueDescription = admiral.DispatchedMissions[0].Description ?? String.Empty;
                AssertContains("<!-- armada-objective-brief:" + child.Id + " -->", rescueDescription,
                    "The rescue uses the objective selected by the existing canonical voyage brief marker.");
                List<string> activeCriteria = JudgeAcceptanceWalk.ExtractCriteria(rescueDescription);
                AssertEqual(1, activeCriteria.Count, "Only the current marked objective frame supplies the active criteria.");
                AssertEqual(child.AcceptanceCriteria[0], activeCriteria[0], "The stale objective frame does not replace the current child criterion.");
                AssertTrue(JudgeAcceptanceWalk.ExtractCriteria(rescueDescription).All(item =>
                    !String.Equals(item, stale.AcceptanceCriteria[0], StringComparison.Ordinal)),
                    "The stale frame is not promoted into the current rescue contract.");
            }).ConfigureAwait(false);

            await RunTest("Every chained rescue stage and voyage retain the current child objective contract", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                const string tenantId = "ten_rescue_child_pipeline";
                const string userId = "usr_rescue_child_pipeline";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, tenantId, userId).ConfigureAwait(false);
                Pipeline pipeline = await testDb.Driver.Pipelines.CreateAsync(new Pipeline("Child objective rescue pipeline")
                {
                    TenantId = tenantId,
                    Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "TestEngineer"),
                        new PipelineStage(3, "Judge")
                    }
                }).ConfigureAwait(false);
                Voyage parentVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Parent child voyage", "")
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Status = VoyageStatusEnum.Failed
                }).ConfigureAwait(false);
                Objective stale = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Old umbrella scope",
                    Status = ObjectiveStatusEnum.InProgress,
                    VoyageIds = new List<string> { parentVoyage.Id },
                    AcceptanceCriteria = new List<string> { "The stale umbrella criterion must not govern the rescue." }
                }).ConfigureAwait(false);
                Objective child = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Current child scope",
                    Description = "Keep the contract for this specific child objective.",
                    Status = ObjectiveStatusEnum.InProgress,
                    ParentObjectiveId = stale.Id,
                    VoyageIds = new List<string> { parentVoyage.Id },
                    SuggestedPipelineId = pipeline.Id,
                    AcceptanceCriteria = new List<string> { "The current child criterion must govern every rescue stage." }
                }).ConfigureAwait(false);
                parentVoyage.Description = ObjectiveBriefRenderer.Render(child);
                await testDb.Driver.Voyages.UpdateAsync(parentVoyage).ConfigureAwait(false);

                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.VoyageId = parentVoyage.Id;
                failed.Description = ObjectiveBriefRenderer.Render(stale);
                failed = await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                    testDb.Driver,
                    admiral,
                    new IncidentService(testDb.Driver),
                    new RunbookService(testDb.Driver, new LoggingModule()));

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "The rescue Worker is dispatched once.");
                Mission worker = admiral.DispatchedMissions[0];
                List<Mission> stages = await testDb.Driver.Missions.EnumerateByVoyageAsync(worker.VoyageId!).ConfigureAwait(false);
                AssertEqual(3, stages.Count, "The configured Worker, TestEngineer, and Judge stages are all persisted.");
                foreach (Mission stage in stages)
                {
                    string description = stage.Description ?? String.Empty;
                    AssertContains("<!-- armada-objective-brief:" + child.Id + " -->", description,
                        "Every rescue stage carries the current child objective frame.");
                    List<string> criteria = JudgeAcceptanceWalk.ExtractCriteria(description);
                    AssertEqual(1, criteria.Count, "Every rescue stage exposes one authoritative criterion.");
                    AssertEqual(child.AcceptanceCriteria[0], criteria[0], "Every rescue stage carries the child criterion.");
                    AssertContains(child.Description!, description, "Every rescue stage carries the child scope.");
                    AssertContains("mission-output:" + failed.Id, description, "Every rescue stage retains the full failed-mission output pointer.");
                    AssertTrue(!JudgeAcceptanceWalk.ExtractCriteria(description).Contains(stale.AcceptanceCriteria[0]),
                        "No rescue stage promotes the stale parent criterion.");
                }

                Voyage rescueVoyage = (await testDb.Driver.Voyages.ReadAsync(worker.VoyageId!).ConfigureAwait(false))!;
                AssertEqual(child.Id, VoyageObjectiveResolver.ReadBriefObjectiveId(rescueVoyage.Description),
                    "The rescue voyage names the same child objective as all of its stages.");
            }).ConfigureAwait(false);

            await RunTest("Rescue defers when a required execution resource is unavailable without spending its budget", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                const string tenantId = "ten_rescue_execution_unavailable";
                const string userId = "usr_rescue_execution_unavailable";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, tenantId, userId).ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                failed.Persona = "Worker";
                failed = await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);
                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Rescue requires an unavailable execution environment",
                    Status = ObjectiveStatusEnum.InProgress,
                    MissionIds = new List<string> { failed.Id },
                    AcceptanceCriteria = new List<string> { "Use the declared captain execution environment." },
                    Preparation = new ObjectivePreparation
                    {
                        ExecutionRequirements = new ObjectiveExecutionRequirements
                        {
                            OperatingSystem = Environment.OSVersion.Platform == PlatformID.Win32NT ? "Linux" : "Windows"
                        }
                    }
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                    testDb.Driver,
                    admiral,
                    incidents,
                    new RunbookService(testDb.Driver, new LoggingModule()));

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);
                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count,
                    "The rescue must not launch when the required captain execution environment is unavailable.");
                Mission? after = await testDb.Driver.Missions.ReadAsync(failed.Id).ConfigureAwait(false);
                AssertEqual(0, after!.RecoveryAttempts, "A resource deferral does not spend a recovery attempt.");
                AssertNull(after.LastRecoveryActionUtc, "A resource deferral does not mark the original failure as handled.");

                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = failed.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, incidentPage.Objects.Count, "The original failure still has an operator incident.");
                AssertContains("execution_unavailable", incidentPage.Objects[0].RecoveryNotes ?? String.Empty,
                    "The incident records the stable execution-resource reason.");
                AssertContains("dispatch a new voyage or rescue", incidentPage.Objects[0].RecoveryNotes ?? String.Empty,
                    "The incident gives an operator a valid action after the automatic retry window.");

                List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(failed.Id, 100).ConfigureAwait(false);
                AssertEqual(1, events.Count(item => item.EventType == "autonomous_recovery.rescue_deferred_execution_unavailable"),
                    "Repeated handling deduplicates the resource deferral event.");
                List<Mission> vesselMissions = await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
                AssertEqual(1, vesselMissions.Count, "Repeated handling creates no rescue mission row.");
                AssertTrue(vesselMissions.All(item => item.Id == failed.Id), "The original mission remains the only mission on the vessel.");
                AssertTrue(!String.IsNullOrWhiteSpace(incidentPage.Objects[0].RecoveryNotes),
                    "The incident keeps the deferred resource reason for operator action.");
            }).ConfigureAwait(false);

            await RunTest("Rescue dispatches when its required execution environment is available", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                const string tenantId = "ten_rescue_execution_available";
                const string userId = "usr_rescue_execution_available";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, tenantId, userId).ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                failed.Persona = "Worker";
                failed = await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);
                LocalCaptainExecutionEnvironmentProbe environment = new LocalCaptainExecutionEnvironmentProbe(new ArmadaSettings());
                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Rescue uses the available execution environment",
                    Status = ObjectiveStatusEnum.InProgress,
                    MissionIds = new List<string> { failed.Id },
                    AcceptanceCriteria = new List<string> { "Run in the available captain environment." },
                    Preparation = new ObjectivePreparation
                    {
                        ExecutionRequirements = new ObjectiveExecutionRequirements
                        {
                            OperatingSystem = environment.OperatingSystem
                        }
                    }
                }).ConfigureAwait(false);

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                    testDb.Driver,
                    admiral,
                    new IncidentService(testDb.Driver),
                    new RunbookService(testDb.Driver, new LoggingModule()));

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count,
                    "A satisfied execution requirement does not block an otherwise valid rescue.");
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

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

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

            await RunTest("Sweep leaves a reconciler-failed mission untouched and still rescues a genuine failure under a failed voyage", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_reconciled", "usr_auto_reconciled").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_reconciled", "usr_auto_reconciled").ConfigureAwait(false);

                Mission reconciled = await CreateReconciledMissionAsync(testDb, vessel, VoyageStatusEnum.Failed).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Failed, reconciled.Status, "The reconciler fails unlanded work under a Failed voyage.");
                AssertTrue(reconciled.LastUpdateUtc > DateTime.UtcNow.AddMinutes(-5), "The reconciled mission is fresh, inside the failed-mission lookback.");

                Voyage genuineVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Genuinely failed voyage")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Failed,
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-1),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
                }).ConfigureAwait(false);
                Mission genuine = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                genuine.VoyageId = genuineVoyage.Id;
                await testDb.Driver.Missions.UpdateAsync(genuine).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

                await orchestrator.SweepAsync().ConfigureAwait(false);
                await orchestrator.HandleMissionOutcomeAsync(reconciled, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "Only the genuine failure is rescued.");
                AssertEqual(genuine.Id, admiral.DispatchedMissions[0].ParentMissionId, "The rescue belongs to the genuine failure.");
                AssertEqual(0, await CountIncidentsAsync(incidents, vessel, reconciled.Id).ConfigureAwait(false), "A reconciler-failed mission opens no incident.");
                AssertEqual(1, await CountIncidentsAsync(incidents, vessel, genuine.Id).ConfigureAwait(false), "A genuine failure under a Failed voyage still opens its incident.");

                Mission reconciledAfter = (await testDb.Driver.Missions.ReadAsync(reconciled.Id).ConfigureAwait(false))!;
                AssertFalse(reconciledAfter.LastRecoveryActionUtc.HasValue, "Recovery records no action on a reconciled mission.");
                AssertEqual(0, reconciledAfter.RecoveryAttempts, "Recovery spends no budget on a reconciled mission.");
                AssertTrue(Math.Abs((reconciledAfter.LastUpdateUtc - reconciled.LastUpdateUtc).TotalMilliseconds) < 5,
                    "Recovery never writes a reconciled mission, so it cannot keep it inside the lookback window.");
            }).ConfigureAwait(false);

            await RunTest("A mission an operator stopped opens no incident and dispatches no rescue", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_operator_stop", "usr_auto_operator_stop").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_operator_stop", "usr_auto_operator_stop").ConfigureAwait(false);
                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

                ArmadaSettings settings = new ArmadaSettings();
                settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_stop_docks_" + Guid.NewGuid().ToString("N"));
                settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_stop_repos_" + Guid.NewGuid().ToString("N"));
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                StubGitService git = new StubGitService();
                CaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, new DockService(logging, testDb.Driver, settings, git));

                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Stopped voyage")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.InProgress
                }).ConfigureAwait(false);
                Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("stopped-worker")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    State = CaptainStateEnum.Working
                }).ConfigureAwait(false);
                Mission running = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    CaptainId = captain.Id,
                    Persona = "Worker",
                    Title = "Running mission",
                    Description = "Original mission description",
                    Status = MissionStatusEnum.InProgress
                }).ConfigureAwait(false);
                captain.CurrentMissionId = running.Id;
                await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                CaptainAdministrationService administration = new CaptainAdministrationService(
                    testDb.Driver, (captainId, token) => captains.RecallAsync(captainId, token: token));
                await administration.StopAsync(captain.Id, null, stop: new OperatorStopContext("tester", OperatorStopContext.RestSurface)).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                Mission stopped = (await testDb.Driver.Missions.ReadAsync(running.Id).ConfigureAwait(false))!;
                await orchestrator.HandleMissionOutcomeAsync(stopped, false).ConfigureAwait(false);
                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count, "No rescue for a mission an operator stopped (status " + stopped.Status + ").");
                AssertEqual(0, await CountIncidentsAsync(incidents, vessel, running.Id).ConfigureAwait(false), "No incident for a mission an operator stopped.");
                AssertEqual(MissionStatusEnum.Cancelled, stopped.Status, "An operator stop ends the mission Cancelled.");
            }).ConfigureAwait(false);

            await RunTest("A reconciler-cancelled mission opens no incident and dispatches no rescue", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_reconciled_cancel", "usr_auto_reconciled_cancel").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_reconciled_cancel", "usr_auto_reconciled_cancel").ConfigureAwait(false);

                Mission reconciled = await CreateReconciledMissionAsync(testDb, vessel, VoyageStatusEnum.Complete).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Cancelled, reconciled.Status, "The reconciler cancels superseded work under a Complete voyage.");

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.HandleMissionOutcomeAsync(reconciled, false).ConfigureAwait(false);
                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count, "No rescue for a reconciler-cancelled mission.");
                AssertEqual(0, await CountIncidentsAsync(incidents, vessel, reconciled.Id).ConfigureAwait(false), "No incident for a reconciler-cancelled mission.");
            }).ConfigureAwait(false);

            await RunTest("A dispatch hold neither defers nor later rescues a reconciler-failed mission", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_reconciled_hold", "usr_auto_reconciled_hold").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_reconciled_hold", "usr_auto_reconciled_hold").ConfigureAwait(false);

                Mission reconciled = await CreateReconciledMissionAsync(testDb, vessel, VoyageStatusEnum.Failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                DispatchHold hold = new DispatchHold();
                AutonomousRecoveryOrchestrator orchestrator = new AutonomousRecoveryOrchestrator(
                    testDb.Driver, admiral, incidents, runbooks, new ArmadaSettings(), new LoggingModule(),
                    null, null, null, null, null, null, null, null, hold);

                hold.Engage("redeploy window", "session-hold");
                await orchestrator.SweepAsync().ConfigureAwait(false);
                AssertEqual(0, orchestrator.HoldDeferredRescueCount, "A reconciled mission is never deferred for the hold.");
                AssertEqual(0, await CountIncidentsAsync(incidents, vessel, reconciled.Id).ConfigureAwait(false), "A held sweep opens no incident for a reconciled mission.");

                hold.Clear();
                await orchestrator.SweepAsync().ConfigureAwait(false);
                AssertEqual(0, admiral.DispatchedMissions.Count, "Clearing the hold dispatches no rescue for a reconciled mission.");
                AssertEqual(0, orchestrator.HoldDeferredRescueCount, "Nothing is left deferred.");
                AssertEqual(0, await CountIncidentsAsync(incidents, vessel, reconciled.Id).ConfigureAwait(false), "No incident after the hold clears.");
            }).ConfigureAwait(false);

            await RunTest("An incident an operator closed during a hold stops the deferred rescue, before and after a restart", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_closed_hold", "usr_auto_closed_hold").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_closed_hold", "usr_auto_closed_hold").ConfigureAwait(false);
                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

                Mission closedByOperator = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                Mission stillOpen = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                DispatchHold hold = new DispatchHold();
                AutonomousRecoveryOrchestrator orchestrator = new AutonomousRecoveryOrchestrator(
                    testDb.Driver, admiral, incidents, runbooks, new ArmadaSettings(), new LoggingModule(),
                    null, null, null, null, null, null, null, null, hold);

                hold.Engage("redeploy window", "session-hold");
                await orchestrator.SweepAsync().ConfigureAwait(false);
                AssertEqual(2, orchestrator.HoldDeferredRescueCount, "Both rescues are deferred while the hold is engaged.");

                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");
                Incident deferredIncident = (await incidents.EnumerateActiveAsync(auth, new IncidentQuery { MissionId = closedByOperator.Id }).ConfigureAwait(false)).Single();
                await incidents.UpdateAsync(auth, deferredIncident.Id, new IncidentUpsertRequest
                {
                    Status = IncidentStatusEnum.Closed,
                    ClosedUtc = DateTime.UtcNow,
                    RootCause = "The owning work landed by another path.",
                    RecoveryNotes = "Operator closed: the owning work landed by another path."
                }).ConfigureAwait(false);

                // A restart loses the in-memory deferral; the replacement process sweeps the same failure.
                AutonomousRecoveryOrchestrator restarted = new AutonomousRecoveryOrchestrator(
                    testDb.Driver, admiral, incidents, runbooks, new ArmadaSettings(), new LoggingModule(),
                    null, null, null, null, null, null, null, null, hold);
                hold.Clear();
                await restarted.SweepAsync().ConfigureAwait(false);
                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "Only the failure whose incident is still open is rescued.");
                AssertEqual(stillOpen.Id, admiral.DispatchedMissions[0].ParentMissionId, "The rescue belongs to the still-open failure.");
                AssertEqual(1, await CountIncidentsAsync(incidents, vessel, closedByOperator.Id).ConfigureAwait(false),
                    "No second incident is opened for a failure an operator closed.");
                Mission closedAfter = (await testDb.Driver.Missions.ReadAsync(closedByOperator.Id).ConfigureAwait(false))!;
                AssertEqual(0, closedAfter.RecoveryAttempts, "Recovery spends no budget on a failure an operator closed.");
            }).ConfigureAwait(false);

            await RunTest("A failure newer than its closed incident is still recovered", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_refailed", "usr_auto_refailed").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_refailed", "usr_auto_refailed").ConfigureAwait(false);
                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

                Mission refailed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                IncidentService incidents = new IncidentService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");
                await incidents.CreateAsync(auth, new IncidentUpsertRequest
                {
                    Title = "Mission failed: earlier attempt",
                    Status = IncidentStatusEnum.Closed,
                    ClosedUtc = DateTime.UtcNow.AddHours(-2),
                    RootCause = "An earlier attempt was resolved.",
                    VesselId = vessel.Id,
                    MissionId = refailed.Id,
                    DetectedUtc = DateTime.UtcNow.AddHours(-3)
                }).ConfigureAwait(false);

                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);
                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "An incident closed before the latest failure does not stop its rescue.");
                AssertEqual(refailed.Id, admiral.DispatchedMissions[0].ParentMissionId, "The rescue belongs to the refailed mission.");
            }).ConfigureAwait(false);

            await RunTest("A later failure-reason rewrite does not return a reconciler-failed mission to recovery", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_reconciled_rewrite", "usr_auto_reconciled_rewrite").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_reconciled_rewrite", "usr_auto_reconciled_rewrite").ConfigureAwait(false);

                Mission reconciled = await CreateReconciledMissionAsync(testDb, vessel, VoyageStatusEnum.Failed).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Failed, reconciled.Status, "The reconciler fails unlanded work under a Failed voyage.");

                // Any later writer may replace the human-readable reason; the outcome must not depend on it.
                reconciled.FailureReason = "Operator note: branch retained for manual review";
                await testDb.Driver.Missions.UpdateAsync(reconciled).ConfigureAwait(false);
                Mission rewritten = (await testDb.Driver.Missions.ReadAsync(reconciled.Id).ConfigureAwait(false))!;
                AssertEqual("Operator note: branch retained for manual review", rewritten.FailureReason, "The rewrite persisted.");

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await orchestrator.SweepAsync().ConfigureAwait(false);
                await orchestrator.HandleMissionOutcomeAsync(rewritten, false).ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count, "A rewritten reason does not make a reconciled mission rescuable.");
                AssertEqual(0, await CountIncidentsAsync(incidents, vessel, reconciled.Id).ConfigureAwait(false), "A rewritten reason does not open an incident.");
                AssertFalse((await testDb.Driver.Missions.ReadAsync(reconciled.Id).ConfigureAwait(false))!.LastRecoveryActionUtc.HasValue,
                    "Recovery records no action on a reconciled mission whose reason was rewritten.");
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

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

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

            await RunTest("Stall nudge is withheld, logged and counted when the mission already emitted its terminal marker", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_marker", "usr_auto_marker").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_marker", "usr_auto_marker").ConfigureAwait(false);
                Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Title = "Finished judge",
                    Description = "Judge that printed its verdict and kept running",
                    Persona = "Judge",
                    Status = MissionStatusEnum.InProgress,
                    StartedUtc = DateTime.UtcNow.AddMinutes(-30),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-30)
                }).ConfigureAwait(false);

                Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Name = "finished-judge",
                    State = CaptainStateEnum.Working,
                    CurrentMissionId = mission.Id,
                    LastHeartbeatUtc = DateTime.UtcNow.AddMinutes(-10)
                }).ConfigureAwait(false);

                TerminalMarkerTracker markers = new TerminalMarkerTracker();
                AssertTrue(markers.TryRecordFirst(mission.Id,
                    new ProgressParser.ProgressSignal { Type = "verdict", Value = "PASS" },
                    DateTime.UtcNow.AddMinutes(-9)), "The verdict is recorded.");

                ArmadaSettings settings = new ArmadaSettings
                {
                    StallThresholdMinutes = 10,
                    AutonomousRecovery = new AutonomousRecoverySettings
                    {
                        SendStallMailNudges = true,
                        StallMailNudgeThresholdRatio = 0.5,
                        StallMailNudgeCooldownMinutes = 30
                    }
                };
                AutonomousRecoveryOrchestrator orchestrator = new AutonomousRecoveryOrchestrator(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    new IncidentService(testDb.Driver),
                    new RunbookService(testDb.Driver, new LoggingModule()),
                    settings,
                    new LoggingModule(),
                    null, null, null, null, null, null, null, null, null,
                    markers);

                await orchestrator.SweepAsync().ConfigureAwait(false);
                await orchestrator.SweepAsync().ConfigureAwait(false);

                EnumerationResult<Signal> signals = await testDb.Driver.Signals.EnumerateAsync(vessel.TenantId!, new EnumerationQuery
                {
                    SignalType = SignalTypeEnum.Mail.ToString(),
                    ToCaptainId = captain.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(0, signals.Objects.Count, "A finished captain must not be asked to continue its mission.");
                AssertEqual(2L, markers.SuppressedNudgeCount, "Every withheld nudge is counted.");

                List<ArmadaEvent> suppressed = await testDb.Driver.Events
                    .EnumerateByTypeAsync("autonomous_recovery.mail_nudge_suppressed")
                    .ConfigureAwait(false);
                AssertEqual(1, suppressed.Count, "The suppression is recorded once per mission, not once per sweep.");
                AssertContains("[verdict] PASS", suppressed[0].Message ?? String.Empty, "The event names the marker that finished the mission.");
                AssertEqual(1, orchestrator.NudgeSuppressedMissionCount, "The suppression is remembered while the captain still holds the mission.");

                captain.State = CaptainStateEnum.Idle;
                captain.CurrentMissionId = null;
                await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
                await orchestrator.SweepAsync().ConfigureAwait(false);
                AssertEqual(0, orchestrator.NudgeSuppressedMissionCount, "The suppression record is dropped once no Working captain holds the mission.");
            }).ConfigureAwait(false);

            await RunTest("ReviewerFeedback_JudgeStageFailure_InlinedIntoWorkerRescueBrief", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_feedback", "usr_auto_feedback").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_feedback", "usr_auto_feedback").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Agent process exited with code 1").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.ReviewComment = "The fix is missing a regression test for the null-branch case; add coverage before resubmitting.";
                failed.AgentOutput = failed.ReviewComment;
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "Reviewer-stage failure should dispatch one rescue.");
                Mission rescue = admiral.DispatchedMissions[0];
                AssertEqual("Worker", rescue.Persona, "Reviewer-stage failures must dispatch a Worker rescue.");
                AssertContains("Reviewer feedback to address:", rescue.Description ?? "", "Rescue brief must label the inlined reviewer feedback.");
                AssertContains(failed.ReviewComment!, rescue.Description ?? "", "Rescue brief must inline the parent's review feedback verbatim.");
            }).ConfigureAwait(false);

            await RunTest("ReviewerFeedback_RescueUsesCompleteJudgeOutputAndNamesItsReadableParent", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_rescue_full_review", "usr_rescue_full_review").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_rescue_full_review", "usr_rescue_full_review").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                failed.Persona = "Judge";

                StringBuilder review = new StringBuilder();
                review.AppendLine("## Completeness");
                review.AppendLine("- src/Example.cs:101 -- complete criterion one. NOT MET.");
                review.AppendLine("- src/Example.cs:102 -- complete criterion two. NOT MET.");
                review.AppendLine("- src/Example.cs:103 -- complete criterion three. NOT MET.");
                review.AppendLine("- src/Example.cs:104 -- complete criterion four. NOT MET.");
                review.AppendLine("- src/Example.cs:105 -- complete criterion five. NOT MET.");
                review.AppendLine("- src/Example.cs:106 -- delivered behavior. MET.");
                review.AppendLine("- The report lists each parent criterion with file:line evidence, or NOT DELIVERED. — MET.");
                review.AppendLine(BuildBenignReviewPadding(15000));
                review.AppendLine("## Correctness");
                review.AppendLine("**R1: Existing shared metadata changes during a successful read.**");
                review.AppendLine("The read at src/Example.cs:78 changes existing shared-memory metadata.");
                review.AppendLine("Preserve existing sidecars and add a regression test.");
                review.AppendLine("**P2: The restored snapshot can enter the wrong admission cap.**");
                review.AppendLine();
                review.AppendLine("The restore at src/Example.cs:77 treats an absent version as a current candidate.");
                review.AppendLine();
                review.AppendLine("Classify absent versions as legacy and add a restart regression.");
                review.AppendLine("1. src/Example.cs:12 -- preserve the first rejected behavior.");
                review.AppendLine("2. src/Example.cs:24 -- preserve the second rejected behavior.");
                review.AppendLine("3. src/Example.cs:36 -- preserve the third rejected behavior.");
                review.AppendLine("4. src/Example.cs:48 -- preserve the fourth rejected behavior.");
                review.AppendLine("5. src/Example.cs:60 -- preserve the fifth rejected behavior.");
                review.AppendLine("6. P2 src/Example.cs:72 -- preserve the sixth rejected behavior.");
                review.AppendLine("7. src/Example.cs:73 -- failure to meet the required output shape is still present.");
                review.AppendLine("8. src/Test.cs:42 -- the result uses the wrong PASS constant.");
                review.AppendLine("9. src/Test.cs:43 -- add assertions for SUCCESS.");
                review.AppendLine("- **Non-blocking:** src/Example.cs:109 -- optional cleanup.");
                review.AppendLine("- **Blocking:** src/Example.cs:110 -- the required branch is still missing.");
                review.AppendLine("### Nested correctness evidence");
                review.AppendLine("- src/Example.cs:74 -- the nested section still has an unresolved defect.");
                review.AppendLine("Non-blocking: src/Example.cs:108 -- optional follow-up.");
                review.AppendLine("## Failure Modes");
                review.AppendLine("- src/Example.cs:84 -- handle the empty-input case.");
                review.AppendLine("- Failure mode for empty input is handled by an explicit guard.");
                review.AppendLine("- src/Example.cs:85 -- failure mode for malformed input is not handled.");
                review.AppendLine("## Tests");
                review.AppendLine("- src/ExampleTests.cs:19 -- add the missing boundary test.");
                review.AppendLine("- src/ExampleTests.cs:27 -- the null result has no assertion.");
                review.AppendLine("Coverage misses historical version inference at src/ExampleTests.cs:31; add the restart regression.");
                review.AppendLine("- P2: 10 passed, 0 failed.");
                review.AppendLine("- Unit: 10 passed, 0 failed; baseline 8, with 2 added cases executed.");
                review.AppendLine("- Web: 5 passed, 0 failed.");
                review.AppendLine("- Script: 1 script, 0 issues.");
                review.AppendLine("No test failed.");
                review.AppendLine("No tests failed.");
                review.AppendLine("- No test failed.");
                review.AppendLine("- No tests failed.");
                review.AppendLine("- No test failed to cover src/ExampleTests.cs:32.");
                review.AppendLine("- No test does not cover src/ExampleTests.cs:36.");
                review.AppendLine("- No tests do not cover src/ExampleTests.cs:37.");
                review.AppendLine("- No test failed to assert src/ExampleTests.cs:38.");
                review.AppendLine("No test covers src/ExampleTests.cs:33 -- add the boundary regression.");
                review.AppendLine("No test asserts src/ExampleTests.cs:34 -- assert the saved result.");
                review.AppendLine("No test for src/ExampleTests.cs:35 -- add the input case.");
                review.AppendLine("- Test 0 covers the changed behaviour and passed in the foreground run.");
                review.AppendLine("- Verified rule output matches the documented contract.");
                review.AppendLine("## Verdict");
                review.AppendLine("NEEDS_REVISION: add src/Example.cs:96 and verify the failure path.");
                string fullReview = review.ToString();
                failed.AgentOutput = fullReview;
                failed.ReviewComment = fullReview.Substring(0, 8000);
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                    testDb.Driver,
                    admiral,
                    new IncidentService(testDb.Driver),
                    new RunbookService(testDb.Driver, new LoggingModule()));
                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);
                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                Mission rescue = admiral.DispatchedMissions.Single();
                string description = rescue.Description ?? String.Empty;
                AssertContains("src/Example.cs:12", description, "The first Correctness defect is carried.");
                AssertContains("src/Example.cs:24", description, "The second Correctness defect is carried.");
                AssertContains("src/Example.cs:36", description, "The third Correctness defect is carried.");
                AssertContains("src/Example.cs:48", description, "The fourth Correctness defect is carried.");
                AssertContains("src/Example.cs:60", description, "The fifth Correctness defect is carried.");
                AssertContains("src/Example.cs:72", description, "A P2 defect is not discarded as a lesser item.");
                AssertContains("src/Example.cs:77", description, "An unbulleted bold P2 finding retains its evidence anchor.");
                AssertContains("add a restart regression", description, "The P2 finding carries its unbulleted corrective context.");
                AssertContains("src/Example.cs:73", description, "MET wording inside a defect does not hide it.");
                AssertContains("src/Test.cs:42", description, "A PASS token inside defect text does not mark it as successful.");
                AssertContains("src/Test.cs:43", description, "A SUCCESS token inside defect text does not mark it as successful.");
                AssertContains("src/Example.cs:74", description, "An unmarked defect under a nested protected heading is retained.");
                AssertContains("src/Example.cs:84", description, "Failure Modes defects are carried.");
                AssertContains("src/Example.cs:85", description, "A failure mode that is not handled remains actionable.");
                AssertContains("src/ExampleTests.cs:19", description, "Tests defects are carried.");
                AssertContains("src/ExampleTests.cs:27", description, "A Tests defect does not need a keyword to be actionable.");
                AssertContains("src/ExampleTests.cs:31", description, "Unbulleted test coverage gaps are carried.");
                AssertContains("src/ExampleTests.cs:33", description, "Explicit missing test coverage remains actionable.");
                AssertContains("src/ExampleTests.cs:34", description, "An explicit missing assertion remains actionable.");
                AssertContains("src/ExampleTests.cs:35", description, "An explicit missing test remains actionable.");
                AssertContains("src/Example.cs:96", description, "A Verdict-only required change is carried.");
                AssertContains("src/Example.cs:110", description, "An explicit bold Blocking label remains actionable.");
                int findingStart = description.IndexOf("Blocking findings to fix", StringComparison.Ordinal);
                int feedbackStart = description.IndexOf("Reviewer feedback to address:", StringComparison.Ordinal);
                AssertTrue(findingStart >= 0 && feedbackStart > findingStart, "The brief has a separate findings list before the full reviewer artifact.");
                string feedback = description.Substring(feedbackStart);
                AssertContains("### Nested correctness evidence", feedback, "A nested protected heading survives the bounded feedback projection.");
                AssertContains("src/Example.cs:74", feedback, "The nested protected defect survives the bounded feedback projection.");
                string findingBlock = description.Substring(findingStart, feedbackStart - findingStart);
                AssertFalse(findingBlock.Contains("optional follow-up", StringComparison.Ordinal), "Explicitly non-blocking commentary is excluded from required fixes.");
                AssertFalse(findingBlock.Contains("optional cleanup", StringComparison.Ordinal), "A bold Non-blocking label is excluded from required fixes.");
                AssertContains("1. **R1:", findingBlock, "The R1 item has its own numbered finding entry.");
                AssertContains("src/Example.cs:78", findingBlock, "An unbulleted bold R1 finding retains its evidence anchor in the findings list.");
                AssertContains("Preserve existing sidecars", findingBlock, "An unbulleted bold R1 finding carries its required correction in the findings list.");
                AssertContains("P2: 10 passed, 0 failed", findingBlock, "An explicitly labeled finding takes priority over a passing-summary shape.");
                AssertFalse(findingBlock.Contains("Unit: 10 passed, 0 failed", StringComparison.Ordinal), "A passing unit summary is not listed as a defect.");
                AssertFalse(findingBlock.Contains("Web: 5 passed, 0 failed", StringComparison.Ordinal), "A passing web summary is not listed as a defect.");
                AssertFalse(findingBlock.Contains("Script: 1 script, 0 issues", StringComparison.Ordinal), "A passing script summary is not listed as a defect.");
                AssertFalse(findingBlock.Contains("Test 0 covers", StringComparison.Ordinal), "A passed test result is not listed as a defect.");
                AssertFalse(findingBlock.Contains("No test failed.\n", StringComparison.Ordinal), "An unbulleted positive statement that no test failed is not listed as a coverage gap.");
                AssertFalse(findingBlock.Contains("No tests failed.\n", StringComparison.Ordinal), "An unbulleted positive statement that no tests failed is not listed as a coverage gap.");
                AssertFalse(findingBlock.Contains("- No test failed.\n", StringComparison.Ordinal), "A bulleted positive statement that no test failed is not listed as a coverage gap.");
                AssertFalse(findingBlock.Contains("- No tests failed.\n", StringComparison.Ordinal), "A bulleted positive statement that no tests failed is not listed as a coverage gap.");
                AssertFalse(findingBlock.Contains("src/ExampleTests.cs:32", StringComparison.Ordinal), "A double-negative no-failure statement is not listed as a coverage gap.");
                AssertFalse(findingBlock.Contains("src/ExampleTests.cs:36", StringComparison.Ordinal), "A double-negative no-coverage statement is not listed as a coverage gap.");
                AssertFalse(findingBlock.Contains("src/ExampleTests.cs:37", StringComparison.Ordinal), "A plural double-negative no-coverage statement is not listed as a coverage gap.");
                AssertFalse(findingBlock.Contains("src/ExampleTests.cs:38", StringComparison.Ordinal), "A double-negative no-assertion statement is not listed as a coverage gap.");
                AssertContains("src/ExampleTests.cs:31", findingBlock, "The existing unbulleted Coverage misses report remains an actionable finding.");
                AssertContains("src/ExampleTests.cs:33", findingBlock, "Explicit no-test-covers wording remains actionable.");
                AssertContains("src/ExampleTests.cs:34", findingBlock, "Explicit no-test-asserts wording remains actionable.");
                AssertContains("src/ExampleTests.cs:35", findingBlock, "Explicit no-test-for wording remains actionable.");
                AssertFalse(findingBlock.Contains("Verified rule output", StringComparison.Ordinal), "A verified test result is not listed as a defect.");
                AssertFalse(findingBlock.Contains("Failure mode for empty input is handled", StringComparison.Ordinal), "A handled failure mode is not listed as a defect.");
                AssertFalse(findingBlock.Contains("delivered behavior", StringComparison.Ordinal), "MET criterion restatements are excluded from required fixes.");
                AssertFalse(findingBlock.Contains("The report lists each parent criterion", StringComparison.Ordinal), "A completed criterion with NOT DELIVERED in its description is excluded.");
                AssertContains("src/Example.cs:105", findingBlock, "NOT MET criteria remain after defects.");
                int firstDefect = findingBlock.IndexOf("src/Example.cs:12", StringComparison.Ordinal);
                int criterion = findingBlock.IndexOf("NOT MET", StringComparison.Ordinal);
                AssertTrue(firstDefect >= 0 && criterion > firstDefect, "Criterion restatements follow defects in the rescue brief.");
                AssertContains("mission-output:" + failed.Id, description, "The complete parent output has a readable pointer.");
                AssertContains("armada_mission_output", description, "The rescue is told how to read the complete parent output.");
                AssertContains("armada_mission_status", description, "The rescue has a readable pointer to the full original mission description.");
                AssertContains("includeDescription=true", description, "The original-description pointer requests the full description field.");
                AssertFalse(description.Contains("remainder in admiral log", StringComparison.Ordinal), "The brief does not send the captain to an unreadable admiral log.");
                AssertFalse(description.Contains("- - ", StringComparison.Ordinal), "The brief does not double its bullets.");

                Mission verdictOnly = new Mission("Verdict-only required change")
                {
                    Id = "msn_test_verdict_only",
                    Status = MissionStatusEnum.Failed,
                    Persona = "Judge",
                    AgentOutput = "## Verdict\n- src/Example.cs:96 -- add the required failure-path check.",
                    ReviewComment = "## Verdict\n- src/Example.cs:96 -- add the required failure-path check."
                };
                Incident syntheticIncident = new Incident
                {
                    Id = "inc_test_verdict_only",
                    Title = "synthetic verdict-only review",
                    Summary = "synthetic summary",
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.Medium
                };
                string verdictBrief = AutonomousRecoveryOrchestrator.BuildRescueDescription(verdictOnly, syntheticIncident, 1);
                AssertContains("src/Example.cs:96", verdictBrief, "A required change that appears only in Verdict remains actionable.");

                Mission passOnly = new Mission("Plain passing review")
                {
                    Id = "msn_test_plain_pass",
                    Status = MissionStatusEnum.Complete,
                    Persona = "Judge",
                    AgentOutput = "## Verdict\nPASS",
                    ReviewComment = "## Verdict\nPASS"
                };
                string passBrief = AutonomousRecoveryOrchestrator.BuildRescueDescription(passOnly, syntheticIncident, 1);
                AssertFalse(passBrief.Contains("Blocking findings to fix", StringComparison.Ordinal), "A plain PASS does not become a defect.");

                Mission capturedOutput = new Mission("Captured output limit")
                {
                    Id = "msn_test_output_capture_limit",
                    Status = MissionStatusEnum.Failed,
                    Persona = "Judge",
                    AgentOutput = MissionOutputArtifact.StreamTruncationMarker + "\n## Correctness\n1. src/Example.cs:120 -- captured tail finding.",
                    ReviewComment = "## Correctness\n1. src/Example.cs:120 -- captured tail finding."
                };
                string capturedBrief = AutonomousRecoveryOrchestrator.BuildRescueDescription(capturedOutput, syntheticIncident, 1);
                AssertContains("incomplete", capturedBrief, "A streamed-output capture marker is not described as a complete review.");
                AssertContains("mission-output:" + capturedOutput.Id, capturedBrief, "The incomplete capture still points to the parent artifact.");

                Mission fallbackReview = new Mission("Review comment fallback")
                {
                    Id = "msn_test_review_comment_fallback",
                    Status = MissionStatusEnum.Failed,
                    Persona = "Judge",
                    AgentOutput = String.Empty,
                    ReviewComment = "## Correctness\n- src/Example.cs:130 -- fallback-only review text."
                };
                string fallbackBrief = AutonomousRecoveryOrchestrator.BuildRescueDescription(fallbackReview, syntheticIncident, 1);
                AssertContains("Persisted Judge output (empty)", fallbackBrief, "The brief identifies that the persisted output is empty.");
                AssertContains("ReviewComment is only a fallback", fallbackBrief, "Fallback text is not described as the complete review.");
                AssertContains("Blocking findings to fix (incomplete", fallbackBrief, "The fallback findings list is explicitly incomplete.");
                AssertFalse(fallbackBrief.Contains("complete list from the full review", StringComparison.Ordinal), "The fallback does not claim full-review completeness.");
            }).ConfigureAwait(false);

            await RunTest("RescueOfAFailedJudge_DoesNotInheritTheJudgeRole", () =>
            {
                string judgeRole = MissionService.BuildPersonaPreamble("Judge", MissionModeEnum.Implementation);
                Mission failedJudge = new Mission("Align the retry policy [Judge]")
                {
                    Id = "msn_test_failed_judge_role",
                    Status = MissionStatusEnum.Failed,
                    Persona = "Judge",
                    AgentOutput = "## Correctness\n1. src/Example.cs:40 -- the retry resends twice.\n## Verdict\nNEEDS_REVISION",
                    Description = "## Acceptance Criteria\n- A first timeout resends exactly once.\n\n## Mission Context\n"
                        + judgeRole
                        + "[ORCHESTRATOR NOTES]\nRouted lint findings stay evidence only."
                };
                Incident incident = new Incident
                {
                    Id = "inc_test_judge_role",
                    Title = "synthetic failed judge",
                    Summary = "synthetic summary",
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.Medium
                };

                string brief = AutonomousRecoveryOrchestrator.BuildRescueDescription(failedJudge, incident, 1);

                AssertFalse(brief.Contains("## Your Role: Judge", StringComparison.Ordinal), "The rescue Worker brief carries no Judge role heading");
                AssertFalse(brief.Contains("You are reviewing the completed work", StringComparison.Ordinal), "The rescue Worker brief carries no Judge role text");
                AssertContains("A first timeout resends exactly once.", brief, "The original acceptance criteria stay in the brief");
                AssertContains("Routed lint findings stay evidence only.", brief, "The text after the role section stays in the brief");
                AssertContains("Its role section is removed", brief, "The brief says the failed stage's role was removed");
                AssertContains("src/Example.cs:40", brief, "The Judge's findings still reach the rescue");

                string revised = MissionService.BuildPersonaPreamble("Linter", MissionModeEnum.Research, 2);
                string stripped = MissionService.StripPersonaPreambles("Scope line.\n\n" + revised + "Report body.");
                AssertFalse(stripped.Contains("## Your Role:", StringComparison.Ordinal), "A revised-report role section is removed");
                AssertFalse(stripped.Contains("The prior stage revised the root report.", StringComparison.Ordinal), "Its follow-on paragraph is removed with it");
                AssertContains("Scope line.", stripped, "Text before the role section is kept");
                AssertContains("Report body.", stripped, "Text after the role section is kept");
            }).ConfigureAwait(false);

            await RunTest("ChainedRescueStages_PointToTheFailedJudgeOutput", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                    testDb.Driver,
                    admiral,
                    new IncidentService(testDb.Driver),
                    new RunbookService(testDb.Driver, new LoggingModule()));
                Mission failed = new Mission("Failed review") { Id = "msn_test_failed_review" };

                Mission stage = orchestrator.BuildChainedRescueStage(
                    failed,
                    "vyg_test_rescue",
                    "msn_test_rescue_worker",
                    new PipelineStage(3, "Judge"),
                    1,
                    new List<SelectedPlaybook>());

                AssertContains("mission-output:" + failed.Id, stage.Description ?? String.Empty, "Every chained rescue stage points to the failed review output.");
                AssertContains("armada_mission_output", stage.Description ?? String.Empty, "Every chained stage is told how to read it.");
                await Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ChainedRescueStages_CarryTheBlockingFindingsOrTheFailureReason", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                    testDb.Driver,
                    admiral,
                    new IncidentService(testDb.Driver),
                    new RunbookService(testDb.Driver, new LoggingModule()));

                Mission failedJudge = new Mission("Failed review")
                {
                    Id = "msn_test_failed_review_findings",
                    Persona = "Judge",
                    Status = MissionStatusEnum.Failed,
                    AgentOutput = "## Correctness\n1. src/Example.cs:40 -- the retry resends twice.\n## Verdict\nNEEDS_REVISION"
                };
                Mission judgeStage = orchestrator.BuildChainedRescueStage(
                    failedJudge, "vyg_test_rescue", "msn_test_rescue_worker", new PipelineStage(3, "Judge"), 1, new List<SelectedPlaybook>());
                string judgeBrief = judgeStage.Description ?? String.Empty;
                AssertContains("Blocking findings from the failed Judge", judgeBrief, "The re-verification stage receives the findings it must check");
                AssertContains("src/Example.cs:40", judgeBrief, "The finding itself is embedded, not only a pointer");
                AssertContains("resolves each blocking finding above", judgeBrief, "The stage is told to check each finding");
                AssertContains(AutonomousRecoveryOrchestrator.CriterionConflictReviewInstruction, judgeBrief,
                    "The re-verification Judge is told to declare a fix that breaks a criterion instead of failing it again");
                Mission testStage = orchestrator.BuildChainedRescueStage(
                    failedJudge, "vyg_test_rescue", "msn_test_rescue_worker", new PipelineStage(2, "TestEngineer"), 1, new List<SelectedPlaybook>());
                AssertFalse((testStage.Description ?? String.Empty).Contains(JudgeCriterionConflicts.LinePrefix, StringComparison.Ordinal),
                    "Only the Judge stage receives the verdict-shaped criterion instruction");

                Mission failedWorker = new Mission("Failed gate")
                {
                    Id = "msn_test_failed_gate",
                    Persona = "Worker",
                    Status = MissionStatusEnum.Failed,
                    FailureReason = "DoD gate failed: classification=Test; unit-test command exited 1"
                };
                Mission gateStage = orchestrator.BuildChainedRescueStage(
                    failedWorker, "vyg_test_rescue", "msn_test_rescue_worker", new PipelineStage(3, "Judge"), 1, new List<SelectedPlaybook>());
                string gateBrief = gateStage.Description ?? String.Empty;
                AssertContains("Failure the revision must fix: DoD gate failed", gateBrief, "A gate failure names the failure, not reviewer feedback");
                AssertFalse(gateBrief.Contains("reviewer feedback", StringComparison.Ordinal), "A gate failure is not described as a review");
                AssertFalse(gateBrief.Contains(JudgeCriterionConflicts.LinePrefix, StringComparison.Ordinal),
                    "A gate failure carries no reviewer-required fix, so no criterion instruction");
                await Task.CompletedTask;
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

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

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

            await RunTest("PlannerCommittedCode_RescueIsAWorkerWithAReJudge", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_pc", "usr_auto_pc").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_pc", "usr_auto_pc").ConfigureAwait(false);
                Voyage parent = await testDb.Driver.Voyages.CreateAsync(new Voyage("Parent voyage", "Product pipeline")
                {
                    TenantId = "ten_auto_pc",
                    UserId = "usr_auto_pc",
                    Status = VoyageStatusEnum.InProgress
                }).ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel,
                    MissionService.PlannerCommittedCodeReason + ": the Product Manager stage committed 3 file(s) that carry behaviour").ConfigureAwait(false);
                failed.Persona = "Product Manager";
                failed.VoyageId = parent.Id;
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "Exactly one rescue should be dispatched.");
                Mission worker = admiral.DispatchedMissions[0];
                AssertEqual("Worker", worker.Persona, "A planner that committed code is rescued by a Worker, not by another planner.");
                AssertTrue(!String.IsNullOrEmpty(worker.VoyageId), "The Worker rescue runs in a rescue voyage.");
                List<Mission> loopMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(worker.VoyageId!).ConfigureAwait(false);
                AssertTrue(loopMissions.Any(item => String.Equals(item.Persona, "Judge", StringComparison.Ordinal)),
                    "A Judge is chained so the planner's code cannot land unreviewed.");
            }).ConfigureAwait(false);

            await RunTest("ReviseRetestRejudge_LinterGateFailureInsideVoyage_ChainsReJudgeOntoTheRescue", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_auto_ln", "usr_auto_ln").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_auto_ln", "usr_auto_ln").ConfigureAwait(false);
                Voyage parent = await testDb.Driver.Voyages.CreateAsync(new Voyage("Parent voyage", "Tested pipeline")
                {
                    TenantId = "ten_auto_ln",
                    UserId = "usr_auto_ln",
                    Status = VoyageStatusEnum.InProgress
                }).ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel,
                    "DoD gate failed: classification=TestFail; unit-test command exited 1").ConfigureAwait(false);
                failed.Persona = "Linter";
                failed.VoyageId = parent.Id;
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);
                await SeedEligibleRecoveryCaptainsAsync(testDb, vessel, "Linter").ConfigureAwait(false);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "Exactly one rescue should be dispatched as the loop root.");
                Mission rescue = admiral.DispatchedMissions[0];
                AssertEqual("Linter", rescue.Persona, "A failed Linter is rescued by a Linter.");
                AssertTrue(!String.IsNullOrEmpty(rescue.VoyageId),
                    "A stage that failed inside a voyage is rescued inside a rescue voyage, never as a standalone mission that could land unreviewed.");
                List<Mission> loopMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(rescue.VoyageId!).ConfigureAwait(false);
                AssertTrue(loopMissions.Any(item => String.Equals(item.Persona, "Judge", StringComparison.Ordinal)),
                    "A Judge is chained so the rescued code cannot land unreviewed.");
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

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "Exactly one rescue should be dispatched.");
                Mission worker = admiral.DispatchedMissions[0];
                AssertEqual("Worker", worker.Persona, "The rescue of a standalone Worker is a Worker.");
                AssertTrue(String.IsNullOrEmpty(worker.VoyageId), "A standalone mission never had review stages; its rescue stays standalone.");
            }).ConfigureAwait(false);
            await RunTest("Recovery preserves objective pipeline tip mode playbooks and remains idempotent", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_recovery_context", "usr_recovery_context").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_recovery_context", "usr_recovery_context").ConfigureAwait(false);

                Pipeline pipeline = new Pipeline("ReferencePortingTestedRecovery")
                {
                    TenantId = vessel.TenantId,
                    Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "PortingReferenceAnalyst") { PreferredModel = "high" },
                        new PipelineStage(3, "TestEngineer"),
                        new PipelineStage(4, "Judge") { RequiresReview = true }
                    }
                };
                pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                Playbook playbook = await testDb.Driver.Playbooks.CreateAsync(new Playbook(
                    "reference-porting.md",
                    "Use the verified source and preserve the fixed target tip.")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId
                }).ConfigureAwait(false);

                Voyage parentVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Parent reference port")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Failed
                }).ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel, "Judge verdict: NEEDS_REVISION").ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.VoyageId = parentVoyage.Id;
                failed.CommitHash = "9999999999999999999999999999999999999999";
                failed.Mode = MissionModeEnum.Implementation;
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                Objective owner = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Title = "Reference port owner",
                    Status = ObjectiveStatusEnum.InProgress,
                    VesselIds = new List<string> { vessel.Id },
                    VoyageIds = new List<string> { parentVoyage.Id },
                    SuggestedPipelineId = pipeline.Id,
                    SuggestedPlaybooks = new List<SelectedPlaybook>
                    {
                        new SelectedPlaybook
                        {
                            PlaybookId = playbook.Id,
                            DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent
                        }
                    }
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                ArmadaSettings settings = StandardRecoveryTierSettings();
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks, settings);

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);
                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count,
                    "A repeated outcome callback must not create a second recovery graph.");
                Mission worker = admiral.DispatchedMissions[0];
                AssertEqual(failed.CommitHash, worker.StartFromRef,
                    "The recovery Worker must start from the reviewed commit, not the default branch.");
                AssertEqual(failed.Mode, worker.Mode, "The recovery Worker must preserve the original mission mode.");
                AssertEqual(1, worker.SelectedPlaybooks.Count, "The recovery Worker must retain the objective playbook.");

                List<Mission> recoveryMissions = await testDb.Driver.Missions
                    .EnumerateByVoyageAsync(worker.VoyageId!).ConfigureAwait(false);
                AssertEqual(4, recoveryMissions.Count,
                    "The recovery graph must contain every stage from the selected four-stage pipeline.");
                AssertEqual(
                    "Worker,PortingReferenceAnalyst,TestEngineer,Judge",
                    String.Join(",", recoveryMissions.OrderBy(item => item.StageOrder ?? 0).Select(item => item.Persona)),
                    "Recovery must not skip the PortingReferenceAnalyst stage.");

                Mission analyst = recoveryMissions.Single(item => item.Persona == "PortingReferenceAnalyst");
                Mission testEngineer = recoveryMissions.Single(item => item.Persona == "TestEngineer");
                Mission judge = recoveryMissions.Single(item => item.Persona == "Judge");
                AssertEqual("mid", worker.PreferredModel ?? "", "The Worker must use its eligible default tier when the failed Judge was high tier.");
                AssertEqual("high", analyst.PreferredModel ?? "", "The analyst must keep its explicit high pipeline tier.");
                AssertEqual("mid", testEngineer.PreferredModel ?? "", "The test stage must use its own eligible default, not inherit the failed Judge's high tier.");
                AssertEqual("mid", judge.PreferredModel ?? "", "The Judge stage must use its own eligible default when its pipeline stage has no tier.");
                AssertEqual(worker.Id, analyst.DependsOnMissionId, "The analyst must follow the recovery Worker.");
                AssertEqual(analyst.Id, testEngineer.DependsOnMissionId, "The TestEngineer must follow the analyst.");
                AssertEqual(testEngineer.Id, judge.DependsOnMissionId, "The Judge must follow the TestEngineer.");
                AssertTrue(recoveryMissions.All(item => item.Mode == failed.Mode),
                    "Every recovery stage must preserve the original mission mode.");

                List<SelectedPlaybook> voyagePlaybooks = await testDb.Driver.Playbooks
                    .GetVoyageSelectionsAsync(worker.VoyageId!).ConfigureAwait(false);
                AssertEqual(playbook.Id, voyagePlaybooks.Single().PlaybookId,
                    "The rescue voyage must record the selected playbook.");
                foreach (Mission stage in recoveryMissions)
                {
                    List<MissionPlaybookSnapshot> snapshots = await testDb.Driver.Playbooks
                        .GetMissionSnapshotsAsync(stage.Id).ConfigureAwait(false);
                    AssertEqual(playbook.Id, snapshots.Single().PlaybookId,
                        "Each deferred recovery stage must have an immutable playbook snapshot.");
                }

                Objective? ownerAfter = await testDb.Driver.Objectives.ReadAsync(owner.Id).ConfigureAwait(false);
                AssertEqual(2, ownerAfter!.VoyageIds.Count,
                    "A repeat callback must link exactly one recovery voyage to the objective.");

                List<ArmadaEvent> rescueSkips = await ReadStageSkippedEventsAsync(testDb, worker.VoyageId!).ConfigureAwait(false);
                AssertEqual(0, rescueSkips.Count, "A rescue of a voyage without recorded skips must record no skip.");
            }).ConfigureAwait(false);

            await RunTest("Rescue of a voyage with recorded stage skips omits those stages and records the carried skips", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                (Mission failed, Voyage parentVoyage, RecordingAdmiralService admiral, AutonomousRecoveryOrchestrator orchestrator) =
                    await CreateSkippedStageRescueAsync(testDb, "ten_recovery_skip", "Judge verdict: NEEDS_REVISION. The error path swallows the exception.")
                        .ConfigureAwait(false);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "Exactly one rescue should be dispatched.");
                Mission worker = admiral.DispatchedMissions[0];
                List<Mission> recoveryMissions = await testDb.Driver.Missions
                    .EnumerateByVoyageAsync(worker.VoyageId!).ConfigureAwait(false);
                AssertEqual(
                    "Worker,Judge",
                    String.Join(",", recoveryMissions.OrderBy(item => item.StageOrder ?? 0).Select(item => item.Persona)),
                    "The rescue must not re-add the stages the failed voyage skipped.");
                Mission judge = recoveryMissions.Single(item => item.Persona == "Judge");
                AssertEqual(worker.Id, judge.DependsOnMissionId, "The Judge must chain across the skipped stages to the Worker.");

                List<ArmadaEvent> rescueSkips = await ReadStageSkippedEventsAsync(testDb, worker.VoyageId!).ConfigureAwait(false);
                AssertEqual(2, rescueSkips.Count, "The rescue voyage must record one skip event per carried stage.");
                foreach (string persona in new[] { "PortingReferenceAnalyst", "TestEngineer" })
                {
                    ArmadaEvent evt = rescueSkips.Single(item => (item.Payload ?? "").Contains("\"Persona\":\"" + persona + "\""));
                    AssertContains("reference already verified", evt.Payload ?? "", "The carried skip must keep the original reason.");
                    AssertContains("carried from " + parentVoyage.Id, evt.Payload ?? "", "The carried skip must name the voyage it came from.");
                }
            }).ConfigureAwait(false);

            await RunTest("Rescue keeps a skipped stage when the failure names that stage's responsibility", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                (Mission failed, Voyage parentVoyage, RecordingAdmiralService admiral, AutonomousRecoveryOrchestrator orchestrator) =
                    await CreateSkippedStageRescueAsync(testDb, "ten_recovery_skip_kept", "Judge verdict: NEEDS_REVISION. No TestEngineer ran, so the new branch has no regression test.")
                        .ConfigureAwait(false);

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                Mission worker = admiral.DispatchedMissions.Single();
                List<Mission> recoveryMissions = await testDb.Driver.Missions
                    .EnumerateByVoyageAsync(worker.VoyageId!).ConfigureAwait(false);
                AssertEqual(
                    "Worker,TestEngineer,Judge",
                    String.Join(",", recoveryMissions.OrderBy(item => item.StageOrder ?? 0).Select(item => item.Persona)),
                    "A skipped stage the failure names must run in the rescue; the other skip is still carried.");

                List<ArmadaEvent> rescueSkips = await ReadStageSkippedEventsAsync(testDb, worker.VoyageId!).ConfigureAwait(false);
                AssertEqual(1, rescueSkips.Count, "Only the stage the failure does not name is recorded as skipped.");
                AssertContains("\"Persona\":\"PortingReferenceAnalyst\"", rescueSkips[0].Payload ?? "", "The analyst skip is the one carried.");
            }).ConfigureAwait(false);

            await RunTest("A Judge rescue on a pipeline with two Worker stages anchors at the Worker stage before the Judge", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DuplicateWorkerRescueOutcome outcome = await RescueOnDuplicateWorkerPipelineAsync(
                    testDb, "ten_recovery_dup_judge", "Judge", 5,
                    "Judge verdict: NEEDS_REVISION. The error path swallows the exception.").ConfigureAwait(false);

                AssertEqual("Worker", outcome.Rescue.Persona, "A Judge rejection is rescued by a Worker.");
                AssertEqual(4, outcome.Rescue.StageOrder ?? 0, "The rescue Worker takes the order of the Worker stage that precedes the failed Judge.");
                AssertEqual("high", outcome.Rescue.PreferredModel ?? "", "The rescue Worker takes the tier of the Worker stage it replaces.");
                AssertEqual(
                    "Worker,Judge",
                    DescribeStageChain(outcome.RecoveryMissions),
                    "Only the stages after the second Worker re-run; the earlier analyst and TestEngineer passes are not rebuilt.");
            }).ConfigureAwait(false);

            await RunTest("A rescue anchors on the stage list its voyage was dispatched with after the pipeline gains a stage", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                EditedPipelineRescueOutcome outcome = await RescueAfterPipelineGainsAStageAsync(testDb, "ten_rescue_rec", true).ConfigureAwait(false);

                AssertEqual("Worker", outcome.Rescue.Persona, "A Judge rejection is rescued by a Worker.");
                AssertEqual(1, outcome.Rescue.StageOrder ?? 0, "The rescue Worker anchors on the Worker before the Judge in the dispatched list.");
                AssertEqual(
                    "Worker,PortingReferenceAnalyst,TestEngineer,Judge",
                    DescribeStageChain(outcome.RecoveryMissions),
                    "The rescue runs the dispatched stages after its anchor, not the stage inserted later.");
                AssertEqual(4, outcome.RecoveryMissions.Single(item => item.Persona == "Judge").StageOrder ?? 0, "The rescue Judge keeps its dispatched order.");
                AssertEqual(0, outcome.FallbackEvents.Count, "A voyage with a recorded list records no live-pipeline fallback.");

                Pipeline? rescueRecord = await VoyagePipelineRecord.ReadAsync(testDb.Driver, outcome.Rescue.VoyageId, null).ConfigureAwait(false);
                AssertNotNull(rescueRecord, "The rescue voyage records the stage list its rescue anchored on.");
                AssertEqual(
                    "Worker,PortingReferenceAnalyst,TestEngineer,Judge",
                    String.Join(",", rescueRecord!.Stages.Select(item => item.PersonaName)),
                    "A rescue of the rescue anchors on the same dispatched list.");
            }).ConfigureAwait(false);

            await RunTest("A rescue of a voyage with no recorded stage list uses the live pipeline and records that it did", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                EditedPipelineRescueOutcome outcome = await RescueAfterPipelineGainsAStageAsync(testDb, "ten_rescue_live", false).ConfigureAwait(false);

                AssertEqual(
                    "Worker,PortingReferenceAnalyst,TestEngineer,Worker,Judge",
                    DescribeStageChain(outcome.RecoveryMissions),
                    "Without a recorded list the rescue runs the live pipeline's stages.");
                AssertEqual(1, outcome.FallbackEvents.Count, "The rescue records one live-pipeline fallback event.");
                AssertEqual(outcome.Failed.VoyageId, outcome.FallbackEvents[0].VoyageId, "The fallback event names the failed voyage.");
                AssertContains("EditedRecovery", outcome.FallbackEvents[0].Message ?? "", "The fallback event names the live pipeline it used.");
            }).ConfigureAwait(false);

            await RunTest("A failed second Worker stage is rescued at its own stage and re-runs only the stages after it", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DuplicateWorkerRescueOutcome outcome = await RescueOnDuplicateWorkerPipelineAsync(
                    testDb, "ten_recovery_dup_worker", "Worker", 4,
                    "DoD gate failed: classification=TestFail; unit-test command exited 1").ConfigureAwait(false);

                AssertEqual("Worker", outcome.Rescue.Persona, "A failed Worker is rescued by a Worker.");
                AssertEqual(4, outcome.Rescue.StageOrder ?? 0, "The rescue replaces the failed Worker stage, not the first Worker stage.");
                AssertEqual(
                    "Worker,Judge",
                    DescribeStageChain(outcome.RecoveryMissions),
                    "Only the stages after the failed Worker re-run.");
            }).ConfigureAwait(false);

            await RunTest("A carried skip whose persona names two rescue stages is not carried and the other skip still is", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                string tenantId = "ten_recovery_dup_skip";
                string userId = "usr_" + tenantId;
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, tenantId, userId).ConfigureAwait(false);
                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

                Pipeline pipeline = await testDb.Driver.Pipelines.CreateAsync(new Pipeline("DuplicateTestEngineerRecovery")
                {
                    TenantId = vessel.TenantId,
                    Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "TestEngineer"),
                        new PipelineStage(3, "PortingReferenceAnalyst"),
                        new PipelineStage(4, "TestEngineer"),
                        new PipelineStage(5, "Judge") { RequiresReview = true }
                    }
                }).ConfigureAwait(false);

                Voyage parentVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Parent duplicate-persona voyage")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Failed
                }).ConfigureAwait(false);

                // The recorded skip names the first TestEngineer stage and the analyst. A persona name
                // cannot say which of the rescue chain's two TestEngineer stages it meant.
                StageSkipRequest skip = new StageSkipRequest
                {
                    Stages = new List<string> { "TestEngineer", "PortingReferenceAnalyst" },
                    Reason = "reference already verified",
                    ConfirmedBy = "operator"
                };
                PipelineStageSkipResult recorded = new PipelineStageSkipResult(
                    pipeline,
                    new List<PipelineStage> { pipeline.Stages[1], pipeline.Stages[2] });
                await PipelineStageSkip.EmitSkippedEventsAsync(testDb.Driver, null, parentVoyage, recorded, skip).ConfigureAwait(false);

                Mission failed = await CreateFailedMissionAsync(testDb, vessel,
                    "DoD gate failed: classification=TestFail; unit-test command exited 1").ConfigureAwait(false);
                failed.Persona = "Worker";
                failed.StageOrder = 1;
                failed.VoyageId = parentVoyage.Id;
                failed.Mode = MissionModeEnum.Implementation;
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Title = "Duplicate-persona skip owner",
                    Status = ObjectiveStatusEnum.InProgress,
                    VesselIds = new List<string> { vessel.Id },
                    VoyageIds = new List<string> { parentVoyage.Id },
                    SuggestedPipelineId = pipeline.Id
                }).ConfigureAwait(false);

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                    testDb.Driver,
                    admiral,
                    new IncidentService(testDb.Driver),
                    new RunbookService(testDb.Driver, new LoggingModule()),
                    StandardRecoveryTierSettings());

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                Mission worker = admiral.DispatchedMissions.Single();
                List<Mission> recoveryMissions = await testDb.Driver.Missions
                    .EnumerateByVoyageAsync(worker.VoyageId!).ConfigureAwait(false);
                AssertEqual(
                    "Worker,TestEngineer,TestEngineer,Judge",
                    DescribeStageChain(recoveryMissions),
                    "Both TestEngineer stages run because the skip cannot name one of them; the analyst skip is carried.");

                List<ArmadaEvent> rescueSkips = await ReadStageSkippedEventsAsync(testDb, worker.VoyageId!).ConfigureAwait(false);
                AssertEqual(1, rescueSkips.Count, "Only the unambiguous skip is recorded on the rescue voyage.");
                AssertContains("\"Persona\":\"PortingReferenceAnalyst\"", rescueSkips[0].Payload ?? "", "The analyst skip is the one carried.");
            }).ConfigureAwait(false);

            await RunTest("Chained rescue stage preserves Research mode and playbook context", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_recovery_research", "usr_recovery_research").ConfigureAwait(false);
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                    testDb.Driver,
                    admiral,
                    new IncidentService(testDb.Driver),
                    new RunbookService(testDb.Driver, new LoggingModule()));
                Mission failed = new Mission("Research source review", "Read-only source comparison")
                {
                    TenantId = "ten_recovery_research",
                    UserId = "usr_recovery_research",
                    VesselId = "vessel-recovery-research",
                    Mode = MissionModeEnum.Research,
                    PreferredModel = "medium",
                    Priority = 80
                };
                SelectedPlaybook selection = new SelectedPlaybook
                {
                    PlaybookId = "playbook-research-context",
                    DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent,
                    InlineFullContent = "Keep the work read-only."
                };

                Mission stage = orchestrator.BuildChainedRescueStage(
                    failed,
                    "vyg_research",
                    "msn_worker",
                    new PipelineStage(2, "PortingReferenceAnalyst"),
                    1,
                    new List<SelectedPlaybook> { selection });

                AssertEqual(MissionModeEnum.Research, stage.Mode,
                    "A chained stage must not widen Research mode to Implementation.");
                AssertEqual(selection.PlaybookId, stage.SelectedPlaybooks.Single().PlaybookId,
                    "A chained Research stage must retain its playbook selection.");
                AssertEqual(selection.InlineFullContent, stage.SelectedPlaybooks.Single().InlineFullContent,
                    "The cloned selection must retain immutable inline content.");
                await Task.CompletedTask;
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

            await RunTest("A Judge failure that declares a criterion conflict holds the rescue for criterion review", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_criterion", "usr_criterion").ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb, "ten_criterion", "usr_criterion").ConfigureAwait(false);
                Mission failed = await CreateFailedMissionAsync(testDb, vessel,
                    "Judge verdict: NEEDS_REVISION " + JudgeCriterionConflicts.RescueBlockMarker).ConfigureAwait(false);
                failed.Persona = "Judge";
                failed.ReviewComment = "Bound the cache with LRU eviction.";
                await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks, TwoAttemptSettings());

                await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchedMissions.Count,
                    "A rescue would build the fix and fail the next Judge against the unchanged criterion, so none is dispatched while budget remains.");

                AuthContext auth = AuthContext.Authenticated("ten_criterion", "usr_criterion", false, true, "UnitTest");
                EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    MissionId = failed.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, incidentPage.Objects.Count, "The operator gets one incident to review the criterion.");

                EnumerationResult<ArmadaEvent> blocked = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery
                {
                    MissionId = failed.Id,
                    EventType = "autonomous_recovery.blocked",
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);
                AssertEqual(1, blocked.Objects.Count, "A blocked event records the decision.");
                AssertTrue((blocked.Objects[0].Message ?? String.Empty).Contains(JudgeCriterionConflicts.ReasonCode + ":", StringComparison.Ordinal),
                    "The blocked reason names the criterion conflict. Message: " + blocked.Objects[0].Message);
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

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

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

                await SeedEligibleRecoveryCaptainsAsync(testDb, vessel, "Worker").ConfigureAwait(false);
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

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

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

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

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

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

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

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

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

                await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

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
            await RunTest("BuildRescueDescription embeds a Judge report within the full-review cap",
                BuildRescueDescription_OverCapJudgeReport_KeepsTheVerdictAndFollowUpsWhole).ConfigureAwait(false);
            await RunTest("BuildRescueDescription embeds an under-cap Judge report whole",
                BuildRescueDescription_UnderCapJudgeReport_IsEmbeddedWhole).ConfigureAwait(false);
            await RunTest("BuildRescueDescription marks protected findings overflow incomplete and points to full output", async () =>
            {
                StringBuilder report = new StringBuilder();
                report.AppendLine("## Correctness");
                for (int i = 1; i <= 24; i++)
                    report.AppendLine(i + ". src/Example.cs:" + (i * 10) + " -- required fix " + i + ": " + new string('x', 900));
                Mission failed = new Mission("Oversized review")
                {
                    Id = "msn_test_overflow_review",
                    Status = MissionStatusEnum.Failed,
                    Persona = "Judge",
                    AgentOutput = report.ToString(),
                    ReviewComment = report.ToString()
                };
                Incident incident = new Incident
                {
                    Id = "inc_test_overflow_review",
                    Title = "synthetic review overflow",
                    Summary = "synthetic summary",
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.Medium
                };

                string brief = AutonomousRecoveryOrchestrator.BuildRescueDescription(failed, incident, 1);

                AssertContains("incomplete", brief, "The brief states that the capped embedded list is incomplete.");
                AssertContains("mission-output:" + failed.Id, brief, "The overflow case names the complete output artifact.");
                AssertContains("armada_mission_output", brief, "The overflow case gives the tool needed to read the complete review.");
                AssertFalse(brief.Contains("fix all of them", StringComparison.Ordinal), "An incomplete embedded list does not claim to contain every defect.");
                AssertTrue(brief.Length < 24000, "The rescue brief remains bounded when protected review material exceeds its cap.");
                await Task.CompletedTask;
            }).ConfigureAwait(false);
            await RunTest("StageReportEssentials marks findings incomplete when the outer section cap cannot hold them", async () =>
            {
                Mission mission = new Mission("bounded report")
                {
                    Id = "msn_test_bounded_report",
                    AgentOutput = "## Correctness\n" + String.Join("\n", Enumerable.Range(1, 12)
                        .Select(index => index + ". src/Test.cs:" + index + " -- required defect " + new string('x', 100)))
                };
                string essentials = StageReportEssentials.Build(
                    "Judge",
                    mission.Id,
                    mission.AgentOutput,
                    MissionOutputArtifact.Build(mission, 0, 64000),
                    400);

                AssertContains("Blocking findings (incomplete", essentials, "The outer cap does not advertise a partial list as complete.");
                AssertContains("armada_mission_output", essentials, "The bounded section points to the complete output.");
                AssertContains("mission-output:" + mission.Id, essentials, "The compact reference retains the artifact pointer.");
                AssertContains(MissionOutputArtifact.Build(mission, 0, 64000).Sha256, essentials, "The bounded reference retains the exact output digest.");
                AssertFalse(essentials.Contains("every one; fix all", StringComparison.Ordinal), "The cut essentials do not claim to list every finding.");
                AssertTrue(essentials.Length <= 400, "The essentials section respects its outer cap.");
                await Task.CompletedTask;
            }).ConfigureAwait(false);
            await RunTest("StageReportEssentials reads Linter, Findings and bold section labels, and flags a rejection it could not read", async () =>
            {
                string linter = "## Code Style\n- Fine.\n## Code Correctness\n- src/Lint.cs:12 -- the null check is inverted.\n## Residual Issues\nNone.\n[ARMADA:RESULT] COMPLETE";
                AssertContains("src/Lint.cs:12", StageReportEssentials.BlockingFindings(linter, 4000), "A Linter's Code Correctness defect is a finding");

                string findings = "## Findings\n1. src/Report.cs:7 -- the cited path does not resolve.\n[ARMADA:VERDICT] NEEDS_REVISION";
                AssertContains("src/Report.cs:7", StageReportEssentials.BlockingFindings(findings, 4000), "A Findings section defect is a finding");

                string required = "## Required Changes\n- Add the missing timeout test.\n[ARMADA:VERDICT] NEEDS_REVISION";
                AssertContains("missing timeout test", StageReportEssentials.BlockingFindings(required, 4000), "A Required Changes item is a finding");

                string bold = "**Correctness**\n- src/Bold.cs:3 -- the retry count is off by one.\n[ARMADA:VERDICT] NEEDS_REVISION";
                AssertContains("src/Bold.cs:3", StageReportEssentials.BlockingFindings(bold, 4000), "A whole-line bold section label opens its section");

                string unreadable = "The work looks incomplete overall and needs another pass.\n[ARMADA:VERDICT] NEEDS_REVISION";
                AssertContains(StageReportEssentials.IncompleteFindingsMarker, StageReportEssentials.BlockingFindings(unreadable, 4000),
                    "A rejection with no readable finding is marked incomplete instead of reading as no findings");

                string quoted = "End with `[ARMADA:VERDICT] FAIL` when it fails.\nAll criteria hold.\n[ARMADA:VERDICT] PASS";
                AssertEqual(String.Empty, StageReportEssentials.BlockingFindings(quoted, 4000), "Only the final verdict marker decides a rejection");
                await Task.CompletedTask;
            }).ConfigureAwait(false);
            await RunTest("TruncateReviewerFeedbackForBrief keeps the head-first cut for text without Judge sections",
                TruncateReviewerFeedbackForBrief_TextWithoutJudgeSections_KeepsTheHeadFirstCut).ConfigureAwait(false);
            await RunTest("BuildRescueDescription carries every blocking finding of a long review whose findings sit mid-report",
                BuildRescueDescription_LongReviewWithMidReportFindings_CarriesEveryBlockingFinding).ConfigureAwait(false);
            await RunTest("BuildRescueDescription keeps unmarked Residual Risks defects from a long report-only Judge review",
                BuildRescueDescription_LongReportOnlyJudgeReview_CarriesResidualRiskDefect).ConfigureAwait(false);
            await RunTest("BuildRescueDescription keeps the newest prior stage's report essentials that the description digest would cut",
                BuildRescueDescription_LongDescription_KeepsTheNewestReportEssentials).ConfigureAwait(false);
            await RunTest("BuildRescueDescription qualifies a do-not-re-implement line with the fixes the review requires and the non-goal rule",
                BuildRescueDescription_DoNotReimplementScope_RequiresTheReviewFixesAndLetsANonGoalWin).ConfigureAwait(false);
            await RunTest("BuildBlockingFindingsForBrief drops whole findings and counts them when the findings are long",
                BuildBlockingFindingsForBrief_ManyLongFindings_DropsWholeFindingsAndCountsThem).ConfigureAwait(false);
            await RunTest("BuildRescueDescription numbers the blocking findings and ends with one demand per finding",
                BuildRescueDescription_ReviewRejection_NumbersFindingsAndEndsWithTheDemands).ConfigureAwait(false);
            await RunTest("BuildBlockingFindingsForBrief carries a required revision written as a paragraph",
                BuildBlockingFindingsForBrief_RequiredRevisionInAParagraph_IsCarried).ConfigureAwait(false);
            await RunTest("BuildBlockingFindingsForBrief carries test and failure-mode gaps written as paragraphs",
                BuildBlockingFindingsForBrief_TestAndFailureModeGapsInParagraphs_AreCarried).ConfigureAwait(false);
            await RunTest("BuildBlockingFindingsForBrief leaves out an item whose closing sentence labels it a follow-up",
                BuildBlockingFindingsForBrief_ItemClosingWithFollowUpLabel_IsLeftOut).ConfigureAwait(false);
            await RunTest("BuildBlockingFindingsForBrief leaves out items labelled not blocking, minor or cosmetic",
                BuildBlockingFindingsForBrief_LesserLabelledItems_AreLeftOut).ConfigureAwait(false);
            await RunTest("BuildBlockingFindingsForBrief leaves out items that report agreement or disclaim a defect",
                BuildBlockingFindingsForBrief_PassingItems_AreLeftOut).ConfigureAwait(false);
            await RunTest("BuildBlockingFindingsForBrief splits required changes written inline in the Verdict",
                BuildBlockingFindingsForBrief_InlineVerdictChanges_AreSplit).ConfigureAwait(false);
            await RunTest("BuildBlockingFindingsForBrief carries a paragraph that follows a defect lead-in",
                BuildBlockingFindingsForBrief_ParagraphUnderDefectLeadIn_IsCarried).ConfigureAwait(false);
            await RunTest("BuildRescueDescription keeps unmet criteria from omitted review sections and names those sections",
                BuildRescueDescription_OversizedJudgeReview_KeepsUnmetCriteriaFromOmittedSections).ConfigureAwait(false);
            await RunTest("Challenge round: a Judge NEEDS_REVISION waits for a Challenger on another model, then the rescue carries only upheld findings", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ChallengeFixture fixture = await CreateChallengeFixtureAsync(testDb, "ten_challenge_upheld").ConfigureAwait(false);
                fixture.Settings.TypedDecisions.Mode = TypedDecisionModeEnum.Gate;
                fixture.Settings.TypedDecisions.Decisions["failure_cause"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate };
                FakeTypedDecisionClient failureCause = new FakeTypedDecisionClient(new TypedDecisionResult { Available = false, UnavailableReason = "test" });
                fixture.Orchestrator.FailureCauseAdapter = new TypedFailureCauseAdapter(
                    failureCause, new TypedDecisionRecorder(testDb.Driver, new LoggingModule()), fixture.Settings.TypedDecisions, new LoggingModule());

                await fixture.Orchestrator.HandleMissionOutcomeAsync(fixture.Failed, false).ConfigureAwait(false);
                AssertEqual(1, fixture.Admiral.DispatchedMissions.Count, "The first pass dispatches only the Challenger.");
                int failureCauseCalls = failureCause.Calls;
                AssertTrue(failureCauseCalls > 0, "The first pass asks failure_cause before a Challenger is spent.");
                Mission challenger = fixture.Admiral.DispatchedMissions[0];
                AssertEqual(PersonaCatalog.Challenger, challenger.Persona, "The dispatched mission is the Challenger.");
                AssertEqual(MissionModeEnum.Audit, challenger.Mode, "The Challenger is read-only.");
                AssertEqual(fixture.Failed.CommitHash, challenger.StartFromRef, "The Challenger reads the reviewed commit.");
                AssertTrue(MissionService.IsCaptainOnRetrySkipList(challenger.RetrySkipCaptainIds, fixture.Failed.CaptainId),
                    "Routing avoids the Judge's captain.");
                AssertContains("1. src/Parser.cs:12", challenger.Description ?? "", "The brief numbers finding 1.");
                AssertContains("3. src/Parser.cs:60", challenger.Description ?? "", "The brief numbers finding 3.");

                await fixture.Orchestrator.HandleMissionOutcomeAsync(fixture.Failed, false).ConfigureAwait(false);
                AssertEqual(1, fixture.Admiral.DispatchedMissions.Count, "A pending challenge dispatches nothing more.");
                AssertEqual(failureCauseCalls, failureCause.Calls, "A pending challenge makes no further failure_cause call.");

                await CompleteChallengerAsync(testDb, challenger, fixture.OtherModelCaptainId, "{\"findings\":["
                    + ChallengeRuling(1, "UPHOLD", 0.9) + "," + ChallengeRuling(2, "REFUTE", 0.85) + "," + ChallengeRuling(3, "REFUTE", 0.65) + "]}").ConfigureAwait(false);
                await fixture.Orchestrator.HandleMissionOutcomeAsync(fixture.Failed, false).ConfigureAwait(false);

                AssertEqual(2, fixture.Admiral.DispatchedMissions.Count, "The settled challenge dispatches the rescue.");
                Mission rescue = fixture.Admiral.DispatchedMissions[1];
                AssertEqual("Worker", rescue.Persona, "The rescue is a Worker.");
                string description = rescue.Description ?? "";
                int refutedAt = description.IndexOf("Findings the challenge round refuted", StringComparison.Ordinal);
                AssertTrue(refutedAt > 0, "The brief names the refuted findings.");
                string upheld = description.Substring(0, refutedAt);
                AssertContains("upheld by the challenge round", upheld, "The brief labels the upheld list.");
                AssertContains("src/Parser.cs:12", upheld, "Finding 1 was upheld.");
                AssertContains("src/Parser.cs:60", upheld, "Finding 3 stands: its refute is below the confidence floor.");
                AssertFalse(upheld.Contains("src/Parser.cs:40", StringComparison.Ordinal), "Finding 2 was refuted and is not to be fixed.");
                AssertContains("src/Parser.cs:40", description.Substring(refutedAt), "The refuted finding is listed with its evidence.");

                List<ArmadaEvent> ruled = await testDb.Driver.Events.EnumerateByTypeAsync(JudgeChallenge.RuledEvent, 10).ConfigureAwait(false);
                AssertEqual(1, ruled.Count, "One ruled event is recorded.");
                AssertContains("upheld 2 and refuted 1 of 3", ruled[0].Message, "The event states the counts.");
                List<Incident> active = await ActiveIncidentsAsync(testDb, fixture.Failed).ConfigureAwait(false);
                AssertTrue(active.Any(item => (item.RecoveryNotes ?? "").Contains("refuted 1 of 3", StringComparison.Ordinal)),
                    "The incident records the challenge.");
            }).ConfigureAwait(false);

            await RunTest("Challenge round: dispatching the Challenger opens the failure's incident and tells the board once that a rescue will follow", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ChallengeFixture fixture = await CreateChallengeFixtureAsync(testDb, "ten_challenge_announce").ConfigureAwait(false);
                FakeBoardNotePoster board = new FakeBoardNotePoster();
                fixture.Orchestrator.BoardNotePoster = board;

                await fixture.Orchestrator.HandleMissionOutcomeAsync(fixture.Failed, false).ConfigureAwait(false);
                Mission challenger = fixture.Admiral.DispatchedMissions[0];
                List<Incident> pending = await ActiveIncidentsAsync(testDb, fixture.Failed).ConfigureAwait(false);
                AssertEqual(1, pending.Count, "The dispatch tick opens the failure's incident.");
                AssertContains("Challenger review pending", pending[0].RecoveryNotes ?? "", "The incident says a Challenger is ruling.");
                AssertContains(challenger.Id, pending[0].RecoveryNotes ?? "", "The incident names the Challenger.");
                AssertEqual(1, board.Posts.Count, "The dispatch tick posts one board note.");
                AssertContains("Challenger review pending for Judge " + fixture.Failed.Id + "; rescue will follow", board.Posts[0],
                    "The board note names the Judge and says a rescue follows.");
                AssertEqual(fixture.Failed.Id, board.MissionIds[0], "The board note carries the failed Judge mission.");

                await fixture.Orchestrator.HandleMissionOutcomeAsync(fixture.Failed, false).ConfigureAwait(false);
                AssertEqual(1, board.Posts.Count, "A tick that only waits posts nothing more.");
                AssertEqual(1, (await ActiveIncidentsAsync(testDb, fixture.Failed).ConfigureAwait(false)).Count, "A waiting tick opens no second incident.");

                await CompleteChallengerAsync(testDb, challenger, fixture.OtherModelCaptainId, "{\"findings\":["
                    + ChallengeRuling(1, "UPHOLD", 0.9) + "," + ChallengeRuling(2, "UPHOLD", 0.9) + "," + ChallengeRuling(3, "UPHOLD", 0.9) + "]}").ConfigureAwait(false);
                await fixture.Orchestrator.HandleMissionOutcomeAsync(fixture.Failed, false).ConfigureAwait(false);
                AssertEqual(2, fixture.Admiral.DispatchedMissions.Count, "The ruling dispatches the rescue.");
                List<Incident> ruled = await ActiveIncidentsAsync(testDb, fixture.Failed).ConfigureAwait(false);
                AssertEqual(1, ruled.Count, "The rescue reuses the incident the dispatch tick opened.");
                AssertEqual(pending[0].Id, ruled[0].Id, "The rescue records on the same incident.");
                AssertEqual(1, board.Posts.Count, "Only the dispatch tick posts to the board.");
            }).ConfigureAwait(false);

            await RunTest("Challenge round: a ruling that arrives after the objective was dispatched again starts no rescue beside the newer voyage", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ChallengeFixture fixture = await CreateChallengeFixtureAsync(testDb, "ten_challenge_superseded").ConfigureAwait(false);

                await fixture.Orchestrator.HandleMissionOutcomeAsync(fixture.Failed, false).ConfigureAwait(false);
                Mission challenger = fixture.Admiral.DispatchedMissions[0];

                // While the Challenger rules, an operator requeues the objective and the scheduler
                // dispatches it as a new voyage.
                Voyage newer = await testDb.Driver.Voyages.CreateAsync(new Voyage("Requeued full pipeline")
                {
                    TenantId = fixture.Failed.TenantId,
                    UserId = fixture.Failed.UserId,
                    Status = VoyageStatusEnum.InProgress
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = fixture.Failed.TenantId,
                    UserId = fixture.Failed.UserId,
                    Title = "Requeued while its Judge is challenged",
                    Status = ObjectiveStatusEnum.InProgress,
                    VesselIds = new List<string> { fixture.Failed.VesselId! },
                    MissionIds = new List<string> { fixture.Failed.Id },
                    VoyageIds = new List<string> { newer.Id }
                }).ConfigureAwait(false);

                await CompleteChallengerAsync(testDb, challenger, fixture.OtherModelCaptainId, "{\"findings\":["
                    + ChallengeRuling(1, "UPHOLD", 0.9) + "," + ChallengeRuling(2, "UPHOLD", 0.9) + "," + ChallengeRuling(3, "UPHOLD", 0.9) + "]}").ConfigureAwait(false);
                await fixture.Orchestrator.HandleMissionOutcomeAsync(fixture.Failed, false).ConfigureAwait(false);

                AssertEqual(1, fixture.Admiral.DispatchedMissions.Count, "No rescue starts beside the newer voyage of the same objective.");
                List<ArmadaEvent> superseded = await testDb.Driver.Events.EnumerateByTypeAsync("autonomous_recovery.rescue_superseded", 10).ConfigureAwait(false);
                AssertEqual(1, superseded.Count, "The skipped rescue is recorded as one named event.");
                AssertContains(newer.Id, superseded[0].Message, "The event names the voyage that carries the work.");
                List<Incident> active = await ActiveIncidentsAsync(testDb, fixture.Failed).ConfigureAwait(false);
                AssertTrue(active.Any(item => (item.RecoveryNotes ?? "").Contains("superseded_by_active_voyage", StringComparison.Ordinal)
                    && (item.RecoveryNotes ?? "").Contains(objective.Id, StringComparison.Ordinal)),
                    "The incident says why no rescue followed the ruling.");

                await fixture.Orchestrator.HandleMissionOutcomeAsync(fixture.Failed, false).ConfigureAwait(false);
                AssertEqual(1, fixture.Admiral.DispatchedMissions.Count, "A later tick dispatches nothing.");
                AssertEqual(1, (await testDb.Driver.Events.EnumerateByTypeAsync("autonomous_recovery.rescue_superseded", 10).ConfigureAwait(false)).Count,
                    "A later tick records nothing more.");
            }).ConfigureAwait(false);

            await RunTest("Challenge round: a block whose every finding is refuted dispatches no rescue and waits for an operator", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ChallengeFixture fixture = await CreateChallengeFixtureAsync(testDb, "ten_challenge_refuted").ConfigureAwait(false);

                await fixture.Orchestrator.HandleMissionOutcomeAsync(fixture.Failed, false).ConfigureAwait(false);
                await CompleteChallengerAsync(testDb, fixture.Admiral.DispatchedMissions[0], fixture.OtherModelCaptainId, "{\"findings\":["
                    + ChallengeRuling(1, "REFUTE", 0.9) + "," + ChallengeRuling(2, "REFUTE", 0.9) + "," + ChallengeRuling(3, "REFUTE", 0.9) + "]}").ConfigureAwait(false);
                await fixture.Orchestrator.HandleMissionOutcomeAsync(fixture.Failed, false).ConfigureAwait(false);
                await fixture.Orchestrator.HandleMissionOutcomeAsync(fixture.Failed, false).ConfigureAwait(false);

                AssertEqual(1, fixture.Admiral.DispatchedMissions.Count, "No rescue follows a fully refuted block.");
                Mission? judge = await testDb.Driver.Missions.ReadAsync(fixture.Failed.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Failed, judge!.Status, "The Judge verdict stands; nothing passes.");
                List<Incident> active = await ActiveIncidentsAsync(testDb, fixture.Failed).ConfigureAwait(false);
                AssertEqual(1, active.Count, "One incident is open for the operator.");
                AssertContains("judge_challenge_refuted_all", active[0].Summary ?? "", "The incident names the refuted block.");
                AssertContains("refuted 3 of 3", active[0].RecoveryNotes ?? "", "The incident lists the refutations.");
            }).ConfigureAwait(false);

            await RunTest("Challenge round: a Challenger that cannot be used leaves the rescue with every finding and records why", async () =>
            {
                Dictionary<string, Func<ChallengeFixture, TestDatabase, Task>> cases = new Dictionary<string, Func<ChallengeFixture, TestDatabase, Task>>
                {
                    ["challenger_failed"] = async (fixture, testDb) =>
                    {
                        Mission challenger = (await testDb.Driver.Missions.ReadAsync(fixture.Admiral.DispatchedMissions[0].Id).ConfigureAwait(false))!;
                        challenger.Status = MissionStatusEnum.Failed;
                        challenger.FailureReason = "Agent process exited with code 1";
                        challenger.CaptainId = fixture.OtherModelCaptainId;
                        await testDb.Driver.Missions.UpdateAsync(challenger).ConfigureAwait(false);
                    },
                    ["same_model"] = (fixture, testDb) => CompleteChallengerAsync(testDb, fixture.Admiral.DispatchedMissions[0], fixture.Failed.CaptainId!,
                        "{\"findings\":[" + ChallengeRuling(1, "REFUTE", 0.9) + "," + ChallengeRuling(2, "REFUTE", 0.9) + "," + ChallengeRuling(3, "REFUTE", 0.9) + "]}"),
                    ["missing_ruling"] = (fixture, testDb) => CompleteChallengerAsync(testDb, fixture.Admiral.DispatchedMissions[0], fixture.OtherModelCaptainId,
                        "{\"findings\":[" + ChallengeRuling(1, "REFUTE", 0.9) + "]}")
                };

                foreach (KeyValuePair<string, Func<ChallengeFixture, TestDatabase, Task>> item in cases)
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    ChallengeFixture fixture = await CreateChallengeFixtureAsync(testDb, "ten_challenge_" + item.Key.Replace("_", "")).ConfigureAwait(false);
                    await fixture.Orchestrator.HandleMissionOutcomeAsync(fixture.Failed, false).ConfigureAwait(false);
                    await item.Value(fixture, testDb).ConfigureAwait(false);
                    await fixture.Orchestrator.HandleMissionOutcomeAsync(fixture.Failed, false).ConfigureAwait(false);

                    AssertEqual(2, fixture.Admiral.DispatchedMissions.Count, item.Key + ": the rescue is dispatched.");
                    string description = fixture.Admiral.DispatchedMissions[1].Description ?? "";
                    AssertContains("Blocking findings to fix (complete list from the full review):", description, item.Key + ": the unchanged list is used.");
                    AssertContains("src/Parser.cs:40", description, item.Key + ": every finding reaches the rescue.");
                    List<ArmadaEvent> unavailable = await testDb.Driver.Events.EnumerateByTypeAsync(JudgeChallenge.UnavailableEvent, 10).ConfigureAwait(false);
                    AssertEqual(1, unavailable.Count, item.Key + ": one unavailable event.");
                    AssertContains(item.Key, unavailable[0].Message, item.Key + ": the event names the reason.");
                }
            }).ConfigureAwait(false);

            await RunTest("Challenge round: off, an excluded vessel, or a red Check on the reviewed commit keeps the unchanged rescue", async () =>
            {
                foreach (string variant in new[] { "off", "excluded", "red_check" })
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    ChallengeFixture fixture = await CreateChallengeFixtureAsync(testDb, "ten_challenge_" + variant.Replace("_", "")).ConfigureAwait(false);
                    JudgeChallengeSettings challenge = fixture.Settings.AutonomousRecovery.JudgeChallenge;
                    if (variant == "off") challenge.Enabled = false;
                    if (variant == "excluded") challenge.ExcludedVesselIds = new List<string> { fixture.Failed.VesselId! };
                    if (variant == "red_check")
                    {
                        await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                        {
                            TenantId = fixture.Failed.TenantId,
                            UserId = fixture.Failed.UserId,
                            VesselId = fixture.Failed.VesselId,
                            MissionId = fixture.Failed.Id,
                            Label = "Unit tests",
                            Type = CheckRunTypeEnum.UnitTest,
                            Status = CheckRunStatusEnum.Failed,
                            CommitHash = fixture.Failed.CommitHash
                        }).ConfigureAwait(false);
                    }

                    await fixture.Orchestrator.HandleMissionOutcomeAsync(fixture.Failed, false).ConfigureAwait(false);
                    AssertEqual(1, fixture.Admiral.DispatchedMissions.Count, variant + ": one mission is dispatched.");
                    AssertEqual("Worker", fixture.Admiral.DispatchedMissions[0].Persona, variant + ": it is the rescue, with no Challenger.");
                    int skipped = (await testDb.Driver.Events.EnumerateByTypeAsync(JudgeChallenge.SkippedEvent, 10).ConfigureAwait(false)).Count;
                    AssertEqual(variant == "red_check" ? 1 : 0, skipped, variant + ": only an enabled round records a skip.");
                }
            }).ConfigureAwait(false);

            await RunTest("Challenge round: a finding that names an unchallengeable marker stands even when the Challenger refutes it", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ChallengeFixture fixture = await CreateChallengeFixtureAsync(testDb, "ten_challenge_guard").ConfigureAwait(false);
                fixture.Settings.AutonomousRecovery.JudgeChallenge.UnchallengeableMarkers = new List<string> { "row count" };

                await fixture.Orchestrator.HandleMissionOutcomeAsync(fixture.Failed, false).ConfigureAwait(false);
                await CompleteChallengerAsync(testDb, fixture.Admiral.DispatchedMissions[0], fixture.OtherModelCaptainId, "{\"findings\":["
                    + ChallengeRuling(1, "REFUTE", 0.9) + "," + ChallengeRuling(2, "REFUTE", 0.9) + "," + ChallengeRuling(3, "REFUTE", 0.9) + "]}").ConfigureAwait(false);
                await fixture.Orchestrator.HandleMissionOutcomeAsync(fixture.Failed, false).ConfigureAwait(false);

                AssertEqual(2, fixture.Admiral.DispatchedMissions.Count, "The guarded finding keeps the rescue.");
                string description = fixture.Admiral.DispatchedMissions[1].Description ?? "";
                string upheld = description.Substring(0, description.IndexOf("Findings the challenge round refuted", StringComparison.Ordinal));
                AssertContains("src/Parser.cs:40", upheld, "The guarded finding is upheld.");
                AssertFalse(upheld.Contains("src/Parser.cs:12", StringComparison.Ordinal), "An ordinary refuted finding is not.");
            }).ConfigureAwait(false);

            await RunTest("BuildBlockingFindingsForBrief keeps passing and minor lists out, so required items are not shortened",
                BuildBlockingFindingsForBrief_PassingAndMinorLists_DoNotCrowdOutRequiredItems).ConfigureAwait(false);
            await RunTest("BuildBlockingFindingsForBrief drops plain reviewed items before shortening a named defect",
                BuildBlockingFindingsForBrief_PlainReviewedItems_GoBeforeNamedDefectsAreShortened).ConfigureAwait(false);
            await RunTest("TruncateReviewerFeedbackForBrief drops an over-budget narration preamble so the findings survive",
                TruncateReviewerFeedbackForBrief_NarrationPreambleOverBudget_KeepsTheFindingsNotTheChatter).ConfigureAwait(false);
        }

        private sealed class ChallengeFixture
        {
            public Mission Failed { get; set; } = null!;
            public RecordingAdmiralService Admiral { get; set; } = null!;
            public AutonomousRecoveryOrchestrator Orchestrator { get; set; } = null!;
            public ArmadaSettings Settings { get; set; } = null!;
            public string OtherModelCaptainId { get; set; } = String.Empty;
        }

        // A failed Judge NEEDS_REVISION with three blocking findings, judged on one model, with an idle
        // Challenger captain on another model and the challenge round enabled.
        private static async Task<ChallengeFixture> CreateChallengeFixtureAsync(TestDatabase testDb, string tenantId)
        {
            string userId = "usr_" + tenantId;
            await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
            Vessel vessel = await CreateVesselAsync(testDb, tenantId, userId).ConfigureAwait(false);
            await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

            Captain judgeCaptain = await testDb.Driver.Captains.CreateAsync(new Captain("challenge-judge-" + Guid.NewGuid().ToString("N"))
            {
                TenantId = tenantId,
                UserId = userId,
                Model = "gpt-5.6-luna",
                Tier = CaptainTierEnum.Premium,
                State = CaptainStateEnum.Idle
            }).ConfigureAwait(false);
            Captain otherModel = await testDb.Driver.Captains.CreateAsync(new Captain("challenge-other-" + Guid.NewGuid().ToString("N"))
            {
                TenantId = tenantId,
                UserId = userId,
                Model = "claude-opus-5-5",
                Tier = CaptainTierEnum.Premium,
                State = CaptainStateEnum.Idle,
                AllowedPersonas = System.Text.Json.JsonSerializer.Serialize(new[] { PersonaCatalog.Challenger })
            }).ConfigureAwait(false);

            Mission failed = await CreateFailedMissionAsync(testDb, vessel, JudgeChallenge.NeedsRevisionReason).ConfigureAwait(false);
            failed.Persona = "Judge";
            failed.CaptainId = judgeCaptain.Id;
            failed.AgentOutput = "## Completeness\n"
                + "- src/Parser.cs:12 -- empty input is handled. NOT MET.\n"
                + "- src/Parser.cs:40 -- the row count is reported. NOT MET.\n"
                + "- src/Parser.cs:60 -- the summary names the source. NOT MET.\n"
                + "- src/Parser.cs:80 -- delivered behaviour. MET.\n";
            failed.ReviewComment = failed.AgentOutput;
            await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

            ArmadaSettings settings = new ArmadaSettings();
            settings.AutonomousRecovery.JudgeChallenge.Enabled = true;
            RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
            AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                testDb.Driver, admiral, new IncidentService(testDb.Driver), new RunbookService(testDb.Driver, new LoggingModule()), settings);
            return new ChallengeFixture
            {
                Failed = failed,
                Admiral = admiral,
                Orchestrator = orchestrator,
                Settings = settings,
                OtherModelCaptainId = otherModel.Id
            };
        }

        private static async Task CompleteChallengerAsync(TestDatabase testDb, Mission dispatched, string captainId, string rulingJson)
        {
            Mission challenger = (await testDb.Driver.Missions.ReadAsync(dispatched.Id).ConfigureAwait(false))!;
            challenger.Status = MissionStatusEnum.Complete;
            challenger.CaptainId = captainId;
            challenger.AgentOutput = "## Rulings\nSee the line below.\n[ARMADA:CHALLENGE] " + rulingJson + "\n[ARMADA:RESULT] COMPLETE";
            challenger.CompletedUtc = DateTime.UtcNow;
            await testDb.Driver.Missions.UpdateAsync(challenger).ConfigureAwait(false);
        }

        private static string ChallengeRuling(int index, string ruling, double confidence)
        {
            return "{\"index\":" + index + ",\"kind\":\"MISSING\",\"ruling\":\"" + ruling + "\",\"confidence\":"
                + confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"evidence\":\"src/Parser.cs:" + (index * 20) + " already shows it\"}";
        }

        private static async Task<List<Incident>> ActiveIncidentsAsync(TestDatabase testDb, Mission failed)
        {
            IncidentService incidents = new IncidentService(testDb.Driver);
            AuthContext auth = AuthContext.Authenticated(failed.TenantId!, failed.UserId!, false, true, "UnitTest");
            return await incidents.EnumerateActiveAsync(auth, new IncidentQuery { MissionId = failed.Id }).ConfigureAwait(false);
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

        private static ArmadaSettings StandardRecoveryTierSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.ModelTier.Records = TierRoutingRecords.ForMinimumTiers(new List<KeyValuePair<string, CaptainTierEnum>>
            {
                new KeyValuePair<string, CaptainTierEnum>("Worker", CaptainTierEnum.Standard),
                new KeyValuePair<string, CaptainTierEnum>("TestEngineer", CaptainTierEnum.Standard),
                new KeyValuePair<string, CaptainTierEnum>("Judge", CaptainTierEnum.Standard)
            });
            return settings;
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

        // Same shape the definition-of-done gate writes: the class, the command label and exit code,
        // then the bounded diagnostic block.
        private static string BuildGateFailureReason(string failureClass, string label, int exitCode, string output)
        {
            return "DoD gate failed: classification=" + failureClass + "; " + label + " command exited " + exitCode +
                "\n--- ACTIONABLE DIAGNOSTICS ---\n(none recognized)\n--- OUTPUT TAIL ---\n" + output.Trim();
        }

        // Runs recovery for one gate failure and asserts no rescue was dispatched and one incident
        // holds the blocked policy. Returns the incident summary for class assertions.
        private async Task<string> AssertGateFailureBlocksRescueAsync(string tenantId, string failureReason)
        {
            string userId = "usr" + tenantId.Substring(3);
            using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
            await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
            Vessel vessel = await CreateVesselAsync(testDb, tenantId, userId).ConfigureAwait(false);
            Mission failed = await CreateFailedMissionAsync(testDb, vessel, failureReason).ConfigureAwait(false);
            failed.Persona = "Worker";
            await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

            IncidentService incidents = new IncidentService(testDb.Driver);
            RunbookService runbooks = new RunbookService(testDb.Driver, new LoggingModule());
            RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
            AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, admiral, incidents, runbooks);

            await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

            AssertEqual(0, admiral.DispatchedMissions.Count, "A host-side gate failure must not dispatch a rescue. Reason: " + failureReason);

            AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");
            EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
            {
                MissionId = failed.Id,
                PageNumber = 1,
                PageSize = 10
            }).ConfigureAwait(false);
            AssertEqual(1, incidentPage.Objects.Count, "The operator still needs an incident to act on.");
            string summary = incidentPage.Objects[0].Summary ?? String.Empty;
            AssertTrue(summary.Contains("Policy: block", StringComparison.Ordinal), "The incident must record the blocked policy. Summary: " + summary);
            AssertTrue(summary.Contains("gate_class_not_rescuable", StringComparison.Ordinal),
                "The incident must retain the stable gate-class reason. Summary: " + summary);

            EnumerationResult<ArmadaEvent> blocked = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery
            {
                MissionId = failed.Id,
                EventType = "autonomous_recovery.blocked",
                PageNumber = 1,
                PageSize = 10
            }).ConfigureAwait(false);
            AssertEqual(1, blocked.Objects.Count, "A blocked event records the gate-class decision.");
            AssertTrue((blocked.Objects[0].Message ?? String.Empty).Contains("gate_class_not_rescuable:", StringComparison.Ordinal),
                "The event must retain the stable gate-class reason. Message: " + blocked.Objects[0].Message);
            return summary;
        }

        // A stored failing-test set for the pure comparison test.
        private static AutonomousRecoveryOrchestrator.StoredFailedTestSet FailedTestSet(bool overflow, params string[] names)
        {
            return new AutonomousRecoveryOrchestrator.StoredFailedTestSet(new List<string>(names), overflow);
        }

        // Settings that allow a second recovery attempt, so the repeated-failure guard runs instead of
        // the default single-attempt budget hard-block pre-empting it.
        private static ArmadaSettings TwoAttemptSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.AutonomousRecovery.MaxMissionRecoveryAttempts = 2;
            return settings;
        }

        // Write a definition-of-done TestFail evaluation event for a mission the way the gate does.
        private static async Task WriteTestFailEvaluationAsync(TestDatabase testDb, string missionId, bool overflow, params string[] failedTestNames)
        {
            DefinitionOfDoneEvaluationRecord record = new DefinitionOfDoneEvaluationRecord
            {
                Outcome = DefinitionOfDoneEvaluationOutcomeEnum.Failed,
                CommandLabel = "unit-test",
                ExitCode = 1,
                FailureClass = DefinitionOfDoneFailureClassEnum.TestFail,
                OutputTail = "--- OUTPUT TAIL ---",
                FailedTestNames = new List<string>(failedTestNames),
                FailedTestNamesOverflow = overflow
            };

            ArmadaEvent evt = new ArmadaEvent(DefinitionOfDoneEvaluationRecord.EventType, "Definition-of-done evaluation")
            {
                MissionId = missionId,
                EntityType = "mission",
                EntityId = missionId,
                Payload = System.Text.Json.JsonSerializer.Serialize(record),
                CreatedUtc = DateTime.UtcNow
            };
            await testDb.Driver.Events.CreateAsync(evt).ConfigureAwait(false);
        }

        // A failed autonomous rescue mission: it carries the auto-rescue marker and links to its parent.
        private static async Task<Mission> CreateRescueMissionAsync(TestDatabase testDb, Vessel vessel, string parentMissionId, string failureReason)
        {
            Mission mission = new Mission
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                VesselId = vessel.Id,
                ParentMissionId = parentMissionId,
                Persona = "Worker",
                Title = "Rescue 1: Failed mission",
                Description = RescueMissionMarker.Marker + "\nAutonomous rescue for a failed mission.",
                Status = MissionStatusEnum.Failed,
                FailureReason = failureReason,
                RecoveryAttempts = 1,
                CommitHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                CompletedUtc = DateTime.UtcNow.AddMinutes(-1),
                LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
            };
            return await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
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

        // A failed Judge on an objective voyage dispatched with the analyst and the TestEngineer
        // skipped. The skip events are written by the same helper a dispatch uses.
        private static async Task<(Mission Failed, Voyage ParentVoyage, RecordingAdmiralService Admiral, AutonomousRecoveryOrchestrator Orchestrator)>
            CreateSkippedStageRescueAsync(TestDatabase testDb, string tenantId, string failureReason)
        {
            string userId = "usr_" + tenantId;
            await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
            Vessel vessel = await CreateVesselAsync(testDb, tenantId, userId).ConfigureAwait(false);
            await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

            Pipeline pipeline = await testDb.Driver.Pipelines.CreateAsync(new Pipeline("ReferencePortingSkipRecovery")
            {
                TenantId = vessel.TenantId,
                Stages = new List<PipelineStage>
                {
                    new PipelineStage(1, "Worker"),
                    new PipelineStage(2, "PortingReferenceAnalyst"),
                    new PipelineStage(3, "TestEngineer"),
                    new PipelineStage(4, "Judge") { RequiresReview = true }
                }
            }).ConfigureAwait(false);

            Voyage parentVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Parent skipped-stage port")
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                Status = VoyageStatusEnum.Failed
            }).ConfigureAwait(false);
            StageSkipRequest skip = new StageSkipRequest
            {
                Stages = new List<string> { "PortingReferenceAnalyst", "TestEngineer" },
                Reason = "reference already verified",
                ConfirmedBy = "operator"
            };
            PipelineStageSkipResult skipResult = PipelineStageSkip.Apply(pipeline, skip);
            await PipelineStageSkip.EmitSkippedEventsAsync(testDb.Driver, null, parentVoyage, skipResult, skip).ConfigureAwait(false);

            Mission failed = await CreateFailedMissionAsync(testDb, vessel, failureReason).ConfigureAwait(false);
            failed.Persona = "Judge";
            failed.VoyageId = parentVoyage.Id;
            failed.Mode = MissionModeEnum.Implementation;
            await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

            await testDb.Driver.Objectives.CreateAsync(new Objective
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                Title = "Skipped-stage owner",
                Status = ObjectiveStatusEnum.InProgress,
                VesselIds = new List<string> { vessel.Id },
                VoyageIds = new List<string> { parentVoyage.Id },
                SuggestedPipelineId = pipeline.Id
            }).ConfigureAwait(false);

            RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
            AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                testDb.Driver,
                admiral,
                new IncidentService(testDb.Driver),
                new RunbookService(testDb.Driver, new LoggingModule()),
                StandardRecoveryTierSettings());
            return (failed, parentVoyage, admiral, orchestrator);
        }

        // A failed stage on an objective voyage whose pipeline names the Worker twice:
        // Worker, analyst, TestEngineer, Worker (revise), Judge. The second Worker carries an explicit tier.
        private static async Task<DuplicateWorkerRescueOutcome> RescueOnDuplicateWorkerPipelineAsync(
            TestDatabase testDb,
            string tenantId,
            string failedPersona,
            int failedStageOrder,
            string failureReason)
        {
            string userId = "usr_" + tenantId;
            await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
            Vessel vessel = await CreateVesselAsync(testDb, tenantId, userId).ConfigureAwait(false);
            await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

            Pipeline pipeline = await testDb.Driver.Pipelines.CreateAsync(new Pipeline("DuplicateWorkerRecovery")
            {
                TenantId = vessel.TenantId,
                Stages = new List<PipelineStage>
                {
                    new PipelineStage(1, "Worker"),
                    new PipelineStage(2, "PortingReferenceAnalyst"),
                    new PipelineStage(3, "TestEngineer"),
                    new PipelineStage(4, "Worker") { PreferredModel = "high" },
                    new PipelineStage(5, "Judge") { RequiresReview = true }
                }
            }).ConfigureAwait(false);

            Voyage parentVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Parent duplicate-Worker voyage")
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                Status = VoyageStatusEnum.Failed
            }).ConfigureAwait(false);

            Mission failed = await CreateFailedMissionAsync(testDb, vessel, failureReason).ConfigureAwait(false);
            failed.Persona = failedPersona;
            failed.StageOrder = failedStageOrder;
            failed.VoyageId = parentVoyage.Id;
            failed.Mode = MissionModeEnum.Implementation;
            await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

            await testDb.Driver.Objectives.CreateAsync(new Objective
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                Title = "Duplicate-Worker owner",
                Status = ObjectiveStatusEnum.InProgress,
                VesselIds = new List<string> { vessel.Id },
                VoyageIds = new List<string> { parentVoyage.Id },
                SuggestedPipelineId = pipeline.Id
            }).ConfigureAwait(false);

            RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
            AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                testDb.Driver,
                admiral,
                new IncidentService(testDb.Driver),
                new RunbookService(testDb.Driver, new LoggingModule()),
                StandardRecoveryTierSettings());

            await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

            if (admiral.DispatchedMissions.Count != 1)
                throw new Exception("Expected exactly one rescue dispatch, got " + admiral.DispatchedMissions.Count + ".");
            Mission rescue = admiral.DispatchedMissions[0];
            if (String.IsNullOrEmpty(rescue.VoyageId))
                throw new Exception("Expected the rescue to run in a rescue voyage.");
            List<Mission> recoveryMissions = await testDb.Driver.Missions
                .EnumerateByVoyageAsync(rescue.VoyageId!).ConfigureAwait(false);
            return new DuplicateWorkerRescueOutcome(rescue, recoveryMissions);
        }

        // Dispatches nothing: records (or not) the four-stage list W(1) > PRA(2) > TE(3) > J(4) on a failed
        // voyage the way a dispatch does, then inserts a revising Worker so the live pipeline becomes
        // W(1) > PRA(2) > TE(3) > W(4) > J(5), and rescues the Judge (stage 4) that returned NEEDS_REVISION.
        private static async Task<EditedPipelineRescueOutcome> RescueAfterPipelineGainsAStageAsync(
            TestDatabase testDb,
            string tenantId,
            bool recordDispatchList)
        {
            string userId = "usr_" + tenantId;
            await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
            Vessel vessel = await CreateVesselAsync(testDb, tenantId, userId).ConfigureAwait(false);
            await SeedEligibleRecoveryRosterAsync(testDb, vessel).ConfigureAwait(false);

            Pipeline pipeline = await testDb.Driver.Pipelines.CreateAsync(new Pipeline("EditedRecovery")
            {
                TenantId = vessel.TenantId,
                Stages = new List<PipelineStage>
                {
                    new PipelineStage(1, "Worker"),
                    new PipelineStage(2, "PortingReferenceAnalyst"),
                    new PipelineStage(3, "TestEngineer"),
                    new PipelineStage(4, "Judge") { RequiresReview = true }
                }
            }).ConfigureAwait(false);

            Voyage parentVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Parent edited-pipeline voyage")
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                Status = VoyageStatusEnum.Failed
            }).ConfigureAwait(false);
            if (recordDispatchList)
                await VoyagePipelineRecord.RecordAsync(testDb.Driver, null, parentVoyage, pipeline).ConfigureAwait(false);

            pipeline.Stages = new List<PipelineStage>
            {
                new PipelineStage(1, "Worker"),
                new PipelineStage(2, "PortingReferenceAnalyst"),
                new PipelineStage(3, "TestEngineer"),
                new PipelineStage(4, "Worker"),
                new PipelineStage(5, "Judge") { RequiresReview = true }
            };
            await testDb.Driver.Pipelines.UpdateAsync(pipeline).ConfigureAwait(false);

            Mission failed = await CreateFailedMissionAsync(testDb, vessel,
                "Judge verdict: NEEDS_REVISION. The error path swallows the exception.").ConfigureAwait(false);
            failed.Persona = "Judge";
            failed.StageOrder = 4;
            failed.VoyageId = parentVoyage.Id;
            failed.Mode = MissionModeEnum.Implementation;
            await testDb.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);

            await testDb.Driver.Objectives.CreateAsync(new Objective
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                Title = "Edited-pipeline owner",
                Status = ObjectiveStatusEnum.InProgress,
                VesselIds = new List<string> { vessel.Id },
                VoyageIds = new List<string> { parentVoyage.Id },
                SuggestedPipelineId = pipeline.Id
            }).ConfigureAwait(false);

            RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
            AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(
                testDb.Driver,
                admiral,
                new IncidentService(testDb.Driver),
                new RunbookService(testDb.Driver, new LoggingModule()),
                StandardRecoveryTierSettings());

            await orchestrator.HandleMissionOutcomeAsync(failed, false).ConfigureAwait(false);

            if (admiral.DispatchedMissions.Count != 1)
                throw new Exception("Expected exactly one rescue dispatch, got " + admiral.DispatchedMissions.Count + ".");
            Mission rescue = admiral.DispatchedMissions[0];
            if (String.IsNullOrEmpty(rescue.VoyageId))
                throw new Exception("Expected the rescue to run in a rescue voyage.");
            List<Mission> recoveryMissions = await testDb.Driver.Missions
                .EnumerateByVoyageAsync(rescue.VoyageId!).ConfigureAwait(false);
            List<ArmadaEvent> fallbackEvents = (await testDb.Driver.Events
                .EnumerateByEntityAsync("mission", failed.Id, 500).ConfigureAwait(false))
                .Where(item => item.EventType == VoyagePipelineRecord.LiveFallbackEventType)
                .ToList();
            return new EditedPipelineRescueOutcome(failed, rescue, recoveryMissions, fallbackEvents);
        }

        private sealed class EditedPipelineRescueOutcome
        {
            public EditedPipelineRescueOutcome(Mission failed, Mission rescue, List<Mission> recoveryMissions, List<ArmadaEvent> fallbackEvents)
            {
                Failed = failed;
                Rescue = rescue;
                RecoveryMissions = recoveryMissions;
                FallbackEvents = fallbackEvents;
            }

            public Mission Failed { get; }
            public Mission Rescue { get; }
            public List<Mission> RecoveryMissions { get; }
            public List<ArmadaEvent> FallbackEvents { get; }
        }

        private static string DescribeStageChain(List<Mission> missions)
        {
            return String.Join(",", missions.OrderBy(item => item.StageOrder ?? 0).Select(item => item.Persona));
        }

        private sealed class DuplicateWorkerRescueOutcome
        {
            public DuplicateWorkerRescueOutcome(Mission rescue, List<Mission> recoveryMissions)
            {
                Rescue = rescue;
                RecoveryMissions = recoveryMissions;
            }

            public Mission Rescue { get; }
            public List<Mission> RecoveryMissions { get; }
        }

        private static async Task<List<ArmadaEvent>> ReadStageSkippedEventsAsync(TestDatabase testDb, string voyageId)
        {
            List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByEntityAsync("voyage", voyageId, 500).ConfigureAwait(false);
            return events.Where(item => item.EventType == PipelineStageSkip.StageSkippedEventType).ToList();
        }

        // Produces a mission the way production does: a WorkProduced stage with no commit under an ended
        // voyage, moved to its terminal status by the real terminal-voyage reconciler.
        private static async Task<Mission> CreateReconciledMissionAsync(TestDatabase testDb, Vessel vessel, VoyageStatusEnum voyageStatus)
        {
            Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Ended voyage")
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                Status = voyageStatus,
                CompletedUtc = DateTime.UtcNow.AddHours(-2),
                LastUpdateUtc = DateTime.UtcNow.AddHours(-2)
            }).ConfigureAwait(false);

            Mission produced = await testDb.Driver.Missions.CreateAsync(new Mission
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                VesselId = vessel.Id,
                VoyageId = voyage.Id,
                Persona = "Worker",
                Title = "Upstream stage",
                Description = "Original mission description",
                Status = MissionStatusEnum.WorkProduced,
                LastUpdateUtc = DateTime.UtcNow.AddHours(-2)
            }).ConfigureAwait(false);

            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            await new TerminalVoyageMissionReconciler(logging, testDb.Driver, new GitService(logging))
                .ReconcileAsync(new TerminalVoyageMissionReconciliationRequest
                {
                    DryRun = false,
                    IncludeHistorical = true,
                    VoyageId = voyage.Id
                }).ConfigureAwait(false);

            return (await testDb.Driver.Missions.ReadAsync(produced.Id).ConfigureAwait(false))!;
        }

        private static async Task<int> CountIncidentsAsync(IncidentService incidents, Vessel vessel, string missionId)
        {
            AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");
            EnumerationResult<Incident> page = await incidents.EnumerateAsync(auth, new IncidentQuery
            {
                MissionId = missionId,
                PageNumber = 1,
                PageSize = 10
            }).ConfigureAwait(false);
            return page.Objects.Count;
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

        private static async Task SeedEligibleRecoveryCaptainsAsync(TestDatabase testDb, Vessel vessel, params string[] personas)
        {
            foreach (string persona in personas)
            {
                await testDb.Driver.Captains.CreateAsync(new Captain("recovery-" + persona + "-" + Guid.NewGuid().ToString("N"))
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Model = "gpt-5.6-luna",
                    Tier = CaptainTierEnum.Premium,
                    State = CaptainStateEnum.Idle,
                    AllowedPersonas = System.Text.Json.JsonSerializer.Serialize(new[] { persona })
                }).ConfigureAwait(false);
            }
        }

        private static async Task SeedEligibleRecoveryRosterAsync(TestDatabase testDb, Vessel vessel)
        {
            // These are the only downstream personas used by the positive recovery pipelines in this suite.
            // Each captain is tenant-owned and persona-specific; negative capability cases seed their own rosters.
            foreach (string persona in new[] { "Worker", "PortingReferenceAnalyst", "TestEngineer", "Judge" })
            {
                await testDb.Driver.Captains.CreateAsync(new Captain("recovery-" + persona + "-" + Guid.NewGuid().ToString("N"))
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Model = "gpt-5.6-luna",
                    Tier = CaptainTierEnum.Premium,
                    State = CaptainStateEnum.Idle,
                    AllowedPersonas = System.Text.Json.JsonSerializer.Serialize(new[] { persona })
                }).ConfigureAwait(false);
            }
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

            public Exception? ThrowOnDispatch { get; set; }

            public FleetCapacityAdmissionException? CapacityRefusal { get; set; }

            public Task<FleetCapacityAdmissionException?> ProbeCapacityAsync(Vessel vessel, string? candidateVoyageId, CancellationToken token = default)
                => Task.FromResult(CapacityRefusal);

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
                if (ThrowOnDispatch != null) throw ThrowOnDispatch;
                if (!String.IsNullOrWhiteSpace(AssignedRescueCaptainId))
                    mission.CaptainId = AssignedRescueCaptainId;

                Mission created = await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                if (created.SelectedPlaybooks.Count > 0 && !String.IsNullOrWhiteSpace(created.TenantId))
                {
                    PlaybookService playbooks = new PlaybookService(_Database, new LoggingModule());
                    List<MissionPlaybookSnapshot> snapshots = await playbooks.CreateSnapshotsAsync(
                        created.TenantId,
                        created.SelectedPlaybooks,
                        token).ConfigureAwait(false);
                    await _Database.Playbooks.SetMissionSnapshotsAsync(created.Id, snapshots, token).ConfigureAwait(false);
                }
                DispatchedMissions.Add(created);
                return created;
            }

            public async Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
            {
                if (!String.IsNullOrWhiteSpace(pipelineIdOrName))
                {
                    Pipeline? byId = await _Database.Pipelines.ReadAsync(pipelineIdOrName, token).ConfigureAwait(false);
                    if (byId != null) return byId;
                    if (!String.IsNullOrWhiteSpace(vessel.TenantId))
                    {
                        return await _Database.Pipelines
                            .ReadByNameAsync(vessel.TenantId, pipelineIdOrName, token).ConfigureAwait(false);
                    }
                }

                if (!String.IsNullOrWhiteSpace(vessel.DefaultPipelineId))
                    return await _Database.Pipelines.ReadAsync(vessel.DefaultPipelineId, token).ConfigureAwait(false);
                return null;
            }

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
            string scope = "scope: add a 4-test coverage for the schema-v2 fixture." + "\n" + "files: TestAssets/example.json" + "\n";
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
            string scope = "title: add schema-v2 fixture" + "\n" + "files: TestAssets/example.json";
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
            return "## Completeness" + "\n" + BuildBenignReviewPadding(completenessChars) + "\n\n"
                + "## Correctness" + "\n" + BuildBenignReviewPadding(correctnessChars) + "\n\n"
                + "## Tests" + "\n" + "Ran the suite in the foreground: 0 failed." + "\n\n"
                + "## Failure Modes" + "\n" + "Reviewed the absence paths." + "\n\n"
                + "## Suggested Follow-ups" + "\n" + "- src/Fixture.cs:12 -- record the sha256 of both copies." + "\n\n"
                + "## Verdict" + "\n" + "NEEDS_REVISION: the disclosure is missing; add the two hashes and the identity statement." + "\n\n"
                + "[ARMADA:VERDICT] NEEDS_REVISION";
        }

        private static string BuildBenignReviewPadding(int minimumChars)
        {
            string[] sentences =
            {
                "The review records the observed behavior for this documented boundary.",
                "The evidence remains within the expected scope for this case.",
                "The reviewer checked the result against the stated contract.",
                "This note preserves ordinary report text for the size-boundary scenario."
            };
            StringBuilder padding = new StringBuilder();
            int sentenceIndex = 0;
            while (padding.Length < minimumChars)
            {
                padding.Append(sentences[sentenceIndex % sentences.Length]).Append(' ');
                sentenceIndex++;
            }
            return padding.ToString().TrimEnd();
        }

        public async Task BuildRescueDescription_OverCapJudgeReport_KeepsTheVerdictAndFollowUpsWhole()
        {
            // The report is larger than the former excerpt cap but fits the full-review cap.
            string report = BuildJudgeReport(4000, 4000);
            AssertTrue(report.Length < 12000,
                "Precondition: the complete report must fit the production review cap. Actual length: " + report.Length);

            Mission failed = new Mission
            {
                Id = "msn_test_judge_over",
                Persona = "Judge",
                Title = "test mission",
                Status = MissionStatusEnum.Failed,
                FailureReason = "Judge verdict: NEEDS_REVISION",
                Description = "title: decompile the update manager",
                ReviewComment = report,
                AgentOutput = report,
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

            AssertContains(report, brief, "The complete Judge report is carried when it fits the full-review cap.");
            AssertFalse(brief.Contains("reviewer feedback truncated", StringComparison.Ordinal), "A report inside the full-review cap is not truncated.");

            int feedbackStart = brief.IndexOf("Reviewer feedback to address:", StringComparison.Ordinal);
            int feedbackEnd = brief.IndexOf("Objective:", StringComparison.Ordinal);
            AssertTrue(feedbackStart > 0 && feedbackEnd > feedbackStart, "The brief keeps its section order.");
            int feedbackLength = feedbackEnd - feedbackStart;
            AssertTrue(feedbackLength <= 12000 + 400,
                "The embedded feedback must stay near the full-review cap. Actual length: " + feedbackLength);
            await Task.CompletedTask;
        }

        // A Judge lists its findings in Completeness and Correctness, between long evidence
        // sections. The rescue brief must name every blocking one, not only those that survive a
        // head-and-tail excerpt.
        public async Task BuildRescueDescription_LongReviewWithMidReportFindings_CarriesEveryBlockingFinding()
        {
            string notDelivered = "- Today's counts: `Status.cs:18-19` declares the fields, but no hunk in `Registry.cs` assigns either; `RiskBook.Today` already counts them (`RiskBook.cs:54-58`). NOT DELIVERED.";
            string blockingOne = "1. **Blocking. Today's trades are never populated although a counter already exists.** One fill today makes `RiskBook.Today(now).Trades` equal 1 while the route reports null (`Registry.cs:137-149`).";
            string blockingTwo = "2. **Blocking. Exit, structure and entry rows ignore the paper window that gates their entries.** `ExitStudy.cs:52`, `StructureStudy.cs:76` and `EntryStudy.cs:87` return before opening a session when the window has passed.";
            string nonBlocking = "Non-blocking observations: the macro row shows study while live entry holds.";
            StringBuilder review = new StringBuilder();
            review.AppendLine("I'll start by reading the briefing memory and mission description files, then review the branch diff.");
            review.AppendLine("The build passed. Now I'll read the full test file.");
            review.AppendLine("## Completeness");
            for (int i = 0; i < 12; i++) review.AppendLine("- Delivered item " + i + ": `src/Area/File" + i + ".cs:1-40` holds the record and its rows. DELIVERED.");
            review.AppendLine(notDelivered);
            for (int i = 12; i < 20; i++) review.AppendLine("- Delivered item " + i + ": `src/Area/File" + i + ".cs:1-40` holds the record and its rows. DELIVERED.");
            review.AppendLine("## Correctness");
            for (int i = 0; i < 14; i++) review.AppendLine("- Verified rule " + i + " against its owner at `src/Owner" + i + ".cs:10`; the behaviour matches the documented contract.");
            review.AppendLine("Defects found:");
            review.AppendLine(blockingOne);
            review.AppendLine(blockingTwo);
            review.AppendLine(nonBlocking);
            review.AppendLine("## Tests");
            for (int i = 0; i < 24; i++) review.AppendLine("- Test " + i + " covers the changed behaviour for case " + i + " and passed in the foreground run.");
            review.AppendLine("## Failure Modes");
            for (int i = 0; i < 10; i++) review.AppendLine("- Failure mode " + i + " is handled by an explicit guard and an error message naming the input.");
            review.AppendLine("## Suggested Follow-ups");
            review.AppendLine("- Populate the counts from the risk book and add a window test per study row.");
            review.AppendLine("## Verdict");
            review.AppendLine("NEEDS_REVISION: two blocking defects.");
            review.AppendLine("[ARMADA:VERDICT] NEEDS_REVISION");
            string text = review.ToString();
            AssertTrue(text.Length > 7000, "Precondition: the review is as long as the observed one. Actual: " + text.Length);

            Mission failed = new Mission
            {
                Id = "msn_test_mid_findings",
                Title = "test mission",
                Status = MissionStatusEnum.Failed,
                FailureReason = "Judge verdict: NEEDS_REVISION",
                Description = "title: build the registry",
                ReviewComment = text,
                AgentOutput = text,
            };
            Incident incident = new Incident { Id = "inc_test_mid_findings", Title = "t", Summary = "s", Status = IncidentStatusEnum.Open, Severity = IncidentSeverityEnum.Medium };

            string brief = AutonomousRecoveryOrchestrator.BuildRescueDescription(failed, incident, 1);

            int blockStart = brief.IndexOf("Blocking findings to fix", StringComparison.Ordinal);
            int feedbackStart = brief.IndexOf("Reviewer feedback to address:", StringComparison.Ordinal);
            AssertTrue(blockStart > 0 && feedbackStart > blockStart, "The blocking findings come before the excerpt");
            string block = brief.Substring(blockStart, feedbackStart - blockStart);
            AssertContains(notDelivered.Substring(2), block, "The NOT DELIVERED item is carried whole");
            AssertContains(blockingOne.Substring(3), block, "The first blocking defect is carried whole without a duplicated list marker");
            AssertContains(blockingTwo.Substring(3), block, "The second blocking defect is carried whole without a duplicated list marker");
            AssertFalse(block.Contains("Non-blocking observations"), "A non-blocking note is not listed as blocking");
            AssertFalse(block.Contains("Verified rule", StringComparison.Ordinal), "Verified Correctness evidence does not crowd out defects.");
            AssertFalse(block.Contains("Test 0 covers", StringComparison.Ordinal), "Passed Tests evidence does not crowd out defects.");
            AssertFalse(block.Contains("Failure mode 0 is handled", StringComparison.Ordinal), "Handled Failure Modes evidence does not crowd out defects.");
            AssertFalse(block.Contains("I'll start by reading"), "Tool narration is not a finding");
            AssertTrue(block.Length <= AutonomousRecoveryOrchestrator._MaxRescueBlockingFindingsChars + 200, "The findings block stays inside its bound. Actual: " + block.Length);
            await Task.CompletedTask;
        }

        public async Task BuildRescueDescription_LongReportOnlyJudgeReview_CarriesResidualRiskDefect()
        {
            string defect = "The report omits the required source comparison for the changed parser.";
            StringBuilder review = new StringBuilder();
            for (int i = 0; i < 220; i++)
                review.AppendLine("The auditor read the repository summary and reviewed the change history before writing this report.");
            review.AppendLine("## Completeness");
            review.AppendLine("- The requested report is present.");
            review.AppendLine("## Correctness");
            review.AppendLine("- Verified the reviewed output matches the checked source.");
            review.AppendLine("## Evidence");
            review.AppendLine("- Verified source evidence matches the reviewed revision.");
            string evidenceDefect = "The cited snapshot does not match the reviewed commit.";
            review.AppendLine("- " + evidenceDefect);
            review.AppendLine("## Residual Risks");
            review.AppendLine("- " + defect);
            review.AppendLine("## Verdict");
            review.AppendLine("NEEDS_REVISION: the source comparison is missing.");
            review.AppendLine("[ARMADA:VERDICT] NEEDS_REVISION");
            string text = review.ToString();
            AssertTrue(text.Length > AutonomousRecoveryOrchestrator._MaxRescueReviewerFeedbackChars,
                "Precondition: the report-only Judge output exceeds the embedded feedback cap.");

            Mission failed = new Mission
            {
                Id = "msn_test_report_only_residual_risk",
                Title = "review a source report",
                Status = MissionStatusEnum.Failed,
                Persona = "Judge",
                FailureReason = "Judge verdict: NEEDS_REVISION",
                Description = "title: review the source report",
                AgentOutput = text,
                ReviewComment = text,
            };
            Incident incident = new Incident
            {
                Id = "inc_test_report_only_residual_risk",
                Title = "synthetic report review",
                Summary = "synthetic summary",
                Status = IncidentStatusEnum.Open,
                Severity = IncidentSeverityEnum.Medium,
            };

            string brief = AutonomousRecoveryOrchestrator.BuildRescueDescription(failed, incident, 1);

            int findingsStart = brief.IndexOf("Blocking findings to fix (complete list from the full review):", StringComparison.Ordinal);
            int feedbackStart = brief.IndexOf("Reviewer feedback to address:", StringComparison.Ordinal);
            AssertTrue(findingsStart >= 0 && feedbackStart > findingsStart, "The complete findings block precedes reviewer feedback.");
            string findings = brief.Substring(findingsStart, feedbackStart - findingsStart);
            AssertContains(defect, findings, "The unmarked Residual Risks defect appears in the blocking-findings list.");
            AssertContains(evidenceDefect, findings, "The unmarked Evidence defect appears in the blocking-findings list.");
            AssertFalse(findings.Contains("Verified source evidence", StringComparison.Ordinal),
                "Positive Evidence does not become a defect.");
            AssertContains("Blocking findings to fix (complete list from the full review):", brief,
                "The complete finding list includes the Residual Risks defect.");
            string feedback = brief.Substring(feedbackStart);
            AssertContains("## Evidence", feedback, "Report-only evidence remains in the protected review excerpt.");
            AssertContains("## Residual Risks", feedback, "Report-only residual risks remain in the protected review excerpt.");
            AssertFalse(feedback.Contains("INCOMPLETE PROTECTED REVIEW", StringComparison.Ordinal),
                "The bounded protected report is complete after narration is omitted.");

            StringBuilder oversizedReview = new StringBuilder();
            oversizedReview.AppendLine("## Completeness");
            oversizedReview.AppendLine("The requested read-only report is present.");
            oversizedReview.AppendLine("## Correctness");
            oversizedReview.AppendLine("Verified against the requested scope.");
            oversizedReview.AppendLine("## Evidence");
            for (int i = 0; i < 600; i++)
                oversizedReview.AppendLine("Evidence narrative records a benign source observation for this read-only audit in full.");
            oversizedReview.AppendLine("## Residual Risks");
            oversizedReview.AppendLine("- " + defect);
            oversizedReview.AppendLine("## Verdict");
            oversizedReview.AppendLine("NEEDS_REVISION: the source comparison is missing.");
            oversizedReview.AppendLine("[ARMADA:VERDICT] NEEDS_REVISION");
            string oversizedText = oversizedReview.ToString();
            AssertTrue(oversizedText.Length > 50000 && oversizedText.Length < 64000,
                "Precondition: the protected report section exceeds the brief cap but fits in one output page.");
            Mission oversizedFailed = new Mission
            {
                Id = "msn_test_report_only_residual_risk_overflow",
                Title = "review an oversized source report",
                Status = MissionStatusEnum.Failed,
                Persona = "Judge",
                FailureReason = "Judge verdict: NEEDS_REVISION",
                Description = "title: review the oversized source report",
                AgentOutput = oversizedText,
                ReviewComment = oversizedText,
            };
            Incident oversizedIncident = new Incident
            {
                Id = "inc_test_report_only_residual_risk_overflow",
                Title = "synthetic oversized report review",
                Summary = "synthetic summary",
                Status = IncidentStatusEnum.Open,
                Severity = IncidentSeverityEnum.Medium,
            };

            string oversizedBrief = AutonomousRecoveryOrchestrator.BuildRescueDescription(oversizedFailed, oversizedIncident, 1);
            int oversizedFindingsStart = oversizedBrief.IndexOf("Blocking findings to fix (complete list from the full review):", StringComparison.Ordinal);
            int oversizedFeedbackStart = oversizedBrief.IndexOf("Reviewer feedback to address:", StringComparison.Ordinal);
            AssertTrue(oversizedFindingsStart >= 0 && oversizedFeedbackStart > oversizedFindingsStart,
                "The overflow case retains a complete findings block before feedback.");
            AssertContains(defect, oversizedBrief.Substring(oversizedFindingsStart, oversizedFeedbackStart - oversizedFindingsStart),
                "The Residual Risks defect remains in the finding list when protected evidence overflows.");
            AssertContains("INCOMPLETE PROTECTED REVIEW", oversizedBrief,
                "Oversized protected evidence is explicitly incomplete and directs the reader to the full output.");
            await Task.CompletedTask;
        }

        public async Task BuildRescueDescription_LongDescription_KeepsTheNewestReportEssentials()
        {
            string essentials = StageReportEssentials.Heading + " (TestEngineer msn_test_tester)\n"
                + "Complete output: mission-output:msn_test_tester (9000 chars, UTF-8 SHA-256 abc). Read it with armada_mission_output before acting on anything this summary leaves out.\n\n"
                + "Tests added:\n- StressedFill_KeepsClerkSlippage\n";
            string description = "title: stress the study\n" + new string('b', 7000)
                + "\n\n---\n<!-- ARMADA:HANDOFF:msn_test_tester -->\n## Prior Stage Output\n" + new string('o', 3000) + "\n" + essentials;
            Mission failed = new Mission
            {
                Id = "msn_test_rescue_essentials",
                Title = "test mission",
                Status = MissionStatusEnum.Failed,
                FailureReason = "Judge verdict: NEEDS_REVISION",
                Description = description,
            };
            Incident incident = new Incident { Id = "inc_test_rescue_essentials", Title = "t", Summary = "s", Status = IncidentStatusEnum.Open, Severity = IncidentSeverityEnum.Medium };

            string brief = AutonomousRecoveryOrchestrator.BuildRescueDescription(failed, incident, 1);
            AssertContains("StressedFill_KeepsClerkSlippage", brief, "The added-tests list survives the description digest");
            AssertContains("Complete output: mission-output:msn_test_tester", brief, "The complete-output reference survives");
            await Task.CompletedTask;
        }

        public async Task BuildRescueDescription_DoNotReimplementScope_RequiresTheReviewFixesAndLetsANonGoalWin()
        {
            string review = String.Join("\n", new[]
            {
                "## Correctness",
                "",
                "**Revision required: the parser must check the record length before it reads the payload.**",
                "",
                "## Verdict",
                "",
                "Fix the length check, then add the missing regression test.",
                "",
                "[ARMADA:VERDICT] NEEDS_REVISION"
            });
            string description = "## Scope\n\nFIRST ACTION (restart from preserved work). This voyage starts from the previous Worker commit, "
                + "which already implements every scope item. Do not re-implement.\n\n## Non-Goals\n\n- Do not change the public reader interface.\n";
            Mission failed = new Mission
            {
                Id = "msn_test_rescue_reimplement",
                Title = "Reader length check",
                Persona = "Judge",
                Status = MissionStatusEnum.Failed,
                FailureReason = "Judge verdict: NEEDS_REVISION",
                Description = description,
                AgentOutput = review,
                ReviewComment = review
            };
            Incident incident = new Incident { Id = "inc_test_rescue_reimplement", Title = "t", Summary = "s", Status = IncidentStatusEnum.Open, Severity = IncidentSeverityEnum.Medium };

            string brief = AutonomousRecoveryOrchestrator.BuildRescueDescription(failed, incident, 1);
            int precedence = brief.IndexOf(AutonomousRecoveryOrchestrator.RescueRevisionPrecedence, StringComparison.Ordinal);
            int carried = brief.IndexOf("Do not re-implement.", StringComparison.Ordinal);
            AssertTrue(carried >= 0, "The original scope line is carried into the rescue");
            AssertTrue(precedence >= 0 && precedence < carried,
                "The precedence that qualifies the carried line comes before it");
            AssertContains("do make every revision the blocking findings require", brief,
                "A do-not-re-implement line does not cancel the fixes the review requires");
            AssertContains("the non-goal wins", brief, "A required fix that a non-goal forbids is reported, not made");

            Mission gateFailure = new Mission
            {
                Id = "msn_test_rescue_gate",
                Title = "Reader length check",
                Persona = "Worker",
                Status = MissionStatusEnum.Failed,
                FailureReason = "DoD gate failed: classification=Compile",
                Description = description
            };
            string gateBrief = AutonomousRecoveryOrchestrator.BuildRescueDescription(gateFailure, incident, 1);
            AssertFalse(gateBrief.Contains(AutonomousRecoveryOrchestrator.RescueRevisionPrecedence, StringComparison.Ordinal),
                "A rescue with no review carries no review precedence");
            await Task.CompletedTask;
        }

        public async Task BuildBlockingFindingsForBrief_ManyLongFindings_DropsWholeFindingsAndCountsThem()
        {
            StringBuilder review = new StringBuilder();
            review.AppendLine("## Correctness");
            for (int i = 0; i < 12; i++)
                review.AppendLine((i + 1) + ". **Blocking. Finding number " + i + " opens here.** " + new string('x', 900) + " End of finding " + i + ".");
            string block = AutonomousRecoveryOrchestrator.BuildBlockingFindingsForBrief(review.ToString(), 4000);

            AssertTrue(block.Length <= 4000, "The block respects its exact bound. Actual: " + block.Length);
            AssertFalse(block.Contains("[detail omitted]", StringComparison.Ordinal), "No finding is cut.");
            int kept = 0;
            while (kept < 12 && block.Contains("End of finding " + kept + ".", StringComparison.Ordinal))
            {
                AssertContains("Finding number " + kept + " opens here.", block, "A kept finding keeps its opening.");
                kept++;
            }
            AssertTrue(kept > 0 && kept < 12, "Some findings fit and some do not. Kept: " + kept);
            for (int i = kept; i < 12; i++)
                AssertFalse(block.Contains("Finding number " + i + " opens here.", StringComparison.Ordinal), "A finding that does not fit is dropped whole, not started.");
            AssertContains((12 - kept) + " lower-priority findings are omitted whole", block, "The block names how many findings it dropped.");
            AssertContains(StageReportEssentials.IncompleteFindingsMarker, block, "The shortened list says it is incomplete.");
            AssertEqual(String.Empty, AutonomousRecoveryOrchestrator.BuildBlockingFindingsForBrief("## Verdict\nPASS", 4000), "A review with no blocking item yields nothing");
            await Task.CompletedTask;
        }

        public async Task BuildRescueDescription_ReviewRejection_NumbersFindingsAndEndsWithTheDemands()
        {
            Mission failed = new Mission
            {
                Id = "msn_test_numbered_findings",
                Title = "review the change",
                Status = MissionStatusEnum.Failed,
                Persona = "Judge",
                FailureReason = "Judge verdict: NEEDS_REVISION",
                Description = "title: review the change",
                AgentOutput = String.Join("\n", new[]
                {
                    "## Correctness",
                    "- **Blocking.** The retry loop never stops after a cancelled token.",
                    "- **Blocking.** The parser accepts a negative length.",
                    "## Verdict",
                    "[ARMADA:VERDICT] NEEDS_REVISION"
                })
            };
            Incident incident = new Incident { Id = "inc_test_numbered_findings", Title = "synthetic", Summary = "synthetic", Status = IncidentStatusEnum.Open, Severity = IncidentSeverityEnum.Medium };

            string brief = AutonomousRecoveryOrchestrator.BuildRescueDescription(failed, incident, 1);

            int findingsStart = brief.IndexOf("Blocking findings to fix", StringComparison.Ordinal);
            AssertTrue(findingsStart >= 0, "The brief carries the findings block.");
            AssertContains("1. **Blocking.** The retry loop never stops after a cancelled token.", brief.Substring(findingsStart), "The first finding is numbered 1.");
            AssertContains("2. **Blocking.** The parser accepts a negative length.", brief.Substring(findingsStart), "The second finding is numbered 2.");
            int demandsStart = brief.LastIndexOf("Demands:", StringComparison.Ordinal);
            AssertTrue(demandsStart > findingsStart, "The demands follow the findings.");
            string demands = brief.Substring(demandsStart);
            AssertContains("1. Finding 1:", demands, "Demand 1 answers finding 1.");
            AssertContains("2. Finding 2:", demands, "Demand 2 answers finding 2.");
            AssertFalse(demands.Contains("3. Finding 3", StringComparison.Ordinal), "There is one demand per finding and no more.");
            AssertTrue(demands.TrimEnd().EndsWith(ReworkPromptBuilder.OutputContract, StringComparison.Ordinal), "The output contract is the last line of the brief.");
            await Task.CompletedTask;
        }

        public async Task BuildBlockingFindingsForBrief_RequiredRevisionInAParagraph_IsCarried()
        {
            string review = String.Join("\n", new[]
            {
                "## Completeness",
                "",
                "The diff adds only the proof document.",
                "",
                "## Correctness",
                "",
                "One required revision: `docs/proof.md:49-50` incorrectly states that both inventories used the same top-level directory.",
                "",
                "The retained runner inventories the root, while the canonical command defaults to the tests directory.",
                "",
                "This affects the proof itself. Preserve the passing run evidence, correct the description, and obtain discovery-only inventories for each configuration.",
                "",
                "## Tests",
                "",
                "All primary files match their receipt digests. Current Build and Unit checks passed.",
                "",
                "## Failure Modes",
                "",
                "The document correctly preserves the unnamed skips and the scheduling overlap.",
                "",
                "No executable behavior changed. The remaining risk is an unsupported discovery-equivalence claim.",
                "",
                "## Verdict",
                "",
                "Correct the discovery evidence and its description.",
                "",
                "[ARMADA:VERDICT] NEEDS_REVISION"
            });

            string block = AutonomousRecoveryOrchestrator.BuildBlockingFindingsForBrief(review, 12000);

            AssertContains("One required revision: `docs/proof.md:49-50` incorrectly states", block, "A required revision written as a paragraph is carried with its anchor");
            AssertContains("Preserve the passing run evidence, correct the description", block, "The paragraph that states the fix is carried");
            AssertContains("unsupported discovery-equivalence claim", block, "An unsupported claim named under Failure Modes is carried");
            AssertFalse(block.Contains("correctly preserves", StringComparison.Ordinal), "A Failure Modes paragraph that reports correct behaviour is not a finding");
            AssertFalse(block.Contains("match their receipt digests", StringComparison.Ordinal), "A passing Tests paragraph is not a finding");
            AssertFalse(block.Contains("The diff adds only", StringComparison.Ordinal), "Completeness narration is not a finding");
            await Task.CompletedTask;
        }

        public async Task BuildBlockingFindingsForBrief_PassingAndMinorLists_DoNotCrowdOutRequiredItems()
        {
            string review = String.Join("\n", new[]
            {
                "## Correctness",
                "",
                "The core logic is sound against the contract:",
                "- Budget, off and other prechecks with no transport rows count as zero attempts. A queue-bound capture is not treated as a request.",
                "- A request with no matching invocation makes the ledger incomplete; it is never counted as zero.",
                "- Fresh attempt IDs are not collapsed. An invocation from another correlation does not leak into the count.",
                "- Adjusted net is `raw + credit - modeled allocation`. For the contract's fixture that gives 9.999748, and the test asserts that value.",
                "- I checked the field names the ledger reads against the real writers: `Client.cs:280-361` and `Clerk.cs:2658-2677`. They match.",
                "- The formula string the ledger checks matches what `Portfolio.cs:299-303` writes.",
                "",
                "Minor issues, all follow-ups:",
                "- With zero attempts and nothing closed, the ledger serializes `adjustedNet` as `-0`.",
                "- `TryAllowance` checks the policy text with `Contains`, so a value of 3000 would also match 30000. The manifest is validated upstream by exact equality, so this cannot trigger.",
                "- A token above `Int64.MaxValue` would still be typed `integer` and read as a clamped value. No realistic response contains such a number.",
                "",
                "## Tests",
                "",
                "Full check run: `sh scripts/check.sh` exited 0.",
                "- Raw output: `/tmp/judge-check.log`, SHA-256 `2e7828e2...59b2da`.",
                "- Result: 945 passed, 0 failed. Node: 83 OK.",
                "- All four new tests are in the PASS lines.",
                "",
                "What the new tests cover:",
                "- Real producer, two cases: `Sweep` -> `ReturnedTaskHandler` -> both HTTP routes -> reopened store. One case has usage in the reply; the other has no usage and is kept unknown.",
                "- Synthetic journal fixture: three attempts with a duplicate replay, a no-fill attempted cost, a closed allowance credited once, an open entry that gets no credit, and a late response excluded by the report clock.",
                "- Store scalar reader: fixed high-water, zero tokens kept as known, malformed and non-object rows, a float token typed `wrong-type`, an oversize row, the read bound, and cancellation.",
                "",
                "Missing, although objective criterion 4 or the contract names it:",
                "- **Budget/off zero attempts.** No ledger assertion exists for either case.",
                "- **Unknown cost.** There is no case for a legacy manifest or an unknown formula. The `unknown-formula` path is a real case: saved manifests from before the formula keep the old attribution only.",
                "- **Identity/version mismatch at the ledger level.** No case covers a wrong question version, symbol, hash or correlation, or a conflicting duplicate.",
                "- **Missing invocation.** No case shows that a request without an invocation makes the ledger incomplete.",
                "- **Control arm credit.** No case shows a closed control allowance credited once with 0 owned attempts.",
                "",
                "## Failure Modes",
                "",
                "Each failure path below is reviewed and fails closed:",
                "- **Cancellation** before or during the ledger returns `incomplete/canceled`. The store read throws between pages, and a test proves it.",
                "- **Read bound and stalled cursor** both stop the read and report incomplete.",
                "- **Unreadable rows.** Any oversize, malformed, non-object or unparseable-clock transport row marks the whole ledger `unreadable-row`.",
                "- **Token overflow** is checked and reported as `token-overflow`.",
                "- **No writes.** Journal count and cash are unchanged after both GETs.",
                "- **Bound per kind.** The read bound applies to each journal kind separately, so total work is at most 300k rows, still bounded. Noted as a follow-up.",
                "",
                "## Verdict",
                "",
                "NEEDS_REVISION. The production code is correct and the full check passes. However, objective criterion 4 requires behavioural proof that the delivery does not contain. Required revisions, all tests only, in `tests/LedgerCoverage.cs`:",
                "1. **Off:** through the real producer and both HTTP routes, assert `status known`, `uniqueAttempts 0` and allocation 0. A budget refusal with a parent request but no transport rows should also show 0 attempts.",
                "2. **Legacy manifest:** a manifest from before the formula, or with unknown rates, reports `incomplete` / `unknown-formula`, with the counts shown and `adjustedNet` null.",
                "3. **Ledger-level mismatches:** a mismatched question version, symbol, hash or correlation is not counted. A conflicting duplicate, or the same attempt ID under a different parent, gives `conflict`. A request without an invocation gives `missing-invocation`.",
                "4. **Control arm:** a closed control trade's allowance is credited once while the control arm owns 0 attempts.",
                "",
                "[ARMADA:VERDICT] NEEDS_REVISION"
            });

            string block = AutonomousRecoveryOrchestrator.BuildBlockingFindingsForBrief(review, AutonomousRecoveryOrchestrator._MaxRescueBlockingFindingsChars);

            AssertTrue(block.Length <= AutonomousRecoveryOrchestrator._MaxRescueBlockingFindingsChars, "The block respects its bound. Actual: " + block.Length);
            AssertFalse(block.Contains("[detail omitted]", StringComparison.Ordinal), "No required item is shortened once passing and minor lists are left out");
            AssertContains("**Unknown cost.** There is no case for a legacy manifest or an unknown formula. The `unknown-formula` path is a real case", block, "A missing-test item is carried whole");
            AssertContains("**Control arm credit.** No case shows a closed control allowance credited once with 0 owned attempts.", block, "Every missing-test item is carried");
            AssertContains("A request without an invocation gives `missing-invocation`.", block, "A Verdict required revision is carried whole");
            AssertContains("**Control arm:** a closed control trade's allowance is credited once", block, "Every Verdict required revision is carried");
            AssertFalse(block.Contains("Fresh attempt IDs are not collapsed", StringComparison.Ordinal), "An item under a passing lead-in is not a finding");
            AssertFalse(block.Contains("Result: 945 passed", StringComparison.Ordinal), "A passing check result is not a finding");
            AssertFalse(block.Contains("Synthetic journal fixture", StringComparison.Ordinal), "A list of what the tests cover is not a finding");
            AssertFalse(block.Contains("`Contains`", StringComparison.Ordinal), "An item under a minor follow-up lead-in is not a finding");
            AssertFalse(block.Contains("Token overflow", StringComparison.Ordinal), "An item under a fails-closed lead-in is not a finding");
            await Task.CompletedTask;
        }

        public async Task BuildBlockingFindingsForBrief_PlainReviewedItems_GoBeforeNamedDefectsAreShortened()
        {
            string padding = " The handler, its route and the reopened store were each read against the contract and against the recorded journal rows from the writer, so this item reports reviewed behaviour only.";
            padding = padding + padding + padding + padding + padding;
            string review = String.Join("\n", new[]
            {
                "## Correctness",
                "",
                "The task-version side is sound:",
                "- The latest-identical skip runs inside the write transaction.",
                "- An archive failure rolls back the live upsert or delete. A trigger-based test covers this.",
                "",
                "Two defects in the projection, both on real data:",
                "1. **Real statuses come back as \"unknown\"** (`History.cs:284`).",
                "   - `KnownStatus` allows only `\"canceled\"`, but every producer writes `\"cancelled\"`: Clerk.cs:885, 912 and 2162.",
                "   - It also turns `\"ready\"`, `\"timeout\"`, `\"budget\"` and `\"unavailable\"` into `\"unknown\"`. Clerk.cs:469-488 writes these into the vote status.",
                "2. **Clocks are mislabelled** (`History.cs:172-175`).",
                "   - `receivedAt` is the journal's `recorded_at` for every kind, including intent rows, so one value is published as two independent clocks.",
                "",
                "## Tests",
                "",
                "The new tests cover the following:",
                "- The literal 61-task, two-provider prune and restart.",
                "- Fixed-high-water paging with limit 1 and an idempotent restart.",
                "",
                "The gap is the producer path. No test uses real producer rows, so the wrong status and clock mapping goes unseen. Cancellation partway through paging is not tested either.",
                "",
                "## Failure Modes",
                "",
                "- **Bad input.** An invalid source, a bad cursor or a cursor/filter mismatch raises an argument error, which the route turns into HTTP 400." + padding,
                "- **Read-only legacy store.** It returns \"unavailable\" and creates no table." + padding,
                "- **Archive failure.** The transaction rolls back and the live task is unchanged." + padding,
                "- **Cancellation.** It is checked before the read, on each row, and after the read." + padding,
                "- **Crash between save and amendment.** The version keeps its operation ID with a null result, which reads as unknown." + padding,
                "- **Concurrency gap (follow-up, not a blocker).** While the gate is released around the submit, a concurrent save would be tagged with this operation.",
                "- **Secrets.** None are in the returned rows." + padding,
                "",
                "## Verdict",
                "",
                "**NEEDS_REVISION.** Required fixes:",
                "1. `History.cs:284`: keep the producers' real status values, including `cancelled`, `ready`, `timeout`, `budget` and `unavailable`, or report each with its source spelling. Do not fold them into `unknown`.",
                "2. `History.cs:172-175`: do not give request or invocation rows a `receivedAt`. Map each clock by kind: `started` or `requestedAt` to asked, and `completedAt` on response and vote rows to received, with the meaning stated.",
                "3. Add the combined-source test in the existing suites. Drive real producer rows plus independent task answers for one candidate through prune, restart and the HTTP history routes. Assert the row IDs, body, hash, attempt link and status for a ready vote and a cancelled vote.",
                "",
                "[ARMADA:VERDICT] NEEDS_REVISION"
            });

            string block = AutonomousRecoveryOrchestrator.BuildBlockingFindingsForBrief(review, AutonomousRecoveryOrchestrator._MaxRescueBlockingFindingsChars);

            AssertTrue(block.Length <= AutonomousRecoveryOrchestrator._MaxRescueBlockingFindingsChars, "The block respects its bound. Actual: " + block.Length);
            AssertFalse(block.Contains("[detail omitted]", StringComparison.Ordinal), "A named defect is never shortened to make room for plain reviewed items");
            AssertContains("or report each with its source spelling. Do not fold them into `unknown`.", block, "The first Verdict fix is carried whole with its anchor");
            AssertContains("`completedAt` on response and vote rows to received, with the meaning stated.", block, "The second Verdict fix is carried whole");
            AssertContains("Assert the row IDs, body, hash, attempt link and status for a ready vote and a cancelled vote.", block, "The third Verdict fix is carried whole");
            AssertContains("every producer writes `\"cancelled\"`: Clerk.cs:885, 912 and 2162.", block, "An item under a defect lead-in is carried with its anchors");
            AssertContains("The gap is the producer path. No test uses real producer rows", block, "A test gap written as a paragraph is carried");
            AssertContains(StageReportEssentials.IncompleteFindingsMarker, block, "Dropping plain items says the list is incomplete");
            AssertFalse(block.Contains("Concurrency gap", StringComparison.Ordinal), "An item marked not a blocker is not a finding");
            AssertFalse(block.Contains("latest-identical skip", StringComparison.Ordinal), "An item under a passing lead-in is not a finding");
            await Task.CompletedTask;
        }

        public async Task BuildBlockingFindingsForBrief_ItemClosingWithFollowUpLabel_IsLeftOut()
        {
            string review = String.Join("\n", new[]
            {
                "## Correctness",
                "The delivered logic is correct for the cases the brief names. Hidden-defect hunt findings:",
                "- **Unbounded pending loop (the blocking defect).** A peer that keeps answering \"pending\" holds the request open forever (`Client.cs:508-517`).",
                "- **Deadline race (reviewer finding, confirmed by reading).** If the read and the delay finish in the same tick, the frame is dropped. The old code had the same race. Follow-up, not a blocker.",
                "- **Transport must honour cancellation promptly.** After the deadline the code awaits the cancelled read. This was true of the old code too. Follow-up.",
                "",
                "## Verdict",
                "NEEDS_REVISION. The one blocking defect:",
                "- **Cap consecutive pending replies.** Add a fixed cap to `RetryPolicy` and count pending replies at `Client.cs:508-517`; on exhaustion throw a distinct failure.",
                "",
                "[ARMADA:VERDICT] NEEDS_REVISION"
            });

            string block = AutonomousRecoveryOrchestrator.BuildBlockingFindingsForBrief(review, AutonomousRecoveryOrchestrator._MaxRescueBlockingFindingsChars);

            AssertContains("Unbounded pending loop", block, "The blocking defect is carried");
            AssertContains("count pending replies at `Client.cs:508-517`", block, "The Verdict's required change is carried with its anchor");
            AssertFalse(block.Contains("Deadline race", StringComparison.Ordinal), "An item that closes with 'Follow-up, not a blocker.' is not a finding");
            AssertFalse(block.Contains("Transport must honour cancellation", StringComparison.Ordinal), "An item that closes with 'Follow-up.' is not a finding");
            await Task.CompletedTask;
        }

        public async Task BuildBlockingFindingsForBrief_LesserLabelledItems_AreLeftOut()
        {
            string review = String.Join("\n", new[]
            {
                "## Correctness",
                "- **Blocking: the export retries without a limit.** `Exporter.cs:120` loops on every transient error.",
                "- Minor, not blocking: the helper takes the concrete store type rather than its interface.",
                "",
                "## Failure Modes",
                "- Observed but not blocking: the compacted file reopens in the default journal mode until the next write.",
                "- **Empty input.** The parser returns an empty result without logging the reason. Cosmetic.",
                "",
                "## Verdict",
                "NEEDS_REVISION. Bound the export retries.",
                "",
                "[ARMADA:VERDICT] NEEDS_REVISION"
            });

            string block = AutonomousRecoveryOrchestrator.BuildBlockingFindingsForBrief(review, AutonomousRecoveryOrchestrator._MaxRescueBlockingFindingsChars);

            AssertContains("the export retries without a limit", block, "The labelled blocking defect is carried");
            AssertFalse(block.Contains("concrete store type", StringComparison.Ordinal), "An item opening 'Minor, not blocking:' is not a finding");
            AssertFalse(block.Contains("default journal mode", StringComparison.Ordinal), "An item opening 'Observed but not blocking:' is not a finding");
            AssertFalse(block.Contains("without logging the reason", StringComparison.Ordinal), "An item closing 'Cosmetic.' is not a finding");
            await Task.CompletedTask;
        }

        public async Task BuildBlockingFindingsForBrief_PassingItems_AreLeftOut()
        {
            string review = String.Join("\n", new[]
            {
                "## Correctness",
                "",
                "Required revisions:",
                "",
                "1. **Keep the failed state.** A retry replaces `unavailable` with `loading` at `Cache.cs:84-95`; keep `unavailable` until a good report replaces it.",
                "",
                "Source fidelity is otherwise sound: counting delegates to the unchanged reader and lookups call their existing owners.",
                "",
                "- **The earlier stage's must-fix is a misreading of the brief, not a defect.** The source maps a missing reply to the same failure the port returns.",
                "- **Retry limit (blocking).** The port stops after two attempts where the source allows three (`Retry.cs:40`).",
                "- **Resend.** Repeated frames are re-sent with the source's limit and the inherited single-frame defect is reproduced. Reviewed and tested.",
                "",
                "## Failure Modes",
                "- **Null resolver.** The constructor falls back to the identity default. Tested implicitly by every test that omits the argument.",
                "- **Disposed reader.** The existing disposed check is unchanged.",
                "",
                "## Verdict",
                "NEEDS_REVISION. Two items block.",
                "",
                "[ARMADA:VERDICT] NEEDS_REVISION"
            });

            string block = AutonomousRecoveryOrchestrator.BuildBlockingFindingsForBrief(review, AutonomousRecoveryOrchestrator._MaxRescueBlockingFindingsChars);

            AssertContains("Keep the failed state.", block, "The numbered required revision is carried");
            AssertContains("Retry limit (blocking).", block, "A bold label naming the item blocking is carried");
            AssertFalse(block.Contains("otherwise sound", StringComparison.Ordinal), "A passing paragraph after the required list is not a finding");
            AssertFalse(block.Contains("misreading of the brief", StringComparison.Ordinal), "An item that disclaims a defect is not a finding");
            AssertFalse(block.Contains("Resend.", StringComparison.Ordinal), "An item closing 'Reviewed and tested.' is not a finding");
            AssertFalse(block.Contains("Null resolver", StringComparison.Ordinal), "A handled failure mode is not a finding");
            AssertFalse(block.Contains("Disposed reader", StringComparison.Ordinal), "An unchanged failure mode is not a finding");
            await Task.CompletedTask;
        }

        public async Task BuildBlockingFindingsForBrief_InlineVerdictChanges_AreSplit()
        {
            string review = String.Join("\n", new[]
            {
                "## Correctness",
                "- **Blocking: the probe waits forever on a held lock.** `Compact.cs:123` sets an unbounded timeout.",
                "",
                "## Verdict",
                "NEEDS_REVISION. Required revisions:",
                "",
                "Required changes: (1) replace the unbounded timeout at `Compact.cs:123` with a bounded one; (2) catch unexpected exceptions and exit 1 with the named reason; and (3) add a test for a held write lock.",
                "",
                "[ARMADA:VERDICT] NEEDS_REVISION"
            });

            string block = AutonomousRecoveryOrchestrator.BuildBlockingFindingsForBrief(review, AutonomousRecoveryOrchestrator._MaxRescueBlockingFindingsChars);

            AssertContains("- (1) replace the unbounded timeout at `Compact.cs:123` with a bounded one", block, "The first inline change is its own finding");
            AssertContains("- (2) catch unexpected exceptions and exit 1 with the named reason", block, "The second inline change is its own finding");
            AssertContains("- (3) add a test for a held write lock", block, "The last inline change is its own finding");
            AssertFalse(block.Contains("Required revisions:", StringComparison.Ordinal), "A Verdict lead-in line is not a finding");
            AssertFalse(block.Contains("[ARMADA:VERDICT]", StringComparison.Ordinal), "The verdict marker is not a finding");
            await Task.CompletedTask;
        }

        public async Task BuildBlockingFindingsForBrief_ParagraphUnderDefectLeadIn_IsCarried()
        {
            string review = String.Join("\n", new[]
            {
                "## Correctness",
                "",
                "Confirmed defect:",
                "",
                "The summary counts the closing bar twice when the window ends on a session boundary (`Summary.cs:61`).",
                "",
                "## Verdict",
                "NEEDS_REVISION.",
                "",
                "[ARMADA:VERDICT] NEEDS_REVISION"
            });

            string block = AutonomousRecoveryOrchestrator.BuildBlockingFindingsForBrief(review, AutonomousRecoveryOrchestrator._MaxRescueBlockingFindingsChars);

            AssertContains("counts the closing bar twice", block, "A paragraph under 'Confirmed defect:' is carried");
            AssertFalse(block.Contains("- Confirmed defect:", StringComparison.Ordinal), "The lead-in itself is not a finding");
            await Task.CompletedTask;
        }

        public async Task BuildRescueDescription_OversizedJudgeReview_KeepsUnmetCriteriaFromOmittedSections()
        {
            StringBuilder review = new StringBuilder();
            review.AppendLine("## Completeness");
            for (int i = 0; i < 200; i++)
                review.AppendLine("- Step " + i + " of the brief is delivered as written and its output matches the requested shape.");
            review.AppendLine("## Acceptance Criteria");
            review.AppendLine("- The export stops after the configured retry limit. — MET.");
            review.AppendLine("- A failed export keeps the last good report marked stale. — NOT MET. `Cache.cs:84-95` overwrites it.");
            review.AppendLine("## Correctness");
            review.AppendLine("- **Blocking: a retry overwrites the failed state.** `Cache.cs:84-95`.");
            review.AppendLine("## Verdict");
            review.AppendLine("NEEDS_REVISION.");
            review.AppendLine("[ARMADA:VERDICT] NEEDS_REVISION");
            string text = review.ToString();
            AssertTrue(text.Length > AutonomousRecoveryOrchestrator._MaxRescueReviewerFeedbackChars,
                "Precondition: the review exceeds the embedded feedback limit, so sections are omitted.");
            Mission failed = new Mission
            {
                Id = "msn_test_unmet_criteria_kept",
                Title = "review the export retry change",
                Status = MissionStatusEnum.Failed,
                Persona = "Judge",
                FailureReason = "Judge verdict: NEEDS_REVISION",
                Description = "title: review the export retry change",
                AgentOutput = text,
                ReviewComment = text,
            };
            Incident incident = new Incident
            {
                Id = "inc_test_unmet_criteria_kept",
                Title = "synthetic review",
                Summary = "synthetic summary",
                Status = IncidentStatusEnum.Open,
                Severity = IncidentSeverityEnum.Medium,
            };

            string brief = AutonomousRecoveryOrchestrator.BuildRescueDescription(failed, incident, 1);
            string feedback = brief.Substring(brief.IndexOf("Reviewer feedback to address:", StringComparison.Ordinal));

            AssertContains("## Acceptance Criteria", feedback, "The heading of a section with an unmet criterion is kept");
            AssertContains("keeps the last good report marked stale. — NOT MET.", feedback, "The NOT MET criterion reaches the reviewer feedback");
            AssertFalse(feedback.Contains("configured retry limit. — MET.", StringComparison.Ordinal), "A MET criterion is still omitted");
            AssertFalse(feedback.Contains("Step 5 of the brief", StringComparison.Ordinal), "Completeness narration is still omitted");
            AssertContains("[Sections reduced to their NOT MET, NOT DELIVERED and NOT RESOLVED lines: Completeness, Acceptance Criteria.", feedback,
                "The note names the reduced sections");
            await Task.CompletedTask;
        }

        public async Task BuildBlockingFindingsForBrief_TestAndFailureModeGapsInParagraphs_AreCarried()
        {
            string review = String.Join("\n", new[]
            {
                "## Correctness",
                "",
                "Required revisions:",
                "",
                "1. **Unknown eligibility can produce a false pass.** `Scorecard.cs:754-758` excludes missing fields as a mismatch.",
                "",
                "2. **Late evidence counts.** `Scorecard.cs:1082-1098` checks clocks against the deadline only.",
                "",
                "## Tests",
                "",
                "Foreground `sh scripts/check.sh` completed at the reviewed tip with exit 0 and zero failures. `git diff --check` also passed.",
                "",
                "The new tests exercise both consumers and verify literal counts. They do not prove the missing behavior above. The bound test changes the read bound to 1. Add production-bound, in-flight cancellation, reopened-store, and deferred-answer proof.",
                "",
                "The retained gate metadata identifies its run as precommit. It does not establish the required after-commit retained gate evidence.",
                "",
                "## Failure Modes",
                "",
                "Reviewed missing fields, conflicting duplicates, late clocks, scan limits, and cancellation.",
                "",
                "`Scorecard.cs:550` still materializes raw rows through `Read`; the complete path does not meet the required retained-data boundary. Replace this loading path with bounded summaries.",
                "",
                "The direct risks are misleading coverage and excessive allocation. The diff does not change order execution.",
                "",
                "## Verdict",
                "",
                "NEEDS_REVISION. Complete the required corrections above.",
                "",
                "[ARMADA:VERDICT] NEEDS_REVISION"
            });

            string block = AutonomousRecoveryOrchestrator.BuildBlockingFindingsForBrief(review, 12000);

            AssertContains("Unknown eligibility can produce a false pass.", block, "Numbered Correctness items stay carried");
            AssertContains("Add production-bound, in-flight cancellation, reopened-store, and deferred-answer proof.", block, "A missing-proof request in a Tests paragraph is carried");
            AssertContains("does not establish the required after-commit retained gate evidence", block, "A missing gate obligation in a Tests paragraph is carried");
            AssertContains("`Scorecard.cs:550` still materializes raw rows", block, "A Failure Modes defect paragraph is carried with its anchor");
            AssertFalse(block.Contains("- Required revisions:", StringComparison.Ordinal), "A bare lead-in line is not a finding");
            AssertFalse(block.Contains("Reviewed missing fields", StringComparison.Ordinal), "A list of what was reviewed is not a finding");
            AssertFalse(block.Contains("does not change order execution", StringComparison.Ordinal), "A no-change scope statement is not a finding");
            AssertFalse(block.Contains("zero failures", StringComparison.Ordinal), "A passing check summary is not a finding");
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
                AgentOutput = report,
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
            string cut = AutonomousRecoveryOrchestrator.TruncateReviewerFeedbackForBrief(log, AutonomousRecoveryOrchestrator._MaxRescueGateLogChars);
            string expected = AutonomousRecoveryOrchestrator.TruncateForBrief(log, AutonomousRecoveryOrchestrator._MaxRescueGateLogChars);

            AssertEqual(expected, cut, "Text without Judge sections uses the head-first cut unchanged.");
            AssertTrue(cut.StartsWith("warning CS0618: line 0", StringComparison.Ordinal), "The head is kept.");
            AssertTrue(cut.Contains("read the complete source record"), "The generic truncation helper does not point at an unrelated artifact.");
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
                "\n\n---\n" + MissionService.BuildHandoffMarker("msn_older") + "\n" +
                "## Prior Stage Output\nThe previous pipeline stage (Worker) completed mission older.\n" +
                "Branch: armada/example/older\n" +
                "### Diff from prior stage\n```diff\n" + new string('o', 6000) + "\n```\n" +
                "<!-- ARMADA:HANDOFF-END:msn_older -->";

            string newestBlock =
                "\n\n---\n" + MissionService.BuildHandoffMarker("msn_newest") + "\n" +
                "## Prior Stage Output\nThe previous pipeline stage (TestEngineer) completed mission newest.\n" +
                "Branch: armada/example/newest\n" +
                "NEWEST-STAGE-FACT the rescue must still see\n" +
                "<!-- ARMADA:HANDOFF-END:msn_newest -->";

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
            AssertTrue(brief.Contains("msn_older"),
                "The older stage must still be named, so the rescue can find its work if needed.");
            await Task.CompletedTask;
        }

    }
}
