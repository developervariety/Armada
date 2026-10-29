namespace Armada.Test.Unit.Suites.Services
{
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>Tests for durable mission attempt fact recording.</summary>
    public sealed class MissionAttemptFactRecorderTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Mission Attempt Fact Recorder";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("RescueLineageResolvesToOriginalMission", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission root = await testDb.Driver.Missions.CreateAsync(new Mission("Implement", "work")).ConfigureAwait(false);
                Mission rescue = await testDb.Driver.Missions.CreateAsync(new Mission("Revision", RescueMissionMarker.Marker + "\nrevise")
                {
                    ParentMissionId = root.Id
                }).ConfigureAwait(false);
                Mission review = await testDb.Driver.Missions.CreateAsync(new Mission("Review revision", RescueMissionMarker.Marker + "\nreview")
                {
                    DependsOnMissionId = rescue.Id
                }).ConfigureAwait(false);

                MissionAttemptFact? fact = await MissionAttemptFactRecorder.RecordAsync(
                    testDb.Driver, review, MissionAttemptFactTypeEnum.AttemptStarted).ConfigureAwait(false);

                AssertNotNull(fact, "fact stored");
                AssertEqual(root.Id, fact!.RootMissionId, "a chained rescue stage resolves to the original mission");
                AssertEqual(rescue.Id, fact.ParentMissionId);
                AssertTrue(fact.IsRescue, "the description marker is the typed rescue classification");
            }).ConfigureAwait(false);

            await RunTest("TitlePrefixAloneIsNotARescueAndFreeTextIsUnclassified", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission upstream = await testDb.Driver.Missions.CreateAsync(new Mission("Plan", "plan")).ConfigureAwait(false);
                Mission titled = await testDb.Driver.Missions.CreateAsync(new Mission("Rescue: not a recovery", "ordinary pipeline stage")
                {
                    DependsOnMissionId = upstream.Id
                }).ConfigureAwait(false);

                MissionAttemptFact? fact = await MissionAttemptFactRecorder.RecordAsync(
                    testDb.Driver, titled, MissionAttemptFactTypeEnum.AttemptStarted, "Some Reason With Spaces").ConfigureAwait(false);

                AssertNotNull(fact, "fact stored");
                AssertFalse(fact!.IsRescue, "a title prefix is never a typed rescue marker");
                AssertEqual(titled.Id, fact.RootMissionId, "a pipeline dependency is not recovery lineage");
                AssertNull(fact.ReasonCode, "free text is not a typed cause");
            }).ConfigureAwait(false);

            await RunTest("StableTypedCauseIsPreserved", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Watchdog", "ordinary mission")
                {
                    FailureReason = "stage_watchdog_no_captain_heartbeat"
                }).ConfigureAwait(false);

                MissionAttemptFact? fact = await MissionAttemptFactRecorder.RecordAsync(
                    testDb.Driver, mission, MissionAttemptFactTypeEnum.Failed, "stage_watchdog_no_captain_heartbeat").ConfigureAwait(false);

                AssertNotNull(fact, "fact stored");
                AssertEqual("stage_watchdog_no_captain_heartbeat", fact!.ReasonCode,
                    "a known deterministic cause keeps its stable code");
            }).ConfigureAwait(false);

            await RunTest("Review denial reason codes require a declared enum name", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission invalid = await testDb.Driver.Missions.CreateAsync(new Mission("Invalid review code", "ordinary mission")).ConfigureAwait(false);
                Mission valid = await testDb.Driver.Missions.CreateAsync(new Mission("Valid review code", "ordinary mission")).ConfigureAwait(false);

                MissionAttemptFact? invalidFact = await MissionAttemptFactRecorder.RecordAsync(
                    testDb.Driver, invalid, MissionAttemptFactTypeEnum.ReviewDenied, "review_denied_0").ConfigureAwait(false);
                MissionAttemptFact? validFact = await MissionAttemptFactRecorder.RecordAsync(
                    testDb.Driver, valid, MissionAttemptFactTypeEnum.ReviewDenied, "review_denied_retrystage").ConfigureAwait(false);

                AssertNotNull(invalidFact, "the fact is stored even when its optional code is unknown");
                AssertNull(invalidFact!.ReasonCode, "a numeric enum value is not an allow-listed reason code");
                AssertNotNull(validFact, "the valid fact is stored");
                AssertEqual("review_denied_retrystage", validFact!.ReasonCode,
                    "a declared review action remains a stable reason code");
            }).ConfigureAwait(false);

            await RunTest("Terminal causes preserve post-test subtype and keep stream endings unknown", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Terminal", "work")
                    { Status = MissionStatusEnum.Failed, FailureReason = "stream ended unexpectedly" }).ConfigureAwait(false);
                    MissionAttemptFact? unknown = await MissionAttemptFactRecorder.RecordAsync(testDb.Driver, mission,
                        MissionAttemptFactTypeEnum.Failed).ConfigureAwait(false);
                    AssertEqual("unknown", unknown!.ReasonCode, "an ambiguous ending never becomes a model error");
                    MissionAttemptFact? post = await MissionAttemptFactRecorder.RecordAsync(testDb.Driver, mission,
                        MissionAttemptFactTypeEnum.Failed,
                        MissionAttemptFactReasonCode.FromDefinitionOfDoneFailure(DefinitionOfDoneFailureClassEnum.PostTestStep)).ConfigureAwait(false);
                    AssertEqual("dod_post_test_step", post!.ReasonCode, "the typed subtype survives normalization");
                    EnumerationResult<ArmadaEvent> events = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery
                    { EventType = "mission.failure_cause", PageSize = 10 }).ConfigureAwait(false);
                    AssertEqual(2, events.Objects.Count, "every terminal cause has a stored event linked to its fact");
                    AssertTrue(events.Objects.All(item => item.MissionId == mission.Id && item.Payload!.Contains("attemptFactId")));
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Rejected voyage")
                    { Status = VoyageStatusEnum.Failed }).ConfigureAwait(false);
                    mission.VoyageId = voyage.Id;
                    await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
                    Mission derivative = new Mission("Retained work") { VoyageId = voyage.Id, Status = MissionStatusEnum.Failed };
                    TerminalVoyageMissionRule.RecordReconciledOutcome(derivative, VoyageStatusEnum.Failed,
                        TerminalVoyageMissionRule.ReasonWorkUnlanded, DateTime.UtcNow);
                    derivative = await testDb.Driver.Missions.CreateAsync(derivative).ConfigureAwait(false);
                    MissionAttemptFact? derived = await MissionAttemptFactRecorder.RecordAsync(testDb.Driver, derivative,
                        MissionAttemptFactTypeEnum.Failed).ConfigureAwait(false);
                    AssertEqual("terminal_voyage_work_unlanded", derived!.ReasonCode);
                    EnumerationResult<ArmadaEvent> linked = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery
                    { EventType = "mission.failure_cause", MissionId = derivative.Id, PageSize = 10 }).ConfigureAwait(false);
                    AssertEqual(1, linked.Objects.Count);
                    AssertContains(mission.Id, linked.Objects[0].Payload!, "derivative failure retains the original failed-stage link");
                    AssertContains("upstream_work_unlanded", linked.Objects[0].Payload!);
                    AssertFalse(linked.Objects[0].Payload!.Contains("model_error"), "derivative status does not blame reasoning");
                }
            }).ConfigureAwait(false);

            await RunTest("RestartRecordsRestartedFact", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                ArmadaSettings settings = new ArmadaSettings();
                settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages = 4;
                settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel = 4;
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("vsl_restart_fact", "https://example.test/restart.git")
                {
                    Id = "vsl_restart_fact",
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId
                }).ConfigureAwait(false);
                Mission failed = await testDb.Driver.Missions.CreateAsync(new Mission("terminal", "terminal")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VesselId = vessel.Id,
                    Status = MissionStatusEnum.Failed
                }).ConfigureAwait(false);
                DateTime before = DateTime.UtcNow.AddMinutes(-1);

                await new MissionRestartService(testDb.Driver, settings, logging).RestartAsync(failed).ConfigureAwait(false);

                ProductionFactPage<MissionAttemptFact> page = await testDb.Driver.MissionAttemptFacts.EnumerateAsync(new ProductionFactQuery
                {
                    FromUtc = before,
                    ToUtc = DateTime.UtcNow.AddMinutes(1)
                }).ConfigureAwait(false);
                AssertEqual(1, page.Items.Count, "restart stores one fact");
                AssertEqual(MissionAttemptFactTypeEnum.Restarted, page.Items[0].FactType);
                AssertEqual(failed.Id, page.Items[0].MissionId);
                AssertEqual(Constants.DefaultTenantId, page.Items[0].TenantId, "the fact carries its owner scope");
            }).ConfigureAwait(false);
        }
    }
}
