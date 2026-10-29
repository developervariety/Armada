namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Microsoft.Data.Sqlite;
    using Armada.Core;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Coverage for the incident projection table: reads equal the event projection, and the table follows the
    /// snapshot events through writes, backfill and delete.
    /// </summary>
    public partial class IncidentServiceTests
    {
        private const string OtherTenantId = "ten_projection_other";
        private const string UserAId = "usr_projection_a";
        private const string UserBId = "usr_projection_b";

        private static readonly JsonSerializerOptions _ReferenceJson = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private async Task RunProjectionTestsAsync()
        {
            await RunTest("Incident reads are served from the projection table", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                IncidentService incidents = new IncidentService(testDb.Driver);
                Incident created = await incidents.CreateAsync(McpTestCaller.Operator, new IncidentUpsertRequest { Title = "Projected only", Severity = IncidentSeverityEnum.Critical }).ConfigureAwait(false);
                await incidents.UpdateAsync(McpTestCaller.Operator, created.Id, new IncidentUpsertRequest { Status = IncidentStatusEnum.Mitigated }).ConfigureAwait(false);

                List<ArmadaEvent> snapshots = await testDb.Driver.Events.EnumerateByEntityAsync(IncidentService.IncidentEntityType, created.Id, 100).ConfigureAwait(false);
                AssertEqual(2, snapshots.Count, "each change wrote a snapshot event");
                foreach (ArmadaEvent snapshot in snapshots) await testDb.Driver.Events.DeleteAsync(snapshot.Id).ConfigureAwait(false);

                Incident? read = await incidents.ReadAsync(McpTestCaller.Operator, created.Id).ConfigureAwait(false);
                AssertNotNull(read, "by-id read comes from the table");
                AssertEqual(IncidentStatusEnum.Mitigated, read!.Status);
                AssertEqual(1, (await incidents.EnumerateAsync(McpTestCaller.Operator, new IncidentQuery()).ConfigureAwait(false)).Objects.Count, "list comes from the table");
                AssertEqual(1, (await incidents.EnumerateActiveAsync(McpTestCaller.Operator, new IncidentQuery()).ConfigureAwait(false)).Count, "active list comes from the table");
                AssertEqual(1, (await incidents.CountAsync(McpTestCaller.Operator).ConfigureAwait(false)).ByStatus[IncidentStatusEnum.Mitigated], "counts come from the table");
            }).ConfigureAwait(false);

            await RunTest("Projection reads equal the event projection across list, active, by-id, count and filters", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                IncidentService incidents = new IncidentService(testDb.Driver);
                await EnsureProjectionTenantsAsync(testDb).ConfigureAwait(false);
                AuthContext userA = AuthContext.Authenticated(Constants.DefaultTenantId, UserAId, false, false, "Test");
                AuthContext userB = AuthContext.Authenticated(OtherTenantId, UserBId, false, false, "Test");
                AuthContext tenantAdmin = AuthContext.Authenticated(Constants.DefaultTenantId, UserAId, false, true, "Test");
                AuthContext tenantAdminOther = AuthContext.Authenticated(OtherTenantId, UserBId, false, true, "Test");
                DateTime baseUtc = DateTime.UtcNow.AddDays(-20);

                for (int i = 0; i < 9; i++)
                {
                    AuthContext creator = i < 5 ? McpTestCaller.Operator : (i < 7 ? userA : userB);
                    Incident created = await incidents.CreateAsync(creator, new IncidentUpsertRequest
                    {
                        Title = i == 1 ? "Beta 50%_done regression" : "Incident number " + i,
                        Severity = (IncidentSeverityEnum)(i % Enum.GetValues<IncidentSeverityEnum>().Length),
                        VesselId = i % 2 == 0 ? "vsl_proj_a" : "vsl_Proj_B",
                        MissionId = "msn_proj_" + (i % 3),
                        DeploymentId = null,
                        EnvironmentName = i == 2 ? "Staging East" : null,
                        Postmortem = i == 3 ? "unique postmortem text" : null,
                        DetectedUtc = baseUtc.AddDays(i)
                    }).ConfigureAwait(false);
                    await incidents.UpdateAutomaticallyAsync(creator, created.Id, new IncidentUpsertRequest { Summary = "first revision " + i }).ConfigureAwait(false);
                    await incidents.UpdateAutomaticallyAsync(creator, created.Id, new IncidentUpsertRequest { Status = IncidentStatusEnum.Mitigated, Impact = "impact " + i }).ConfigureAwait(false);
                    if (i % 3 == 0)
                        await incidents.UpdateAutomaticallyAsync(creator, created.Id, new IncidentUpsertRequest { Status = IncidentStatusEnum.Closed }).ConfigureAwait(false);
                    else if (i % 3 == 1)
                        await incidents.UpdateAutomaticallyAsync(creator, created.Id, new IncidentUpsertRequest { Status = IncidentStatusEnum.Open }).ConfigureAwait(false);
                }

                // Incidents that share one update time are ordered by identifier.
                DateTime tied = DateTime.UtcNow.AddMinutes(1);
                for (int i = 0; i < 4; i++)
                    await WriteDirectSnapshotAsync(testDb, "incident-projection-tie_" + (char)('a' + i), tied, IncidentStatusEnum.Open, null).ConfigureAwait(false);

                Func<IncidentQuery>[] queries =
                {
                    () => new IncidentQuery(),
                    () => new IncidentQuery { Status = IncidentStatusEnum.Open },
                    () => new IncidentQuery { Status = IncidentStatusEnum.Closed },
                    () => new IncidentQuery { Severity = IncidentSeverityEnum.Critical },
                    () => new IncidentQuery { VesselId = "VSL_PROJ_B" },
                    () => new IncidentQuery { MissionId = "msn_proj_1" },
                    () => new IncidentQuery { MissionId = "msn_proj_2", Status = IncidentStatusEnum.Mitigated },
                    () => new IncidentQuery { Search = "50%_done" },
                    () => new IncidentQuery { Search = "UNIQUE postmortem" },
                    () => new IncidentQuery { Search = "staging east" },
                    () => new IncidentQuery { Search = "revision" },
                    () => new IncidentQuery { ExcludeTerminal = true },
                    () => new IncidentQuery { FromUtc = baseUtc.AddDays(2), ToUtc = baseUtc.AddDays(6) },
                    () => new IncidentQuery { OldestFirst = true },
                    () => new IncidentQuery { PageNumber = 2, PageSize = 4 },
                    () => new IncidentQuery { PageNumber = 3, PageSize = 4, OldestFirst = true },
                    () => new IncidentQuery { PageNumber = 1, PageSize = 2000 }
                };

                foreach (AuthContext auth in new[] { McpTestCaller.Operator, tenantAdmin, tenantAdminOther, userA, userB })
                {
                    List<Incident> expectedAll = await ReferenceProjectionAsync(testDb, auth).ConfigureAwait(false);
                    foreach (Func<IncidentQuery> factory in queries)
                    {
                        IncidentQuery query = factory();
                        string label = auth.UserId + "/" + auth.IsAdmin + "/" + auth.IsTenantAdmin + " " + JsonSerializer.Serialize(query);
                        EnumerationResult<Incident> actual = await incidents.EnumerateAsync(auth, factory()).ConfigureAwait(false);
                        EnumerationResult<Incident> expected = ReferenceEnumerate(expectedAll, query);
                        AssertEqual(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual), "list " + label);
                    }

                    List<Incident> activeActual = await incidents.EnumerateActiveAsync(auth, new IncidentQuery { VesselId = "vsl_proj_a" }).ConfigureAwait(false);
                    IncidentQuery activeQuery = new IncidentQuery { VesselId = "vsl_proj_a", ExcludeTerminal = true, PageSize = 500 };
                    AssertEqual(JsonSerializer.Serialize(ReferenceEnumerate(expectedAll, activeQuery).Objects), JsonSerializer.Serialize(activeActual), "active " + auth.UserId);

                    foreach (Incident expectedIncident in expectedAll)
                        AssertEqual(JsonSerializer.Serialize(expectedIncident), JsonSerializer.Serialize(await incidents.ReadAsync(auth, expectedIncident.Id).ConfigureAwait(false)), "by-id " + expectedIncident.Id);

                    IncidentCountSummary counts = await incidents.CountAsync(auth).ConfigureAwait(false);
                    AssertEqual(expectedAll.Count, counts.ByStatus.Values.Sum(), "count total " + auth.UserId);
                    foreach (IncidentStatusEnum status in Enum.GetValues<IncidentStatusEnum>())
                    {
                        int byStatus = expectedAll.Count(item => item.Status == status);
                        counts.ByStatus.TryGetValue(status, out int actualByStatus);
                        AssertEqual(byStatus, actualByStatus, "count " + status + " " + auth.UserId);
                    }
                    foreach (IncidentSeverityEnum severity in Enum.GetValues<IncidentSeverityEnum>())
                    {
                        int open = expectedAll.Count(item => item.Status == IncidentStatusEnum.Open && item.Severity == severity);
                        counts.OpenBySeverity.TryGetValue(severity, out int actualOpen);
                        AssertEqual(open, actualOpen, "open " + severity + " " + auth.UserId);
                    }
                }

                AssertTrue((await ReferenceProjectionAsync(testDb, McpTestCaller.Operator).ConfigureAwait(false)).Count >= 13, "the seed produced the incidents the comparison covers");
            }).ConfigureAwait(false);

            await RunTest("Oldest-first paging by cursor walks incidents that share an update time without repeating or dropping one", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                IncidentService incidents = new IncidentService(testDb.Driver);
                DateTime tied = DateTime.UtcNow.AddMinutes(-5);
                for (int i = 0; i < 7; i++)
                    await WriteDirectSnapshotAsync(testDb, "incident-projection-cursor_" + i, i < 5 ? tied : tied.AddSeconds(1), IncidentStatusEnum.Open, null).ConfigureAwait(false);

                List<string> walked = new List<string>();
                IncidentQuery query = new IncidentQuery { OldestFirst = true, PageSize = 2 };
                while (true)
                {
                    EnumerationResult<Incident> page = await incidents.EnumerateAsync(McpTestCaller.Operator, query).ConfigureAwait(false);
                    if (page.Objects.Count == 0) break;
                    walked.AddRange(page.Objects.Select(item => item.Id));
                    Incident last = page.Objects[page.Objects.Count - 1];
                    query = new IncidentQuery { OldestFirst = true, PageSize = 2, AfterLastUpdateUtc = last.LastUpdateUtc, AfterId = last.Id };
                }

                AssertEqual(7, walked.Count, "every incident is visited once");
                AssertEqual(7, walked.Distinct(StringComparer.Ordinal).Count(), "no incident repeats");
                AssertEqual(String.Join(",", walked.OrderBy(id => id, StringComparer.Ordinal).Take(5)), String.Join(",", walked.Take(5)), "ties break by identifier");
            }).ConfigureAwait(false);

            await RunTest("A snapshot event and its projection row are written in one transaction", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                IncidentService incidents = new IncidentService(testDb.Driver);
                Incident first = await WriteDirectSnapshotAsync(testDb, "incident-projection-atomic_a", DateTime.UtcNow, IncidentStatusEnum.Open, "evt_projection_atomic_1").ConfigureAwait(false);

                Incident other = new Incident { Id = "incident-projection-atomic_b", Title = "Never projected", LastUpdateUtc = DateTime.UtcNow };
                ArmadaEvent clash = new ArmadaEvent(IncidentService.SnapshotEventType, other.Title)
                {
                    Id = "evt_projection_atomic_1",
                    EntityType = IncidentService.IncidentEntityType,
                    EntityId = other.Id,
                    Payload = JsonSerializer.Serialize(other, _ReferenceJson),
                    CreatedUtc = other.LastUpdateUtc
                };
                await AssertThrowsAsync<Exception>(() => testDb.Driver.Incidents.WriteSnapshotAsync(clash, other), "a snapshot whose event cannot be written is refused").ConfigureAwait(false);

                AssertNull(await incidents.ReadAsync(McpTestCaller.Operator, other.Id).ConfigureAwait(false), "no projection row without its event");
                AssertEqual(1, (await incidents.EnumerateAsync(McpTestCaller.Operator, new IncidentQuery()).ConfigureAwait(false)).Objects.Count);
                AssertEqual(first.Id, (await incidents.ReadAsync(McpTestCaller.Operator, first.Id).ConfigureAwait(false))!.Id);

                // A projection write that fails after its event was inserted takes the event back with it.
                using (SqliteConnection connection = new SqliteConnection(testDb.ConnectionString))
                {
                    await connection.OpenAsync().ConfigureAwait(false);
                    using (SqliteCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "CREATE TRIGGER incidents_refuse_insert BEFORE INSERT ON incidents BEGIN SELECT RAISE(ABORT, 'projection refused'); END;";
                        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }

                Incident refused = new Incident { Id = "incident-projection-atomic_c", Title = "Refused projection", LastUpdateUtc = DateTime.UtcNow };
                ArmadaEvent refusedSnapshot = new ArmadaEvent(IncidentService.SnapshotEventType, refused.Title)
                {
                    Id = "evt_projection_atomic_3",
                    EntityType = IncidentService.IncidentEntityType,
                    EntityId = refused.Id,
                    Payload = JsonSerializer.Serialize(refused, _ReferenceJson),
                    CreatedUtc = refused.LastUpdateUtc
                };
                await AssertThrowsAsync<Exception>(() => testDb.Driver.Incidents.WriteSnapshotAsync(refusedSnapshot, refused), "a refused projection write fails the snapshot").ConfigureAwait(false);
                AssertNull(await testDb.Driver.Events.ReadAsync("evt_projection_atomic_3").ConfigureAwait(false), "the event is not left without its projection row");
            }).ConfigureAwait(false);

            await RunTest("A snapshot older than the projected one is recorded and leaves the projection unchanged", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                IncidentService incidents = new IncidentService(testDb.Driver);
                DateTime now = DateTime.UtcNow;
                await WriteDirectSnapshotAsync(testDb, "incident-projection-order", now, IncidentStatusEnum.Mitigated, null).ConfigureAwait(false);
                await WriteDirectSnapshotAsync(testDb, "incident-projection-order", now.AddMinutes(-10), IncidentStatusEnum.Open, null).ConfigureAwait(false);

                AssertEqual(IncidentStatusEnum.Mitigated, (await incidents.ReadAsync(McpTestCaller.Operator, "incident-projection-order").ConfigureAwait(false))!.Status);
                AssertEqual(2, (await testDb.Driver.Events.EnumerateByEntityAsync(IncidentService.IncidentEntityType, "incident-projection-order", 10).ConfigureAwait(false)).Count, "both snapshots stay on record");
            }).ConfigureAwait(false);

            await RunTest("Backfill projects the newest snapshot of each unprojected incident once", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                IncidentService incidents = new IncidentService(testDb.Driver);
                DateTime now = DateTime.UtcNow;
                await SeedEventOnlyAsync(testDb, "incident-backfill-a", now.AddMinutes(-3), IncidentStatusEnum.Open, "evt_backfill_a1").ConfigureAwait(false);
                await SeedEventOnlyAsync(testDb, "incident-backfill-a", now.AddMinutes(-1), IncidentStatusEnum.Closed, "evt_backfill_a3").ConfigureAwait(false);
                await SeedEventOnlyAsync(testDb, "incident-backfill-a", now.AddMinutes(-2), IncidentStatusEnum.Mitigated, "evt_backfill_a9").ConfigureAwait(false);
                await SeedEventOnlyAsync(testDb, "incident-backfill-b", now.AddMinutes(-4), IncidentStatusEnum.Open, "evt_backfill_b1").ConfigureAwait(false);
                await testDb.Driver.Events.CreateAsync(new ArmadaEvent(IncidentService.SnapshotEventType, "Unreadable")
                {
                    EntityType = IncidentService.IncidentEntityType,
                    EntityId = "incident-backfill-bad",
                    Payload = "{ not json"
                }).ConfigureAwait(false);
                AssertEqual(0, (await incidents.EnumerateAsync(McpTestCaller.Operator, new IncidentQuery()).ConfigureAwait(false)).Objects.Count, "events alone are not read");

                IncidentBackfillResult first = await testDb.Driver.Incidents.BackfillFromEventsAsync().ConfigureAwait(false);
                AssertEqual(2, first.Projected);
                AssertEqual("incident-backfill-bad", String.Join(",", first.Unreadable), "an unreadable newest snapshot is named, not skipped silently");
                AssertEqual(IncidentStatusEnum.Closed, (await incidents.ReadAsync(McpTestCaller.Operator, "incident-backfill-a").ConfigureAwait(false))!.Status, "the newest snapshot by time wins, not the last written");
                AssertEqual(IncidentStatusEnum.Open, (await incidents.ReadAsync(McpTestCaller.Operator, "incident-backfill-b").ConfigureAwait(false))!.Status);

                await incidents.UpdateAutomaticallyAsync(McpTestCaller.Operator, "incident-backfill-b", new IncidentUpsertRequest { Status = IncidentStatusEnum.Mitigated }).ConfigureAwait(false);
                IncidentBackfillResult second = await testDb.Driver.Incidents.BackfillFromEventsAsync().ConfigureAwait(false);
                AssertEqual(0, second.Projected, "a second run projects nothing");
                AssertEqual(IncidentStatusEnum.Mitigated, (await incidents.ReadAsync(McpTestCaller.Operator, "incident-backfill-b").ConfigureAwait(false))!.Status, "a second run leaves later changes alone");
            }).ConfigureAwait(false);

            await RunTest("Deleting an incident removes its projection row and every snapshot event, and only for a caller who can see it", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                IncidentService incidents = new IncidentService(testDb.Driver);
                await EnsureProjectionTenantsAsync(testDb).ConfigureAwait(false);
                AuthContext userA = AuthContext.Authenticated(Constants.DefaultTenantId, UserAId, false, false, "Test");
                AuthContext userB = AuthContext.Authenticated(OtherTenantId, UserBId, false, false, "Test");
                Incident owned = await incidents.CreateAsync(userA, new IncidentUpsertRequest { Title = "Owned by A" }).ConfigureAwait(false);
                await incidents.UpdateAsync(userA, owned.Id, new IncidentUpsertRequest { Summary = "second snapshot" }).ConfigureAwait(false);

                await AssertThrowsAsync<InvalidOperationException>(() => incidents.DeleteAsync(userB, owned.Id), "another tenant's user cannot delete it").ConfigureAwait(false);
                AssertEqual(2, (await testDb.Driver.Events.EnumerateByEntityAsync(IncidentService.IncidentEntityType, owned.Id, 10).ConfigureAwait(false)).Count);

                await incidents.DeleteAsync(userA, owned.Id).ConfigureAwait(false);
                AssertNull(await incidents.ReadAsync(McpTestCaller.Operator, owned.Id).ConfigureAwait(false), "the projection row is gone");
                AssertEqual(0, (await testDb.Driver.Events.EnumerateByEntityAsync(IncidentService.IncidentEntityType, owned.Id, 10).ConfigureAwait(false)).Count, "the snapshot events are gone");
                await AssertThrowsAsync<InvalidOperationException>(() => incidents.DeleteAsync(userA, owned.Id), "a deleted incident is not found").ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        private static async Task EnsureProjectionTenantsAsync(TestDatabase testDb)
        {
            if (await testDb.Driver.Tenants.ReadAsync(OtherTenantId).ConfigureAwait(false) == null)
                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata { Id = OtherTenantId, Name = OtherTenantId }).ConfigureAwait(false);
            foreach (KeyValuePair<string, string> user in new[]
            {
                new KeyValuePair<string, string>(UserAId, Constants.DefaultTenantId),
                new KeyValuePair<string, string>(UserBId, OtherTenantId)
            })
            {
                if (await testDb.Driver.Users.ReadByIdAsync(user.Key).ConfigureAwait(false) == null)
                {
                    await testDb.Driver.Users.CreateAsync(new UserMaster
                    {
                        Id = user.Key,
                        TenantId = user.Value,
                        Email = user.Key + "@armada.test",
                        PasswordSha256 = UserMaster.ComputePasswordHash("password"),
                        IsTenantAdmin = true
                    }).ConfigureAwait(false);
                }
            }
        }

        private static async Task<Incident> WriteDirectSnapshotAsync(TestDatabase testDb, string incidentId, DateTime updatedUtc, IncidentStatusEnum status, string? eventId)
        {
            Incident incident = new Incident { Id = incidentId, Title = "Direct " + incidentId, Status = status, LastUpdateUtc = updatedUtc };
            ArmadaEvent snapshot = new ArmadaEvent(IncidentService.SnapshotEventType, incident.Title)
            {
                EntityType = IncidentService.IncidentEntityType,
                EntityId = incident.Id,
                Payload = JsonSerializer.Serialize(incident, _ReferenceJson),
                CreatedUtc = updatedUtc
            };
            if (eventId != null) snapshot.Id = eventId;
            await testDb.Driver.Incidents.WriteSnapshotAsync(snapshot, incident).ConfigureAwait(false);
            return incident;
        }

        private static async Task SeedEventOnlyAsync(TestDatabase testDb, string incidentId, DateTime updatedUtc, IncidentStatusEnum status, string eventId)
        {
            Incident incident = new Incident { Id = incidentId, Title = "Event only " + incidentId, Status = status, LastUpdateUtc = updatedUtc };
            await testDb.Driver.Events.CreateAsync(new ArmadaEvent(IncidentService.SnapshotEventType, incident.Title)
            {
                Id = eventId,
                EntityType = IncidentService.IncidentEntityType,
                EntityId = incident.Id,
                Payload = JsonSerializer.Serialize(incident, _ReferenceJson),
                CreatedUtc = updatedUtc
            }).ConfigureAwait(false);
        }

        // The incidents a caller sees as the snapshot events record them: the newest snapshot of each incident the
        // caller's events can see, read from every snapshot event.
        private static async Task<List<Incident>> ReferenceProjectionAsync(TestDatabase testDb, AuthContext auth)
        {
            bool operatorScope = String.IsNullOrWhiteSpace(auth.MissionId);
            List<ArmadaEvent> events = new List<ArmadaEvent>();
            EnumerationQuery page = new EnumerationQuery { PageNumber = 1, PageSize = 500, EventType = IncidentService.SnapshotEventType };
            while (true)
            {
                EnumerationResult<ArmadaEvent> result = await testDb.Driver.Events.EnumerateAsync(page).ConfigureAwait(false);
                events.AddRange(result.Objects.Where(item => String.Equals(item.EntityType, IncidentService.IncidentEntityType, StringComparison.OrdinalIgnoreCase)));
                if (result.Objects.Count < page.PageSize) break;
                page.PageNumber++;
            }

            IEnumerable<ArmadaEvent> visible = events;
            if (!(operatorScope && auth.IsAdmin))
            {
                visible = visible.Where(item => item.TenantId == auth.TenantId);
                if (!(operatorScope && auth.IsTenantAdmin)) visible = visible.Where(item => item.UserId == auth.UserId);
            }

            return visible
                .GroupBy(item => item.EntityId!, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id, StringComparer.Ordinal).First())
                .Select(snapshot =>
                {
                    Incident incident = JsonSerializer.Deserialize<Incident>(snapshot.Payload!, _ReferenceJson)!;
                    incident.TenantId = incident.TenantId ?? snapshot.TenantId;
                    incident.UserId = incident.UserId ?? snapshot.UserId;
                    incident.LastUpdateUtc = incident.LastUpdateUtc == default ? snapshot.CreatedUtc : incident.LastUpdateUtc;
                    if (incident.OpenedReason == null && incident.RootCauseWrittenBy == null) incident.OpenedReason = incident.RootCause;
                    return incident;
                })
                .ToList();
        }

        private static EnumerationResult<Incident> ReferenceEnumerate(List<Incident> all, IncidentQuery query)
        {
            IEnumerable<Incident> filtered = all;
            if (query.FromUtc.HasValue) filtered = filtered.Where(item => item.DetectedUtc >= query.FromUtc.Value.ToUniversalTime());
            if (query.ToUtc.HasValue) filtered = filtered.Where(item => item.DetectedUtc <= query.ToUtc.Value.ToUniversalTime());
            if (!String.IsNullOrWhiteSpace(query.VesselId)) filtered = filtered.Where(item => String.Equals(item.VesselId, query.VesselId, StringComparison.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.MissionId)) filtered = filtered.Where(item => String.Equals(item.MissionId, query.MissionId, StringComparison.OrdinalIgnoreCase));
            if (query.Status.HasValue) filtered = filtered.Where(item => item.Status == query.Status.Value);
            if (query.Severity.HasValue) filtered = filtered.Where(item => item.Severity == query.Severity.Value);
            if (!String.IsNullOrWhiteSpace(query.Search))
            {
                string search = query.Search.Trim();
                filtered = filtered.Where(item => new[] { item.Title, item.Summary, item.Impact, item.RootCause, item.RecoveryNotes, item.Postmortem, item.EnvironmentName }
                    .Any(value => !String.IsNullOrWhiteSpace(value) && value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));
            }
            if (query.ExcludeTerminal) filtered = filtered.Where(item => item.Status != IncidentStatusEnum.Closed && item.Status != IncidentStatusEnum.RolledBack);

            List<Incident> ordered = query.OldestFirst
                ? filtered.OrderBy(item => item.LastUpdateUtc).ThenBy(item => item.Id, StringComparer.Ordinal).ToList()
                : filtered.OrderByDescending(item => item.LastUpdateUtc).ThenByDescending(item => item.Id, StringComparer.Ordinal).ToList();
            return EnumerationResult<Incident>.Page(ordered, query.PageNumber, query.PageSize);
        }
    }
}
