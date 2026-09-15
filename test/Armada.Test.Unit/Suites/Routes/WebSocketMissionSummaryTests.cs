namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Server;
    using Armada.Server.WebSocket;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// The WebSocket list_missions_summary command reads through the same caller-scoped query REST serves:
    /// each caller receives exactly the summaries REST returns to it, a caller never receives another
    /// tenant's or another user's missions, and a command without a caller is refused.
    /// </summary>
    public sealed class WebSocketMissionSummaryTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "WebSocket Mission Summaries";

        private static readonly JsonSerializerOptions _ServerJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("TenantAdministrator_ReceivesOnlyOwnTenantMissions", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    List<Mission> seeded = await SeedAsync(testDb.Driver).ConfigureAwait(false);
                    Mission tenantA = seeded[0];
                    Mission tenantB1 = seeded[1];
                    Mission tenantB2 = seeded[2];
                    AuthContext caller = AuthContext.Authenticated(tenantB1.TenantId!, tenantB1.UserId!, false, true, "Test", null, "Tenant B admin");

                    string json = await ListAsync(testDb.Driver, caller).ConfigureAwait(false);

                    AssertContains("command.result", json);
                    AssertContains(tenantB1.Id, json, "the tenant administrator receives its tenant's missions");
                    AssertContains(tenantB2.Id, json, "the tenant administrator receives every user's missions in its tenant");
                    AssertFalse(json.Contains(tenantA.Id, StringComparison.Ordinal), "another tenant's mission is never returned");
                    await AssertMatchesRestAsync(testDb.Driver, caller, json).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("OrdinaryUser_ReceivesOnlyOwnMissions", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    List<Mission> seeded = await SeedAsync(testDb.Driver).ConfigureAwait(false);
                    Mission tenantA = seeded[0];
                    Mission tenantB1 = seeded[1];
                    Mission tenantB2 = seeded[2];
                    AuthContext caller = AuthContext.Authenticated(tenantB1.TenantId!, tenantB1.UserId!, false, false, "Test", null, "Tenant B user");

                    string json = await ListAsync(testDb.Driver, caller).ConfigureAwait(false);

                    AssertContains(tenantB1.Id, json, "the user receives its own mission");
                    AssertFalse(json.Contains(tenantB2.Id, StringComparison.Ordinal), "another user's mission in the same tenant is not returned");
                    AssertFalse(json.Contains(tenantA.Id, StringComparison.Ordinal), "another tenant's mission is never returned");
                    await AssertMatchesRestAsync(testDb.Driver, caller, json).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("GlobalAdministrator_ReceivesEveryTenant", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    List<Mission> seeded = await SeedAsync(testDb.Driver).ConfigureAwait(false);

                    string json = await ListAsync(testDb.Driver, McpTestCaller.Operator).ConfigureAwait(false);

                    foreach (Mission mission in seeded)
                        AssertContains(mission.Id, json, "a global administrator receives every tenant's missions");
                    await AssertMatchesRestAsync(testDb.Driver, McpTestCaller.Operator, json).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("WithoutCaller_IsRefused", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    List<Mission> seeded = await SeedAsync(testDb.Driver).ConfigureAwait(false);

                    string json = await ListAsync(testDb.Driver, null).ConfigureAwait(false);

                    AssertContains("command.error", json, "a command without a caller is refused");
                    foreach (Mission mission in seeded)
                        AssertFalse(json.Contains(mission.Id, StringComparison.Ordinal), "a refused command returns no mission");
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Seed a mission in tenant A and one mission for each of two users in tenant B, returned in that order.
        /// </summary>
        private static async Task<List<Mission>> SeedAsync(DatabaseDriver database)
        {
            TenantMetadata tenantA = new TenantMetadata("Summary Tenant A");
            TenantMetadata tenantB = new TenantMetadata("Summary Tenant B");
            await database.Tenants.CreateAsync(tenantA).ConfigureAwait(false);
            await database.Tenants.CreateAsync(tenantB).ConfigureAwait(false);
            UserMaster userA = new UserMaster(tenantA.Id, "summary-a@example.com", "pass");
            UserMaster userB1 = new UserMaster(tenantB.Id, "summary-b1@example.com", "pass");
            UserMaster userB2 = new UserMaster(tenantB.Id, "summary-b2@example.com", "pass");
            await database.Users.CreateAsync(userA).ConfigureAwait(false);
            await database.Users.CreateAsync(userB1).ConfigureAwait(false);
            await database.Users.CreateAsync(userB2).ConfigureAwait(false);

            List<Mission> missions = new List<Mission>
            {
                new Mission("summary tenant A") { TenantId = tenantA.Id, UserId = userA.Id },
                new Mission("summary tenant B user 1") { TenantId = tenantB.Id, UserId = userB1.Id },
                new Mission("summary tenant B user 2") { TenantId = tenantB.Id, UserId = userB2.Id }
            };
            List<Mission> created = new List<Mission>();
            foreach (Mission mission in missions)
                created.Add(await database.Missions.CreateAsync(mission).ConfigureAwait(false));
            return created;
        }

        private static async Task<string> ListAsync(DatabaseDriver database, AuthContext? caller)
        {
            WebSocketCommandHandler handler = new WebSocketCommandHandler(
                null!,
                database,
                null!,
                null,
                null,
                null,
                _ServerJsonOptions,
                mission => { },
                voyage => { });
            object result = await handler.HandleCommandAsync(
                "list_missions_summary",
                new WebSocketCommand { Action = "list_missions_summary" },
                "{\"Route\":\"command\",\"action\":\"list_missions_summary\"}",
                caller).ConfigureAwait(false);
            return JsonSerializer.Serialize(result, _ServerJsonOptions);
        }

        /// <summary>
        /// The command returns exactly the rows the REST summaries route returns to the same caller.
        /// </summary>
        private async Task AssertMatchesRestAsync(DatabaseDriver database, AuthContext caller, string commandJson)
        {
            EnumerationResult<MissionSummary> rest = await MissionSummaryQuery.EnumerateForCallerAsync(database, caller, new EnumerationQuery()).ConfigureAwait(false);
            AssertContains("\"totalRecords\":" + rest.TotalRecords, commandJson, "the command reports the same total REST reports");
            foreach (MissionSummary summary in rest.Objects)
                AssertContains(summary.Id, commandJson, "every summary REST returns is in the command result");
        }
    }
}
