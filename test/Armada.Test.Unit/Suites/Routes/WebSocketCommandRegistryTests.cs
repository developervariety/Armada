namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server.Mcp;
    using Armada.Server.WebSocket;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Every command the WebSocket handler dispatches has one declared authorization rule, and the rule is the stricter
    /// of the matching REST route and MCP tool. Undeclared commands, missing callers and callers without the declared
    /// role are refused before a command runs.
    /// </summary>
    public class WebSocketCommandRegistryTests : TestSuite
    {
        #region Public-Members

        /// <summary>Suite name.</summary>
        public override string Name => "WebSocket Command Registry";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _ServerJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        #endregion

        #region Protected-Methods

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("EveryDispatchedCommand_HasADeclaredRule_AndEveryRuleIsDispatched", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    WebSocketCommandHandler handler = CreateHandler(testDb);
                    List<string> dispatched = handler.CommandNames.OrderBy(name => name, StringComparer.Ordinal).ToList();
                    List<string> declared = WebSocketCommandRegistry.Rules.Select(rule => rule.Action).OrderBy(name => name, StringComparer.Ordinal).ToList();

                    foreach (string name in dispatched)
                        AssertTrue(WebSocketCommandRegistry.TryGetRule(name, out WebSocketCommandRule? _), "dispatched command " + name + " has a declared rule");
                    foreach (string name in declared)
                        AssertTrue(dispatched.Contains(name), "declared command " + name + " is dispatched by the handler");
                    AssertEqual(declared.Count, dispatched.Count, "the handler dispatches exactly the declared commands");
                }
            }).ConfigureAwait(false);

            await RunTest("EveryRule_IsTheStricterOfItsRestRouteAndMcpTool", () =>
            {
                AuthContext user = AuthContext.Authenticated("ten_example", "usr_example", false, false, "Test");
                AuthContext tenantAdmin = AuthContext.Authenticated("ten_example", "usr_example_admin", false, true, "Test");
                foreach (WebSocketCommandRule rule in WebSocketCommandRegistry.Rules)
                {
                    bool restAdmitsUser = true;
                    bool restAdmitsTenantAdmin = true;
                    if (rule.RestMethod != null && rule.RestPath != null)
                    {
                        PermissionLevel level = AuthorizationConfig.GetPermissionLevel(rule.RestMethod, SamplePath(rule.RestPath));
                        restAdmitsUser = level == PermissionLevel.Authenticated || level == PermissionLevel.NoAuthRequired;
                        restAdmitsTenantAdmin = restAdmitsUser || level == PermissionLevel.TenantAdmin;
                    }
                    bool mcpAdmitsUser = rule.McpTool == null || McpToolAccessPolicy.IsAllowed(user, rule.McpTool);
                    bool mcpAdmitsTenantAdmin = rule.McpTool == null || McpToolAccessPolicy.IsAllowed(tenantAdmin, rule.McpTool);
                    AssertTrue(rule.RestMethod != null || rule.McpTool != null || rule.Rule == WebSocketCommandRuleEnum.GlobalAdmin,
                        rule.Action + " has no counterpart, so it is reserved for global administrators");

                    WebSocketCommandRuleEnum expected;
                    if (restAdmitsUser && mcpAdmitsUser)
                        expected = rule.Operation == WebSocketCommandOperationEnum.List ? WebSocketCommandRuleEnum.ListScoped : WebSocketCommandRuleEnum.ReadScoped;
                    else if (restAdmitsTenantAdmin && mcpAdmitsTenantAdmin)
                        expected = WebSocketCommandRuleEnum.TenantAdminScoped;
                    else
                        expected = WebSocketCommandRuleEnum.GlobalAdmin;
                    AssertEqual(expected, rule.Rule, rule.Action + " takes the stricter of REST (" + rule.RestMethod + " " + rule.RestPath + ") and MCP (" + rule.McpTool + ")");
                    if (rule.Rule == WebSocketCommandRuleEnum.ReadScoped || rule.Rule == WebSocketCommandRuleEnum.ListScoped)
                        AssertTrue(rule.Operation == WebSocketCommandOperationEnum.Read || rule.Operation == WebSocketCommandOperationEnum.List,
                            rule.Action + " opens only a read to every authenticated caller");
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("TenantAdminScopedRules_RefuseTenantUsers_AndAdmitAdministrators", () =>
            {
                AuthContext user = AuthContext.Authenticated("ten_example", "usr_example", false, false, "Test");
                AuthContext tenantAdmin = AuthContext.Authenticated("ten_example", "usr_example_admin", false, true, "Test");
                List<WebSocketCommandRule> scoped = WebSocketCommandRegistry.Rules.Where(rule => rule.Rule == WebSocketCommandRuleEnum.TenantAdminScoped).ToList();
                AssertTrue(scoped.Count > 0, "tenant-administrator commands are declared");
                foreach (WebSocketCommandRule rule in scoped)
                {
                    WebSocketCommandRefusal? refusal = WebSocketCommandRegistry.Authorize(rule.Action, user);
                    AssertNotNull(refusal, rule.Action + " refuses a tenant user");
                    AssertEqual(WebSocketCommandRefusal.TenantAdministratorRequiredCode, refusal!.Code, rule.Action + " names the missing role");
                    AssertNull(WebSocketCommandRegistry.Authorize(rule.Action, tenantAdmin), rule.Action + " admits a tenant administrator, who is then held to the ownership rule");
                    AssertNull(WebSocketCommandRegistry.Authorize(rule.Action, McpTestCaller.Operator), rule.Action + " admits a global administrator");
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("UnknownCommand_IsRefusedForEveryCaller", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    WebSocketCommandHandler handler = CreateHandler(testDb);
                    foreach (AuthContext? caller in new AuthContext?[] { McpTestCaller.Operator, null })
                    {
                        string json = await SendAsync(handler, "totally_bogus_action", null, caller).ConfigureAwait(false);
                        AssertContains("\"code\":\"unknown_command\"", json, "an undeclared command is refused: " + json);
                        AssertContains("Unknown action", json);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("GlobalAdminRules_RefuseEveryNarrowerCaller_BeforeTheCommandRuns", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    TenantMetadata tenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("ws-registry-tenant")).ConfigureAwait(false);
                    UserMaster owner = await testDb.Driver.Users.CreateAsync(new UserMaster(tenant.Id, "ws-registry-owner@example.com", "password")).ConfigureAwait(false);
                    Fleet fleet = new Fleet("ws-registry-fleet");
                    fleet.TenantId = tenant.Id;
                    fleet.UserId = owner.Id;
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);
                    Persona persona = new Persona("WsRegistryPersona", "persona.worker");
                    persona.TenantId = tenant.Id;
                    persona.UserId = owner.Id;
                    persona.Description = "original";
                    persona = await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);

                    AuthContext tenantAdmin = AuthContext.Authenticated(tenant.Id, owner.Id, false, true, "Test", null, "tenant administrator");
                    AuthContext tenantUser = AuthContext.Authenticated(tenant.Id, owner.Id, false, false, "Test", null, "tenant user");

                    // The handler has no admiral, merge queue, settings or backup service: a command that ran would
                    // throw or report the missing service instead of the named refusal.
                    WebSocketCommandHandler handler = CreateHandler(testDb);
                    List<WebSocketCommandRule> operatorRules = WebSocketCommandRegistry.Rules.Where(rule => rule.Rule == WebSocketCommandRuleEnum.GlobalAdmin).ToList();
                    AssertTrue(operatorRules.Count > 0, "operator commands are declared");
                    foreach (WebSocketCommandRule rule in operatorRules)
                    {
                        string id = rule.Action.Contains("persona", StringComparison.Ordinal) ? persona.Name : fleet.Id;
                        foreach (AuthContext caller in new[] { tenantAdmin, tenantUser })
                        {
                            string json = await SendAsync(handler, rule.Action, id, caller).ConfigureAwait(false);
                            AssertContains("\"code\":\"global_administrator_required\"", json, rule.Action + " refuses the " + caller.PrincipalDisplay + ": " + json);
                        }
                    }

                    foreach (WebSocketCommandRule rule in WebSocketCommandRegistry.Rules)
                    {
                        string json = await SendAsync(handler, rule.Action, fleet.Id, null).ConfigureAwait(false);
                        AssertContains("\"code\":\"authentication_required\"", json, rule.Action + " refuses a command without a caller: " + json);
                    }

                    Fleet? storedFleet = await testDb.Driver.Fleets.ReadAsync(fleet.Id).ConfigureAwait(false);
                    AssertNotNull(storedFleet, "no refused command deleted the fleet");
                    AssertEqual("ws-registry-fleet", storedFleet!.Name, "no refused command changed the fleet");
                    Persona? storedPersona = await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false);
                    AssertNotNull(storedPersona, "no refused command deleted the persona");
                    AssertEqual("original", storedPersona!.Description, "no refused command changed the persona");
                }
            }).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private static string SamplePath(string template)
        {
            return template.Replace("{id}", "example", StringComparison.Ordinal).Replace("{name}", "example", StringComparison.Ordinal);
        }

        private static WebSocketCommandHandler CreateHandler(TestDatabase testDb)
        {
            return new WebSocketCommandHandler(
                null!,
                testDb.Driver,
                null!,
                null,
                null,
                null,
                _ServerJsonOptions,
                mission => { },
                voyage => { });
        }

        private static async Task<string> SendAsync(WebSocketCommandHandler handler, string action, string? id, AuthContext? caller)
        {
            string rawBody = JsonSerializer.Serialize(new { Route = "command", action = action, id = id, data = new { Name = "renamed", Description = "renamed", Title = "renamed" } });
            try
            {
                object result = await handler.HandleCommandAsync(action, new WebSocketCommand { Action = action, Id = id }, rawBody, caller).ConfigureAwait(false);
                return JsonSerializer.Serialize(result, _ServerJsonOptions);
            }
            catch (Exception ex)
            {
                return "{\"type\":\"command.exception\",\"error\":" + JsonSerializer.Serialize(ex.GetType().Name + ": " + ex.Message) + "}";
            }
        }

        #endregion
    }
}
