namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Covers authoritative fleet snapshots for active global and terminal voyage scopes.
    /// </summary>
    public sealed class FleetReconciliationSnapshotServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Fleet Reconciliation Snapshot Service";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Global state contains active voyages and all captains", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SeedState state = await SeedAsync(testDb).ConfigureAwait(false);
                    FleetReconciliationSnapshotService service = new FleetReconciliationSnapshotService(testDb.Driver);

                    FleetReconciliationSnapshot snapshot = await service.GetAsync().ConfigureAwait(false);

                    AssertEqual(2, snapshot.Voyages.Count, "Only Open and InProgress voyages are present.");
                    AssertTrue(snapshot.Voyages.Any(voyage => voyage.Id == state.OpenVoyage.Id), "The Open voyage is present.");
                    AssertTrue(snapshot.Voyages.Any(voyage => voyage.Id == state.ActiveVoyage.Id), "The InProgress voyage is present.");
                    AssertFalse(snapshot.Voyages.Any(voyage => voyage.Id == state.TerminalVoyage.Id), "The terminal voyage is absent.");
                    AssertEqual(3, snapshot.Missions.Count, "Missions linked to active voyages and active standalone missions are present.");
                    AssertEqual(3, snapshot.CheckRuns.Count, "Checks linked to active voyages and active standalone missions are present.");
                    AssertEqual(3, snapshot.Captains.Count, "All captains are present in a global snapshot.");
                    AssertTrue(snapshot.Captains.Any(captain => captain.Id == state.UnrelatedCaptain.Id), "An unrelated captain is present globally.");
                    AssertTrue(snapshot.Missions.Any(mission => mission.Id == state.StandaloneMission.Id), "An active standalone mission is present globally.");
                    AssertTrue(snapshot.CheckRuns.Any(check => check.MissionId == state.StandaloneMission.Id), "A standalone mission Check is present globally.");
                }
            }).ConfigureAwait(false);

            await RunTest("Scoped state contains one terminal voyage and all linked state", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SeedState state = await SeedAsync(testDb).ConfigureAwait(false);
                    FleetReconciliationSnapshotService service = new FleetReconciliationSnapshotService(testDb.Driver);

                    FleetReconciliationSnapshot snapshot = await service.GetAsync(state.TerminalVoyage.Id).ConfigureAwait(false);

                    AssertEqual(state.TerminalVoyage.Id, snapshot.VoyageId, "The requested voyage scope is reported.");
                    AssertEqual(1, snapshot.Voyages.Count, "Only the exact voyage is present.");
                    AssertEqual(VoyageStatusEnum.Complete, snapshot.Voyages[0].Status, "A terminal voyage is retained.");
                    AssertEqual(1, snapshot.Missions.Count, "The linked terminal mission is present.");
                    AssertEqual(state.TerminalMission.Id, snapshot.Missions[0].Id, "The exact linked mission is present.");
                    AssertEqual(1, snapshot.CheckRuns.Count, "The linked terminal Check is present.");
                    AssertEqual(state.TerminalCaptain.Id, snapshot.Captains.Single().Id, "Only the related captain is present.");
                }
            }).ConfigureAwait(false);

            await RunTest("Missing scoped voyage returns authoritative empty state", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FleetReconciliationSnapshotService service = new FleetReconciliationSnapshotService(testDb.Driver);

                    FleetReconciliationSnapshot snapshot = await service.GetAsync("vyg_missing").ConfigureAwait(false);

                    AssertEqual("vyg_missing", snapshot.VoyageId, "The requested scope is retained.");
                    AssertEqual(0, snapshot.Voyages.Count, "No voyage is invented.");
                    AssertEqual(0, snapshot.Missions.Count, "No mission is returned.");
                    AssertEqual(0, snapshot.CheckRuns.Count, "No Check is returned.");
                    AssertEqual(0, snapshot.Captains.Count, "No captain is returned.");
                }
            }).ConfigureAwait(false);
        }

        private static async Task<SeedState> SeedAsync(TestDatabase testDb)
        {
            Captain activeCaptain = await testDb.Driver.Captains.CreateAsync(new Captain("snapshot-active")
            {
                State = CaptainStateEnum.Working
            }).ConfigureAwait(false);
            Captain terminalCaptain = await testDb.Driver.Captains.CreateAsync(new Captain("snapshot-terminal")
            {
                State = CaptainStateEnum.Idle
            }).ConfigureAwait(false);
            Captain unrelatedCaptain = await testDb.Driver.Captains.CreateAsync(new Captain("snapshot-unrelated")).ConfigureAwait(false);

            Voyage openVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("snapshot-open")).ConfigureAwait(false);
            Voyage activeVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("snapshot-active")
            {
                Status = VoyageStatusEnum.InProgress
            }).ConfigureAwait(false);
            Voyage terminalVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("snapshot-terminal")
            {
                Status = VoyageStatusEnum.Complete,
                CompletedUtc = DateTime.UtcNow
            }).ConfigureAwait(false);

            Mission openMission = await testDb.Driver.Missions.CreateAsync(new Mission("snapshot-open-mission", "large details are not needed")
            {
                VoyageId = openVoyage.Id,
                Status = MissionStatusEnum.Pending
            }).ConfigureAwait(false);
            Mission activeMission = await testDb.Driver.Missions.CreateAsync(new Mission("snapshot-active-mission", "large details are not needed")
            {
                VoyageId = activeVoyage.Id,
                CaptainId = activeCaptain.Id,
                Status = MissionStatusEnum.InProgress
            }).ConfigureAwait(false);
            Mission terminalMission = await testDb.Driver.Missions.CreateAsync(new Mission("snapshot-terminal-mission", "large details are not needed")
            {
                VoyageId = terminalVoyage.Id,
                CaptainId = terminalCaptain.Id,
                Status = MissionStatusEnum.Complete,
                CompletedUtc = DateTime.UtcNow
            }).ConfigureAwait(false);
            Mission standaloneMission = await testDb.Driver.Missions.CreateAsync(new Mission("snapshot-standalone-mission", "large details are not needed")
            {
                Status = MissionStatusEnum.InProgress
            }).ConfigureAwait(false);

            activeCaptain.CurrentMissionId = activeMission.Id;
            await testDb.Driver.Captains.UpdateAsync(activeCaptain).ConfigureAwait(false);

            await CreateCheckAsync(testDb, openVoyage.Id, openMission.Id, CheckRunStatusEnum.Pending).ConfigureAwait(false);
            await CreateCheckAsync(testDb, activeVoyage.Id, activeMission.Id, CheckRunStatusEnum.Running).ConfigureAwait(false);
            await CreateCheckAsync(testDb, null, terminalMission.Id, CheckRunStatusEnum.Passed).ConfigureAwait(false);
            await CreateCheckAsync(testDb, null, standaloneMission.Id, CheckRunStatusEnum.Running).ConfigureAwait(false);

            return new SeedState(openVoyage, activeVoyage, terminalVoyage, terminalMission, standaloneMission, terminalCaptain, unrelatedCaptain);
        }

        private static async Task CreateCheckAsync(
            TestDatabase testDb,
            string? voyageId,
            string missionId,
            CheckRunStatusEnum status)
        {
            await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
            {
                VoyageId = voyageId,
                MissionId = missionId,
                Label = "snapshot-check",
                Type = CheckRunTypeEnum.UnitTest,
                Status = status,
                Command = "dotnet test"
            }).ConfigureAwait(false);
        }

        private sealed record SeedState(
            Voyage OpenVoyage,
            Voyage ActiveVoyage,
            Voyage TerminalVoyage,
            Mission TerminalMission,
            Mission StandaloneMission,
            Captain TerminalCaptain,
            Captain UnrelatedCaptain);
    }
}
