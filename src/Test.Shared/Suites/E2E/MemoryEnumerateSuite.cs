namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end memory enumeration through the real server: POST /api/v1/memories/enumerate and the GET list with
    /// query parameters. Covers paging boundaries and totals, both order values, every sort column, date ranges,
    /// vessel/voyage/mission filters, search/type/topic and the column filters, combined filters, LIKE wildcard
    /// escaping, visibility scoping across users and tenants, the unchanged default order, and error parity with
    /// POST /api/v1/missions/enumerate. Every case seeds its own tenant so totals are exact.
    /// </summary>
    public sealed class MemoryEnumerateSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "E2E.MemoryEnumerate";
        private const string EnumeratePath = "/api/v1/memories/enumerate";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("paging_boundaries_and_totals", "Pages split the tenant's memories with exact totals; a page past the end is empty", async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                E2ETenantUser admin = await E2ETenantUser.CreateAsync(fx.AuthClient, "mem-page", true).ConfigureAwait(false);
                using HttpClient a = admin.CreateClient(fx.BaseUrl);
                List<Memory> seeded = await SeedSequenceAsync(a, 7, "page").ConfigureAwait(false);

                EnumerationResult<Memory> first = await EnumerateAsync(a, new { PageNumber = 1, PageSize = 3, Order = "CreatedAscending" }).ConfigureAwait(false);
                AssertEqual(7L, first.TotalRecords, "total records");
                AssertEqual(3, first.TotalPages, "total pages");
                AssertEqual(1, first.PageNumber, "page number");
                AssertEqual(3, first.PageSize, "page size");
                AssertEqual(String.Join(",", seeded.Take(3).Select(m => m.Id)), Ids(first), "first page");

                EnumerationResult<Memory> last = await EnumerateAsync(a, new { PageNumber = 3, PageSize = 3, Order = "CreatedAscending" }).ConfigureAwait(false);
                AssertEqual(seeded[6].Id, Ids(last), "last page holds the remainder");

                EnumerationResult<Memory> past = await EnumerateAsync(a, new { PageNumber = 4, PageSize = 3 }).ConfigureAwait(false);
                AssertEqual(0, past.Objects.Count, "page past the end");
                AssertEqual(7L, past.TotalRecords, "total past the end");

                EnumerationResult<Memory> exact = await EnumerateAsync(a, new { PageNumber = 1, PageSize = 7 }).ConfigureAwait(false);
                AssertEqual(1, exact.TotalPages, "exact fit is one page");
                AssertEqual(7, exact.Objects.Count, "exact fit rows");

                EnumerationResult<Memory> empty = await EnumerateAsync(a, new { VesselId = "vsl_none_" + Guid.NewGuid().ToString("N") }).ConfigureAwait(false);
                AssertTrue(empty.Success, "empty result succeeds");
                AssertEqual(0L, empty.TotalRecords, "empty total");
                AssertEqual(0, empty.Objects.Count, "empty rows");
            }));

            cases.Add(CaseAsync("order_values", "Order CreatedAscending returns oldest first and CreatedDescending newest first, on POST and GET", async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                E2ETenantUser admin = await E2ETenantUser.CreateAsync(fx.AuthClient, "mem-order", true).ConfigureAwait(false);
                using HttpClient a = admin.CreateClient(fx.BaseUrl);
                List<Memory> seeded = await SeedSequenceAsync(a, 4, "order").ConfigureAwait(false);
                string ascending = String.Join(",", seeded.Select(m => m.Id));
                string descending = String.Join(",", Enumerable.Reverse(seeded).Select(m => m.Id));

                AssertEqual(ascending, Ids(await EnumerateAsync(a, new { Order = "CreatedAscending" }).ConfigureAwait(false)), "POST ascending");
                AssertEqual(descending, Ids(await EnumerateAsync(a, new { Order = "CreatedDescending" }).ConfigureAwait(false)), "POST descending");
                AssertEqual(ascending, Ids(await ListAsync(a, "?order=CreatedAscending").ConfigureAwait(false)), "GET ascending");
                AssertEqual(descending, Ids(await ListAsync(a, "?order=CreatedDescending").ConfigureAwait(false)), "GET descending");
                AssertEqual(ascending, Ids(await EnumerateAsync(a, new { Order = "CreatedDescending" }, "?order=CreatedAscending").ConfigureAwait(false)), "querystring overrides body");
            }));

            cases.Add(CaseAsync("default_order_unchanged", "Without order or sortBy, POST, GET, and the MCP enumerate tool keep salience descending, then newest first", async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                E2ETenantUser admin = await E2ETenantUser.CreateAsync(fx.AuthClient, "mem-default", true).ConfigureAwait(false);
                using HttpClient a = admin.CreateClient(fx.BaseUrl);
                string vessel = "vsl_memdefault_" + Guid.NewGuid().ToString("N");
                Memory lowOld = await CreateAsync(a, new { Content = "low old", Salience = 0.2, VesselId = vessel }).ConfigureAwait(false);
                Memory highOld = await CreateAsync(a, new { Content = "high old", Salience = 0.9, VesselId = vessel }).ConfigureAwait(false);
                Memory lowNew = await CreateAsync(a, new { Content = "low new", Salience = 0.2, VesselId = vessel }).ConfigureAwait(false);
                Memory highNew = await CreateAsync(a, new { Content = "high new", Salience = 0.9, VesselId = vessel }).ConfigureAwait(false);
                string expected = String.Join(",", new[] { highNew.Id, highOld.Id, lowNew.Id, lowOld.Id });

                AssertEqual(expected, Ids(await EnumerateAsync(a, new { }).ConfigureAwait(false)), "POST default");
                AssertEqual(expected, Ids(await EnumerateAsync(a, new { PageSize = 25 }).ConfigureAwait(false)), "POST default with paging only");
                AssertEqual(expected, Ids(await ListAsync(a, "").ConfigureAwait(false)), "GET default");
                AssertEqual(expected, Ids(await EnumerateAsync(a, new { SortBy = "Salience" }).ConfigureAwait(false)), "explicit salience sort defaults to descending");
                AssertEqual(String.Join(",", new[] { lowOld.Id, lowNew.Id, highOld.Id, highNew.Id }),
                    Ids(await EnumerateAsync(a, new { SortBy = "Salience", Order = "CreatedAscending" }).ConfigureAwait(false)), "salience ascending, oldest first");

                using (HttpClient mcp = new HttpClient { BaseAddress = fx.McpClient.BaseAddress })
                {
                    mcp.DefaultRequestHeaders.Add("X-Api-Key", fx.ApiKey);
                    string relevance = await McpTestCalls.CallToolAsync(mcp, "enumerate", new { entityType = "memories", vesselId = vessel }).ConfigureAwait(false);
                    AssertEqual(expected, IdsInText(relevance, highNew, highOld, lowNew, lowOld), "MCP enumerate default keeps the relevance order");
                    string created = await McpTestCalls.CallToolAsync(mcp, "enumerate", new { entityType = "memories", vesselId = vessel, order = "CreatedAscending" }).ConfigureAwait(false);
                    AssertEqual(Join(lowOld, highOld, lowNew, highNew), IdsInText(created, highNew, highOld, lowNew, lowOld), "MCP enumerate honors an explicit order");
                }
            }));

            cases.Add(CaseAsync("sort_by_each_column", "SortBy orders by vessel name, type, topic, summary, salience, visibility, created and updated in the Order direction", async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                E2ETenantUser admin = await E2ETenantUser.CreateAsync(fx.AuthClient, "mem-sort", true).ConfigureAwait(false);
                using HttpClient a = admin.CreateClient(fx.BaseUrl);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                string zetaVessel = await CreateVesselAsync(a, "zeta-mem-" + suffix).ConfigureAwait(false);
                string alphaVessel = await CreateVesselAsync(a, "Alpha-mem-" + suffix).ConfigureAwait(false);

                Memory m1 = await CreateAsync(a, new { Content = "content b", Summary = "Charlie", Topic = "beta", Type = "Semantic", Salience = 0.5, Scope = "UserSpecific", VesselId = zetaVessel }).ConfigureAwait(false);
                Memory m2 = await CreateAsync(a, new { Content = "content a", Summary = "", Topic = "Alpha", Type = "Episodic", Salience = 0.9, Scope = "TenantWide", SourceVesselId = alphaVessel }).ConfigureAwait(false);
                Memory m3 = await CreateAsync(a, new { Content = "bravo content", Topic = "gamma", Type = "Procedural", Salience = 0.1, Scope = "TenantWide" }).ConfigureAwait(false);
                await Task.Delay(20).ConfigureAwait(false);
                m1.Salience = 0.6;
                HttpResponseMessage update = await a.PutAsync("/api/v1/memories/" + m1.Id, JsonHelper.ToJsonContent(m1)).ConfigureAwait(false);
                AssertStatusCode(HttpStatusCode.OK, update, "update m1");

                AssertEqual(Join(m3, m2, m1), Ids(await SortAsync(a, "Vessel", true).ConfigureAwait(false)), "vessel ascending (no vessel, Alpha, zeta; case-insensitive)");
                AssertEqual(Join(m1, m2, m3), Ids(await SortAsync(a, "Vessel", false).ConfigureAwait(false)), "vessel descending");
                AssertEqual(Join(m2, m3, m1), Ids(await SortAsync(a, "Type", true).ConfigureAwait(false)), "type ascending");
                AssertEqual(Join(m2, m1, m3), Ids(await SortAsync(a, "Topic", true).ConfigureAwait(false)), "topic ascending (case-insensitive)");
                AssertEqual(Join(m3, m1, m2), Ids(await SortAsync(a, "Topic", false).ConfigureAwait(false)), "topic descending");
                AssertEqual(Join(m3, m1, m2), Ids(await SortAsync(a, "Summary", true).ConfigureAwait(false)), "summary ascending (content when no summary)");
                AssertEqual(Join(m3, m1, m2), Ids(await SortAsync(a, "Salience", true).ConfigureAwait(false)), "salience ascending");
                AssertEqual(Join(m2, m1, m3), Ids(await SortAsync(a, "Salience", false).ConfigureAwait(false)), "salience descending");
                AssertEqual(Join(m2, m3, m1), Ids(await SortAsync(a, "Visibility", true).ConfigureAwait(false)), "visibility ascending, ties oldest first");
                AssertEqual(Join(m1, m2, m3), Ids(await SortAsync(a, "Created", true).ConfigureAwait(false)), "created ascending");
                AssertEqual(Join(m2, m3, m1), Ids(await SortAsync(a, "Updated", true).ConfigureAwait(false)), "updated ascending (m1 updated last)");
                AssertEqual(Join(m1, m3, m2), Ids(await SortAsync(a, "Updated", false).ConfigureAwait(false)), "updated descending");
                AssertEqual(Join(m3, m2, m1), Ids(await ListAsync(a, "?sortBy=Vessel&order=CreatedAscending").ConfigureAwait(false)), "GET sortBy");
            }));

            cases.Add(CaseAsync("date_range", "createdAfter and createdBefore are exclusive bounds on POST and GET", async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                E2ETenantUser admin = await E2ETenantUser.CreateAsync(fx.AuthClient, "mem-dates", true).ConfigureAwait(false);
                using HttpClient a = admin.CreateClient(fx.BaseUrl);
                List<Memory> seeded = await SeedSequenceAsync(a, 4, "dates").ConfigureAwait(false);
                DateTime after = seeded[0].CreatedUtc;
                DateTime before = seeded[3].CreatedUtc;

                EnumerationResult<Memory> between = await EnumerateAsync(a, new { CreatedAfter = after, CreatedBefore = before, Order = "CreatedAscending" }).ConfigureAwait(false);
                AssertEqual(Join(seeded[1], seeded[2]), Ids(between), "between (exclusive)");
                AssertEqual(2L, between.TotalRecords, "between total");

                EnumerationResult<Memory> afterOnly = await EnumerateAsync(a, new { CreatedAfter = seeded[1].CreatedUtc, Order = "CreatedAscending" }).ConfigureAwait(false);
                AssertEqual(Join(seeded[2], seeded[3]), Ids(afterOnly), "after only");

                string query = "?order=CreatedAscending&createdBefore=" + seeded[2].CreatedUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
                AssertEqual(Join(seeded[0], seeded[1]), Ids(await ListAsync(a, query).ConfigureAwait(false)), "GET createdBefore");

                EnumerationResult<Memory> future = await EnumerateAsync(a, new { CreatedAfter = DateTime.UtcNow.AddDays(1) }).ConfigureAwait(false);
                AssertEqual(0L, future.TotalRecords, "nothing after tomorrow");
            }));

            cases.Add(CaseAsync("vessel_voyage_mission_filters", "vesselId matches the vessel or source vessel; voyageId and missionId match the source voyage and mission", async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                E2ETenantUser admin = await E2ETenantUser.CreateAsync(fx.AuthClient, "mem-ids", true).ConfigureAwait(false);
                using HttpClient a = admin.CreateClient(fx.BaseUrl);
                string vessel = "vsl_mem_" + Guid.NewGuid().ToString("N");
                string voyage = "vyg_mem_" + Guid.NewGuid().ToString("N");
                string mission = "msn_mem_" + Guid.NewGuid().ToString("N");
                Memory direct = await CreateAsync(a, new { Content = "direct", VesselId = vessel }).ConfigureAwait(false);
                Memory sourced = await CreateAsync(a, new { Content = "sourced", SourceVesselId = vessel, SourceVoyageId = voyage }).ConfigureAwait(false);
                Memory fromMission = await CreateAsync(a, new { Content = "mission", SourceVoyageId = voyage, SourceMissionId = mission }).ConfigureAwait(false);
                await CreateAsync(a, new { Content = "unrelated" }).ConfigureAwait(false);

                AssertEqual(Join(direct, sourced), Ids(await EnumerateAsync(a, new { VesselId = vessel, Order = "CreatedAscending" }).ConfigureAwait(false)), "vessel filter");
                AssertEqual(Join(sourced, fromMission), Ids(await EnumerateAsync(a, new { VoyageId = voyage, Order = "CreatedAscending" }).ConfigureAwait(false)), "voyage filter");
                AssertEqual(fromMission.Id, Ids(await EnumerateAsync(a, new { MissionId = mission }).ConfigureAwait(false)), "mission filter");
                AssertEqual(sourced.Id, Ids(await EnumerateAsync(a, new { VesselId = vessel, VoyageId = voyage }).ConfigureAwait(false)), "vessel and voyage combined");
                AssertEqual(Join(sourced, fromMission), Ids(await ListAsync(a, "?order=CreatedAscending&voyageId=" + voyage).ConfigureAwait(false)), "GET voyageId");
                AssertEqual(fromMission.Id, Ids(await ListAsync(a, "?missionId=" + mission).ConfigureAwait(false)), "GET missionId");
            }));

            cases.Add(CaseAsync("search_type_topic_and_column_filters", "search, type, topic, topicContains, vesselName, summaryContains, scope and minSalience filter in the database, alone and combined", async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                E2ETenantUser admin = await E2ETenantUser.CreateAsync(fx.AuthClient, "mem-filters", true).ConfigureAwait(false);
                using HttpClient a = admin.CreateClient(fx.BaseUrl);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                string billingVessel = await CreateVesselAsync(a, "Billing-API-" + suffix).ConfigureAwait(false);

                Memory webhook = await CreateAsync(a, new { Content = "Webhooks retry three times", Topic = "Billing", Type = "Semantic", Salience = 0.8, Scope = "TenantWide", VesselId = billingVessel, Tags = new[] { "payments" } }).ConfigureAwait(false);
                Memory deploy = await CreateAsync(a, new { Content = "Deploy with the blue/green script", Summary = "Deploy checklist", Topic = "deployment", Type = "Procedural", Salience = 0.4, Scope = "UserSpecific" }).ConfigureAwait(false);
                Memory tagged = await CreateAsync(a, new { Content = "Release notes go in CHANGELOG", Topic = "billing-docs", Type = "Episodic", Salience = 0.6, Scope = "TenantWide", Tags = new[] { "Webhook-Docs" } }).ConfigureAwait(false);
                Memory percent = await CreateAsync(a, new { Content = "Coverage must stay at 100% for core", Topic = "quality_gate", Type = "Semantic", Salience = 0.3, Scope = "TenantWide" }).ConfigureAwait(false);

                AssertEqual(Join(webhook, tagged), Ids(await FilterAsync(a, new { Search = "WEBHOOK", Order = "CreatedAscending" }).ConfigureAwait(false)), "search matches content and tags case-insensitively");
                AssertEqual(Join(webhook, tagged), Ids(await FilterAsync(a, new { Search = "billing", Order = "CreatedAscending" }).ConfigureAwait(false)), "search matches topic");
                AssertEqual(Join(webhook, percent), Ids(await FilterAsync(a, new { Type = "Semantic", Order = "CreatedAscending" }).ConfigureAwait(false)), "type");
                AssertEqual(webhook.Id, Ids(await FilterAsync(a, new { Topic = "billing" }).ConfigureAwait(false)), "topic is exact (case-insensitive)");
                AssertEqual(Join(webhook, tagged), Ids(await FilterAsync(a, new { TopicContains = "BILL", Order = "CreatedAscending" }).ConfigureAwait(false)), "topicContains");
                AssertEqual(webhook.Id, Ids(await FilterAsync(a, new { VesselName = "billing-api" }).ConfigureAwait(false)), "vesselName joins the vessel");
                AssertEqual(deploy.Id, Ids(await FilterAsync(a, new { SummaryContains = "checklist" }).ConfigureAwait(false)), "summaryContains on the summary");
                AssertEqual(webhook.Id, Ids(await FilterAsync(a, new { SummaryContains = "retry three" }).ConfigureAwait(false)), "summaryContains falls back to content");
                AssertEqual(deploy.Id, Ids(await FilterAsync(a, new { Scope = "UserSpecific" }).ConfigureAwait(false)), "scope");
                AssertEqual(Join(webhook, tagged), Ids(await FilterAsync(a, new { MinSalience = 0.6, Order = "CreatedAscending" }).ConfigureAwait(false)), "minSalience is inclusive");
                AssertEqual(percent.Id, Ids(await FilterAsync(a, new { Search = "%" }).ConfigureAwait(false)), "a literal % is not a wildcard");
                AssertEqual(percent.Id, Ids(await FilterAsync(a, new { TopicContains = "_" }).ConfigureAwait(false)), "a literal _ is not a wildcard");
                AssertEqual(0L, (await FilterAsync(a, new { Search = "%" + "nomatch" }).ConfigureAwait(false)).TotalRecords, "no match");

                EnumerationResult<Memory> combined = await FilterAsync(a, new { Search = "webhook", Type = "Semantic", MinSalience = 0.5, Scope = "TenantWide", VesselName = "API" }).ConfigureAwait(false);
                AssertEqual(webhook.Id, Ids(combined), "combined filters");
                AssertEqual(1L, combined.TotalRecords, "combined total");

                string query = "?type=Procedural&topicContains=deploy&summaryContains=CHECK&scope=UserSpecific&minSalience=0.1&search=green";
                AssertEqual(deploy.Id, Ids(await ListAsync(a, query).ConfigureAwait(false)), "GET passes every memory filter");
                AssertEqual(Join(webhook, percent), Ids(await ListAsync(a, "?type=semantic&order=CreatedAscending").ConfigureAwait(false)), "GET type is the memory type");
                AssertEqual(4L, (await ListAsync(a, "?type=NotAType").ConfigureAwait(false)).TotalRecords, "GET ignores an unknown type like the other list routes");
            }));

            cases.Add(CaseAsync("visibility_scoping", "Users see tenant-wide memories plus their own; other users' personal memories and other tenants never appear", async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                E2ETenantUser admin = await E2ETenantUser.CreateAsync(fx.AuthClient, "mem-vis-admin", true).ConfigureAwait(false);
                E2ETenantUser alice = await E2ETenantUser.CreateAsync(fx.AuthClient, "mem-vis-alice", false, admin.TenantId).ConfigureAwait(false);
                E2ETenantUser bob = await E2ETenantUser.CreateAsync(fx.AuthClient, "mem-vis-bob", false, admin.TenantId).ConfigureAwait(false);
                E2ETenantUser outsider = await E2ETenantUser.CreateAsync(fx.AuthClient, "mem-vis-other", true).ConfigureAwait(false);
                using HttpClient a = admin.CreateClient(fx.BaseUrl);
                using HttpClient al = alice.CreateClient(fx.BaseUrl);
                using HttpClient b = bob.CreateClient(fx.BaseUrl);
                using HttpClient o = outsider.CreateClient(fx.BaseUrl);

                Memory shared = await CreateAsync(a, new { Content = "shared fact", Scope = "TenantWide" }).ConfigureAwait(false);
                Memory adminPrivate = await CreateAsync(a, new { Content = "admin private", Scope = "UserSpecific" }).ConfigureAwait(false);
                Memory alicePrivate = await CreateAsync(al, new { Content = "alice private" }).ConfigureAwait(false);
                Memory bobPrivate = await CreateAsync(b, new { Content = "bob private" }).ConfigureAwait(false);
                Memory foreign = await CreateAsync(o, new { Content = "foreign", Scope = "TenantWide" }).ConfigureAwait(false);
                AssertEqual(ScopeEnum.UserSpecific, alicePrivate.Scope, "a regular user creates personal memories");

                EnumerationResult<Memory> aliceView = await EnumerateAsync(al, new { Order = "CreatedAscending" }).ConfigureAwait(false);
                AssertEqual(Join(shared, alicePrivate), Ids(aliceView), "alice sees tenant-wide plus her own");
                AssertEqual(2L, aliceView.TotalRecords, "alice total counts only visible rows");
                AssertEqual(Join(shared, bobPrivate), Ids(await ListAsync(b, "?order=CreatedAscending").ConfigureAwait(false)), "bob sees tenant-wide plus his own (GET)");
                AssertEqual(0L, (await EnumerateAsync(al, new { Search = "bob private" }).ConfigureAwait(false)).TotalRecords, "alice cannot find bob's personal memory");
                AssertEqual(Join(shared, alicePrivate), Ids(await EnumerateAsync(al, new { UserId = bob.UserId, TenantId = outsider.TenantId, Order = "CreatedAscending" }).ConfigureAwait(false)), "a regular user cannot widen scope with userId or tenantId");

                AssertEqual(Join(shared, adminPrivate, alicePrivate, bobPrivate), Ids(await EnumerateAsync(a, new { Order = "CreatedAscending" }).ConfigureAwait(false)), "tenant admin sees the whole tenant");
                AssertEqual(alicePrivate.Id, Ids(await EnumerateAsync(a, new { UserId = alice.UserId }).ConfigureAwait(false)), "tenant admin narrows to one owner");
                AssertEqual(Join(shared, adminPrivate, alicePrivate, bobPrivate), Ids(await EnumerateAsync(a, new { TenantId = outsider.TenantId, Order = "CreatedAscending" }).ConfigureAwait(false)), "tenant admin cannot switch tenants");

                AssertEqual(foreign.Id, Ids(await EnumerateAsync(o, new { }).ConfigureAwait(false)), "another tenant sees only its own memory");
                AssertEqual(foreign.Id, Ids(await EnumerateAsync(fx.AuthClient, new { TenantId = outsider.TenantId }).ConfigureAwait(false)), "global admin narrows to a tenant");
                AssertEqual(bobPrivate.Id, Ids(await EnumerateAsync(fx.AuthClient, new { TenantId = admin.TenantId, UserId = bob.UserId }).ConfigureAwait(false)), "global admin narrows to a tenant user");
            }));

            cases.Add(CaseAsync("errors_match_missions_enumerate", "Unauthenticated, malformed, and out-of-range requests behave exactly like POST /api/v1/missions/enumerate", async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                E2ETenantUser admin = await E2ETenantUser.CreateAsync(fx.AuthClient, "mem-errors", true).ConfigureAwait(false);
                using HttpClient a = admin.CreateClient(fx.BaseUrl);
                await SeedSequenceAsync(a, 2, "errors").ConfigureAwait(false);

                using (HttpClient anonymous = new HttpClient { BaseAddress = new Uri(fx.BaseUrl) })
                {
                    HttpResponseMessage memories = await anonymous.PostAsync(EnumeratePath, JsonHelper.ToJsonContent(new { })).ConfigureAwait(false);
                    HttpResponseMessage missions = await anonymous.PostAsync("/api/v1/missions/enumerate", JsonHelper.ToJsonContent(new { })).ConfigureAwait(false);
                    AssertStatusCode(HttpStatusCode.Unauthorized, memories, "unauthenticated memories enumerate");
                    AssertEqual(missions.StatusCode, memories.StatusCode, "unauthenticated status matches missions");
                }

                string[] badBodies = new[] { "{not json", "{\"pageSize\":\"many\"}", "{\"order\":\"Sideways\"}", "[]" };
                foreach (string body in badBodies)
                {
                    HttpResponseMessage memories = await a.PostAsync(EnumeratePath, new StringContent(body, System.Text.Encoding.UTF8, "application/json")).ConfigureAwait(false);
                    HttpResponseMessage missions = await a.PostAsync("/api/v1/missions/enumerate", new StringContent(body, System.Text.Encoding.UTF8, "application/json")).ConfigureAwait(false);
                    AssertEqual(missions.StatusCode, memories.StatusCode, "status for body " + body);
                    AssertFalse(memories.IsSuccessStatusCode, "malformed body is rejected: " + body);
                }

                HttpResponseMessage badSort = await a.PostAsync(EnumeratePath, JsonHelper.ToJsonContent(new { SortBy = "Sideways" })).ConfigureAwait(false);
                AssertFalse(badSort.IsSuccessStatusCode, "an unknown sortBy in the body is rejected like an unknown order");

                EnumerationResult<Memory> small = await EnumerateAsync(a, new { PageSize = 0, PageNumber = 0 }).ConfigureAwait(false);
                EnumerationResult<Mission> smallMissions = await JsonHelper.DeserializeAsync<EnumerationResult<Mission>>(await a.PostAsync("/api/v1/missions/enumerate", JsonHelper.ToJsonContent(new { PageSize = 0, PageNumber = 0 })).ConfigureAwait(false)).ConfigureAwait(false);
                AssertEqual(smallMissions.PageSize, small.PageSize, "page size below range is clamped like missions");
                AssertEqual(1, small.PageSize, "page size clamps to 1");
                AssertEqual(1, small.PageNumber, "page number clamps to 1");

                EnumerationResult<Memory> large = await EnumerateAsync(a, new { PageSize = 5000 }).ConfigureAwait(false);
                AssertEqual(1000, large.PageSize, "page size clamps to 1000");
                AssertEqual(2, large.Objects.Count, "clamped page returns every row");
            }));

            return new TestSuiteDescriptor(suiteId: SuiteId, displayName: "Memory enumerate API", cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<Memory> CreateAsync(HttpClient client, object body)
        {
            HttpResponseMessage response = await client.PostAsync("/api/v1/memories", JsonHelper.ToJsonContent(body)).ConfigureAwait(false);
            AssertStatusCode(HttpStatusCode.Created, response, "create memory");
            Memory memory = await JsonHelper.DeserializeAsync<Memory>(response).ConfigureAwait(false);
            // Distinct created times keep the created-order assertions deterministic.
            await Task.Delay(15).ConfigureAwait(false);
            return memory;
        }

        private static async Task<List<Memory>> SeedSequenceAsync(HttpClient client, int count, string label)
        {
            List<Memory> seeded = new List<Memory>();
            for (int i = 0; i < count; i++)
                seeded.Add(await CreateAsync(client, new { Content = label + " memory " + i, Salience = 0.5 }).ConfigureAwait(false));
            return seeded;
        }

        private static async Task<EnumerationResult<Memory>> EnumerateAsync(HttpClient client, object body, string querystring = "")
        {
            HttpResponseMessage response = await client.PostAsync(EnumeratePath + querystring, JsonHelper.ToJsonContent(body)).ConfigureAwait(false);
            AssertStatusCode(HttpStatusCode.OK, response, "enumerate memories");
            return await JsonHelper.DeserializeAsync<EnumerationResult<Memory>>(response).ConfigureAwait(false);
        }

        private static async Task<EnumerationResult<Memory>> ListAsync(HttpClient client, string querystring)
        {
            HttpResponseMessage response = await client.GetAsync("/api/v1/memories" + querystring).ConfigureAwait(false);
            AssertStatusCode(HttpStatusCode.OK, response, "list memories");
            return await JsonHelper.DeserializeAsync<EnumerationResult<Memory>>(response).ConfigureAwait(false);
        }

        private static Task<EnumerationResult<Memory>> SortAsync(HttpClient client, string sortBy, bool ascending)
        {
            return EnumerateAsync(client, new { SortBy = sortBy, Order = ascending ? "CreatedAscending" : "CreatedDescending" });
        }

        private static Task<EnumerationResult<Memory>> FilterAsync(HttpClient client, object body)
        {
            return EnumerateAsync(client, body);
        }

        private static async Task<string> CreateVesselAsync(HttpClient client, string name)
        {
            HttpResponseMessage fleetResponse = await client.PostAsync("/api/v1/fleets", JsonHelper.ToJsonContent(new { Name = name + "-fleet" })).ConfigureAwait(false);
            Fleet fleet = await JsonHelper.DeserializeAsync<Fleet>(fleetResponse).ConfigureAwait(false);
            AssertFalse(String.IsNullOrEmpty(fleet.Id), "create fleet");
            HttpResponseMessage vesselResponse = await client.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new { Name = name, RepoUrl = TestRepoHelper.GetLocalBareRepoUrl(), FleetId = fleet.Id })).ConfigureAwait(false);
            Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(vesselResponse).ConfigureAwait(false);
            AssertFalse(String.IsNullOrEmpty(vessel.Id), "create vessel " + name);
            return vessel.Id;
        }

        private static string Ids(EnumerationResult<Memory> result)
        {
            return String.Join(",", result.Objects.Select(m => m.Id));
        }

        private static string IdsInText(string text, params Memory[] memories)
        {
            List<Memory> present = memories.Where(m => text.Contains(m.Id, StringComparison.Ordinal)).ToList();
            return String.Join(",", present.OrderBy(m => text.IndexOf(m.Id, StringComparison.Ordinal)).Select(m => m.Id));
        }

        private static string Join(params Memory[] memories)
        {
            return String.Join(",", memories.Select(m => m.Id));
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { TestTags.EndToEnd });
        }

        #endregion
    }
}
