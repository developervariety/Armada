namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for the typed, scoped manual quarantine and release operations shared by REST, MCP and the dashboard.
    /// </summary>
    public sealed class CaptainQuarantineScopedServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Captain Quarantine Scoped Service";

        private static readonly string _Tenant = Armada.Core.Constants.DefaultTenantId;
        private static readonly string _User = Armada.Core.Constants.DefaultUserId;
        private static readonly AuthContext _Admin = AuthContext.Authenticated(_Tenant, _User, true, true, "Test");
        private static readonly AuthContext _TenantAdmin = AuthContext.Authenticated(_Tenant, _User, false, true, "Test");

        private static CaptainQuarantineService CreateService(SqliteDatabaseDriver db)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            ArmadaSettings settings = new ArmadaSettings();
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_quarantine_scoped_" + Guid.NewGuid().ToString("N"));
            return new CaptainQuarantineService(db, settings, logging);
        }

        private static async Task<Captain> CreateIdleCaptainAsync(SqliteDatabaseDriver db)
        {
            Captain captain = new Captain("scoped-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            captain.TenantId = _Tenant;
            captain.UserId = _User;
            captain.State = CaptainStateEnum.Idle;
            return await db.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        private static async Task<Captain> CreateWorkingCaptainAsync(SqliteDatabaseDriver db)
        {
            Vessel vessel = new Vessel("scoped-vessel-" + Guid.NewGuid().ToString("N"), "https://github.com/test/scoped.git");
            await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);
            Mission mission = new Mission("scoped mission", "scoped");
            mission.VesselId = vessel.Id;
            await db.Missions.CreateAsync(mission).ConfigureAwait(false);
            Captain captain = await CreateIdleCaptainAsync(db).ConfigureAwait(false);
            Dock dock = new Dock(vessel.Id);
            dock.CaptainId = captain.Id;
            dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_scoped_wt_" + Guid.NewGuid().ToString("N"));
            dock.BranchName = "armada/scoped/" + mission.Id;
            await db.Docks.CreateAsync(dock).ConfigureAwait(false);
            captain.State = CaptainStateEnum.Working;
            captain.CurrentMissionId = mission.Id;
            captain.CurrentDockId = dock.Id;
            captain.ProcessId = 7373;
            return await db.Captains.UpdateAsync(captain).ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A busy captain is refused with Busy and keeps its ownership", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    CaptainQuarantineService service = CreateService(testDb.Driver);
                    Captain working = await CreateWorkingCaptainAsync(testDb.Driver).ConfigureAwait(false);

                    CaptainQuarantineResult result = await service.QuarantineCaptainAsync(_Admin, working.Id, "operator hold", null).ConfigureAwait(false);

                    AssertEqual(CaptainQuarantineOutcomeEnum.Busy, result.Outcome, "Busy outcome");
                    AssertNotNull(result.Captain, "The current captain is returned");
                    Captain after = (await testDb.Driver.Captains.ReadAsync(working.Id).ConfigureAwait(false))!;
                    AssertEqual(CaptainStateEnum.Working, after.State, "State kept");
                    AssertEqual(working.CurrentMissionId, after.CurrentMissionId, "Mission kept");
                    AssertEqual(working.CurrentDockId, after.CurrentDockId, "Dock kept");
                    AssertEqual(7373, after.ProcessId, "Process kept");
                    AssertNull(after.QuarantineReason, "No reason written");

                    CaptainQuarantineResult release = await service.ReleaseCaptainAsync(_Admin, working.Id).ConfigureAwait(false);
                    AssertEqual(CaptainQuarantineOutcomeEnum.NotQuarantined, release.Outcome, "A working captain is not released");
                    AssertEqual(CaptainStateEnum.Working, release.Captain!.State, "Still working after release request");
                }
            }).ConfigureAwait(false);

            await RunTest("Invalid input returns InvalidRequest and changes nothing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    CaptainQuarantineService service = CreateService(testDb.Driver);
                    Captain idle = await CreateIdleCaptainAsync(testDb.Driver).ConfigureAwait(false);

                    CaptainQuarantineResult blank = await service.QuarantineCaptainAsync(_Admin, idle.Id, "   ", null).ConfigureAwait(false);
                    AssertEqual(CaptainQuarantineOutcomeEnum.InvalidRequest, blank.Outcome, "Blank reason");

                    CaptainQuarantineResult past = await service.QuarantineCaptainAsync(_Admin, idle.Id, "hold", DateTime.UtcNow.AddMinutes(-1)).ConfigureAwait(false);
                    AssertEqual(CaptainQuarantineOutcomeEnum.InvalidRequest, past.Outcome, "Past expiry");

                    Captain after = (await testDb.Driver.Captains.ReadAsync(idle.Id).ConfigureAwait(false))!;
                    AssertEqual(CaptainStateEnum.Idle, after.State, "Still Idle");
                    AssertNull(after.QuarantineReason, "No reason written");
                }
            }).ConfigureAwait(false);

            await RunTest("A caller outside the captain's scope gets NotFound and changes nothing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    CaptainQuarantineService service = CreateService(testDb.Driver);
                    Captain idle = await CreateIdleCaptainAsync(testDb.Driver).ConfigureAwait(false);

                    AuthContext otherTenant = AuthContext.Authenticated("ten_quarantine_other", "usr_quarantine_other", false, true, "Test");
                    AuthContext otherUser = AuthContext.Authenticated(_Tenant, "usr_quarantine_other", false, false, "Test");

                    AssertEqual(CaptainQuarantineOutcomeEnum.NotFound,
                        (await service.QuarantineCaptainAsync(otherTenant, idle.Id, "hold", null).ConfigureAwait(false)).Outcome, "Other tenant");
                    AssertEqual(CaptainQuarantineOutcomeEnum.NotFound,
                        (await service.QuarantineCaptainAsync(otherUser, idle.Id, "hold", null).ConfigureAwait(false)).Outcome, "Other user in the tenant");
                    Captain untouched = (await testDb.Driver.Captains.ReadAsync(idle.Id).ConfigureAwait(false))!;
                    AssertEqual(CaptainStateEnum.Idle, untouched.State, "Unchanged by out-of-scope callers");

                    CaptainQuarantineResult own = await service.QuarantineCaptainAsync(_TenantAdmin, idle.Id, "hold", null).ConfigureAwait(false);
                    AssertEqual(CaptainQuarantineOutcomeEnum.Quarantined, own.Outcome, "Tenant admin can quarantine");
                    AssertEqual(CaptainQuarantineOutcomeEnum.NotFound,
                        (await service.ReleaseCaptainAsync(otherTenant, idle.Id).ConfigureAwait(false)).Outcome, "Other tenant cannot release");
                    AssertEqual(CaptainQuarantineOutcomeEnum.NotFound,
                        (await service.ReleaseCaptainAsync(_Admin, "cpt_does_not_exist").ConfigureAwait(false)).Outcome, "Unknown captain");
                }
            }).ConfigureAwait(false);

            await RunTest("Repeated bench updates the hold and repeated or expired release reports a typed outcome", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    CaptainQuarantineService service = CreateService(testDb.Driver);
                    Captain idle = await CreateIdleCaptainAsync(testDb.Driver).ConfigureAwait(false);

                    DateTime until = DateTime.UtcNow.AddHours(2);
                    CaptainQuarantineResult first = await service.QuarantineCaptainAsync(_Admin, idle.Id, "timed hold", until).ConfigureAwait(false);
                    AssertEqual(CaptainQuarantineOutcomeEnum.Quarantined, first.Outcome, "First bench");
                    AssertTrue(first.Captain!.QuarantineUntilUtc.HasValue, "Expiry recorded");

                    CaptainQuarantineResult second = await service.QuarantineCaptainAsync(_Admin, idle.Id, "indefinite hold", null).ConfigureAwait(false);
                    AssertEqual(CaptainQuarantineOutcomeEnum.Quarantined, second.Outcome, "Repeated bench");
                    AssertEqual("indefinite hold", second.Captain!.QuarantineReason, "Reason updated");
                    AssertFalse(second.Captain.QuarantineUntilUtc.HasValue, "Hold is now indefinite");
                    AssertTrue(service.IsQuarantined(second.Captain), "Assignment skips the held captain");

                    Captain expired = (await testDb.Driver.Captains.ReadAsync(idle.Id).ConfigureAwait(false))!;
                    expired.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(-5);
                    await testDb.Driver.Captains.UpdateAsync(expired).ConfigureAwait(false);

                    CaptainQuarantineResult released = await service.ReleaseCaptainAsync(_Admin, idle.Id).ConfigureAwait(false);
                    AssertEqual(CaptainQuarantineOutcomeEnum.Released, released.Outcome, "An expired hold still in Quarantined state is released");
                    AssertEqual(CaptainStateEnum.Idle, released.Captain!.State, "Idle after release");

                    CaptainQuarantineResult again = await service.ReleaseCaptainAsync(_Admin, idle.Id).ConfigureAwait(false);
                    AssertEqual(CaptainQuarantineOutcomeEnum.NotQuarantined, again.Outcome, "Repeated release changes nothing");
                }
            }).ConfigureAwait(false);

            await RunTest("A concurrent claim and bench never leave a captain both claimed and quarantined", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    CaptainQuarantineService service = CreateService(testDb.Driver);
                    Vessel vessel = new Vessel("race-vessel-" + Guid.NewGuid().ToString("N"), "https://github.com/test/race.git");
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Mission mission = new Mission("race mission", "race");
                    mission.VesselId = vessel.Id;
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    for (int round = 0; round < 20; round++)
                    {
                        Captain captain = await CreateIdleCaptainAsync(testDb.Driver).ConfigureAwait(false);
                        Dock dock = new Dock(vessel.Id);
                        dock.CaptainId = captain.Id;
                        dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_race_wt_" + Guid.NewGuid().ToString("N"));
                        dock.BranchName = "armada/race/" + round;
                        await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                        Task<bool> claim = Task.Run(() => testDb.Driver.Captains.TryClaimAsync(captain.Id, mission.Id, dock.Id));
                        Task<CaptainQuarantineResult> bench = Task.Run(() => service.QuarantineCaptainAsync(_Admin, captain.Id, "race hold", null));
                        await Task.WhenAll(claim, bench).ConfigureAwait(false);

                        Captain after = (await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!;
                        bool claimed = claim.Result;
                        bool benched = bench.Result.Outcome == CaptainQuarantineOutcomeEnum.Quarantined;
                        AssertFalse(claimed && benched, "Round " + round + ": both the claim and the bench succeeded");
                        AssertTrue(claimed || benched, "Round " + round + ": one of the two must win");
                        if (claimed)
                        {
                            AssertEqual(CaptainStateEnum.Working, after.State, "Round " + round + ": claimed captain is Working");
                            AssertEqual(mission.Id, after.CurrentMissionId, "Round " + round + ": claim keeps its mission");
                            AssertNull(after.QuarantineReason, "Round " + round + ": no quarantine on a claimed captain");
                        }
                        else
                        {
                            AssertEqual(CaptainStateEnum.Quarantined, after.State, "Round " + round + ": benched captain is Quarantined");
                            AssertNull(after.CurrentMissionId, "Round " + round + ": no mission on a benched captain");
                        }
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("A cancelled quarantine request throws and changes nothing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    CaptainQuarantineService service = CreateService(testDb.Driver);
                    Captain idle = await CreateIdleCaptainAsync(testDb.Driver).ConfigureAwait(false);

                    using (CancellationTokenSource cts = new CancellationTokenSource())
                    {
                        cts.Cancel();
                        bool cancelled = false;
                        try
                        {
                            await service.QuarantineCaptainAsync(_Admin, idle.Id, "cancelled hold", null, cts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            cancelled = true;
                        }

                        AssertTrue(cancelled, "Cancellation is surfaced, not swallowed");
                    }

                    Captain after = (await testDb.Driver.Captains.ReadAsync(idle.Id).ConfigureAwait(false))!;
                    AssertEqual(CaptainStateEnum.Idle, after.State, "Unchanged after cancellation");
                    AssertNull(after.QuarantineReason, "No reason written after cancellation");
                }
            }).ConfigureAwait(false);
        }
    }
}
