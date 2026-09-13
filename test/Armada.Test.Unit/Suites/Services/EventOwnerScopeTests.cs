namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for the shared event owner scope rule.
    /// </summary>
    public class EventOwnerScopeTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Event Owner Scope";

        private static readonly string _Tenant = Armada.Core.Constants.DefaultTenantId;
        private static readonly string _User = Armada.Core.Constants.DefaultUserId;

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("ApplyAsync takes the owner from the referenced mission first", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // The vessel carries the tenant but no user, so a vessel-first order would leave the user empty.
                    Vessel vessel = new Vessel("scope-vessel", "https://github.com/test/scope.git");
                    vessel.TenantId = _Tenant;
                    vessel.UserId = null;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Mission mission = new Mission("scope mission");
                    mission.TenantId = _Tenant;
                    mission.UserId = _User;
                    mission.VesselId = vessel.Id;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    ArmadaEvent evt = new ArmadaEvent("test.scope", "mission first");
                    evt.MissionId = mission.Id;
                    evt.VesselId = vessel.Id;
                    EventOwnerScopeResult result = await EventOwnerScope.ApplyAsync(testDb.Driver, evt).ConfigureAwait(false);

                    AssertEqual(EventOwnerScopeOutcomeEnum.Scoped, result.Outcome);
                    AssertEqual("mission", result.Detail);
                    AssertEqual(_Tenant, evt.TenantId);
                    AssertEqual(_User, evt.UserId);
                }
            }).ConfigureAwait(false);

            await RunTest("ApplyAsync falls back to the vessel owner when no mission is referenced", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("scope-vessel-only", "https://github.com/test/scope-vessel.git");
                    vessel.TenantId = _Tenant;
                    vessel.UserId = _User;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    ArmadaEvent evt = new ArmadaEvent("test.scope", "vessel only");
                    evt.VesselId = vessel.Id;
                    evt.MissionId = "msn_absent";
                    EventOwnerScopeResult result = await EventOwnerScope.ApplyAsync(testDb.Driver, evt).ConfigureAwait(false);

                    AssertEqual(EventOwnerScopeOutcomeEnum.Scoped, result.Outcome);
                    AssertEqual("vessel", result.Detail);
                    AssertEqual(_Tenant, evt.TenantId);
                    AssertEqual(_User, evt.UserId);
                }
            }).ConfigureAwait(false);

            await RunTest("ApplyAsync uses the event entity when no owning id is referenced", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("scope-fleet");
                    fleet.TenantId = _Tenant;
                    fleet.UserId = _User;
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    ArmadaEvent evt = new ArmadaEvent("test.scope", "entity only");
                    evt.EntityType = "fleet";
                    evt.EntityId = fleet.Id;
                    EventOwnerScopeResult result = await EventOwnerScope.ApplyAsync(testDb.Driver, evt).ConfigureAwait(false);

                    AssertEqual(EventOwnerScopeOutcomeEnum.Scoped, result.Outcome);
                    AssertEqual("entity fleet", result.Detail);
                    AssertEqual(_Tenant, evt.TenantId);
                    AssertEqual(_User, evt.UserId);
                }
            }).ConfigureAwait(false);

            await RunTest("ApplyAsync leaves an already scoped event unchanged", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = new Mission("scope mission preset");
                    mission.TenantId = _Tenant;
                    mission.UserId = _User;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    ArmadaEvent evt = new ArmadaEvent("test.scope", "preset");
                    evt.MissionId = mission.Id;
                    evt.TenantId = "ten_preset";
                    evt.UserId = "usr_preset";
                    EventOwnerScopeResult result = await EventOwnerScope.ApplyAsync(testDb.Driver, evt).ConfigureAwait(false);

                    AssertEqual(EventOwnerScopeOutcomeEnum.AlreadyScoped, result.Outcome);
                    AssertEqual("ten_preset", evt.TenantId);
                    AssertEqual("usr_preset", evt.UserId);
                }
            }).ConfigureAwait(false);

            await RunTest("ApplyAsync reports NoOwnerRecord and leaves the event unscoped when nothing resolves", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaEvent evt = new ArmadaEvent("test.scope", "nothing");
                    evt.MissionId = "msn_absent";
                    evt.EntityType = "objective";
                    evt.EntityId = "obj_not_resolved";
                    EventOwnerScopeResult result = await EventOwnerScope.ApplyAsync(testDb.Driver, evt).ConfigureAwait(false);

                    AssertEqual(EventOwnerScopeOutcomeEnum.NoOwnerRecord, result.Outcome);
                    AssertNull(evt.TenantId);
                    AssertNull(evt.UserId);
                }
            }).ConfigureAwait(false);
        }
    }
}
