#nullable enable

namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// The incident projection on each provider: a snapshot and its projection row are written together, reads
    /// filter, order and page in the database, the owner scope limits what a caller reads, and a backfill projects
    /// incidents that have snapshot events and no row.
    /// </summary>
    internal sealed class IncidentProjectionDatabaseTests
    {
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };

        private readonly DatabaseDriver _Driver;
        private readonly bool _NoCleanup;

        internal IncidentProjectionDatabaseTests(DatabaseDriver driver, bool noCleanup)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _NoCleanup = noCleanup;
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string run = Guid.NewGuid().ToString("N").Substring(0, 12);
            string tenantId = (await fixture.CreateTenantAsync("incproj-tenant", token: token).ConfigureAwait(false)).Id;
            string otherTenantId = (await fixture.CreateTenantAsync("incproj-other", token: token).ConfigureAwait(false)).Id;
            string userA = (await fixture.CreateUserAsync(tenantId, "incproj-a", token: token).ConfigureAwait(false)).Id;
            string userB = (await fixture.CreateUserAsync(tenantId, "incproj-b", token: token).ConfigureAwait(false)).Id;
            string otherUser = (await fixture.CreateUserAsync(otherTenantId, "incproj-other", token: token).ConfigureAwait(false)).Id;
            string vesselId = "vsl_incproj_" + run;
            DateTime tied = new DateTime(2031, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            List<string> ids = new List<string>();
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    string id = "incident-proj-" + run + "_" + i;
                    ids.Add(id);
                    await WriteAsync(id, tenantId, userA, i == 3 ? IncidentStatusEnum.Closed : IncidentStatusEnum.Open, IncidentSeverityEnum.High, vesselId,
                        i == 1 ? "50%_done [x]!" : "Incident " + i, tied, "evt_proj_" + run + "_" + i, token).ConfigureAwait(false);
                }

                string otherId = "incident-proj-" + run + "_other";
                ids.Add(otherId);
                await WriteAsync(otherId, otherTenantId, otherUser, IncidentStatusEnum.Open, IncidentSeverityEnum.Low, null, "Other tenant", tied.AddSeconds(5), "evt_proj_" + run + "_other", token).ConfigureAwait(false);

                // A newer snapshot replaces the row; an older one is recorded and leaves it.
                await WriteAsync(ids[0], tenantId, userA, IncidentStatusEnum.Mitigated, IncidentSeverityEnum.High, vesselId, "Incident 0", tied.AddSeconds(1), "evt_proj_" + run + "_0b", token).ConfigureAwait(false);
                await WriteAsync(ids[0], tenantId, userA, IncidentStatusEnum.Open, IncidentSeverityEnum.High, vesselId, "Incident 0", tied.AddSeconds(-60), "evt_proj_" + run + "_0a", token).ConfigureAwait(false);
                Incident? read = await _Driver.Incidents.ReadAsync(IncidentReadScope.Tenant(tenantId), ids[0], token).ConfigureAwait(false);
                DatabaseAssert.True(read != null && read.Status == IncidentStatusEnum.Mitigated, "the newest snapshot is the projected state");

                IncidentReadScope scope = IncidentReadScope.Tenant(tenantId);
                DatabaseAssert.True((await _Driver.Incidents.ReadAsync(IncidentReadScope.Tenant(otherTenantId), ids[1], token).ConfigureAwait(false)) == null, "another tenant cannot read the incident");
                DatabaseAssert.True((await _Driver.Incidents.ReadAsync(IncidentReadScope.User(tenantId, userB), ids[1], token).ConfigureAwait(false)) == null, "another user cannot read the incident");
                DatabaseAssert.True((await _Driver.Incidents.ReadAsync(IncidentReadScope.User(tenantId, userA), ids[1], token).ConfigureAwait(false)) != null, "the owner reads the incident");

                EnumerationResult<Incident> open = await _Driver.Incidents.EnumerateAsync(scope, new IncidentQuery { ExcludeTerminal = true, VesselId = vesselId.ToUpperInvariant() }, token).ConfigureAwait(false);
                DatabaseAssert.True(open.TotalRecords == 4, "the active list excludes the closed incident and ignores id case (" + open.TotalRecords + ")");
                EnumerationResult<Incident> wildcard = await _Driver.Incidents.EnumerateAsync(scope, new IncidentQuery { Search = "50%_done [x]!" }, token).ConfigureAwait(false);
                DatabaseAssert.True(wildcard.TotalRecords == 1 && wildcard.Objects[0].Id == ids[1], "a search treats wildcard characters as text");

                List<string> walked = new List<string>();
                IncidentQuery cursor = new IncidentQuery { OldestFirst = true, PageSize = 2 };
                while (true)
                {
                    EnumerationResult<Incident> page = await _Driver.Incidents.EnumerateAsync(scope, cursor, token).ConfigureAwait(false);
                    if (page.Objects.Count == 0) break;
                    walked.AddRange(page.Objects.Select(item => item.Id));
                    Incident last = page.Objects[page.Objects.Count - 1];
                    cursor = new IncidentQuery { OldestFirst = true, PageSize = 2, AfterLastUpdateUtc = last.LastUpdateUtc, AfterId = last.Id };
                }
                DatabaseAssert.True(walked.Count == 5 && walked.Distinct(StringComparer.Ordinal).Count() == 5, "a cursor walk visits each incident once (" + walked.Count + ")");

                List<string> pages = new List<string>();
                for (int number = 1; number <= 3; number++)
                    pages.AddRange((await _Driver.Incidents.EnumerateAsync(scope, new IncidentQuery { PageNumber = number, PageSize = 2 }, token).ConfigureAwait(false)).Objects.Select(item => item.Id));
                DatabaseAssert.True(pages.Count == 5 && pages.Distinct(StringComparer.Ordinal).Count() == 5, "numbered pages neither repeat nor drop an incident");

                IncidentCountSummary counts = await _Driver.Incidents.CountAsync(scope, token).ConfigureAwait(false);
                DatabaseAssert.True(counts.ByStatus.Values.Sum() == 5 && counts.ByStatus[IncidentStatusEnum.Closed] == 1 && counts.OpenBySeverity[IncidentSeverityEnum.High] == 3, "counts group by status and by open severity");

                // A snapshot whose event cannot be written leaves no projection row.
                string clashId = "incident-proj-" + run + "_clash";
                bool refused = false;
                try
                {
                    await WriteAsync(clashId, tenantId, userA, IncidentStatusEnum.Open, IncidentSeverityEnum.High, null, "Clash", tied, "evt_proj_" + run + "_0", token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    refused = true;
                }
                DatabaseAssert.True(refused && await _Driver.Incidents.ReadAsync(IncidentReadScope.All(), clashId, token).ConfigureAwait(false) == null, "a refused snapshot event leaves no projection row");

                // Backfill projects an incident that has events and no row, once.
                string backfillId = "incident-proj-" + run + "_backfill";
                ids.Add(backfillId);
                Incident legacy = new Incident { Id = backfillId, TenantId = tenantId, UserId = userA, Title = "Event only", Status = IncidentStatusEnum.Mitigated, LastUpdateUtc = tied };
                await _Driver.Events.CreateAsync(new ArmadaEvent(IncidentService.SnapshotEventType, legacy.Title)
                {
                    Id = "evt_proj_" + run + "_legacy",
                    TenantId = tenantId,
                    UserId = userA,
                    EntityType = IncidentService.IncidentEntityType,
                    EntityId = backfillId,
                    Payload = JsonSerializer.Serialize(legacy, _Json),
                    CreatedUtc = tied
                }, token).ConfigureAwait(false);
                IncidentBackfillResult first = await _Driver.Incidents.BackfillFromEventsAsync(token).ConfigureAwait(false);
                IncidentBackfillResult second = await _Driver.Incidents.BackfillFromEventsAsync(token).ConfigureAwait(false);
                DatabaseAssert.True(first.Projected >= 1 && second.Projected == 0, "the backfill projects once");
                DatabaseAssert.True((await _Driver.Incidents.ReadAsync(scope, backfillId, token).ConfigureAwait(false))?.Status == IncidentStatusEnum.Mitigated, "the backfilled incident reads from the table");

                DatabaseAssert.True(await _Driver.Incidents.DeleteAsync(scope, ids[2], token).ConfigureAwait(false), "an incident the scope sees is deleted");
                DatabaseAssert.True(!await _Driver.Incidents.DeleteAsync(scope, ids[2], token).ConfigureAwait(false), "a deleted incident is not found");
                DatabaseAssert.True((await _Driver.Events.EnumerateByEntityAsync(IncidentService.IncidentEntityType, ids[2], 10, token).ConfigureAwait(false)).Count == 0, "the snapshot events go with the incident");
            }
            finally
            {
                if (!_NoCleanup)
                {
                    foreach (string id in ids) await _Driver.Incidents.DeleteAsync(IncidentReadScope.All(), id, token).ConfigureAwait(false);
                }
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task WriteAsync(string id, string tenantId, string userId, IncidentStatusEnum status, IncidentSeverityEnum severity, string? vesselId, string title, DateTime updatedUtc, string eventId, CancellationToken token)
        {
            Incident incident = new Incident
            {
                Id = id,
                TenantId = tenantId,
                UserId = userId,
                Title = title,
                Status = status,
                Severity = severity,
                VesselId = vesselId,
                LastUpdateUtc = updatedUtc
            };
            ArmadaEvent snapshot = new ArmadaEvent(IncidentService.SnapshotEventType, title)
            {
                Id = eventId,
                TenantId = tenantId,
                UserId = userId,
                EntityType = IncidentService.IncidentEntityType,
                EntityId = id,
                Payload = JsonSerializer.Serialize(incident, _Json),
                CreatedUtc = updatedUtc
            };
            await _Driver.Incidents.WriteSnapshotAsync(snapshot, incident, token).ConfigureAwait(false);
        }
    }
}
