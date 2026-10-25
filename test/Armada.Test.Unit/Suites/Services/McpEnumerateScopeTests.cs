namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// armada_enumerate lists, for every entity type, only the records the caller may read under that
    /// entity's per-id read rule: a global administrator every record, a tenant administrator its own
    /// tenant's records, and any other caller its own records. A mission caller lists as its owner without
    /// the owner's administrator rights. An argument the tool does not declare is refused by name.
    /// </summary>
    public class McpEnumerateScopeTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "MCP enumerate scope";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Every entity listing hides cross-tenant and cross-owner records from a mission caller and keeps the operator listing whole", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    TenantMetadata otherTenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("enumerate scope other tenant")).ConfigureAwait(false);
                    UserMaster foreignUser = await testDb.Driver.Users.CreateAsync(new UserMaster(otherTenant.Id, "enumerate-foreign@example.com", "password")).ConfigureAwait(false);
                    UserMaster otherUser = await testDb.Driver.Users.CreateAsync(new UserMaster(Constants.DefaultTenantId, "enumerate-other@example.com", "password")).ConfigureAwait(false);
                    Vessel dockVessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("dock host", "https://example.com/dock-host.git")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId
                    }).ConfigureAwait(false);

                    List<ScopedEntity> entities = BuildEntities(testDb, dockVessel.Id);
                    Dictionary<string, ScopedRecords> records = new Dictionary<string, ScopedRecords>(StringComparer.Ordinal);
                    foreach (ScopedEntity entity in entities)
                    {
                        records[entity.EntityType] = new ScopedRecords
                        {
                            Own = await entity.Create(Constants.DefaultTenantId, Constants.DefaultUserId, entity.EntityType + "-own").ConfigureAwait(false),
                            OtherOwner = await entity.Create(Constants.DefaultTenantId, otherUser.Id, entity.EntityType + "-other-owner").ConfigureAwait(false),
                            OtherTenant = await entity.Create(otherTenant.Id, foreignUser.Id, entity.EntityType + "-other-tenant").ConfigureAwait(false)
                        };
                    }

                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                    McpEnumerateTools.Register((name, _, _, handler) => { handlers[name] = handler; }, testDb.Driver);
                    Func<JsonElement?, Task<object>> enumerate = handlers["armada_enumerate"];

                    AuthContext missionCaller = McpTestCaller.Operator;
                    missionCaller.MissionId = "msn_enumerate_scope_reader";
                    AuthContext tenantAdmin = AuthContext.Authenticated(Constants.DefaultTenantId, otherUser.Id, false, true, "Test");

                    foreach (ScopedEntity entity in entities)
                    {
                        ScopedRecords created = records[entity.EntityType];

                        string asMission = await ListAsync(enumerate, missionCaller, entity.EntityType).ConfigureAwait(false);
                        AssertContains(created.Own, asMission, entity.EntityType + ": a mission caller lists its own record");
                        AssertFalse(asMission.Contains(created.OtherTenant, StringComparison.Ordinal),
                            entity.EntityType + ": a mission caller never lists another tenant's record");
                        if (entity.TenantWide)
                            AssertContains(created.OtherOwner, asMission, entity.EntityType + ": a tenant-wide record is listed to every caller in its tenant, as its per-id read allows");
                        else
                            AssertFalse(asMission.Contains(created.OtherOwner, StringComparison.Ordinal),
                                entity.EntityType + ": a mission caller never lists another owner's record, even when its owner is an administrator");

                        string asTenantAdmin = await ListAsync(enumerate, tenantAdmin, entity.EntityType).ConfigureAwait(false);
                        AssertContains(created.Own, asTenantAdmin, entity.EntityType + ": a tenant administrator lists its tenant's records");
                        AssertContains(created.OtherOwner, asTenantAdmin, entity.EntityType + ": a tenant administrator lists every owner in its tenant");
                        AssertFalse(asTenantAdmin.Contains(created.OtherTenant, StringComparison.Ordinal),
                            entity.EntityType + ": a tenant administrator never lists another tenant's record");

                        string asOperator = await ListAsync(enumerate, McpTestCaller.Operator, entity.EntityType).ConfigureAwait(false);
                        foreach (string id in new[] { created.Own, created.OtherOwner, created.OtherTenant })
                            AssertContains(id, asOperator, entity.EntityType + ": the operator listing is unchanged");
                    }
                }
            });

            await RunTest("Incident date windows filter detection time before paging and retain other filters", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                IncidentService incidents = new IncidentService(testDb.Driver);
                DateTime from = new DateTime(2026, 2, 3, 4, 5, 0, DateTimeKind.Utc);
                DateTime to = from.AddMinutes(1);
                List<Incident> created = new List<Incident>();
                foreach (var row in new[]
                {
                    (Title: "old incident", Detected: from.AddTicks(-1), Status: IncidentStatusEnum.Open),
                    (Title: "lower boundary", Detected: from, Status: IncidentStatusEnum.Open),
                    (Title: "upper boundary", Detected: to, Status: IncidentStatusEnum.Open),
                    (Title: "new incident", Detected: to.AddTicks(1), Status: IncidentStatusEnum.Open),
                    (Title: "mitigated incident", Detected: from, Status: IncidentStatusEnum.Mitigated)
                })
                    created.Add(await incidents.CreateAsync(McpTestCaller.Operator,
                        new IncidentUpsertRequest { Title = row.Title, DetectedUtc = row.Detected, Status = row.Status }).ConfigureAwait(false));
                await incidents.UpdateAsync(McpTestCaller.Operator, created[0].Id,
                    new IncidentUpsertRequest { Title = "old incident updated recently" }).ConfigureAwait(false);

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                McpEnumerateTools.Register((name, _, _, handler) => { handlers[name] = handler; }, testDb.Driver);
                async Task<EnumerationResult<Incident>> QueryAsync(DateTime? after, DateTime? before, int page = 1, int size = 50)
                {
                    using (McpCallerContext.Begin(McpTestCaller.Operator))
                        return (EnumerationResult<Incident>)await handlers["armada_enumerate"](JsonSerializer.SerializeToElement(new
                        { entityType = "incidents", createdAfter = after, createdBefore = before, status = "Open", pageNumber = page, pageSize = size })).ConfigureAwait(false);
                }
                EnumerationResult<Incident> bounded = await QueryAsync(from, to).ConfigureAwait(false);
                AssertEqual(2L, bounded.TotalRecords, "both detection boundaries are inclusive; snapshots and mitigated rows are excluded");
                AssertTrue(bounded.Objects.Select(item => item.Id).ToHashSet().SetEquals(new[] { created[1].Id, created[2].Id }));
                AssertEqual(3L, (await QueryAsync(from, null).ConfigureAwait(false)).TotalRecords, "lower bound alone applies");
                AssertEqual(3L, (await QueryAsync(null, to).ConfigureAwait(false)).TotalRecords, "upper bound alone applies");
                EnumerationResult<Incident> first = await QueryAsync(from, to, 1, 1).ConfigureAwait(false);
                EnumerationResult<Incident> second = await QueryAsync(from, to, 2, 1).ConfigureAwait(false);
                AssertEqual(2L, first.TotalRecords);
                AssertEqual(2L, second.TotalRecords);
                AssertEqual(1, first.Objects.Count);
                AssertEqual(1, second.Objects.Count);
                AssertFalse(first.Objects[0].Id == second.Objects[0].Id, "paging occurs after filtering");
                EnumerationResult<Incident> empty = await QueryAsync(to.AddDays(1), null).ConfigureAwait(false);
                AssertEqual(0L, empty.TotalRecords);
                AssertEqual(0, empty.Objects.Count);
            });

            await RunTest("An undeclared argument is refused by name instead of listing unfiltered, and the declared status filter applies", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission running = await testDb.Driver.Missions.CreateAsync(new Mission("running")
                    {
                        TenantId = Constants.DefaultTenantId,
                        Status = MissionStatusEnum.InProgress
                    }).ConfigureAwait(false);
                    Mission waiting = await testDb.Driver.Missions.CreateAsync(new Mission("waiting")
                    {
                        TenantId = Constants.DefaultTenantId,
                        Status = MissionStatusEnum.Pending
                    }).ConfigureAwait(false);

                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                    McpEnumerateTools.Register((name, _, _, handler) => { handlers[name] = McpTestCaller.Wrap(handler); }, testDb.Driver);

                    string refused = JsonSerializer.Serialize(await handlers["armada_enumerate"](JsonSerializer.SerializeToElement(new
                    {
                        entityType = "missions",
                        statuses = new[] { "InProgress" }
                    })).ConfigureAwait(false));
                    AssertContains("Unknown argument", refused, "an undeclared argument is refused");
                    AssertContains("statuses", refused, "the refusal names the undeclared argument");
                    AssertFalse(refused.Contains(waiting.Id, StringComparison.Ordinal), "a refused call lists nothing");

                    string filtered = JsonSerializer.Serialize(await handlers["armada_enumerate"](JsonSerializer.SerializeToElement(new
                    {
                        entityType = "missions",
                        status = "InProgress"
                    })).ConfigureAwait(false));
                    AssertContains(running.Id, filtered, "the status filter keeps the matching mission");
                    AssertFalse(filtered.Contains(waiting.Id, StringComparison.Ordinal), "the status filter drops the other mission");
                }
            });
        }

        private static async Task<string> ListAsync(Func<JsonElement?, Task<object>> enumerate, AuthContext caller, string entityType)
        {
            using (McpCallerContext.Begin(caller))
            {
                object page = await enumerate(JsonSerializer.SerializeToElement(new { entityType, pageSize = 1000 })).ConfigureAwait(false);
                return JsonSerializer.Serialize(page);
            }
        }

        private static List<ScopedEntity> BuildEntities(TestDatabase testDb, string dockVesselId)
        {
            return new List<ScopedEntity>
            {
                new ScopedEntity("fleets", false, async (tenantId, userId, label) =>
                    (await testDb.Driver.Fleets.CreateAsync(new Fleet(label) { TenantId = tenantId, UserId = userId }).ConfigureAwait(false)).Id),
                new ScopedEntity("vessels", false, async (tenantId, userId, label) =>
                    (await testDb.Driver.Vessels.CreateAsync(new Vessel(label, "https://example.com/" + label + ".git") { TenantId = tenantId, UserId = userId }).ConfigureAwait(false)).Id),
                new ScopedEntity("captains", false, async (tenantId, userId, label) =>
                    (await testDb.Driver.Captains.CreateAsync(new Captain(label) { TenantId = tenantId, UserId = userId }).ConfigureAwait(false)).Id),
                new ScopedEntity("docks", false, async (tenantId, userId, label) =>
                    (await testDb.Driver.Docks.CreateAsync(new Dock(dockVesselId) { TenantId = tenantId, UserId = userId }).ConfigureAwait(false)).Id),
                new ScopedEntity("signals", false, async (tenantId, userId, label) =>
                    (await testDb.Driver.Signals.CreateAsync(new Signal(SignalTypeEnum.Mail, label) { TenantId = tenantId, UserId = userId }).ConfigureAwait(false)).Id),
                new ScopedEntity("events", false, async (tenantId, userId, label) =>
                    (await testDb.Driver.Events.CreateAsync(new ArmadaEvent("test.enumerate_scope", label) { TenantId = tenantId, UserId = userId }).ConfigureAwait(false)).Id),
                new ScopedEntity("merge_queue", false, async (tenantId, userId, label) =>
                    (await testDb.Driver.MergeEntries.CreateAsync(new MergeEntry("branch-" + label) { TenantId = tenantId, UserId = userId }).ConfigureAwait(false)).Id),
                new ScopedEntity("playbooks", true, async (tenantId, userId, label) =>
                    (await testDb.Driver.Playbooks.CreateAsync(new Playbook(label + ".md", "content " + label) { TenantId = tenantId, UserId = userId }).ConfigureAwait(false)).Id),
                new ScopedEntity("checks", false, async (tenantId, userId, label) =>
                    (await testDb.Driver.CheckRuns.CreateAsync(new CheckRun { TenantId = tenantId, UserId = userId, Label = label, Command = "echo " + label }).ConfigureAwait(false)).Id),
                new ScopedEntity("releases", false, async (tenantId, userId, label) =>
                    (await testDb.Driver.Releases.CreateAsync(new Release { TenantId = tenantId, UserId = userId, Title = label }).ConfigureAwait(false)).Id),
                new ScopedEntity("incidents", false, async (tenantId, userId, label) =>
                    (await new IncidentService(testDb.Driver).CreateAsync(
                        AuthContext.Authenticated(tenantId, userId, false, false, "Test"),
                        new IncidentUpsertRequest { Title = label }).ConfigureAwait(false)).Id),
                new ScopedEntity("deployments", false, async (tenantId, userId, label) =>
                    (await testDb.Driver.Deployments.CreateAsync(new Deployment { TenantId = tenantId, UserId = userId, Title = label }).ConfigureAwait(false)).Id)
            };
        }

        private sealed class ScopedEntity
        {
            public ScopedEntity(string entityType, bool tenantWide, Func<string, string, string, Task<string>> create)
            {
                EntityType = entityType;
                TenantWide = tenantWide;
                Create = create;
            }

            public string EntityType { get; }

            public bool TenantWide { get; }

            public Func<string, string, string, Task<string>> Create { get; }
        }

        private sealed class ScopedRecords
        {
            public string Own { get; set; } = "";

            public string OtherOwner { get; set; } = "";

            public string OtherTenant { get; set; } = "";
        }
    }
}
