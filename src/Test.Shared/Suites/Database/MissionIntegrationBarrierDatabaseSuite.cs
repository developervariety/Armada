namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Shared database-provider coverage for durable integration barriers.</summary>
    public sealed class MissionIntegrationBarrierDatabaseSuite : IArmadaTestSuite
    {
        /// <inheritdoc />
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>
            {
                Case("barrier_round_trip_and_conditional_update", RoundTripAndConditionalUpdateAsync)
            };
            return new TestSuiteDescriptor("Database.MissionIntegrationBarrier", "Mission Integration Barrier Database", cases);
        }

        private static TestCaseDescriptor Case(string id, Func<Task> body) =>
            new TestCaseDescriptor("Database.MissionIntegrationBarrier", id, id.Replace('_', ' '),
                _ => body(), new List<string> { TestTags.Positive });

        private static async Task RoundTripAndConditionalUpdateAsync()
        {
            using (TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                DatabaseDriver db = database.Driver;
                Mission mission = new Mission("Integration barrier provider test")
                {
                    TenantId = Armada.Core.Constants.DefaultTenantId,
                    UserId = Armada.Core.Constants.DefaultUserId
                };
                mission.IntegrationBarrier = new MissionIntegrationBarrier
                {
                    Phase = MissionIntegrationBarrierPhaseEnum.Preparing,
                    ArchitectMissionId = "msn_architect",
                    PlanHash = new string('a', 64),
                    IntegrationMissionId = mission.Id,
                    TenantId = mission.TenantId,
                    UserId = mission.UserId,
                    OriginVoyageId = "vyg_origin",
                    ExpectedBranchCount = 1
                };
                Mission created = await db.Missions.CreateAsync(mission).ConfigureAwait(false);
                AssertEqual(MissionIntegrationBarrierPhaseEnum.Preparing, created.IntegrationBarrier!.Phase);
                Mission stale = (await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;

                MissionIntegrationBarrier accepted = new MissionIntegrationBarrier
                {
                    Version = 1,
                    Phase = MissionIntegrationBarrierPhaseEnum.Accepted,
                    ArchitectMissionId = "msn_architect",
                    PlanHash = new string('a', 64),
                    IntegrationMissionId = created.Id,
                    TenantId = created.TenantId,
                    UserId = created.UserId,
                    VesselId = created.VesselId,
                    VoyageId = created.VoyageId,
                    OriginVoyageId = "vyg_origin",
                    ExpectedBranchCount = 1,
                    Members = new List<MissionIntegrationBarrierMember>
                    {
                        new MissionIntegrationBarrierMember
                        {
                            BranchKey = "M1",
                            RootMissionId = "msn_branch_root",
                            AcceptedTerminalMissionId = "test-branch-terminal",
                            CurrentTerminalMissionId = "test-branch-terminal"
                        }
                    }
                };
                AssertTrue(await db.Missions.TryUpdateIntegrationBarrierAsync(created, accepted).ConfigureAwait(false),
                    "The provider should persist a pending barrier update.");
                Mission? read = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                AssertEqual("test-branch-terminal", read!.IntegrationBarrier!.Members[0].CurrentTerminalMissionId);
                AssertFalse(await db.Missions.TryUpdateIntegrationBarrierAsync(stale, accepted).ConfigureAwait(false),
                    "The provider should reject a stale barrier snapshot.");

                stale.Title = "Stale ordinary update";
                await db.Missions.UpdateAsync(stale).ConfigureAwait(false);
                Mission? afterUpdate = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                AssertEqual("test-branch-terminal", afterUpdate!.IntegrationBarrier!.Members[0].AcceptedTerminalMissionId,
                    "An ordinary mission update should preserve the barrier columns.");
            }
        }
    }
}
