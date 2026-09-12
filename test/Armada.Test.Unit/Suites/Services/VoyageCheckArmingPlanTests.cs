namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Coverage for the dispatch-time Check arming decision. A voyage with no green independent
    /// Check has its Judge PASS rejected, so these cases pin what gets armed, and equally what
    /// must NOT be armed a second time.
    /// </summary>
    public class VoyageCheckArmingPlanTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Voyage Check Arming Plan";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Both Build and UnitTest are armed when the profile defines both", () =>
            {
                IReadOnlyList<CheckRunTypeEnum> planned = VoyageCheckArmingPlan.Resolve(
                    new VoyageCheckArmingSettings(),
                    MakeProfile("dotnet build", "dotnet test"),
                    null);

                AssertEqual(2, planned.Count, "Build and UnitTest are the stated minimum for code work.");
                AssertTrue(planned.Contains(CheckRunTypeEnum.Build));
                AssertTrue(planned.Contains(CheckRunTypeEnum.UnitTest));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A type with no command on the profile is not armed", () =>
            {
                // A Check with no command cannot produce a real signal; it would sit Pending and
                // then fail the Judge, which is worse than not arming it.
                IReadOnlyList<CheckRunTypeEnum> planned = VoyageCheckArmingPlan.Resolve(
                    new VoyageCheckArmingSettings(),
                    MakeProfile("dotnet build", null),
                    null);

                AssertEqual(1, planned.Count);
                AssertEqual(CheckRunTypeEnum.Build, planned[0]);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A type already attached to the voyage is never armed again", () =>
            {
                IReadOnlyList<CheckRunTypeEnum> planned = VoyageCheckArmingPlan.Resolve(
                    new VoyageCheckArmingSettings(),
                    MakeProfile("dotnet build", "dotnet test"),
                    new List<CheckRun> { new CheckRun { Type = CheckRunTypeEnum.Build } });

                AssertEqual(1, planned.Count, "Only the missing type may be armed.");
                AssertEqual(CheckRunTypeEnum.UnitTest, planned[0]);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A FAILED existing check still blocks re-arming that type", () =>
            {
                // A single failed Check rejects a Judge PASS however many green ones sit beside it.
                // Arming a second Build here would manufacture exactly that condition.
                IReadOnlyList<CheckRunTypeEnum> planned = VoyageCheckArmingPlan.Resolve(
                    new VoyageCheckArmingSettings(),
                    MakeProfile("dotnet build", "dotnet test"),
                    new List<CheckRun>
                    {
                        new CheckRun { Type = CheckRunTypeEnum.Build, Status = CheckRunStatusEnum.Failed },
                        new CheckRun { Type = CheckRunTypeEnum.UnitTest, Status = CheckRunStatusEnum.Passed }
                    });

                AssertEqual(0, planned.Count, "Neither type may be armed a second time, whatever state the first is in.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Arming disabled yields no plan", () =>
            {
                IReadOnlyList<CheckRunTypeEnum> planned = VoyageCheckArmingPlan.Resolve(
                    new VoyageCheckArmingSettings { Enabled = false },
                    MakeProfile("dotnet build", "dotnet test"),
                    null);

                AssertEqual(0, planned.Count);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Each type can be disabled independently", () =>
            {
                IReadOnlyList<CheckRunTypeEnum> buildOnly = VoyageCheckArmingPlan.Resolve(
                    new VoyageCheckArmingSettings { ArmUnitTest = false },
                    MakeProfile("dotnet build", "dotnet test"),
                    null);
                AssertEqual(1, buildOnly.Count);
                AssertEqual(CheckRunTypeEnum.Build, buildOnly[0]);

                IReadOnlyList<CheckRunTypeEnum> testOnly = VoyageCheckArmingPlan.Resolve(
                    new VoyageCheckArmingSettings { ArmBuild = false },
                    MakeProfile("dotnet build", "dotnet test"),
                    null);
                AssertEqual(1, testOnly.Count);
                AssertEqual(CheckRunTypeEnum.UnitTest, testOnly[0]);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("No profile and no settings yield no plan rather than throwing", () =>
            {
                AssertEqual(0, VoyageCheckArmingPlan.Resolve(new VoyageCheckArmingSettings(), null, null).Count);
                AssertEqual(0, VoyageCheckArmingPlan.Resolve(null, MakeProfile("dotnet build", "dotnet test"), null).Count);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A whitespace-only command counts as no command", () =>
            {
                IReadOnlyList<CheckRunTypeEnum> planned = VoyageCheckArmingPlan.Resolve(
                    new VoyageCheckArmingSettings(),
                    MakeProfile("   ", "dotnet test"),
                    null);

                AssertEqual(1, planned.Count);
                AssertEqual(CheckRunTypeEnum.UnitTest, planned[0]);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A fully report-only voyage is not armed with code Checks", () =>
            {
                IReadOnlyList<CheckRunTypeEnum> planned = VoyageCheckArmingPlan.Resolve(
                    new VoyageCheckArmingSettings(),
                    MakeProfile("dotnet build", "dotnet test"),
                    null,
                    isFullyReportOnlyVoyage: true);

                AssertEqual(0, planned.Count, "report-only voyages must not arm Build or UnitTest");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Arming service persists no Checks for an all-Audit voyage", async () =>
            {
                await AssertServiceArmsExpectedCountAsync(
                    new List<MissionModeEnum> { MissionModeEnum.Audit, MissionModeEnum.Audit },
                    0).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("Arming service persists no Checks for an all-Research voyage", async () =>
            {
                await AssertServiceArmsExpectedCountAsync(
                    new List<MissionModeEnum> { MissionModeEnum.Research, MissionModeEnum.Research },
                    0).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("Arming service preserves code Checks for mixed report-only modes", async () =>
            {
                await AssertServiceArmsExpectedCountAsync(
                    new List<MissionModeEnum> { MissionModeEnum.Audit, MissionModeEnum.Research },
                    2).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        #region Private-Methods

        private static WorkflowProfile MakeProfile(string? build, string? unitTest)
        {
            return new WorkflowProfile
            {
                Id = "wfp_test",
                Name = "Test profile",
                BuildCommand = build,
                UnitTestCommand = unitTest
            };
        }

        private async Task AssertServiceArmsExpectedCountAsync(
            IReadOnlyList<MissionModeEnum> modes,
            int expectedCount)
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Vessel vessel = new Vessel("arming-vessel", "https://github.com/test/repo.git")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId
                };
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                WorkflowProfile profile = MakeProfile("dotnet build", "dotnet test");
                profile.TenantId = Constants.DefaultTenantId;
                profile.UserId = Constants.DefaultUserId;
                profile.Scope = WorkflowProfileScopeEnum.Vessel;
                profile.VesselId = vessel.Id;
                await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                Voyage voyage = new Voyage("arming-voyage")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId
                };
                voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                for (int index = 0; index < modes.Count; index++)
                {
                    Mission mission = new Mission("Stage " + index, "Report stage")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        Mode = modes[index]
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                }

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                VoyageCheckArmingService service = new VoyageCheckArmingService(
                    testDb.Driver,
                    new ArmadaSettings { VoyageCheckArming = new VoyageCheckArmingSettings() },
                    logging);

                int createdCount = await service.ArmAsync(voyage, vessel, "test").ConfigureAwait(false);
                EnumerationResult<CheckRun> persisted = await testDb.Driver.CheckRuns.EnumerateAsync(
                    new CheckRunQuery
                    {
                        TenantId = Constants.DefaultTenantId,
                        VoyageId = voyage.Id,
                        PageSize = 100
                    }).ConfigureAwait(false);

                AssertEqual(expectedCount, createdCount, "the service must report the number of created Checks");
                AssertEqual(expectedCount, persisted.Objects.Count, "the database must contain exactly the planned Checks");
            }
        }

        #endregion
    }
}
