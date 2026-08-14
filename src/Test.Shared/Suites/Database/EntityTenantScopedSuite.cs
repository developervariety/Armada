namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors verifying tenant-scoped overloads (ReadAsync, DeleteAsync, EnumerateAsync)
    /// enforce tenant isolation across Fleet (exhaustive), Vessel, Mission, and Signal.
    /// Positive cases confirm same-tenant reads, deletes, and correctly counted enumerations;
    /// negative cases confirm cross-tenant reads return null and cross-tenant deletes are no-ops.
    /// </summary>
    public sealed class EntityTenantScopedSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Entity Tenant-Scoped Operations suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            // -- Fleet --

            cases.Add(CaseAsync("fleet_read_correct_tenant_returns_fleet", "Fleet ReadAsync(tenantId, id) returns fleet for correct tenant", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Fleet fleet = new Fleet("Fleet-Read-OK");
                    fleet.TenantId = t1;
                    await db.Fleets.CreateAsync(fleet);

                    Fleet? result = await db.Fleets.ReadAsync(t1, fleet.Id);
                    AssertNotNull(result);
                    AssertEqual(fleet.Id, result!.Id);
                    AssertEqual(t1, result.TenantId);
                }
            }));

            cases.Add(CaseAsync("fleet_read_wrong_tenant_returns_null", "Fleet ReadAsync(tenantId, id) returns null for wrong tenant", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Fleet fleet = new Fleet("Fleet-Read-WrongTenant");
                    fleet.TenantId = t1;
                    await db.Fleets.CreateAsync(fleet);

                    Fleet? result = await db.Fleets.ReadAsync(t2, fleet.Id);
                    AssertNull(result);
                }
            }));

            cases.Add(CaseAsync("fleet_delete_correct_tenant_removes", "Fleet DeleteAsync(tenantId, id) removes fleet for correct tenant", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Fleet fleet = new Fleet("Fleet-Delete-OK");
                    fleet.TenantId = t1;
                    await db.Fleets.CreateAsync(fleet);

                    await db.Fleets.DeleteAsync(t1, fleet.Id);

                    // Fleet should be gone even via global read
                    AssertNull(await db.Fleets.ReadAsync(fleet.Id));
                }
            }));

            cases.Add(CaseAsync("fleet_delete_wrong_tenant_does_nothing", "Fleet DeleteAsync(tenantId, id) does nothing for wrong tenant", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Fleet fleet = new Fleet("Fleet-Delete-WrongTenant");
                    fleet.TenantId = t1;
                    await db.Fleets.CreateAsync(fleet);

                    // Attempt delete from wrong tenant
                    await db.Fleets.DeleteAsync(t2, fleet.Id);

                    // Fleet should still exist
                    AssertNotNull(await db.Fleets.ReadAsync(t1, fleet.Id));
                }
            }));

            cases.Add(CaseAsync("fleet_enumerate_returns_only_matching_tenant", "Fleet EnumerateAsync(tenantId) returns only matching tenant", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Fleet f1 = new Fleet("Fleet-T1-A") { TenantId = t1 };
                    Fleet f2 = new Fleet("Fleet-T1-B") { TenantId = t1 };
                    Fleet f3 = new Fleet("Fleet-T2-A") { TenantId = t2 };
                    await db.Fleets.CreateAsync(f1);
                    await db.Fleets.CreateAsync(f2);
                    await db.Fleets.CreateAsync(f3);

                    List<Fleet> t1Fleets = await db.Fleets.EnumerateAsync(t1);
                    AssertEqual(2, t1Fleets.Count);
                    foreach (Fleet f in t1Fleets)
                    {
                        AssertEqual(t1, f.TenantId, "All enumerated fleets should belong to t1");
                    }

                    List<Fleet> t2Fleets = await db.Fleets.EnumerateAsync(t2);
                    AssertEqual(1, t2Fleets.Count);
                    AssertEqual(t2, t2Fleets[0].TenantId);
                }
            }));

            cases.Add(CaseAsync("fleet_enumerate_query_paginates_within_tenant", "Fleet EnumerateAsync(tenantId, query) paginates within tenant", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    // Create 3 fleets in t1 and 1 in t2
                    for (int i = 0; i < 3; i++)
                    {
                        Fleet f = new Fleet("Fleet-Page-" + i) { TenantId = t1 };
                        await db.Fleets.CreateAsync(f);
                    }
                    Fleet noise = new Fleet("Fleet-Noise") { TenantId = t2 };
                    await db.Fleets.CreateAsync(noise);

                    EnumerationQuery query = new EnumerationQuery { PageSize = 2, PageNumber = 1 };
                    EnumerationResult<Fleet> page1 = await db.Fleets.EnumerateAsync(t1, query);

                    AssertEqual(2, page1.Objects.Count);
                    AssertEqual(3, (int)page1.TotalRecords);
                    foreach (Fleet f in page1.Objects)
                    {
                        AssertEqual(t1, f.TenantId, "Paginated results should belong to t1");
                    }
                }
            }));

            // -- Vessel --

            cases.Add(CaseAsync("vessel_read_wrong_tenant_returns_null", "Vessel ReadAsync(tenantId, id) returns null for wrong tenant", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Fleet fleet = new Fleet("Vessel-Fleet") { TenantId = t1 };
                    await db.Fleets.CreateAsync(fleet);

                    Vessel vessel = new Vessel("Vessel-Read-Wrong", "https://github.com/test/repo");
                    vessel.TenantId = t1;
                    vessel.FleetId = fleet.Id;
                    await db.Vessels.CreateAsync(vessel);

                    AssertNotNull(await db.Vessels.ReadAsync(t1, vessel.Id));
                    AssertNull(await db.Vessels.ReadAsync(t2, vessel.Id));
                }
            }));

            cases.Add(CaseAsync("vessel_delete_wrong_tenant_does_nothing_audit", "Vessel DeleteAsync(tenantId, id) does nothing for wrong tenant", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Fleet fleet = new Fleet("Vessel-Delete-Fleet") { TenantId = t1 };
                    await db.Fleets.CreateAsync(fleet);

                    Vessel vessel = new Vessel("Vessel-Delete-Wrong", "https://github.com/test/delete-repo");
                    vessel.TenantId = t1;
                    vessel.FleetId = fleet.Id;
                    await db.Vessels.CreateAsync(vessel);

                    // Attempt delete from wrong tenant
                    await db.Vessels.DeleteAsync(t2, vessel.Id);

                    // Vessel should still exist for the owning tenant
                    AssertNotNull(await db.Vessels.ReadAsync(t1, vessel.Id));
                }
            }));

            cases.Add(CaseAsync("vessel_enumerate_returns_correct_count", "Vessel EnumerateAsync(tenantId) returns correct count", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Fleet fleetA = new Fleet("Vessel-FleetA") { TenantId = t1 };
                    Fleet fleetB = new Fleet("Vessel-FleetB") { TenantId = t2 };
                    await db.Fleets.CreateAsync(fleetA);
                    await db.Fleets.CreateAsync(fleetB);

                    Vessel v1 = new Vessel("V1-T1", "https://github.com/t1/r1") { TenantId = t1, FleetId = fleetA.Id };
                    Vessel v2 = new Vessel("V2-T1", "https://github.com/t1/r2") { TenantId = t1, FleetId = fleetA.Id };
                    Vessel v3 = new Vessel("V3-T2", "https://github.com/t2/r1") { TenantId = t2, FleetId = fleetB.Id };
                    await db.Vessels.CreateAsync(v1);
                    await db.Vessels.CreateAsync(v2);
                    await db.Vessels.CreateAsync(v3);

                    List<Vessel> t1Vessels = await db.Vessels.EnumerateAsync(t1);
                    AssertEqual(2, t1Vessels.Count);
                    foreach (Vessel v in t1Vessels)
                    {
                        AssertEqual(t1, v.TenantId, "All enumerated vessels should belong to t1");
                    }

                    List<Vessel> t2Vessels = await db.Vessels.EnumerateAsync(t2);
                    AssertEqual(1, t2Vessels.Count);
                }
            }));

            // -- Mission --

            cases.Add(CaseAsync("mission_read_wrong_tenant_returns_null", "Mission ReadAsync(tenantId, id) returns null for wrong tenant", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    // Seed prerequisite entities
                    Fleet fleet = new Fleet("Mission-Fleet") { TenantId = t1 };
                    await db.Fleets.CreateAsync(fleet);

                    Vessel vessel = new Vessel("Mission-Vessel", "https://github.com/test/mission") { TenantId = t1, FleetId = fleet.Id };
                    await db.Vessels.CreateAsync(vessel);

                    Voyage voyage = new Voyage("Mission-Voyage") { TenantId = t1 };
                    await db.Voyages.CreateAsync(voyage);

                    Mission mission = new Mission("Mission-Read-Wrong", "desc");
                    mission.TenantId = t1;
                    mission.VesselId = vessel.Id;
                    mission.VoyageId = voyage.Id;
                    await db.Missions.CreateAsync(mission);

                    AssertNotNull(await db.Missions.ReadAsync(t1, mission.Id));
                    AssertNull(await db.Missions.ReadAsync(t2, mission.Id));
                }
            }));

            cases.Add(CaseAsync("mission_enumerate_returns_correct_count", "Mission EnumerateAsync(tenantId) returns correct count", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    // Seed prerequisites for t1
                    Fleet fleetA = new Fleet("Mission-FleetA") { TenantId = t1 };
                    await db.Fleets.CreateAsync(fleetA);
                    Vessel vesselA = new Vessel("Mission-VesselA", "https://github.com/t1/m") { TenantId = t1, FleetId = fleetA.Id };
                    await db.Vessels.CreateAsync(vesselA);
                    Voyage voyageA = new Voyage("Mission-VoyageA") { TenantId = t1 };
                    await db.Voyages.CreateAsync(voyageA);

                    // Seed prerequisites for t2
                    Fleet fleetB = new Fleet("Mission-FleetB") { TenantId = t2 };
                    await db.Fleets.CreateAsync(fleetB);
                    Vessel vesselB = new Vessel("Mission-VesselB", "https://github.com/t2/m") { TenantId = t2, FleetId = fleetB.Id };
                    await db.Vessels.CreateAsync(vesselB);
                    Voyage voyageB = new Voyage("Mission-VoyageB") { TenantId = t2 };
                    await db.Voyages.CreateAsync(voyageB);

                    Mission m1 = new Mission("M1-T1") { TenantId = t1, VesselId = vesselA.Id, VoyageId = voyageA.Id };
                    Mission m2 = new Mission("M2-T1") { TenantId = t1, VesselId = vesselA.Id, VoyageId = voyageA.Id };
                    Mission m3 = new Mission("M3-T2") { TenantId = t2, VesselId = vesselB.Id, VoyageId = voyageB.Id };
                    await db.Missions.CreateAsync(m1);
                    await db.Missions.CreateAsync(m2);
                    await db.Missions.CreateAsync(m3);

                    List<Mission> t1Missions = await db.Missions.EnumerateAsync(t1);
                    AssertEqual(2, t1Missions.Count);
                    foreach (Mission m in t1Missions)
                    {
                        AssertEqual(t1, m.TenantId, "All enumerated missions should belong to t1");
                    }

                    List<Mission> t2Missions = await db.Missions.EnumerateAsync(t2);
                    AssertEqual(1, t2Missions.Count);
                }
            }));

            // -- Signal --

            cases.Add(CaseAsync("signal_read_wrong_tenant_returns_null", "Signal ReadAsync(tenantId, id) returns null for wrong tenant", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Signal signal = new Signal(SignalTypeEnum.Assignment, "{\"data\":1}");
                    signal.TenantId = t1;
                    await db.Signals.CreateAsync(signal);

                    AssertNotNull(await db.Signals.ReadAsync(t1, signal.Id));
                    AssertNull(await db.Signals.ReadAsync(t2, signal.Id));
                }
            }));

            cases.Add(CaseAsync("signal_enumerate_returns_correct_count", "Signal EnumerateAsync(tenantId) returns correct count", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Signal s1 = new Signal(SignalTypeEnum.Nudge) { TenantId = t1 };
                    Signal s2 = new Signal(SignalTypeEnum.Heartbeat) { TenantId = t1 };
                    Signal s3 = new Signal(SignalTypeEnum.Error) { TenantId = t2 };
                    await db.Signals.CreateAsync(s1);
                    await db.Signals.CreateAsync(s2);
                    await db.Signals.CreateAsync(s3);

                    List<Signal> t1Signals = await db.Signals.EnumerateAsync(t1);
                    AssertEqual(2, t1Signals.Count);
                    foreach (Signal s in t1Signals)
                    {
                        AssertEqual(t1, s.TenantId, "All enumerated signals should belong to t1");
                    }

                    List<Signal> t2Signals = await db.Signals.EnumerateAsync(t2);
                    AssertEqual(1, t2Signals.Count);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: "Database.EntityTenantScoped",
                displayName: "Entity Tenant-Scoped Operations",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<TenantPairResult> CreateTwoTenantsAsync(DatabaseDriver db)
        {
            TenantMetadata tA = new TenantMetadata("TenantA " + Guid.NewGuid().ToString("N").Substring(0, 6));
            TenantMetadata tB = new TenantMetadata("TenantB " + Guid.NewGuid().ToString("N").Substring(0, 6));
            await db.Tenants.CreateAsync(tA);
            await db.Tenants.CreateAsync(tB);
            return new TenantPairResult
            {
                TenantA = tA.Id,
                TenantB = tB.Id
            };
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Database.EntityTenantScoped",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        private sealed class TenantPairResult
        {
            public string TenantA { get; set; } = string.Empty;

            public string TenantB { get; set; } = string.Empty;
        }

        #endregion
    }
}
