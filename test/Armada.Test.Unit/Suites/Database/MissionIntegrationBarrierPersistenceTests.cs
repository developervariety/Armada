namespace Armada.Test.Unit.Suites.Database
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>Persistence and conditional-write tests for integration barriers.</summary>
    public class MissionIntegrationBarrierPersistenceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Mission Integration Barrier Persistence";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await Mission_IntegrationBarrierRoundTripsAndUsesRevisionCas().ConfigureAwait(false);
            await Mission_InvalidPresentIntegrationBarrierFailsClosed().ConfigureAwait(false);
            await Mission_IntegrationBarrierRejectsOverCapMembership().ConfigureAwait(false);
            await Mission_ZeroBranchBarrierInitializesAndAccepts().ConfigureAwait(false);
        }

        private async Task Mission_IntegrationBarrierRoundTripsAndUsesRevisionCas()
        {
            await RunTest("Mission_IntegrationBarrierRoundTripsAndUsesRevisionCas", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Mission mission = CreateMission("Barrier CAS mission");
                    mission.IntegrationBarrier = Preparing(mission);
                    Mission created = await db.Missions.CreateAsync(mission).ConfigureAwait(false);
                    AssertNotNull(created.IntegrationBarrier, "A new preparing barrier must round trip.");
                    AssertEqual(MissionIntegrationBarrierPhaseEnum.Preparing, created.IntegrationBarrier!.Phase);
                    AssertEqual(0, created.IntegrationBarrier.Members.Count, "Preparing has no accepted members.");
                    AssertFalse(JsonSerializer.Serialize(created).Contains("integrationBarrier", StringComparison.OrdinalIgnoreCase),
                        "Mission API JSON must not expose barrier control state.");

                    Mission stale = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false) ?? throw new Exception("Mission read returned null.");
                    MissionIntegrationBarrier accepted = Accepted(created);
                    AssertTrue(await db.Missions.TryUpdateIntegrationBarrierAsync(created, accepted).ConfigureAwait(false), "The first acceptance should win.");

                    Mission? persisted = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(persisted?.IntegrationBarrier, "The accepted list must round trip from storage.");
                    AssertEqual(MissionIntegrationBarrierPhaseEnum.Accepted, persisted!.IntegrationBarrier!.Phase);
                    AssertEqual(2, persisted.IntegrationBarrier.Members.Count);
                    AssertEqual("M1", persisted.IntegrationBarrier.Members[0].BranchKey);
                    AssertEqual("msn_terminal_1", persisted.IntegrationBarrier.Members[0].AcceptedTerminalMissionId);
                    AssertEqual("msn_terminal_1", persisted.IntegrationBarrier.Members[0].CurrentTerminalMissionId);
                    AssertEqual("M2", persisted.IntegrationBarrier.Members[1].BranchKey);

                    Mission? mutatedSnapshot = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    mutatedSnapshot!.IntegrationBarrier!.Members[0].AcceptedTerminalMissionId = "test-forged-terminal";
                    MissionIntegrationBarrier forgedNext = CloneBarrier(mutatedSnapshot.IntegrationBarrier);
                    forgedNext.Members[0].CurrentTerminalMissionId = "test-replacement-terminal";
                    bool forgedAccepted = false;
                    try
                    {
                        await db.Missions.TryUpdateIntegrationBarrierAsync(mutatedSnapshot, forgedNext).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        forgedAccepted = true;
                    }
                    AssertTrue(forgedAccepted,
                        "A mutated loaded object cannot rewrite immutable membership; CAS validates the stored JSON snapshot.");

                    AssertFalse(await db.Missions.TryUpdateIntegrationBarrierAsync(stale, accepted).ConfigureAwait(false),
                        "A stale preparing snapshot cannot replace accepted membership.");

                    stale.Title = "Ordinary stale mission update";
                    await db.Missions.UpdateAsync(stale).ConfigureAwait(false);
                    Mission? afterStaleUpdate = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionIntegrationBarrierPhaseEnum.Accepted, afterStaleUpdate!.IntegrationBarrier!.Phase,
                        "A generic full-row update must preserve the stored barrier.");
                    AssertEqual("msn_terminal_1", afterStaleUpdate.IntegrationBarrier.Members[0].AcceptedTerminalMissionId);

                    Mission? sameSnapshot = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(await db.Missions.TryUpdateIntegrationBarrierAsync(sameSnapshot!, sameSnapshot!.IntegrationBarrier!).ConfigureAwait(false),
                        "An exact accepted retry should be idempotent.");

                    Mission rescueIntegration = CreateMission("Rescue integration clone");
                    MissionIntegrationBarrier rescueBarrier = CloneBarrier(sameSnapshot!.IntegrationBarrier!);
                    rescueBarrier.IntegrationMissionId = rescueIntegration.Id;
                    rescueBarrier.TenantId = rescueIntegration.TenantId;
                    rescueBarrier.UserId = rescueIntegration.UserId;
                    rescueBarrier.VoyageId = rescueIntegration.VoyageId;
                    rescueBarrier.VesselId = rescueIntegration.VesselId;
                    rescueIntegration.IntegrationBarrier = rescueBarrier;
                    Mission rescueCreated = await db.Missions.CreateAsync(rescueIntegration).ConfigureAwait(false);
                    AssertEqual(MissionIntegrationBarrierPhaseEnum.Accepted, rescueCreated.IntegrationBarrier!.Phase,
                        "A trusted rescue integration clone must retain accepted membership.");
                    AssertEqual("vyg_origin", rescueCreated.IntegrationBarrier.OriginVoyageId,
                        "A rescue clone keeps its immutable origin voyage.");
                    AssertEqual("msn_terminal_1", rescueCreated.IntegrationBarrier.Members[0].CurrentTerminalMissionId);

                    Mission? changedScope = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    changedScope!.TenantId = "tnt_other";
                    MissionIntegrationBarrier sameBarrier = CloneBarrier(changedScope.IntegrationBarrier!);
                    AssertFalse(await db.Missions.TryUpdateIntegrationBarrierAsync(changedScope, sameBarrier).ConfigureAwait(false),
                        "A changed owner snapshot cannot write the barrier.");

                    Mission? cancelled = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    cancelled!.Status = MissionStatusEnum.Cancelled;
                    AssertTrue(await db.Missions.TryUpdateIfStatusAsync(cancelled, MissionStatusEnum.Pending).ConfigureAwait(false),
                        "The mission cancellation setup should persist.");
                    AssertFalse(await db.Missions.TryUpdateIntegrationBarrierAsync(cancelled, CloneBarrier(cancelled.IntegrationBarrier!)).ConfigureAwait(false),
                        "A cancelled mission cannot accept barrier changes.");
                }
            }).ConfigureAwait(false);
        }

        private async Task Mission_InvalidPresentIntegrationBarrierFailsClosed()
        {
            await RunTest("Mission_InvalidPresentIntegrationBarrierFailsClosed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Mission mission = await db.Missions.CreateAsync(CreateMission("Corrupt barrier mission")).ConfigureAwait(false);
                    using (SqliteConnection connection = new SqliteConnection(testDb.ConnectionString))
                    {
                        await connection.OpenAsync().ConfigureAwait(false);
                        using (SqliteCommand command = connection.CreateCommand())
                        {
                            command.CommandText = "UPDATE missions SET integration_barrier_json = '{broken' WHERE id = @id;";
                            command.Parameters.AddWithValue("@id", mission.Id);
                            AssertEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
                        }
                    }

                    Mission? read = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(read!.IntegrationBarrierInvalid, "Malformed non-null JSON must be recorded as invalid.");
                    AssertNull(read.IntegrationBarrier, "Malformed JSON must not look like an absent legacy barrier.");
                }
            }).ConfigureAwait(false);
        }

        private async Task Mission_IntegrationBarrierRejectsOverCapMembership()
        {
            await RunTest("Mission_IntegrationBarrierRejectsOverCapMembership", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Mission mission = CreateMission("Over-cap barrier mission");
                    MissionIntegrationBarrier barrier = Preparing(mission);
                    barrier.ExpectedBranchCount = MissionIntegrationBarrierPersistence.MaximumMembers + 1;
                    mission.IntegrationBarrier = barrier;
                    bool rejected = false;
                    try
                    {
                        await db.Missions.CreateAsync(mission).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        rejected = true;
                    }
                    AssertTrue(rejected, "A barrier over the hard branch cap must be rejected before storage.");
                }
            }).ConfigureAwait(false);
        }

        private async Task Mission_ZeroBranchBarrierInitializesAndAccepts()
        {
            await RunTest("Mission_ZeroBranchBarrierInitializesAndAccepts", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Mission mission = await db.Missions.CreateAsync(CreateMission("Single-mission plan")).ConfigureAwait(false);
                    Mission absent = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false) ?? throw new Exception("Mission read returned null.");
                    Mission staleAbsent = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false) ?? throw new Exception("Mission read returned null.");

                    MissionIntegrationBarrier preparing = Preparing(absent);
                    preparing.ExpectedBranchCount = 0;
                    AssertTrue(await db.Missions.TryUpdateIntegrationBarrierAsync(absent, preparing).ConfigureAwait(false),
                        "A trusted runtime may initialize an absent barrier from a loaded snapshot.");
                    AssertFalse(await db.Missions.TryUpdateIntegrationBarrierAsync(staleAbsent, preparing).ConfigureAwait(false),
                        "A stale absent-barrier snapshot must lose the initialization CAS.");

                    Mission? persisted = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    MissionIntegrationBarrier accepted = CloneBarrier(persisted!.IntegrationBarrier!);
                    accepted.Phase = MissionIntegrationBarrierPhaseEnum.Accepted;
                    AssertTrue(await db.Missions.TryUpdateIntegrationBarrierAsync(persisted, accepted).ConfigureAwait(false),
                        "A valid single-mission plan accepts an empty branch list.");
                    Mission? final = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionIntegrationBarrierPhaseEnum.Accepted, final!.IntegrationBarrier!.Phase);
                    AssertEqual(0, final.IntegrationBarrier.Members.Count);
                }
            }).ConfigureAwait(false);
        }

        private static Mission CreateMission(string title)
        {
            return new Mission(title)
            {
                TenantId = Armada.Core.Constants.DefaultTenantId,
                UserId = Armada.Core.Constants.DefaultUserId
            };
        }

        private static MissionIntegrationBarrier Preparing(Mission mission)
        {
            return new MissionIntegrationBarrier
            {
                Phase = MissionIntegrationBarrierPhaseEnum.Preparing,
                ArchitectMissionId = "msn_architect",
                PlanHash = new string('a', 64),
                IntegrationMissionId = mission.Id,
                TenantId = mission.TenantId,
                UserId = mission.UserId,
                VesselId = mission.VesselId,
                VoyageId = mission.VoyageId,
                OriginVoyageId = "vyg_origin",
                ExpectedBranchCount = 2
            };
        }

        private static MissionIntegrationBarrier Accepted(Mission mission)
        {
            MissionIntegrationBarrier barrier = CloneBarrier(mission.IntegrationBarrier!);
            barrier.Phase = MissionIntegrationBarrierPhaseEnum.Accepted;
            barrier.Members.Add(new MissionIntegrationBarrierMember
            {
                BranchKey = "M1",
                RootMissionId = "msn_root_1",
                AcceptedTerminalMissionId = "msn_terminal_1",
                CurrentTerminalMissionId = "msn_terminal_1"
            });
            barrier.Members.Add(new MissionIntegrationBarrierMember
            {
                BranchKey = "M2",
                RootMissionId = "msn_root_2",
                AcceptedTerminalMissionId = "msn_terminal_2",
                CurrentTerminalMissionId = "msn_terminal_2"
            });
            return barrier;
        }

        private static MissionIntegrationBarrier CloneBarrier(MissionIntegrationBarrier barrier)
        {
            return JsonSerializer.Deserialize<MissionIntegrationBarrier>(JsonSerializer.Serialize(barrier))!;
        }
    }
}
