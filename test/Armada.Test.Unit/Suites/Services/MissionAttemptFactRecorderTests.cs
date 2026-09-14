namespace Armada.Test.Unit.Suites.Services
{
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

            await RunTest("TitlePrefixAloneIsNotARescue", async () =>
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
                AssertEqual("some_reason_with_spaces", fact.ReasonCode);
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
