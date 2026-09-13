namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for the native memory MCP tools: the registered names, the write-search-read-correct-delete
    /// round trip a captain performs, the refusals a captain must be able to act on, the tenant fence,
    /// and the proof that every memory path works while the learned-facts feature is disabled.
    /// </summary>
    public class McpMemoryToolsTests : TestSuite
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>Suite name.</summary>
        public override string Name => "MCP Memory Tools";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("The five memory tools are registered", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterMemoryTools(testDb);
                    foreach (string name in new[] { "search_memory", "get_memory", "create_memory", "update_memory", "delete_memory" })
                        AssertTrue(handlers.ContainsKey(name), "Tool should be registered: " + name);
                }
            });

            await RunTest("A captain records, recalls, corrects and prunes one record", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterMemoryTools(testDb);

                    string created = await CallAsync(handlers, "create_memory", new
                    {
                        type = "Procedural",
                        topic = "build",
                        key = "build/quiet-window",
                        summary = "Run a suspect failure alone",
                        content = "The suite is load sensitive. Re-run a single failure alone before triage.",
                        salience = 0.8,
                        tags = new[] { "tests" },
                        sourceKind = "Voyage",
                        vesselId = "vsl_example"
                    }).ConfigureAwait(false);
                    AssertContains("mem_", created);
                    string memoryId = ValueOf(created, "id");

                    string upserted = await CallAsync(handlers, "create_memory", new
                    {
                        type = "Procedural",
                        topic = "build",
                        key = "build/quiet-window",
                        content = "The suite is load sensitive. Re-run a single failure alone, twice, before triage.",
                        vesselId = "vsl_example"
                    }).ConfigureAwait(false);
                    AssertEqual(memoryId, ValueOf(upserted, "id"), "The same key writes the same record");
                    AssertContains("\"version\":2", Compact(upserted));
                    // A write by key replaces the record's fields, so a field the second write omits is cleared.
                    AssertFalse(Compact(upserted).Contains("\"tags\":[\"tests\"]", StringComparison.Ordinal), "The omitted tag list is replaced, not merged");

                    string search = await CallAsync(handlers, "search_memory", new { search = "load sensitive", vesselId = "vsl_example" }).ConfigureAwait(false);
                    AssertContains(memoryId, search);

                    string read = await CallAsync(handlers, "get_memory", new { memoryId = memoryId }).ConfigureAwait(false);
                    AssertContains("twice", read);

                    string updated = await CallAsync(handlers, "update_memory", new { memoryId = memoryId, salience = 0.95, expectedVersion = 2 }).ConfigureAwait(false);
                    AssertContains("\"version\":3", Compact(updated));

                    string deleted = await CallAsync(handlers, "delete_memory", new { memoryId = memoryId }).ConfigureAwait(false);
                    AssertContains("deleted", deleted);
                    AssertContains("not_found", await CallAsync(handlers, "get_memory", new { memoryId = memoryId }).ConfigureAwait(false));
                }
            });

            await RunTest("A refused write says why, and changes nothing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterMemoryTools(testDb);
                    string created = await CallAsync(handlers, "create_memory", new { content = "a finding", key = "one" }).ConfigureAwait(false);
                    string memoryId = ValueOf(created, "id");
                    await CallAsync(handlers, "update_memory", new { memoryId = memoryId, content = "writer one" }).ConfigureAwait(false);

                    string stale = await CallAsync(handlers, "update_memory", new { memoryId = memoryId, content = "writer two", expectedVersion = 1 }).ConfigureAwait(false);
                    AssertContains("conflict", stale);
                    AssertContains("version", stale);

                    string read = await CallAsync(handlers, "get_memory", new { memoryId = memoryId }).ConfigureAwait(false);
                    AssertContains("writer one", read);

                    AssertContains("invalid", await CallAsync(handlers, "create_memory", new { content = "x", key = "not a slug" }).ConfigureAwait(false));
                    AssertContains("invalid", await CallAsync(handlers, "create_memory", new { content = "x", type = "Nonsense" }).ConfigureAwait(false));
                    AssertContains("invalid", await CallAsync(handlers, "get_memory", new { memoryId = "" }).ConfigureAwait(false));
                    AssertContains("not_found", await CallAsync(handlers, "delete_memory", new { memoryId = "mem_absent" }).ConfigureAwait(false));
                }
            });

            await RunTest("The tools never reach another tenant's record", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterMemoryTools(testDb);

                    Memory foreignRecord = new Memory();
                    foreignRecord.TenantId = "ten_other";
                    foreignRecord.UserId = "usr_other";
                    foreignRecord.Content = "another tenant's finding";
                    await testDb.Driver.Memories.CreateAsync(foreignRecord).ConfigureAwait(false);

                    string search = await CallAsync(handlers, "search_memory", new { search = "another tenant" }).ConfigureAwait(false);
                    AssertFalse(search.Contains(foreignRecord.Id, StringComparison.Ordinal), "A foreign record must not appear in search");
                    AssertContains("not_found", await CallAsync(handlers, "get_memory", new { memoryId = foreignRecord.Id }).ConfigureAwait(false));
                    AssertContains("not_found", await CallAsync(handlers, "update_memory", new { memoryId = foreignRecord.Id, content = "rewritten" }).ConfigureAwait(false));
                    AssertContains("not_found", await CallAsync(handlers, "delete_memory", new { memoryId = foreignRecord.Id }).ConfigureAwait(false));

                    Memory? stored = await testDb.Driver.Memories.ReadAsync(foreignRecord.Id).ConfigureAwait(false);
                    AssertEqual("another tenant's finding", stored!.Content, "The foreign record is untouched");
                }
            });

            await RunTest("Every native memory path works", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings();

                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                    McpToolRegistrar.RegisterAll(
                        (name, _, _, handler) => { handlers[name] = handler; },
                        testDb.Driver,
                        new StubAdmiralService(),
                        settings: settings);

                    foreach (string name in new[] { "search_memory", "get_memory", "create_memory", "update_memory", "delete_memory" })
                        AssertTrue(handlers.ContainsKey(name), "Native memory tool registered: " + name);

                    string created = await CallAsync(handlers, "create_memory", new { content = "a finding recorded with learned facts off", key = "off/one" }).ConfigureAwait(false);
                    string memoryId = ValueOf(created, "id");
                    AssertContains(memoryId, await CallAsync(handlers, "search_memory", new { search = "learned facts off" }).ConfigureAwait(false));
                    AssertContains("a finding", await CallAsync(handlers, "get_memory", new { memoryId = memoryId }).ConfigureAwait(false));
                    AssertContains("\"version\":2", Compact(await CallAsync(handlers, "update_memory", new { memoryId = memoryId, salience = 0.7 }).ConfigureAwait(false)));
                    AssertContains("deleted", await CallAsync(handlers, "delete_memory", new { memoryId = memoryId }).ConfigureAwait(false));

                    string enumerated = await CallAsync(handlers, "armada_enumerate", new { entityType = "memories" }).ConfigureAwait(false);
                    AssertContains("totalrecords", Compact(enumerated).ToLowerInvariant());
                }
            });
        }

        private static Dictionary<string, Func<JsonElement?, Task<object>>> RegisterMemoryTools(TestDatabase testDb)
        {
            Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
            McpMemoryTools.Register((name, _, _, handler) => { handlers[name] = handler; }, testDb.Driver);
            return handlers;
        }

        private static async Task<string> CallAsync(Dictionary<string, Func<JsonElement?, Task<object>>> handlers, string tool, object args)
        {
            JsonElement element = JsonSerializer.SerializeToElement(args, _JsonOptions);
            object result = await handlers[tool](element).ConfigureAwait(false);
            return JsonSerializer.Serialize(result, _JsonOptions);
        }

        private static string Compact(string json)
        {
            return json.Replace(" ", "").Replace("\n", "").Replace("\r", "");
        }

        private static string ValueOf(string json, string property)
        {
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                return document.RootElement.GetProperty(property).GetString()!;
            }
        }

        #region Private-Types

        private sealed class StubAdmiralService : IAdmiralService
        {
            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => Task.FromResult<Pipeline?>(null);

            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
                => Task.FromResult(new ArmadaStatus());

            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
                => Task.CompletedTask;

            public Task RecallAllAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task StopAllAgentProcessesAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task HealthCheckAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
                => Task.CompletedTask;
        }

        #endregion
    }
}
