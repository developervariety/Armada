namespace Armada.Test.Unit
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests database transaction support.
    /// </summary>
    public class TransactionSupportTests : TestSuite
    {
        #region Public-Members

        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "Transaction Support";

        #endregion

        #region Protected-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("ExecuteInTransactionAsync_WhenActionThrows_RollsBackSqliteAssignmentWrites", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = new Mission("Transactional mission", "Rolls back assignment state.");
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Captain captain = new Captain("transaction-captain");
                    captain.State = CaptainStateEnum.Idle;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Vessel vessel = new Vessel("transaction-vessel", "https://github.com/test/transaction.git");
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.CaptainId = captain.Id;
                    dock.BranchName = "armada/test/transaction";
                    dock = await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    try
                    {
                        await testDb.Driver.ExecuteInTransactionAsync(async () =>
                        {
                            mission.Status = MissionStatusEnum.Assigned;
                            mission.AssignmentState = MissionAssignmentStateEnum.Provisioning;
                            mission.CaptainId = captain.Id;
                            mission.DockId = dock.Id;
                            await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);

                            bool claimed = await testDb.Driver.Captains.TryClaimAsync(captain.Id, mission.Id, dock.Id).ConfigureAwait(false);
                            AssertTrue(claimed, "Captain claim inside transaction should succeed");

                            await testDb.Driver.Docks.DeleteAsync(dock.Id).ConfigureAwait(false);
                            throw new InvalidOperationException("force rollback");
                        }).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    Mission? rolledBackMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Captain? rolledBackCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    Dock? rolledBackDock = await testDb.Driver.Docks.ReadAsync(dock.Id).ConfigureAwait(false);

                    AssertNotNull(rolledBackMission, "Mission should still exist");
                    AssertEqual(MissionStatusEnum.Pending, rolledBackMission!.Status, "Mission status should roll back");
                    AssertEqual(MissionAssignmentStateEnum.Pending, rolledBackMission.AssignmentState, "Mission assignment state should roll back");
                    AssertNull(rolledBackMission.CaptainId, "Mission captain should roll back");
                    AssertNull(rolledBackMission.DockId, "Mission dock should roll back");

                    AssertNotNull(rolledBackCaptain, "Captain should still exist");
                    AssertEqual(CaptainStateEnum.Idle, rolledBackCaptain!.State, "Captain claim should roll back");
                    AssertNull(rolledBackCaptain.CurrentMissionId, "Captain mission should roll back");
                    AssertNull(rolledBackCaptain.CurrentDockId, "Captain dock should roll back");

                    AssertNotNull(rolledBackDock, "Dock delete should roll back");
                }
            }).ConfigureAwait(false);
        }

        #endregion
    }
}
