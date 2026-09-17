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
