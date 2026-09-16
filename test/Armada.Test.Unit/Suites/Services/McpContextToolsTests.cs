namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Context;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for the captain-facing context fetch tool. It is registered in the mission-scoped
    /// catalogue, returns the relevant leaves for a query, includes a vessel's must_retrieve safety
    /// leaf when the mission names that vessel, enforces the per-mission call budget, and writes no
    /// Armada record.
    /// </summary>
    public class McpContextToolsTests : TestSuite
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>Suite name.</summary>
        public override string Name => "MCP Context Tools";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("The fetch tool is registered and mission-scoped", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Harness harness = Harness.Create(testDb);
                    AssertTrue(harness.Handlers.ContainsKey("armada_fetch_context"), "the fetch tool is registered");

                    AuthContext captain = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "Bearer");
                    AssertTrue(McpToolAccessPolicy.IsAllowed(captain, "armada_fetch_context"), "a mission caller may use the fetch tool");
                }
            });

            await RunTest("Returns leaves for a query and writes no record", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Harness harness = Harness.Create(testDb);

                    int before = (await testDb.Driver.Events.EnumerateRecentAsync(500).ConfigureAwait(false)).Count;

                    string response = await harness.CallAsync("armada_fetch_context", new { query = "alpha review general" }).ConfigureAwait(false);
                    AssertContains("\"available\":true", Compact(response));
                    // The general and alpha leaves are returned; the core is NOT (it ships in the brief).
                    AssertContains("leaf.general", response);
                    AssertFalse(response.Contains("core.a", StringComparison.Ordinal), "the fetch tool returns leaves only, not core");

                    int after = (await testDb.Driver.Events.EnumerateRecentAsync(500).ConfigureAwait(false)).Count;
                    AssertEqual(before, after, "the read-only fetch tool writes no Armada record");
                }
            });

            await RunTest("A mission's vessel pulls its must_retrieve safety leaf", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Harness harness = Harness.Create(testDb);
                    string missionId = await harness.SeedMissionAsync(testDb, "ExampleVessel").ConfigureAwait(false);

                    string response = await harness.CallAsync("armada_fetch_context", new { query = "unrelated", missionId }).ConfigureAwait(false);
                    AssertContains("leaf.safety.example", response);
                    AssertContains("\"mustRetrieve\":true", Compact(response));
                }
            });

            await RunTest("The per-mission call budget is enforced", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Harness harness = Harness.Create(testDb, maxCallsPerMission: 2);
                    string missionId = await harness.SeedMissionAsync(testDb, "ExampleVessel").ConfigureAwait(false);

                    string first = await harness.CallAsync("armada_fetch_context", new { query = "alpha", missionId }).ConfigureAwait(false);
                    string second = await harness.CallAsync("armada_fetch_context", new { query = "beta", missionId }).ConfigureAwait(false);
                    string third = await harness.CallAsync("armada_fetch_context", new { query = "general", missionId }).ConfigureAwait(false);

                    AssertContains("\"available\":true", Compact(first));
                    AssertContains("\"available\":true", Compact(second));
                    AssertContains("\"available\":false", Compact(third));
                    AssertContains("budget", third);
                }
            });

            await RunTest("Disabled: the tool returns unavailable", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Harness harness = Harness.Create(testDb, enabled: false);
                    string response = await harness.CallAsync("armada_fetch_context", new { query = "alpha" }).ConfigureAwait(false);
                    AssertContains("\"available\":false", Compact(response));
                    AssertContains("disabled", response);
                }
            });
        }

        private static string Compact(string json)
        {
            return json.Replace(" ", "").Replace("\n", "").Replace("\r", "");
        }

        private sealed class Harness
        {
            public Dictionary<string, Func<JsonElement?, Task<object>>> Handlers { get; } =
                new Dictionary<string, Func<JsonElement?, Task<object>>>();

            public static Harness Create(TestDatabase testDb, bool enabled = true, int maxCallsPerMission = 60)
            {
                McpContextTools.ResetBudgetForTests();
                ArmadaSettings settings = new ArmadaSettings();
                settings.ContextRetrieval.FetchToolEnabled = enabled;
                settings.ContextRetrieval.MaxCallsPerMission = maxCallsPerMission;

                Harness harness = new Harness();
                ContextRetrievalService retrieval = new ContextRetrievalService(SampleChunks());
                McpContextTools.Register(
                    (name, _, _, handler) => { harness.Handlers[name] = handler; },
                    retrieval,
                    testDb.Driver,
                    settings,
                    new LoggingModule());
                return harness;
            }

            public async Task<string> CallAsync(string tool, object args)
            {
                JsonElement element = JsonSerializer.SerializeToElement(args, _JsonOptions);
                AuthContext caller = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "Bearer");
                using (McpCallerContext.Begin(caller))
                {
                    object result = await Handlers[tool](element).ConfigureAwait(false);
                    return JsonSerializer.Serialize(result, _JsonOptions);
                }
            }

            public async Task<string> SeedMissionAsync(TestDatabase testDb, string vesselName)
            {
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                    new Vessel(vesselName, "https://github.com/test/repo.git")).ConfigureAwait(false);

                Mission mission = new Mission();
                mission.TenantId = Constants.DefaultTenantId;
                mission.UserId = Constants.DefaultUserId;
                mission.VesselId = vessel.Id;
                mission.Title = "seed mission";
                mission.Status = MissionStatusEnum.InProgress;
                Mission created = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                return created.Id;
            }
        }

        private static List<ContextChunk> SampleChunks()
        {
            return new List<ContextChunk>
            {
                Core("core.a", 1, "First core rule."),
                Leaf("leaf.general", new List<string> { "all" }, null, "General leaf about alpha and review and general."),
                Leaf("leaf.alpha", new List<string> { "all" }, null, "Alpha leaf mentioning alpha."),
                Leaf("leaf.beta", new List<string> { "all" }, null, "Beta leaf mentioning beta and general."),
                Leaf("leaf.safety.example", new List<string> { "all" }, new List<string> { "vessel:ExampleVessel" }, "Safety leaf for ExampleVessel."),
            };
        }

        private static ContextChunk Core(string topic, int order, string body)
        {
            return new ContextChunk
            {
                Topic = topic,
                Path = "AI-Memory/shared/" + topic + ".md",
                Summary = body,
                AppliesTo = new List<string> { "all" },
                Tier = ContextTierEnum.Core,
                Text = body,
                CoreOrder = order
            };
        }

        private static ContextChunk Leaf(string topic, List<string> appliesTo, List<string>? mustRetrieve, string body)
        {
            return new ContextChunk
            {
                Topic = topic,
                Path = "AI-Memory/repos/" + topic + ".md",
                Summary = body,
                ReadWhen = "When the task needs " + topic + ".",
                AppliesTo = appliesTo,
                Tier = ContextTierEnum.Leaf,
                MustRetrieve = mustRetrieve ?? new List<string>(),
                Text = body,
                CoreOrder = int.MaxValue
            };
        }
    }
}
