namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Persona writes behave the same on REST, MCP, and WebSocket (one shared write service): required fields, routing
    /// validation, default-captain presence semantics, the built-in delete refusal, and ownership. Also covers the MCP
    /// captain tools accepting a managed model endpoint, and a regular user writing personas over MCP like over REST.
    /// </summary>
    public sealed class PersonaSurfaceParitySuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string Suite = "E2E.PersonaSurfaceParity";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the suite descriptor.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();
            foreach (string surface in new[] { "rest", "mcp", "ws" })
            {
                string s = surface;
                cases.Add(new TestCaseDescriptor(Suite, s + "_persona_write_rules",
                    s.ToUpperInvariant() + ": persona create, update, and delete apply the shared rules",
                    async ct => await PersonaRulesAsync(s).ConfigureAwait(false)));
            }

            cases.Add(new TestCaseDescriptor(Suite, "rest_ignores_client_built_in", "REST create ignores a client-sent IsBuiltIn, so the persona stays deletable", async ct =>
            {
                using SecurityTestServer fx = await SecurityTestServer.PrepareAsync("127.0.0.1", null).ConfigureAwait(false);
                await fx.StartAsync().ConfigureAwait(false);
                using HttpClient rest = fx.CreateRestClient(true);
                using HttpResponseMessage created = await rest.PostAsync("/api/v1/personas", JsonHelper.ToJsonContent(new { Name = "spoof-builtin", PromptTemplateName = "persona.worker", IsBuiltIn = true })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, created.StatusCode);
                AssertFalse((await JsonHelper.DeserializeAsync<Persona>(created).ConfigureAwait(false)).IsBuiltIn, "IsBuiltIn is ignored on create");
                using HttpResponseMessage deleted = await rest.DeleteAsync("/api/v1/personas/spoof-builtin").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NoContent, deleted.StatusCode);
            }));

            cases.Add(new TestCaseDescriptor(Suite, "mcp_regular_user_writes_own_persona", "A regular user creates a UserSpecific persona over MCP, as over REST, and cannot edit a tenant-wide one", async ct =>
            {
                using SecurityTestServer fx = await SecurityTestServer.PrepareAsync("127.0.0.1", null).ConfigureAwait(false);
                await fx.StartAsync().ConfigureAwait(false);
                using HttpClient admin = fx.CreateRestClient(true);
                string bearer = await CreateRegularUserAsync(admin).ConfigureAwait(false);
                using HttpClient mcp = fx.CreateMcpClient(false);
                mcp.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);

                string created = await McpTestCalls.CallToolAsync(mcp, "create_persona", new { name = "mine", promptTemplateName = "persona.worker", scope = "TenantWide" }).ConfigureAwait(false);
                AssertContains("UserSpecific", created, "a regular user's persona is user-specific even when TenantWide is requested");
                string edit = await McpTestCalls.CallToolAsync(mcp, "update_persona", new { name = "mine", description = "edited" }).ConfigureAwait(false);
                AssertContains("edited", edit);
                string worker = await McpTestCalls.CallToolAsync(mcp, "update_persona", new { name = "Worker", description = "hijack" }).ConfigureAwait(false);
                AssertContains("Forbidden", worker, "a tenant-wide built-in persona needs a tenant admin");
            }));

            cases.Add(new TestCaseDescriptor(Suite, "mcp_captain_model_endpoint", "MCP create_captain and update_captain set and clear a managed model endpoint", async ct =>
            {
                using SecurityTestServer fx = await SecurityTestServer.PrepareAsync("127.0.0.1", null).ConfigureAwait(false);
                await fx.StartAsync().ConfigureAwait(false);
                LoggingModule quiet = new LoggingModule(); quiet.Settings.EnableConsole = false;
                using DatabaseDriver db = DatabaseDriverFactory.Create(fx.Settings.Database, quiet);
                await db.InitializeAsync().ConfigureAwait(false);
                ModelEndpoint endpoint = await db.ModelEndpoints.CreateAsync(new ModelEndpoint
                {
                    Name = "parity-endpoint", Kind = ModelEndpointKindEnum.Inference, Provider = ModelProviderEnum.OpenAICompatible,
                    BaseUrl = "http://127.0.0.1:9", Model = "parity-model", Enabled = true
                }).ConfigureAwait(false);

                using HttpClient mcp = fx.CreateMcpClient(true);
                string created = await McpTestCalls.CallToolAsync(mcp, "create_captain", new { name = "api-captain", runtime = "ApiEndpoint", modelEndpointId = endpoint.Id }).ConfigureAwait(false);
                AssertContains(endpoint.Id, created, "the API captain is bound to the endpoint");
                Captain captain = (await db.Captains.EnumerateAsync().ConfigureAwait(false)).Single(c => c.Name == "api-captain");
                AssertEqual(endpoint.Id, captain.ModelEndpointId);

                string missing = await McpTestCalls.CallToolAsync(mcp, "update_captain", new { captainId = captain.Id, modelEndpointId = "mep_missing" }).ConfigureAwait(false);
                AssertContains("EndpointNotFound", missing, "an unknown endpoint is refused with the REST validation");
                AssertEqual(endpoint.Id, (await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!.ModelEndpointId, "a refused update changes nothing");
            }));

            cases.Add(new TestCaseDescriptor(Suite, "mcp_captain_pi_runtime", "MCP create_captain creates a Pi captain and update_captain keeps it", async ct =>
            {
                using SecurityTestServer fx = await SecurityTestServer.PrepareAsync("127.0.0.1", null).ConfigureAwait(false);
                await fx.StartAsync().ConfigureAwait(false);
                LoggingModule quiet = new LoggingModule(); quiet.Settings.EnableConsole = false;
                using DatabaseDriver db = DatabaseDriverFactory.Create(fx.Settings.Database, quiet);
                await db.InitializeAsync().ConfigureAwait(false);

                using HttpClient mcp = fx.CreateMcpClient(true);
                await McpTestCalls.CallToolAsync(mcp, "create_captain", new { name = "pi-captain", runtime = "Pi", reasoningEffort = "High" }).ConfigureAwait(false);
                Captain captain = (await db.Captains.EnumerateAsync().ConfigureAwait(false)).Single(c => c.Name == "pi-captain");
                AssertEqual(AgentRuntimeEnum.Pi, captain.Runtime);
                AssertEqual(ReasoningEffortEnum.High, captain.ReasoningEffort);

                await McpTestCalls.CallToolAsync(mcp, "update_captain", new { captainId = captain.Id, name = "pi-captain-2", runtime = "pi" }).ConfigureAwait(false);
                Captain updated = (await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!;
                AssertEqual("pi-captain-2", updated.Name);
                AssertEqual(AgentRuntimeEnum.Pi, updated.Runtime);
            }));

            return new TestSuiteDescriptor(Suite, "Persona writes match across surfaces", cases);
        }

        #endregion

        #region Private-Methods

        private static async Task PersonaRulesAsync(string surface)
        {
            using SecurityTestServer fx = await SecurityTestServer.PrepareAsync("127.0.0.1", null).ConfigureAwait(false);
            await fx.StartAsync().ConfigureAwait(false);
            LoggingModule quiet = new LoggingModule(); quiet.Settings.EnableConsole = false;
            using DatabaseDriver db = DatabaseDriverFactory.Create(fx.Settings.Database, quiet);
            await db.InitializeAsync().ConfigureAwait(false);
            Captain captain = await db.Captains.CreateAsync(new Captain("parity-default")).ConfigureAwait(false);
            PersonaSurface api = new PersonaSurface(fx, surface);

            // Required fields.
            AssertEqual("invalid", await api.CreateAsync(new { Name = "no-template", PromptTemplateName = "" }).ConfigureAwait(false), "PromptTemplateName is required");

            // Create with routing fields and a default captain.
            AssertEqual("ok", await api.CreateAsync(new
            {
                Name = "parity", PromptTemplateName = "persona.worker", DefaultCaptainId = captain.Id,
                CaptainPriorities = new[] { new { Runtime = "ClaudeCode" } }
            }).ConfigureAwait(false));
            Persona stored = (await db.Personas.ReadByNameAsync("parity").ConfigureAwait(false))!;
            AssertEqual(captain.Id, stored.DefaultCaptainId);
            AssertEqual(1, stored.CaptainPriorities.Count);

            // Routing fields update (WebSocket used to drop them), an omitted default captain is kept.
            AssertEqual("ok", await api.UpdateAsync("parity", new { CaptainPriorities = new[] { new { Runtime = "Codex" }, new { Runtime = "ClaudeCode" } }, PriorityExhaustion = "UseEligibleCaptain" }).ConfigureAwait(false));
            stored = (await db.Personas.ReadByNameAsync("parity").ConfigureAwait(false))!;
            AssertEqual(2, stored.CaptainPriorities.Count, "priorities applied");
            AssertEqual(AgentRuntimeEnum.Codex, stored.CaptainPriorities[0].Runtime);
            AssertEqual(PersonaPriorityExhaustionEnum.UseEligibleCaptain, stored.PriorityExhaustion);
            AssertEqual(captain.Id, stored.DefaultCaptainId, "an omitted default captain is kept");

            // Invalid routing and an unknown default captain are refused the same way.
            AssertEqual("invalid", await api.UpdateAsync("parity", new { CaptainPriorities = new[] { new { } } }).ConfigureAwait(false), "an empty priority entry is invalid");
            AssertEqual("invalid", await api.UpdateAsync("parity", new { DefaultCaptainId = "cpt_missing" }).ConfigureAwait(false), "an unknown default captain is invalid");

            // An empty string clears the default captain on every surface; REST and WebSocket also take an explicit null
            // (MCP arguments are schema-checked as strings).
            AssertEqual("ok", await api.UpdateAsync("parity", new { DefaultCaptainId = "" }).ConfigureAwait(false));
            AssertNull((await db.Personas.ReadByNameAsync("parity").ConfigureAwait(false))!.DefaultCaptainId, "an empty string clears the default captain");
            if (surface != "mcp")
            {
                AssertEqual("ok", await api.UpdateAsync("parity", new { DefaultCaptainId = captain.Id }).ConfigureAwait(false));
                AssertEqual("ok", await api.UpdateAsync("parity", new Dictionary<string, object?> { ["DefaultCaptainId"] = null }).ConfigureAwait(false));
                AssertNull((await db.Personas.ReadByNameAsync("parity").ConfigureAwait(false))!.DefaultCaptainId, "an explicit null clears the default captain");
            }

            // Built-in personas cannot be deleted; a custom one can.
            AssertEqual("invalid", await api.DeleteAsync("Worker").ConfigureAwait(false), "built-in delete is refused as invalid on every surface");
            AssertEqual("notfound", await api.DeleteAsync("does-not-exist").ConfigureAwait(false));
            AssertEqual("ok", await api.DeleteAsync("parity").ConfigureAwait(false));
        }

        private static async Task<string> CreateRegularUserAsync(HttpClient admin)
        {
            using HttpResponseMessage userResponse = await admin.PostAsync("/api/v1/users", JsonHelper.ToJsonContent(new
            {
                TenantId = Armada.Core.Constants.DefaultTenantId, Email = "persona-parity@example.test", PasswordSha256 = UserMaster.ComputePasswordHash("fixture-only")
            })).ConfigureAwait(false);
            UserMaster user = await JsonHelper.DeserializeAsync<UserMaster>(userResponse).ConfigureAwait(false);
            using HttpResponseMessage credentialResponse = await admin.PostAsync("/api/v1/credentials", JsonHelper.ToJsonContent(new { TenantId = Armada.Core.Constants.DefaultTenantId, UserId = user.Id, Name = "fixture" })).ConfigureAwait(false);
            Credential credential = await JsonHelper.DeserializeAsync<Credential>(credentialResponse).ConfigureAwait(false);
            return credential.BearerToken;
        }

        #endregion
    }
}
