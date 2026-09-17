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
    /// through the MCP captain tools, and a persona's specialist flag through the MCP persona tools and the
    /// WebSocket persona command. A write that omits a field keeps its stored value.
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
                    await handler.HandleCommandAsync("update_persona", new WebSocketCommand { Action = "update_persona", Id = "WsReviewer" }, flag).ConfigureAwait(false);
                    AssertTrue((await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false))!.Specialist, "the flag is set");

                    string describe = JsonSerializer.Serialize(new { Route = "command", action = "update_persona", id = "WsReviewer", data = new { description = "reviews" } });
                    await handler.HandleCommandAsync("update_persona", new WebSocketCommand { Action = "update_persona", Id = "WsReviewer" }, describe).ConfigureAwait(false);
                    Persona? kept = await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false);
                    AssertEqual("reviews", kept!.Description);
                    AssertTrue(kept.Specialist, "an update that omits the flag keeps it");
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
