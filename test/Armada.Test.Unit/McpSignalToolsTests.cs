namespace Armada.Test.Unit
{
    using System.Text.Json;
    using Armada.Core.Models;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for <see cref="McpSignalTools"/> armada_nudge_voyage tool: validation and successful creation.
    /// </summary>
    public sealed class McpSignalToolsTests : TestSuite
    {
        private static readonly JsonSerializerOptions _JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>Suite name.</summary>
        public override string Name => "McpSignalTools";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("NudgeVoyage_MissingBothTargets_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_nudge_voyage") handler = h; },
                        testDb.Driver);
                    AssertNotNull(handler, "armada_nudge_voyage handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { type = "Nudge", message = "hello" }, _JsonOpts);
                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("Exactly one of voyageId or missionId is required", resultJson);
                }
            });

            await RunTest("NudgeVoyage_BothTargetsProvided_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_nudge_voyage") handler = h; },
                        testDb.Driver);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        voyageId = "vyg_123",
                        missionId = "msn_456",
                        type = "Mail",
                        message = "both"
                    }, _JsonOpts);
                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("both were provided", resultJson);
                }
            });

            await RunTest("NudgeVoyage_InvalidType_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = new Voyage("type-test-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_nudge_voyage") handler = h; },
                        testDb.Driver);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        voyageId = voyage.Id,
                        type = "Heartbeat",
                        message = "wrong type"
                    }, _JsonOpts);
                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("type must be Nudge or Mail", resultJson);
                }
            });

            await RunTest("NudgeVoyage_EmptyMessage_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = new Voyage("empty-msg-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_nudge_voyage") handler = h; },
                        testDb.Driver);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        voyageId = voyage.Id,
                        type = "Nudge",
                        message = ""
                    }, _JsonOpts);
                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("message is required", resultJson);
                }
            });

            await RunTest("NudgeVoyage_VoyageNotFound_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_nudge_voyage") handler = h; },
                        testDb.Driver);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        voyageId = "vyg_doesnotexist",
                        type = "Nudge",
                        message = "test"
                    }, _JsonOpts);
                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("Voyage not found", resultJson);
                }
            });

            await RunTest("NudgeVoyage_MissionNotFound_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_nudge_voyage") handler = h; },
                        testDb.Driver);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        missionId = "msn_doesnotexist",
                        type = "Mail",
                        message = "test"
                    }, _JsonOpts);
                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("Mission not found", resultJson);
                }
            });

            await RunTest("NudgeVoyage_WithVoyageId_CreatesNudgeSignal", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = new Voyage("nudge-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_nudge_voyage") handler = h; },
                        testDb.Driver);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        voyageId = voyage.Id,
                        type = "Nudge",
                        message = "please focus on performance",
                        createdBy = "user_42"
                    }, _JsonOpts);
                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("sig_", resultJson, "Result should contain a signal ID");

                    // Verify payload is stored correctly
                    Signal? readBack = await testDb.Driver.Signals.ReadAsync(
                        JsonSerializer.Deserialize<Signal>(resultJson)!.Id).ConfigureAwait(false);
                    AssertNotNull(readBack);
                    AssertEqual("Nudge", readBack!.Type.ToString());
                    AssertFalse(readBack.Read, "New signal should not be marked read");
                    AssertNotNull(readBack.Payload);
                    AssertContains("\"voyageId\"", readBack.Payload!, "Payload should include voyageId");
                    AssertContains(voyage.Id, readBack.Payload!);
                    AssertContains("please focus on performance", readBack.Payload);
                }
            });

            await RunTest("NudgeVoyage_WithMissionId_CreatesMailSignalAndIncludesVoyageId", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = new Voyage("mail-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission mission = new Mission("mail-mission", "desc");
                    mission.VoyageId = voyage.Id;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_nudge_voyage") handler = h; },
                        testDb.Driver);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        missionId = mission.Id,
                        type = "Mail",
                        message = "check the tests please"
                    }, _JsonOpts);
                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("sig_", resultJson);

                    Signal? readBack = await testDb.Driver.Signals.ReadAsync(
                        JsonSerializer.Deserialize<Signal>(resultJson)!.Id).ConfigureAwait(false);
                    AssertNotNull(readBack);
                    AssertEqual("Mail", readBack!.Type.ToString());
                    AssertNotNull(readBack.Payload);
                    AssertContains(mission.Id, readBack.Payload!);
                    AssertContains(voyage.Id, readBack.Payload!, "Payload should include voyageId resolved from mission");
                    AssertContains("check the tests please", readBack.Payload);
                }
            });
        }
    }
}
