namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server.Mcp.Tools;
    using Armada.Server.WebSocket;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// The write surfaces for the routing fields that live on records: a captain's tier and preference rank
    /// through the MCP captain tools, and a persona's specialist flag and default captain through the MCP persona
    /// tools and the WebSocket persona command. A write that omits a field keeps its stored value.
    /// </summary>
    public sealed class TierRoutingWriteSurfaceTests : TestSuite
    {
        #region Public-Members

        /// <inheritdoc />
        public override string Name => "Tier Routing Write Surfaces";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _ServerJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Mcp captain create and update set the tier and preference rank and keep an omitted one", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, Func<JsonElement?, Task<object>>> tools = CaptainTools(testDb.Driver);
                    await tools["armada_create_captain"](JsonSerializer.SerializeToElement(new { name = "ranked-mcp", tier = "premium", preferenceRank = 7 })).ConfigureAwait(false);
                    Captain? created = await testDb.Driver.Captains.ReadByNameAsync("ranked-mcp").ConfigureAwait(false);
                    AssertEqual(CaptainTierEnum.Premium, created!.Tier, "tier is parsed case-insensitively");
                    AssertEqual(7, created.PreferenceRank);

                    await tools["armada_update_captain"](JsonSerializer.SerializeToElement(new { captainId = created.Id, preferenceRank = 9 })).ConfigureAwait(false);
                    Captain? ranked = await testDb.Driver.Captains.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertEqual(9, ranked!.PreferenceRank, "the rank is updated");
                    AssertEqual(CaptainTierEnum.Premium, ranked.Tier, "an omitted tier keeps its stored value");

                    await tools["armada_update_captain"](JsonSerializer.SerializeToElement(new { captainId = created.Id, tier = "" })).ConfigureAwait(false);
                    Captain? auto = await testDb.Driver.Captains.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertNull(auto!.Tier, "an empty tier returns the captain to model classification");
                    AssertEqual(9, auto.PreferenceRank, "an omitted rank keeps its stored value");

                    string refused = JsonSerializer.Serialize(await tools["armada_update_captain"](JsonSerializer.SerializeToElement(new { captainId = created.Id, tier = "Ultra" })).ConfigureAwait(false));
                    AssertContains("tier must be Economy, Standard, or Premium", refused, "an unknown tier is refused");
                }
            });

            await RunTest("Mcp captain update returns every persisted field, not a lossy projection", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, Func<JsonElement?, Task<object>>> tools = CaptainTools(testDb.Driver);

                    ModelEndpoint endpoint = new ModelEndpoint
                    {
                        Name = "ProjectionEndpoint",
                        Kind = ModelEndpointKindEnum.Inference,
                        Scope = ScopeEnum.TenantWide,
                        Provider = ModelProviderEnum.OpenAICompatible,
                        BaseUrl = "http://localhost:9999",
                        Model = "claude-fable-5",
                        Enabled = true
                    };
                    endpoint = await testDb.Driver.ModelEndpoints.CreateAsync(endpoint).ConfigureAwait(false);

                    Captain seeded = new Captain("projection-captain");
                    seeded.Runtime = AgentRuntimeEnum.ClaudeCode;
                    seeded.Model = "claude-fable-5";
                    seeded.ModelEndpointId = endpoint.Id;
                    seeded.Tier = CaptainTierEnum.Premium;
                    seeded.PreferenceRank = 3;
                    seeded.AllowedPersonas = "[\"Judge\"]";
                    seeded = await testDb.Driver.Captains.CreateAsync(seeded).ConfigureAwait(false);

                    object response = await tools["armada_update_captain"](
                        JsonSerializer.SerializeToElement(new { captainId = seeded.Id, preferenceRank = 5 })).ConfigureAwait(false);

                    Captain? returned = response as Captain;
                    AssertNotNull(returned, "the update tool returns a captain");
                    Captain? persisted = await testDb.Driver.Captains.ReadAsync(seeded.Id).ConfigureAwait(false);
                    AssertNotNull(persisted, "the captain is readable after the update");

                    // Compared by reflection rather than by a hand-written field list, so a field added to
                    // Captain later cannot be left out of the projection unnoticed. ApiKey is deliberately
                    // masked in the response and is the one field allowed to differ.
                    List<string> mismatched = new List<string>();
                    foreach (System.Reflection.PropertyInfo property in typeof(Captain).GetProperties())
                    {
                        if (!property.CanRead) continue;
                        if (String.Equals(property.Name, nameof(Captain.ApiKey), StringComparison.Ordinal)) continue;

                        object? fromResponse = property.GetValue(returned);
                        object? fromDatabase = property.GetValue(persisted);
                        if (SameFieldValue(fromResponse, fromDatabase)) continue;
                        mismatched.Add(property.Name + " (response '" + fromResponse + "', database '" + fromDatabase + "')");
                    }

                    AssertEqual(0, mismatched.Count, "the returned captain must equal the persisted captain, but these differ: " + String.Join("; ", mismatched));
                    AssertEqual(5, returned!.PreferenceRank, "the rank the caller set comes back");
                    AssertEqual(CaptainTierEnum.Premium, returned.Tier, "an omitted tier comes back at its stored value, not null");
                    AssertEqual(endpoint.Id, returned.ModelEndpointId, "the endpoint reference comes back rather than reading as cleared");
                }
            });

            await RunTest("Mcp persona create and update set the specialist flag and keep an omitted one", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, Func<JsonElement?, Task<object>>> tools = PersonaTools(testDb.Driver);
                    await tools["create_persona"](JsonSerializer.SerializeToElement(new { name = "SpecialistReviewer", promptTemplateName = "persona.worker", specialist = true })).ConfigureAwait(false);
                    Persona? created = await testDb.Driver.Personas.ReadByNameAsync("SpecialistReviewer").ConfigureAwait(false);
                    AssertTrue(created!.Specialist, "create persists the flag");

                    await tools["update_persona"](JsonSerializer.SerializeToElement(new { name = "SpecialistReviewer", description = "renamed" })).ConfigureAwait(false);
                    Persona? kept = await testDb.Driver.Personas.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertTrue(kept!.Specialist, "an update that omits the flag keeps it");

                    await tools["update_persona"](JsonSerializer.SerializeToElement(new { name = "SpecialistReviewer", specialist = false })).ConfigureAwait(false);
                    Persona? cleared = await testDb.Driver.Personas.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertFalse(cleared!.Specialist, "an update clears the flag");
                }
            });

            await RunTest("WebSocket persona update sets the specialist flag and keeps it when omitted", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Persona persona = await testDb.Driver.Personas.CreateAsync(new Persona("WsReviewer", "persona.worker")).ConfigureAwait(false);
                    WebSocketCommandHandler handler = new WebSocketCommandHandler(null!, testDb.Driver, null!, null, null, null, _ServerJsonOptions, mission => { }, voyage => { });

                    string flag = JsonSerializer.Serialize(new { Route = "command", action = "update_persona", id = "WsReviewer", data = new { specialist = true } });
                    await handler.HandleCommandAsync("update_persona", new WebSocketCommand { Action = "update_persona", Id = "WsReviewer" }, flag, McpTestCaller.Operator).ConfigureAwait(false);
                    AssertTrue((await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false))!.Specialist, "the flag is set");

                    string describe = JsonSerializer.Serialize(new { Route = "command", action = "update_persona", id = "WsReviewer", data = new { description = "reviews" } });
                    await handler.HandleCommandAsync("update_persona", new WebSocketCommand { Action = "update_persona", Id = "WsReviewer" }, describe, McpTestCaller.Operator).ConfigureAwait(false);
                    Persona? kept = await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false);
                    AssertEqual("reviews", kept!.Description);
                    AssertTrue(kept.Specialist, "an update that omits the flag keeps it");
                }
            });

            await RunTest("Mcp persona update sets, keeps and clears the default captain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, Func<JsonElement?, Task<object>>> tools = PersonaTools(testDb.Driver);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("default-mcp")).ConfigureAwait(false);
                    await tools["create_persona"](JsonSerializer.SerializeToElement(new { name = "DefaultedReviewer", promptTemplateName = "persona.worker" })).ConfigureAwait(false);
                    Persona? created = await testDb.Driver.Personas.ReadByNameAsync("DefaultedReviewer").ConfigureAwait(false);

                    await tools["update_persona"](JsonSerializer.SerializeToElement(new { name = "DefaultedReviewer", defaultCaptainId = captain.Id, specialist = true })).ConfigureAwait(false);
                    Persona? set = await testDb.Driver.Personas.ReadAsync(created!.Id).ConfigureAwait(false);
                    AssertEqual(captain.Id, set!.DefaultCaptainId, "the default captain is set");
                    AssertTrue(set.Specialist, "the specialist flag in the same update is saved");

                    await tools["update_persona"](JsonSerializer.SerializeToElement(new { name = "DefaultedReviewer", description = "reviews" })).ConfigureAwait(false);
                    Persona? kept = await testDb.Driver.Personas.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertEqual(captain.Id, kept!.DefaultCaptainId, "an update that omits the default captain keeps it");

                    await tools["update_persona"](JsonSerializer.SerializeToElement(new { name = "DefaultedReviewer", defaultCaptainId = "" })).ConfigureAwait(false);
                    Persona? cleared = await testDb.Driver.Personas.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertNull(cleared!.DefaultCaptainId, "an empty default captain clears it");
                }
            });

            await RunTest("Mcp persona update refuses an unknown, other-tenant or persona-locked default captain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, Func<JsonElement?, Task<object>>> tools = PersonaTools(testDb.Driver);
                    await tools["create_persona"](JsonSerializer.SerializeToElement(new { name = "GuardedReviewer", promptTemplateName = "persona.worker" })).ConfigureAwait(false);
                    Persona? persona = await testDb.Driver.Personas.ReadByNameAsync("GuardedReviewer").ConfigureAwait(false);

                    TenantMetadata tenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("OtherDefaultTenant")).ConfigureAwait(false);
                    Captain otherTenant = new Captain("other-tenant-default");
                    otherTenant.TenantId = tenant.Id;
                    otherTenant = await testDb.Driver.Captains.CreateAsync(otherTenant).ConfigureAwait(false);
                    Captain locked = new Captain("locked-default");
                    locked.AllowedPersonas = "[\"Worker\"]";
                    locked = await testDb.Driver.Captains.CreateAsync(locked).ConfigureAwait(false);

                    string unknown = JsonSerializer.Serialize(await tools["update_persona"](JsonSerializer.SerializeToElement(new { name = "GuardedReviewer", defaultCaptainId = "cpt_examplemissing" })).ConfigureAwait(false));
                    AssertContains(PersonaDefaultCaptainRule.NotFoundErrorCode, unknown, "an unknown captain is refused");

                    string foreign = JsonSerializer.Serialize(await tools["update_persona"](JsonSerializer.SerializeToElement(new { name = "GuardedReviewer", defaultCaptainId = otherTenant.Id })).ConfigureAwait(false));
                    AssertContains(PersonaDefaultCaptainRule.NotFoundErrorCode, foreign, "a captain from another tenant counts as not found");

                    string fenced = JsonSerializer.Serialize(await tools["update_persona"](JsonSerializer.SerializeToElement(new { name = "GuardedReviewer", defaultCaptainId = locked.Id })).ConfigureAwait(false));
                    AssertContains(PersonaDefaultCaptainRule.PersonaLockedErrorCode, fenced, "a captain whose allow-list excludes the persona is refused");

                    Persona? unchanged = await testDb.Driver.Personas.ReadAsync(persona!.Id).ConfigureAwait(false);
                    AssertNull(unchanged!.DefaultCaptainId, "a refused update stores nothing");
                }
            });

            await RunTest("Mcp persona create sets the default captain through the shared rule", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, Func<JsonElement?, Task<object>>> tools = PersonaTools(testDb.Driver);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("create-default-mcp")).ConfigureAwait(false);
                    TenantMetadata tenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("OtherCreateDefaultTenant")).ConfigureAwait(false);
                    Captain otherTenant = new Captain("other-tenant-create-default");
                    otherTenant.TenantId = tenant.Id;
                    otherTenant = await testDb.Driver.Captains.CreateAsync(otherTenant).ConfigureAwait(false);

                    await tools["create_persona"](JsonSerializer.SerializeToElement(new { name = "CreatedDefaulted", promptTemplateName = "persona.worker", defaultCaptainId = captain.Id })).ConfigureAwait(false);
                    Persona? created = await testDb.Driver.Personas.ReadByNameAsync("CreatedDefaulted").ConfigureAwait(false);
                    AssertNotNull(created, "the persona is created");
                    AssertEqual(captain.Id, created!.DefaultCaptainId, "the default captain named on create is stored");

                    string foreign = JsonSerializer.Serialize(await tools["create_persona"](JsonSerializer.SerializeToElement(new { name = "CreatedForeign", promptTemplateName = "persona.worker", defaultCaptainId = otherTenant.Id })).ConfigureAwait(false));
                    AssertContains(PersonaDefaultCaptainRule.NotFoundErrorCode, foreign, "a captain from another tenant is refused");
                    AssertNull(await testDb.Driver.Personas.ReadByNameAsync("CreatedForeign").ConfigureAwait(false), "a refused create writes nothing");
                }
            });

            await RunTest("WebSocket persona update sets, clears and refuses the default captain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Persona persona = await testDb.Driver.Personas.CreateAsync(new Persona("WsDefaulted", "persona.worker")).ConfigureAwait(false);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("default-ws")).ConfigureAwait(false);
                    WebSocketCommandHandler handler = new WebSocketCommandHandler(null!, testDb.Driver, null!, null, null, null, _ServerJsonOptions, mission => { }, voyage => { });
                    WebSocketCommand command = new WebSocketCommand { Action = "update_persona", Id = "WsDefaulted" };

                    string set = JsonSerializer.Serialize(new { Route = "command", action = "update_persona", id = "WsDefaulted", data = new { defaultCaptainId = captain.Id } });
                    await handler.HandleCommandAsync("update_persona", command, set, McpTestCaller.Operator).ConfigureAwait(false);
                    AssertEqual(captain.Id, (await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false))!.DefaultCaptainId, "the default captain is set");

                    string missing = JsonSerializer.Serialize(new { Route = "command", action = "update_persona", id = "WsDefaulted", data = new { defaultCaptainId = "cpt_examplemissing" } });
                    string refused = JsonSerializer.Serialize(await handler.HandleCommandAsync("update_persona", command, missing, McpTestCaller.Operator).ConfigureAwait(false));
                    AssertContains(PersonaDefaultCaptainRule.NotFoundErrorCode, refused, "an unknown captain is refused");
                    AssertEqual(captain.Id, (await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false))!.DefaultCaptainId, "a refused update keeps the stored captain");

                    string clear = JsonSerializer.Serialize(new { Route = "command", action = "update_persona", id = "WsDefaulted", data = new { defaultCaptainId = (string?)null } });
                    await handler.HandleCommandAsync("update_persona", command, clear, McpTestCaller.Operator).ConfigureAwait(false);
                    AssertNull((await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false))!.DefaultCaptainId, "a null default captain clears it");
                }
            });
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Compare one captain field from a tool response against the same field read back from the
        /// database. Timestamps are compared with a millisecond tolerance, because a driver may round a
        /// stored time on the round trip and that difference is not a projection defect.
        /// </summary>
        /// <param name="fromResponse">Value carried by the tool response.</param>
        /// <param name="fromDatabase">Value read back from the database.</param>
        /// <returns>True when the two values agree.</returns>
        private static bool SameFieldValue(object? fromResponse, object? fromDatabase)
        {
            if (fromResponse is DateTime responseTime && fromDatabase is DateTime databaseTime)
                return Math.Abs((responseTime - databaseTime).TotalSeconds) < 1;

            return Equals(fromResponse, fromDatabase);
        }

        private static Dictionary<string, Func<JsonElement?, Task<object>>> CaptainTools(DatabaseDriver database)
        {
            Dictionary<string, Func<JsonElement?, Task<object>>> tools = new Dictionary<string, Func<JsonElement?, Task<object>>>(StringComparer.Ordinal);
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            ArmadaSettings settings = new ArmadaSettings();
            McpCaptainTools.Register(
                (name, _, _, handler) => tools[name] = McpTestCaller.Wrap(handler),
                database,
                null!,
                settings,
                null,
                null,
                logging,
                new CaptainQuarantineService(database, settings, logging));
            return tools;
        }

        private static Dictionary<string, Func<JsonElement?, Task<object>>> PersonaTools(DatabaseDriver database)
        {
            Dictionary<string, Func<JsonElement?, Task<object>>> tools = new Dictionary<string, Func<JsonElement?, Task<object>>>(StringComparer.Ordinal);
            McpPersonaTools.Register((name, _, _, handler) => tools[name] = McpTestCaller.Wrap(handler), database);
            return tools;
        }

        #endregion
    }
}
