namespace Armada.Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for tenant-scoped paginated enumeration of Fleet, Vessel, and Mission.
    /// Positive cases assert per-page counts, total records/pages, tenant-scoped totals,
    /// created-after filtering, ascending/descending ordering, and full property round-trips.
    /// Negative cases assert that requesting a page beyond the available range yields an empty
    /// page while still reporting the correct total record count.
    /// </summary>
    public sealed class TenantScopedPaginationSuite : IArmadaTestSuite
    {
        #region Private-Members

        private static DateTime BaseTime => new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Tenant-Scoped Pagination suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            // Fleet tenant-scoped paginated enumeration

            cases.Add(CaseAsync("fleet_tenant_pagination_page1_correct_counts", "Fleet tenant pagination page 1 returns correct counts", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    for (int i = 0; i < 5; i++)
                    {
                        Fleet f = new Fleet("Fleet-T1-" + i) { TenantId = t1, CreatedUtc = BaseTime.AddMinutes(i) };
                        await db.Fleets.CreateAsync(f);
                    }
                    for (int i = 0; i < 2; i++)
                    {
                        Fleet f = new Fleet("Fleet-T2-" + i) { TenantId = t2, CreatedUtc = BaseTime.AddMinutes(i) };
                        await db.Fleets.CreateAsync(f);
                    }

                    EnumerationQuery query = new EnumerationQuery { PageSize = 2, PageNumber = 1 };
                    EnumerationResult<Fleet> page1 = await db.Fleets.EnumerateAsync(t1, query);

                    AssertEqual(2, page1.Objects.Count);
                    AssertEqual(5, (int)page1.TotalRecords);
                    AssertEqual(3, page1.TotalPages);
                }
            }));

            cases.Add(CaseAsync("fleet_tenant_pagination_page2_returns_2", "Fleet tenant pagination page 2 returns 2 objects", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    for (int i = 0; i < 5; i++)
                    {
                        Fleet f = new Fleet("Fleet-T1-" + i) { TenantId = t1, CreatedUtc = BaseTime.AddMinutes(i) };
                        await db.Fleets.CreateAsync(f);
                    }
                    for (int i = 0; i < 2; i++)
                    {
                        Fleet f = new Fleet("Fleet-T2-" + i) { TenantId = t2, CreatedUtc = BaseTime.AddMinutes(i) };
                        await db.Fleets.CreateAsync(f);
                    }

                    EnumerationQuery query = new EnumerationQuery { PageSize = 2, PageNumber = 2 };
                    EnumerationResult<Fleet> page2 = await db.Fleets.EnumerateAsync(t1, query);

                    AssertEqual(2, page2.Objects.Count);
                    AssertEqual(5, (int)page2.TotalRecords);
                    AssertEqual(3, page2.TotalPages);
                }
            }));

            cases.Add(CaseAsync("fleet_tenant_pagination_page3_returns_1", "Fleet tenant pagination page 3 returns 1 object", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    for (int i = 0; i < 5; i++)
                    {
                        Fleet f = new Fleet("Fleet-T1-" + i) { TenantId = t1, CreatedUtc = BaseTime.AddMinutes(i) };
                        await db.Fleets.CreateAsync(f);
                    }
                    for (int i = 0; i < 2; i++)
                    {
                        Fleet f = new Fleet("Fleet-T2-" + i) { TenantId = t2, CreatedUtc = BaseTime.AddMinutes(i) };
                        await db.Fleets.CreateAsync(f);
                    }

                    EnumerationQuery query = new EnumerationQuery { PageSize = 2, PageNumber = 3 };
                    EnumerationResult<Fleet> page3 = await db.Fleets.EnumerateAsync(t1, query);

                    AssertEqual(1, page3.Objects.Count);
                    AssertEqual(5, (int)page3.TotalRecords);
                    AssertEqual(3, page3.TotalPages);
                }
            }));

            cases.Add(CaseAsync("fleet_tenant_pagination_beyond_range_empty", "Fleet tenant pagination beyond range returns empty with correct totals", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    for (int i = 0; i < 5; i++)
                    {
                        Fleet f = new Fleet("Fleet-T1-" + i) { TenantId = t1, CreatedUtc = BaseTime.AddMinutes(i) };
                        await db.Fleets.CreateAsync(f);
                    }
                    for (int i = 0; i < 2; i++)
                    {
                        Fleet f = new Fleet("Fleet-T2-" + i) { TenantId = t2, CreatedUtc = BaseTime.AddMinutes(i) };
                        await db.Fleets.CreateAsync(f);
                    }

                    EnumerationQuery query = new EnumerationQuery { PageSize = 2, PageNumber = 10 };
                    EnumerationResult<Fleet> beyondRange = await db.Fleets.EnumerateAsync(t1, query);

                    AssertEqual(0, beyondRange.Objects.Count);
                    AssertEqual(5, (int)beyondRange.TotalRecords);
                }
            }));

            cases.Add(CaseAsync("fleet_tenant_pagination_t2_total_records", "Fleet tenant pagination t2 returns correct total records", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    for (int i = 0; i < 5; i++)
                    {
                        Fleet f = new Fleet("Fleet-T1-" + i) { TenantId = t1, CreatedUtc = BaseTime.AddMinutes(i) };
                        await db.Fleets.CreateAsync(f);
                    }
                    for (int i = 0; i < 2; i++)
                    {
                        Fleet f = new Fleet("Fleet-T2-" + i) { TenantId = t2, CreatedUtc = BaseTime.AddMinutes(i) };
                        await db.Fleets.CreateAsync(f);
                    }

                    EnumerationQuery query = new EnumerationQuery { PageSize = 10, PageNumber = 1 };
                    EnumerationResult<Fleet> t2Result = await db.Fleets.EnumerateAsync(t2, query);

                    AssertEqual(2, t2Result.Objects.Count);
                    AssertEqual(2, (int)t2Result.TotalRecords);
                }
            }));

            cases.Add(CaseAsync("fleet_tenant_pagination_created_after_filter", "Fleet tenant pagination CreatedAfter filter through tenant path", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    for (int i = 0; i < 5; i++)
                    {
                        Fleet f = new Fleet("Fleet-T1-" + i) { TenantId = t1, CreatedUtc = BaseTime.AddMinutes(i) };
                        await db.Fleets.CreateAsync(f);
                    }
                    // Also seed t2 noise
                    Fleet noise = new Fleet("Fleet-T2-Noise") { TenantId = t2, CreatedUtc = BaseTime.AddMinutes(10) };
                    await db.Fleets.CreateAsync(noise);

                    // Filter to fleets created after minute 2 (should get minutes 3 and 4 = 2 fleets)
                    EnumerationQuery query = new EnumerationQuery
                    {
                        PageSize = 10,
                        PageNumber = 1,
                        CreatedAfter = BaseTime.AddMinutes(2)
                    };
                    EnumerationResult<Fleet> result = await db.Fleets.EnumerateAsync(t1, query);

                    AssertEqual(2, result.Objects.Count);
                    AssertEqual(2, (int)result.TotalRecords);
                    foreach (Fleet f in result.Objects)
                    {
                        AssertEqual(t1, f.TenantId, "CreatedAfter-filtered fleets should belong to t1");
                    }
                }
            }));

            cases.Add(CaseAsync("fleet_tenant_pagination_order_asc_vs_desc", "Fleet tenant pagination order ascending vs descending", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Fleet oldest = new Fleet("Fleet-Oldest") { TenantId = t1, CreatedUtc = BaseTime };
                    Fleet middle = new Fleet("Fleet-Middle") { TenantId = t1, CreatedUtc = BaseTime.AddHours(1) };
                    Fleet newest = new Fleet("Fleet-Newest") { TenantId = t1, CreatedUtc = BaseTime.AddHours(2) };
                    await db.Fleets.CreateAsync(oldest);
                    await db.Fleets.CreateAsync(middle);
                    await db.Fleets.CreateAsync(newest);

                    // Noise in t2
                    Fleet t2Fleet = new Fleet("Fleet-T2") { TenantId = t2, CreatedUtc = BaseTime.AddHours(3) };
                    await db.Fleets.CreateAsync(t2Fleet);

                    // Ascending
                    EnumerationQuery ascQuery = new EnumerationQuery
                    {
                        PageSize = 10,
                        PageNumber = 1,
                        Order = EnumerationOrderEnum.CreatedAscending
                    };
                    EnumerationResult<Fleet> ascResult = await db.Fleets.EnumerateAsync(t1, ascQuery);

                    AssertEqual(3, ascResult.Objects.Count);
                    AssertEqual("Fleet-Oldest", ascResult.Objects[0].Name);
                    AssertEqual("Fleet-Middle", ascResult.Objects[1].Name);
                    AssertEqual("Fleet-Newest", ascResult.Objects[2].Name);

                    // Descending
                    EnumerationQuery descQuery = new EnumerationQuery
                    {
                        PageSize = 10,
                        PageNumber = 1,
                        Order = EnumerationOrderEnum.CreatedDescending
                    };
                    EnumerationResult<Fleet> descResult = await db.Fleets.EnumerateAsync(t1, descQuery);

                    AssertEqual(3, descResult.Objects.Count);
                    AssertEqual("Fleet-Newest", descResult.Objects[0].Name);
                    AssertEqual("Fleet-Middle", descResult.Objects[1].Name);
                    AssertEqual("Fleet-Oldest", descResult.Objects[2].Name);
                }
            }));

            cases.Add(CaseAsync("fleet_tenant_pagination_property_validation", "Fleet tenant pagination full property validation", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Fleet fleet = new Fleet("PropVal-Fleet");
                    fleet.TenantId = t1;
                    fleet.Description = "Test fleet description";
                    fleet.Active = true;
                    await db.Fleets.CreateAsync(fleet);

                    EnumerationQuery query = new EnumerationQuery { PageSize = 10, PageNumber = 1 };
                    EnumerationResult<Fleet> result = await db.Fleets.EnumerateAsync(t1, query);

                    AssertEqual(1, result.Objects.Count);
                    Fleet readBack = result.Objects[0];

                    AssertEqual(fleet.Id, readBack.Id);
                    AssertEqual("PropVal-Fleet", readBack.Name);
                    AssertEqual("Test fleet description", readBack.Description);
                    AssertEqual(true, readBack.Active);
                    AssertEqual(t1, readBack.TenantId);
                    AssertNotEqual(default(DateTime), readBack.CreatedUtc, "CreatedUtc should not be default");
                    AssertNotEqual(default(DateTime), readBack.LastUpdateUtc, "LastUpdateUtc should not be default");
                }
            }));

            // Vessel tenant-scoped paginated enumeration

            cases.Add(CaseAsync("vessel_tenant_pagination_page1_correct_counts", "Vessel tenant pagination page 1 returns correct counts", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Fleet fleetT1 = new Fleet("Vessel-FleetT1") { TenantId = t1 };
                    Fleet fleetT2 = new Fleet("Vessel-FleetT2") { TenantId = t2 };
                    await db.Fleets.CreateAsync(fleetT1);
                    await db.Fleets.CreateAsync(fleetT2);

                    for (int i = 0; i < 4; i++)
                    {
                        Vessel v = new Vessel("Vessel-T1-" + i, "https://github.com/t1/repo-" + i)
                        {
                            TenantId = t1,
                            FleetId = fleetT1.Id,
                            CreatedUtc = BaseTime.AddMinutes(i)
                        };
                        await db.Vessels.CreateAsync(v);
                    }
                    Vessel vT2 = new Vessel("Vessel-T2-0", "https://github.com/t2/repo-0")
                    {
                        TenantId = t2,
                        FleetId = fleetT2.Id,
                        CreatedUtc = BaseTime.AddMinutes(10)
                    };
                    await db.Vessels.CreateAsync(vT2);

                    EnumerationQuery query = new EnumerationQuery { PageSize = 2, PageNumber = 1 };
                    EnumerationResult<Vessel> page1 = await db.Vessels.EnumerateAsync(t1, query);

                    AssertEqual(2, page1.Objects.Count);
                    AssertEqual(4, (int)page1.TotalRecords);
                    AssertEqual(2, page1.TotalPages);
                }
            }));

            cases.Add(CaseAsync("vessel_tenant_pagination_page2_returns_2", "Vessel tenant pagination page 2 returns 2 objects", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Fleet fleetT1 = new Fleet("Vessel-FleetT1") { TenantId = t1 };
                    Fleet fleetT2 = new Fleet("Vessel-FleetT2") { TenantId = t2 };
                    await db.Fleets.CreateAsync(fleetT1);
                    await db.Fleets.CreateAsync(fleetT2);

                    for (int i = 0; i < 4; i++)
                    {
                        Vessel v = new Vessel("Vessel-T1-" + i, "https://github.com/t1/repo-" + i)
                        {
                            TenantId = t1,
                            FleetId = fleetT1.Id,
                            CreatedUtc = BaseTime.AddMinutes(i)
                        };
                        await db.Vessels.CreateAsync(v);
                    }
                    Vessel vT2 = new Vessel("Vessel-T2-0", "https://github.com/t2/repo-0")
                    {
                        TenantId = t2,
                        FleetId = fleetT2.Id
                    };
                    await db.Vessels.CreateAsync(vT2);

                    EnumerationQuery query = new EnumerationQuery { PageSize = 2, PageNumber = 2 };
                    EnumerationResult<Vessel> page2 = await db.Vessels.EnumerateAsync(t1, query);

                    AssertEqual(2, page2.Objects.Count);
                    AssertEqual(4, (int)page2.TotalRecords);
                    AssertEqual(2, page2.TotalPages);
                }
            }));

            cases.Add(CaseAsync("vessel_tenant_pagination_beyond_range_empty", "Vessel tenant pagination beyond range returns empty", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Fleet fleetT1 = new Fleet("Vessel-FleetT1") { TenantId = t1 };
                    await db.Fleets.CreateAsync(fleetT1);

                    for (int i = 0; i < 4; i++)
                    {
                        Vessel v = new Vessel("Vessel-T1-" + i, "https://github.com/t1/repo-" + i)
                        {
                            TenantId = t1,
                            FleetId = fleetT1.Id,
                            CreatedUtc = BaseTime.AddMinutes(i)
                        };
                        await db.Vessels.CreateAsync(v);
                    }

                    EnumerationQuery query = new EnumerationQuery { PageSize = 2, PageNumber = 10 };
                    EnumerationResult<Vessel> beyondRange = await db.Vessels.EnumerateAsync(t1, query);

                    AssertEqual(0, beyondRange.Objects.Count);
                    AssertEqual(4, (int)beyondRange.TotalRecords);
                }
            }));

            cases.Add(CaseAsync("vessel_tenant_pagination_property_validation", "Vessel tenant pagination full property validation", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Fleet fleet = new Fleet("Vessel-PropVal-Fleet") { TenantId = t1 };
                    await db.Fleets.CreateAsync(fleet);

                    Vessel vessel = new Vessel("PropVal-Vessel", "https://github.com/propval/repo");
                    vessel.TenantId = t1;
                    vessel.FleetId = fleet.Id;
                    vessel.DefaultBranch = "main";
                    vessel.Active = true;
                    await db.Vessels.CreateAsync(vessel);

                    EnumerationQuery query = new EnumerationQuery { PageSize = 10, PageNumber = 1 };
                    EnumerationResult<Vessel> result = await db.Vessels.EnumerateAsync(t1, query);

                    AssertEqual(1, result.Objects.Count);
                    Vessel readBack = result.Objects[0];

                    AssertEqual(vessel.Id, readBack.Id);
                    AssertEqual("PropVal-Vessel", readBack.Name);
                    AssertEqual(fleet.Id, readBack.FleetId);
                    AssertEqual(t1, readBack.TenantId);
                    AssertEqual("main", readBack.DefaultBranch);
                    AssertEqual(true, readBack.Active);
                    AssertEqual("https://github.com/propval/repo", readBack.RepoUrl);
                    AssertNotEqual(default(DateTime), readBack.CreatedUtc, "CreatedUtc should not be default");
                    AssertNotEqual(default(DateTime), readBack.LastUpdateUtc, "LastUpdateUtc should not be default");
                }
            }));

            // Mission tenant-scoped paginated enumeration

            cases.Add(CaseAsync("mission_tenant_pagination_page1_correct_counts", "Mission tenant pagination page 1 returns correct counts", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    // Seed prerequisites for t1
                    Fleet fleetT1 = new Fleet("Mission-FleetT1") { TenantId = t1 };
                    await db.Fleets.CreateAsync(fleetT1);
                    Vessel vesselT1 = new Vessel("Mission-VesselT1", "https://github.com/t1/mission")
                    {
                        TenantId = t1,
                        FleetId = fleetT1.Id
                    };
                    await db.Vessels.CreateAsync(vesselT1);
                    Voyage voyageT1 = new Voyage("Mission-VoyageT1") { TenantId = t1 };
                    await db.Voyages.CreateAsync(voyageT1);

                    // Seed prerequisites for t2
                    Fleet fleetT2 = new Fleet("Mission-FleetT2") { TenantId = t2 };
                    await db.Fleets.CreateAsync(fleetT2);
                    Vessel vesselT2 = new Vessel("Mission-VesselT2", "https://github.com/t2/mission")
                    {
                        TenantId = t2,
                        FleetId = fleetT2.Id
                    };
                    await db.Vessels.CreateAsync(vesselT2);
                    Voyage voyageT2 = new Voyage("Mission-VoyageT2") { TenantId = t2 };
                    await db.Voyages.CreateAsync(voyageT2);

                    for (int i = 0; i < 3; i++)
                    {
                        Mission m = new Mission("Mission-T1-" + i)
                        {
                            TenantId = t1,
                            VesselId = vesselT1.Id,
                            VoyageId = voyageT1.Id,
                            CreatedUtc = BaseTime.AddMinutes(i)
                        };
                        await db.Missions.CreateAsync(m);
                    }
                    Mission mT2 = new Mission("Mission-T2-0")
                    {
                        TenantId = t2,
                        VesselId = vesselT2.Id,
                        VoyageId = voyageT2.Id,
                        CreatedUtc = BaseTime.AddMinutes(10)
                    };
                    await db.Missions.CreateAsync(mT2);

                    EnumerationQuery query = new EnumerationQuery { PageSize = 2, PageNumber = 1 };
                    EnumerationResult<Mission> page1 = await db.Missions.EnumerateAsync(t1, query);

                    AssertEqual(2, page1.Objects.Count);
                    AssertEqual(3, (int)page1.TotalRecords);
                    AssertEqual(2, page1.TotalPages);
                }
            }));

            cases.Add(CaseAsync("mission_tenant_pagination_page2_returns_1", "Mission tenant pagination page 2 returns 1 object", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Fleet fleetT1 = new Fleet("Mission-FleetT1") { TenantId = t1 };
                    await db.Fleets.CreateAsync(fleetT1);
                    Vessel vesselT1 = new Vessel("Mission-VesselT1", "https://github.com/t1/mission")
                    {
                        TenantId = t1,
                        FleetId = fleetT1.Id
                    };
                    await db.Vessels.CreateAsync(vesselT1);
                    Voyage voyageT1 = new Voyage("Mission-VoyageT1") { TenantId = t1 };
                    await db.Voyages.CreateAsync(voyageT1);

                    for (int i = 0; i < 3; i++)
                    {
                        Mission m = new Mission("Mission-T1-" + i)
                        {
                            TenantId = t1,
                            VesselId = vesselT1.Id,
                            VoyageId = voyageT1.Id,
                            CreatedUtc = BaseTime.AddMinutes(i)
                        };
                        await db.Missions.CreateAsync(m);
                    }

                    EnumerationQuery query = new EnumerationQuery { PageSize = 2, PageNumber = 2 };
                    EnumerationResult<Mission> page2 = await db.Missions.EnumerateAsync(t1, query);

                    AssertEqual(1, page2.Objects.Count);
                    AssertEqual(3, (int)page2.TotalRecords);
                    AssertEqual(2, page2.TotalPages);
                }
            }));

            cases.Add(CaseAsync("mission_tenant_pagination_beyond_range_empty_audit", "Mission tenant pagination beyond range returns empty", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Fleet fleetT1 = new Fleet("Mission-FleetT1") { TenantId = t1 };
                    await db.Fleets.CreateAsync(fleetT1);
                    Vessel vesselT1 = new Vessel("Mission-VesselT1", "https://github.com/t1/mission")
                    {
                        TenantId = t1,
                        FleetId = fleetT1.Id
                    };
                    await db.Vessels.CreateAsync(vesselT1);
                    Voyage voyageT1 = new Voyage("Mission-VoyageT1") { TenantId = t1 };
                    await db.Voyages.CreateAsync(voyageT1);

                    for (int i = 0; i < 3; i++)
                    {
                        Mission m = new Mission("Mission-T1-" + i)
                        {
                            TenantId = t1,
                            VesselId = vesselT1.Id,
                            VoyageId = voyageT1.Id,
                            CreatedUtc = BaseTime.AddMinutes(i)
                        };
                        await db.Missions.CreateAsync(m);
                    }

                    EnumerationQuery query = new EnumerationQuery { PageSize = 2, PageNumber = 10 };
                    EnumerationResult<Mission> beyondRange = await db.Missions.EnumerateAsync(t1, query);

                    AssertEqual(0, beyondRange.Objects.Count);
                    AssertEqual(3, (int)beyondRange.TotalRecords);
                }
            }));

            cases.Add(CaseAsync("mission_tenant_pagination_property_validation", "Mission tenant pagination full property validation", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantPairResult tenants = await CreateTwoTenantsAsync(db);
                    string t1 = tenants.TenantA;
                    string t2 = tenants.TenantB;

                    Fleet fleet = new Fleet("Mission-PropVal-Fleet") { TenantId = t1 };
                    await db.Fleets.CreateAsync(fleet);
                    Vessel vessel = new Vessel("Mission-PropVal-Vessel", "https://github.com/propval/mission")
                    {
                        TenantId = t1,
                        FleetId = fleet.Id
                    };
                    await db.Vessels.CreateAsync(vessel);
                    Voyage voyage = new Voyage("Mission-PropVal-Voyage") { TenantId = t1 };
                    await db.Voyages.CreateAsync(voyage);

                    Mission mission = new Mission("PropVal-Mission", "Detailed mission description");
                    mission.TenantId = t1;
                    mission.VesselId = vessel.Id;
                    mission.VoyageId = voyage.Id;
                    mission.Status = MissionStatusEnum.Pending;
                    mission.Priority = 42;
                    await db.Missions.CreateAsync(mission);

                    EnumerationQuery query = new EnumerationQuery { PageSize = 10, PageNumber = 1 };
                    EnumerationResult<Mission> result = await db.Missions.EnumerateAsync(t1, query);

                    AssertEqual(1, result.Objects.Count);
                    Mission readBack = result.Objects[0];

                    AssertEqual(mission.Id, readBack.Id);
                    AssertEqual("PropVal-Mission", readBack.Title);
                    AssertEqual("Detailed mission description", readBack.Description);
                    AssertEqual(vessel.Id, readBack.VesselId);
                    AssertEqual(voyage.Id, readBack.VoyageId);
                    AssertEqual(t1, readBack.TenantId);
                    AssertEqual(MissionStatusEnum.Pending, readBack.Status);
                    AssertEqual(42, readBack.Priority);
                    AssertNotEqual(default(DateTime), readBack.CreatedUtc, "CreatedUtc should not be default");
                    AssertNotEqual(default(DateTime), readBack.LastUpdateUtc, "LastUpdateUtc should not be default");
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: "Database.TenantScopedPagination",
                displayName: "Tenant-Scoped Pagination",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<TenantPairResult> CreateTwoTenantsAsync(SqliteDatabaseDriver db)
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
                suiteId: "Database.TenantScopedPagination",
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
