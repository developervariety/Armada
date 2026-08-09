namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for tenant-scoped paginated enumeration of Signal, Event, Voyage, and Dock
    /// entities. Positive cases cover first/second-page counts and totals, full property
    /// read-back, and combined filters; negative cases cover beyond-range pages returning an
    /// empty result set. Each case runs against its own fresh SQLite store, and every scenario
    /// seeds a second "noise" tenant to prove enumeration is fenced to the requested tenant.
    /// </summary>
    public sealed class TenantScopedPagination2Suite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Database.TenantScopedPagination2";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Tenant-Scoped Pagination 2 suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            // Signal Tenant-Scoped Paginated Enumeration

            cases.Add(CaseAsync("signal_enumerate_page1_count_and_totals", "Signal tenant-scoped enumerate page 1 returns correct count and totals", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTenantAsync(db, "TenantA " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    string t2 = await CreateTenantAsync(db, "TenantB " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    List<string> captainIds = new List<string>();

                    for (int i = 0; i < 4; i++)
                    {
                        string captainId = await CreateCaptainAsync(db, t1, "capt_target_" + i);
                        captainIds.Add(captainId);
                        Signal s = new Signal(SignalTypeEnum.Nudge, "{\"index\":" + i + "}");
                        s.TenantId = t1;
                        s.ToCaptainId = captainId;
                        s.CreatedUtc = BaseTime.AddMinutes(i);
                        await db.Signals.CreateAsync(s);
                    }

                    string noiseCaptainId = await CreateCaptainAsync(db, t2, "capt_noise");
                    Signal noise = new Signal(SignalTypeEnum.Heartbeat, "{\"noise\":true}");
                    noise.TenantId = t2;
                    noise.ToCaptainId = noiseCaptainId;
                    noise.CreatedUtc = BaseTime.AddMinutes(10);
                    await db.Signals.CreateAsync(noise);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 1;
                    query.Order = EnumerationOrderEnum.CreatedAscending;
                    EnumerationResult<Signal> page1 = await db.Signals.EnumerateAsync(t1, query);

                    AssertEqual(2, page1.Objects.Count);
                    AssertEqual(4, (int)page1.TotalRecords);
                    AssertEqual(2, page1.TotalPages);
                }
            }));

            cases.Add(CaseAsync("signal_enumerate_page2_remaining", "Signal tenant-scoped enumerate page 2 returns remaining items", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTenantAsync(db, "TenantA " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    string t2 = await CreateTenantAsync(db, "TenantB " + Guid.NewGuid().ToString("N").Substring(0, 6));

                    for (int i = 0; i < 4; i++)
                    {
                        string captainId = await CreateCaptainAsync(db, t1, "capt_target_" + i);
                        Signal s = new Signal(SignalTypeEnum.Nudge, "{\"index\":" + i + "}");
                        s.TenantId = t1;
                        s.ToCaptainId = captainId;
                        s.CreatedUtc = BaseTime.AddMinutes(i);
                        await db.Signals.CreateAsync(s);
                    }

                    string noiseCaptainId = await CreateCaptainAsync(db, t2, "capt_noise");
                    Signal noise = new Signal(SignalTypeEnum.Heartbeat);
                    noise.TenantId = t2;
                    noise.ToCaptainId = noiseCaptainId;
                    await db.Signals.CreateAsync(noise);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 2;
                    query.Order = EnumerationOrderEnum.CreatedAscending;
                    EnumerationResult<Signal> page2 = await db.Signals.EnumerateAsync(t1, query);

                    AssertEqual(2, page2.Objects.Count);
                }
            }));

            cases.Add(CaseAsync("signal_enumerate_beyond_range_empty", "Signal tenant-scoped enumerate beyond range returns empty", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTenantAsync(db, "TenantA " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    string t2 = await CreateTenantAsync(db, "TenantB " + Guid.NewGuid().ToString("N").Substring(0, 6));

                    for (int i = 0; i < 4; i++)
                    {
                        string captainId = await CreateCaptainAsync(db, t1, "capt_target_" + i);
                        Signal s = new Signal(SignalTypeEnum.Nudge, "{\"index\":" + i + "}");
                        s.TenantId = t1;
                        s.ToCaptainId = captainId;
                        s.CreatedUtc = BaseTime.AddMinutes(i);
                        await db.Signals.CreateAsync(s);
                    }

                    string noiseCaptainId = await CreateCaptainAsync(db, t2, "capt_noise");
                    Signal noise = new Signal(SignalTypeEnum.Heartbeat);
                    noise.TenantId = t2;
                    noise.ToCaptainId = noiseCaptainId;
                    await db.Signals.CreateAsync(noise);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 999;
                    EnumerationResult<Signal> beyond = await db.Signals.EnumerateAsync(t1, query);

                    AssertEqual(0, beyond.Objects.Count);
                    AssertEqual(4, (int)beyond.TotalRecords);
                    AssertEqual(2, beyond.TotalPages);
                }
            }));

            cases.Add(CaseAsync("signal_enumerate_full_property_validation", "Signal tenant-scoped enumerate validates full properties on read-back", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTenantAsync(db, "TenantA " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    string t2 = await CreateTenantAsync(db, "TenantB " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    string validateCaptainId = await CreateCaptainAsync(db, t1, "capt_validate");

                    Signal original = new Signal(SignalTypeEnum.Assignment, "{\"task\":\"validate\"}");
                    original.TenantId = t1;
                    original.ToCaptainId = validateCaptainId;
                    original.CreatedUtc = BaseTime;
                    await db.Signals.CreateAsync(original);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 10;
                    query.PageNumber = 1;
                    EnumerationResult<Signal> result = await db.Signals.EnumerateAsync(t1, query);

                    AssertEqual(1, result.Objects.Count);
                    Signal readBack = result.Objects[0];
                    AssertEqual(original.Id, readBack.Id);
                    AssertEqual(t1, readBack.TenantId);
                    AssertEqual(SignalTypeEnum.Assignment, readBack.Type);
                    AssertEqual("{\"task\":\"validate\"}", readBack.Payload);
                    AssertEqual(validateCaptainId, readBack.ToCaptainId);
                    AssertNotEqual(default(DateTime), readBack.CreatedUtc, "CreatedUtc should not be default");
                }
            }));

            // Event Tenant-Scoped Paginated Enumeration

            cases.Add(CaseAsync("event_enumerate_page1_count_and_totals", "Event tenant-scoped enumerate page 1 returns correct count and totals", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTenantAsync(db, "TenantA " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    string t2 = await CreateTenantAsync(db, "TenantB " + Guid.NewGuid().ToString("N").Substring(0, 6));

                    for (int i = 0; i < 4; i++)
                    {
                        ArmadaEvent evt = new ArmadaEvent("mission.created", "Mission created " + i);
                        evt.TenantId = t1;
                        evt.EntityType = "mission";
                        evt.EntityId = "msn_entity_" + i;
                        evt.CreatedUtc = BaseTime.AddMinutes(i);
                        await db.Events.CreateAsync(evt);
                    }

                    ArmadaEvent noise = new ArmadaEvent("captain.stalled", "Captain stalled");
                    noise.TenantId = t2;
                    noise.EntityType = "captain";
                    noise.EntityId = "capt_noise";
                    noise.CreatedUtc = BaseTime.AddMinutes(10);
                    await db.Events.CreateAsync(noise);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 1;
                    query.Order = EnumerationOrderEnum.CreatedAscending;
                    EnumerationResult<ArmadaEvent> page1 = await db.Events.EnumerateAsync(t1, query);

                    AssertEqual(2, page1.Objects.Count);
                    AssertEqual(4, (int)page1.TotalRecords);
                    AssertEqual(2, page1.TotalPages);
                }
            }));

            cases.Add(CaseAsync("event_enumerate_page2_remaining", "Event tenant-scoped enumerate page 2 returns remaining items", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTenantAsync(db, "TenantA " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    string t2 = await CreateTenantAsync(db, "TenantB " + Guid.NewGuid().ToString("N").Substring(0, 6));

                    for (int i = 0; i < 4; i++)
                    {
                        ArmadaEvent evt = new ArmadaEvent("mission.created", "Mission created " + i);
                        evt.TenantId = t1;
                        evt.EntityType = "mission";
                        evt.EntityId = "msn_entity_" + i;
                        evt.CreatedUtc = BaseTime.AddMinutes(i);
                        await db.Events.CreateAsync(evt);
                    }

                    ArmadaEvent noise = new ArmadaEvent("captain.stalled", "Captain stalled");
                    noise.TenantId = t2;
                    await db.Events.CreateAsync(noise);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 2;
                    query.Order = EnumerationOrderEnum.CreatedAscending;
                    EnumerationResult<ArmadaEvent> page2 = await db.Events.EnumerateAsync(t1, query);

                    AssertEqual(2, page2.Objects.Count);
                }
            }));

            cases.Add(CaseAsync("event_enumerate_beyond_range_empty", "Event tenant-scoped enumerate beyond range returns empty", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTenantAsync(db, "TenantA " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    string t2 = await CreateTenantAsync(db, "TenantB " + Guid.NewGuid().ToString("N").Substring(0, 6));

                    for (int i = 0; i < 4; i++)
                    {
                        ArmadaEvent evt = new ArmadaEvent("mission.created", "Mission created " + i);
                        evt.TenantId = t1;
                        evt.EntityType = "mission";
                        evt.EntityId = "msn_entity_" + i;
                        evt.CreatedUtc = BaseTime.AddMinutes(i);
                        await db.Events.CreateAsync(evt);
                    }

                    ArmadaEvent noise = new ArmadaEvent("captain.stalled", "Captain stalled");
                    noise.TenantId = t2;
                    await db.Events.CreateAsync(noise);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 999;
                    EnumerationResult<ArmadaEvent> beyond = await db.Events.EnumerateAsync(t1, query);

                    AssertEqual(0, beyond.Objects.Count);
                    AssertEqual(4, (int)beyond.TotalRecords);
                    AssertEqual(2, beyond.TotalPages);
                }
            }));

            cases.Add(CaseAsync("event_enumerate_full_property_validation", "Event tenant-scoped enumerate validates full properties on read-back", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTenantAsync(db, "TenantA " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    string t2 = await CreateTenantAsync(db, "TenantB " + Guid.NewGuid().ToString("N").Substring(0, 6));

                    ArmadaEvent original = new ArmadaEvent("voyage.completed", "Voyage completed successfully");
                    original.TenantId = t1;
                    original.EntityType = "voyage";
                    original.EntityId = "vyg_validate_01";
                    original.CreatedUtc = BaseTime;
                    await db.Events.CreateAsync(original);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 10;
                    query.PageNumber = 1;
                    EnumerationResult<ArmadaEvent> result = await db.Events.EnumerateAsync(t1, query);

                    AssertEqual(1, result.Objects.Count);
                    ArmadaEvent readBack = result.Objects[0];
                    AssertEqual(original.Id, readBack.Id);
                    AssertEqual(t1, readBack.TenantId);
                    AssertEqual("voyage.completed", readBack.EventType);
                    AssertEqual("voyage", readBack.EntityType);
                    AssertEqual("Voyage completed successfully", readBack.Message);
                    AssertNotEqual(default(DateTime), readBack.CreatedUtc, "CreatedUtc should not be default");
                }
            }));

            cases.Add(CaseAsync("event_enumerate_combined_eventtype_vesselid_filters", "Event tenant-scoped enumerate applies combined EventType and VesselId filters", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTenantAsync(db, "TenantA " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    string t2 = await CreateTenantAsync(db, "TenantB " + Guid.NewGuid().ToString("N").Substring(0, 6));

                    ArmadaEvent matching = new ArmadaEvent("mission.status_changed", "Matching event");
                    matching.TenantId = t1;
                    matching.VesselId = "vsl_target";
                    matching.MissionId = "msn_target";
                    await db.Events.CreateAsync(matching);

                    ArmadaEvent wrongType = new ArmadaEvent("mission.created", "Wrong type");
                    wrongType.TenantId = t1;
                    wrongType.VesselId = "vsl_target";
                    wrongType.MissionId = "msn_wrong_type";
                    await db.Events.CreateAsync(wrongType);

                    ArmadaEvent wrongVessel = new ArmadaEvent("mission.status_changed", "Wrong vessel");
                    wrongVessel.TenantId = t1;
                    wrongVessel.VesselId = "vsl_other";
                    wrongVessel.MissionId = "msn_wrong_vessel";
                    await db.Events.CreateAsync(wrongVessel);

                    ArmadaEvent otherTenant = new ArmadaEvent("mission.status_changed", "Other tenant");
                    otherTenant.TenantId = t2;
                    otherTenant.VesselId = "vsl_target";
                    otherTenant.MissionId = "msn_other_tenant";
                    await db.Events.CreateAsync(otherTenant);

                    EnumerationQuery query = new EnumerationQuery
                    {
                        EventType = "mission.status_changed",
                        VesselId = "vsl_target",
                        PageSize = 10,
                        PageNumber = 1
                    };

                    EnumerationResult<ArmadaEvent> result = await db.Events.EnumerateAsync(t1, query);

                    AssertEqual(1, result.Objects.Count);
                    AssertEqual(matching.Id, result.Objects[0].Id);
                    AssertEqual("vsl_target", result.Objects[0].VesselId);
                    AssertEqual("mission.status_changed", result.Objects[0].EventType);
                }
            }));

            // Voyage Tenant-Scoped Paginated Enumeration

            cases.Add(CaseAsync("voyage_enumerate_page1_count_and_totals", "Voyage tenant-scoped enumerate page 1 returns correct count and totals", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTenantAsync(db, "TenantA " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    string t2 = await CreateTenantAsync(db, "TenantB " + Guid.NewGuid().ToString("N").Substring(0, 6));

                    for (int i = 0; i < 3; i++)
                    {
                        Voyage v = new Voyage("Voyage-T1-" + i, "Description " + i);
                        v.TenantId = t1;
                        v.Status = VoyageStatusEnum.Open;
                        v.CreatedUtc = BaseTime.AddMinutes(i);
                        await db.Voyages.CreateAsync(v);
                    }

                    Voyage noise = new Voyage("Voyage-T2-Noise", "Noise description");
                    noise.TenantId = t2;
                    noise.CreatedUtc = BaseTime.AddMinutes(10);
                    await db.Voyages.CreateAsync(noise);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 1;
                    query.Order = EnumerationOrderEnum.CreatedAscending;
                    EnumerationResult<Voyage> page1 = await db.Voyages.EnumerateAsync(t1, query);

                    AssertEqual(2, page1.Objects.Count);
                    AssertEqual(3, (int)page1.TotalRecords);
                    AssertEqual(2, page1.TotalPages);
                }
            }));

            cases.Add(CaseAsync("voyage_enumerate_page2_remaining", "Voyage tenant-scoped enumerate page 2 returns remaining items", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTenantAsync(db, "TenantA " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    string t2 = await CreateTenantAsync(db, "TenantB " + Guid.NewGuid().ToString("N").Substring(0, 6));

                    for (int i = 0; i < 3; i++)
                    {
                        Voyage v = new Voyage("Voyage-T1-" + i, "Description " + i);
                        v.TenantId = t1;
                        v.Status = VoyageStatusEnum.Open;
                        v.CreatedUtc = BaseTime.AddMinutes(i);
                        await db.Voyages.CreateAsync(v);
                    }

                    Voyage noise = new Voyage("Voyage-T2-Noise");
                    noise.TenantId = t2;
                    await db.Voyages.CreateAsync(noise);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 2;
                    query.Order = EnumerationOrderEnum.CreatedAscending;
                    EnumerationResult<Voyage> page2 = await db.Voyages.EnumerateAsync(t1, query);

                    AssertEqual(1, page2.Objects.Count);
                }
            }));

            cases.Add(CaseAsync("voyage_enumerate_full_property_validation", "Voyage tenant-scoped enumerate validates full properties on read-back", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTenantAsync(db, "TenantA " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    string t2 = await CreateTenantAsync(db, "TenantB " + Guid.NewGuid().ToString("N").Substring(0, 6));

                    Voyage original = new Voyage("Voyage-Validate", "Validate description");
                    original.TenantId = t1;
                    original.Status = VoyageStatusEnum.Open;
                    original.CreatedUtc = BaseTime;
                    await db.Voyages.CreateAsync(original);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 10;
                    query.PageNumber = 1;
                    EnumerationResult<Voyage> result = await db.Voyages.EnumerateAsync(t1, query);

                    AssertEqual(1, result.Objects.Count);
                    Voyage readBack = result.Objects[0];
                    AssertEqual(original.Id, readBack.Id);
                    AssertEqual(t1, readBack.TenantId);
                    AssertEqual("Voyage-Validate", readBack.Title);
                    AssertEqual("Validate description", readBack.Description);
                    AssertEqual(VoyageStatusEnum.Open, readBack.Status);
                    AssertNotEqual(default(DateTime), readBack.CreatedUtc, "CreatedUtc should not be default");
                }
            }));

            // Audit addition: Voyage beyond-range page returns empty. Signal, Event, and Dock
            // each have this negative in the legacy suite; Voyage was the only entity missing it.
            cases.Add(CaseAsync("voyage_enumerate_beyond_range_empty_audit", "Voyage tenant-scoped enumerate beyond range returns empty", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTenantAsync(db, "TenantA " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    string t2 = await CreateTenantAsync(db, "TenantB " + Guid.NewGuid().ToString("N").Substring(0, 6));

                    for (int i = 0; i < 3; i++)
                    {
                        Voyage v = new Voyage("Voyage-T1-" + i, "Description " + i);
                        v.TenantId = t1;
                        v.Status = VoyageStatusEnum.Open;
                        v.CreatedUtc = BaseTime.AddMinutes(i);
                        await db.Voyages.CreateAsync(v);
                    }

                    Voyage noise = new Voyage("Voyage-T2-Noise");
                    noise.TenantId = t2;
                    noise.CreatedUtc = BaseTime.AddMinutes(10);
                    await db.Voyages.CreateAsync(noise);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 999;
                    EnumerationResult<Voyage> beyond = await db.Voyages.EnumerateAsync(t1, query);

                    AssertEqual(0, beyond.Objects.Count);
                    AssertEqual(3, (int)beyond.TotalRecords);
                    AssertEqual(2, beyond.TotalPages);
                }
            }));

            // Dock Tenant-Scoped Paginated Enumeration

            cases.Add(CaseAsync("dock_enumerate_page1_count_and_totals", "Dock tenant-scoped enumerate page 1 returns correct count and totals", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTenantAsync(db, "TenantA " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    string t2 = await CreateTenantAsync(db, "TenantB " + Guid.NewGuid().ToString("N").Substring(0, 6));

                    // Create fleet and vessel prerequisites for t1
                    Fleet fleet1 = new Fleet("Dock-Fleet-T1") { TenantId = t1 };
                    await db.Fleets.CreateAsync(fleet1);
                    Vessel vessel1 = new Vessel("Dock-Vessel-T1", "https://github.com/test/dock-t1") { TenantId = t1, FleetId = fleet1.Id };
                    await db.Vessels.CreateAsync(vessel1);

                    // Create fleet and vessel prerequisites for t2
                    Fleet fleet2 = new Fleet("Dock-Fleet-T2") { TenantId = t2 };
                    await db.Fleets.CreateAsync(fleet2);
                    Vessel vessel2 = new Vessel("Dock-Vessel-T2", "https://github.com/test/dock-t2") { TenantId = t2, FleetId = fleet2.Id };
                    await db.Vessels.CreateAsync(vessel2);

                    for (int i = 0; i < 3; i++)
                    {
                        Dock d = new Dock(vessel1.Id);
                        d.TenantId = t1;
                        d.WorktreePath = "/tmp/worktree/t1_" + i;
                        d.Active = true;
                        d.CreatedUtc = BaseTime.AddMinutes(i);
                        await db.Docks.CreateAsync(d);
                    }

                    Dock noise = new Dock(vessel2.Id);
                    noise.TenantId = t2;
                    noise.WorktreePath = "/tmp/worktree/t2_noise";
                    noise.Active = true;
                    noise.CreatedUtc = BaseTime.AddMinutes(10);
                    await db.Docks.CreateAsync(noise);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 1;
                    query.Order = EnumerationOrderEnum.CreatedAscending;
                    EnumerationResult<Dock> page1 = await db.Docks.EnumerateAsync(t1, query);

                    AssertEqual(2, page1.Objects.Count);
                    AssertEqual(3, (int)page1.TotalRecords);
                    AssertEqual(2, page1.TotalPages);
                }
            }));

            cases.Add(CaseAsync("dock_enumerate_page2_remaining", "Dock tenant-scoped enumerate page 2 returns remaining items", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTenantAsync(db, "TenantA " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    string t2 = await CreateTenantAsync(db, "TenantB " + Guid.NewGuid().ToString("N").Substring(0, 6));

                    Fleet fleet1 = new Fleet("Dock-Fleet-T1") { TenantId = t1 };
                    await db.Fleets.CreateAsync(fleet1);
                    Vessel vessel1 = new Vessel("Dock-Vessel-T1", "https://github.com/test/dock-t1") { TenantId = t1, FleetId = fleet1.Id };
                    await db.Vessels.CreateAsync(vessel1);

                    for (int i = 0; i < 3; i++)
                    {
                        Dock d = new Dock(vessel1.Id);
                        d.TenantId = t1;
                        d.WorktreePath = "/tmp/worktree/t1_" + i;
                        d.Active = true;
                        d.CreatedUtc = BaseTime.AddMinutes(i);
                        await db.Docks.CreateAsync(d);
                    }

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 2;
                    query.Order = EnumerationOrderEnum.CreatedAscending;
                    EnumerationResult<Dock> page2 = await db.Docks.EnumerateAsync(t1, query);

                    AssertEqual(1, page2.Objects.Count);
                }
            }));

            cases.Add(CaseAsync("dock_enumerate_beyond_range_empty", "Dock tenant-scoped enumerate beyond range returns empty", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTenantAsync(db, "TenantA " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    string t2 = await CreateTenantAsync(db, "TenantB " + Guid.NewGuid().ToString("N").Substring(0, 6));

                    Fleet fleet1 = new Fleet("Dock-Fleet-T1") { TenantId = t1 };
                    await db.Fleets.CreateAsync(fleet1);
                    Vessel vessel1 = new Vessel("Dock-Vessel-T1", "https://github.com/test/dock-t1") { TenantId = t1, FleetId = fleet1.Id };
                    await db.Vessels.CreateAsync(vessel1);

                    for (int i = 0; i < 3; i++)
                    {
                        Dock d = new Dock(vessel1.Id);
                        d.TenantId = t1;
                        d.WorktreePath = "/tmp/worktree/t1_" + i;
                        d.Active = true;
                        d.CreatedUtc = BaseTime.AddMinutes(i);
                        await db.Docks.CreateAsync(d);
                    }

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 999;
                    EnumerationResult<Dock> beyond = await db.Docks.EnumerateAsync(t1, query);

                    AssertEqual(0, beyond.Objects.Count);
                    AssertEqual(3, (int)beyond.TotalRecords);
                    AssertEqual(2, beyond.TotalPages);
                }
            }));

            cases.Add(CaseAsync("dock_enumerate_full_property_validation", "Dock tenant-scoped enumerate validates full properties on read-back", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTenantAsync(db, "TenantA " + Guid.NewGuid().ToString("N").Substring(0, 6));
                    string t2 = await CreateTenantAsync(db, "TenantB " + Guid.NewGuid().ToString("N").Substring(0, 6));

                    Fleet fleet1 = new Fleet("Dock-Fleet-Validate") { TenantId = t1 };
                    await db.Fleets.CreateAsync(fleet1);
                    Vessel vessel1 = new Vessel("Dock-Vessel-Validate", "https://github.com/test/dock-validate") { TenantId = t1, FleetId = fleet1.Id };
                    await db.Vessels.CreateAsync(vessel1);

                    Dock original = new Dock(vessel1.Id);
                    original.TenantId = t1;
                    original.WorktreePath = "/tmp/worktree/validate";
                    original.Active = true;
                    original.CreatedUtc = BaseTime;
                    await db.Docks.CreateAsync(original);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 10;
                    query.PageNumber = 1;
                    EnumerationResult<Dock> result = await db.Docks.EnumerateAsync(t1, query);

                    AssertEqual(1, result.Objects.Count);
                    Dock readBack = result.Objects[0];
                    AssertEqual(original.Id, readBack.Id);
                    AssertEqual(t1, readBack.TenantId);
                    AssertEqual(vessel1.Id, readBack.VesselId);
                    AssertEqual("/tmp/worktree/validate", readBack.WorktreePath);
                    AssertEqual(true, readBack.Active);
                    AssertNotEqual(default(DateTime), readBack.CreatedUtc, "CreatedUtc should not be default");
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Tenant-Scoped Pagination 2",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static DateTime BaseTime => new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static async Task<string> CreateTenantAsync(SqliteDatabaseDriver db, string name)
        {
            TenantMetadata tenant = new TenantMetadata(name);
            await db.Tenants.CreateAsync(tenant);
            return tenant.Id;
        }

        private static async Task<string> CreateCaptainAsync(SqliteDatabaseDriver db, string tenantId, string name)
        {
            Captain captain = new Captain(name);
            captain.TenantId = tenantId;
            captain.State = CaptainStateEnum.Idle;
            captain = await db.Captains.CreateAsync(captain);
            return captain.Id;
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
