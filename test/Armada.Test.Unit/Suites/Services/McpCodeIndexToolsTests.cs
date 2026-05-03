namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Text.Json;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for MCP tool registration and dispatch for code index tools.
    /// </summary>
    public class McpCodeIndexToolsTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "MCP Code Index Tools";

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Register adds all code index tools", () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);

                AssertTrue(handlers.ContainsKey("armada_index_status"));
                AssertTrue(handlers.ContainsKey("armada_index_update"));
                AssertTrue(handlers.ContainsKey("armada_code_search"));
                AssertTrue(handlers.ContainsKey("armada_context_pack"));
            });

            await RunTest("armada_context_pack delegates and returns prestaged file", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                service.ContextPackResponse = new ContextPackResponse
                {
                    Status = NewStatus("vsl_test"),
                    Goal = "build context",
                    Markdown = "# pack\n",
                    EstimatedTokens = 2,
                    MaterializedPath = "C:/tmp/context.md"
                };
                service.ContextPackResponse.PrestagedFiles.Add(new PrestagedFile("C:/tmp/context.md", "_briefing/context-pack.md"));

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    vesselId = "vsl_test",
                    goal = "build context",
                    tokenBudget = 1000,
                    maxResults = 3
                });

                object result = await handlers["armada_context_pack"](args).ConfigureAwait(false);

                ContextPackResponse response = (ContextPackResponse)result;
                AssertNotNull(service.LastContextPackRequest);
                AssertEqual("vsl_test", service.LastContextPackRequest!.VesselId);
                AssertEqual("build context", service.LastContextPackRequest.Goal);
                AssertEqual(1000, service.LastContextPackRequest.TokenBudget);
                AssertEqual(3, service.LastContextPackRequest.MaxResults);
                AssertEqual("_briefing/context-pack.md", response.PrestagedFiles[0].DestPath);
            });

            await RunTest("armada_code_search delegates typed request", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                service.SearchResponse = new CodeSearchResponse
                {
                    Status = NewStatus("vsl_test"),
                    Query = "needle"
                };

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    vesselId = "vsl_test",
                    query = "needle",
                    limit = 2,
                    includeContent = true
                });

                object result = await handlers["armada_code_search"](args).ConfigureAwait(false);

                CodeSearchResponse response = (CodeSearchResponse)result;
                AssertNotNull(service.LastSearchRequest);
                AssertEqual("vsl_test", service.LastSearchRequest!.VesselId);
                AssertEqual("needle", service.LastSearchRequest.Query);
                AssertEqual(2, service.LastSearchRequest.Limit);
                AssertTrue(service.LastSearchRequest.IncludeContent);
                AssertEqual("needle", response.Query);
            });

            await RunTest("Handlers reject missing required arguments", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);

                object missingArgs = await handlers["armada_index_status"](null).ConfigureAwait(false);
                string missingArgsJson = JsonSerializer.Serialize(missingArgs);
                AssertContains("missing args", missingArgsJson);

                JsonElement missingGoal = JsonSerializer.SerializeToElement(new
                {
                    vesselId = "vsl_test",
                    tokenBudget = 1000
                });
                object missingGoalResult = await handlers["armada_context_pack"](missingGoal).ConfigureAwait(false);
                string missingGoalJson = JsonSerializer.Serialize(missingGoalResult);
                AssertContains("goal is required", missingGoalJson);
            });
        }

        private static Dictionary<string, Func<JsonElement?, Task<object>>> RegisterHandlers(RecordingCodeIndexService service)
        {
            Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
            McpCodeIndexTools.Register(
                (name, _, _, handler) =>
                {
                    handlers[name] = handler;
                },
                service);
            return handlers;
        }

        private static CodeIndexStatus NewStatus(string vesselId)
        {
            return new CodeIndexStatus
            {
                VesselId = vesselId,
                VesselName = "Test Vessel",
                DefaultBranch = "main",
                IndexedCommitSha = "abc123",
                CurrentCommitSha = "abc123",
                IndexedAtUtc = DateTime.UtcNow,
                Freshness = "Fresh",
                DocumentCount = 1,
                ChunkCount = 1,
                IndexDirectory = "C:/tmp/index"
            };
        }

        private sealed class RecordingCodeIndexService : ICodeIndexService
        {
            public ContextPackRequest? LastContextPackRequest { get; private set; }

            public CodeSearchRequest? LastSearchRequest { get; private set; }

            public string? LastStatusVesselId { get; private set; }

            public string? LastUpdateVesselId { get; private set; }

            public ContextPackResponse ContextPackResponse { get; set; } = new ContextPackResponse
            {
                Status = NewStatus("vsl_default"),
                Goal = "default",
                Markdown = "# default\n",
                EstimatedTokens = 3,
                MaterializedPath = "C:/tmp/default.md"
            };

            public CodeSearchResponse SearchResponse { get; set; } = new CodeSearchResponse
            {
                Status = NewStatus("vsl_default"),
                Query = "default"
            };

            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
            {
                LastStatusVesselId = vesselId;
                return Task.FromResult(NewStatus(vesselId));
            }

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
            {
                LastUpdateVesselId = vesselId;
                return Task.FromResult(NewStatus(vesselId));
            }

            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
            {
                LastSearchRequest = request;
                return Task.FromResult(SearchResponse);
            }

            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
            {
                LastContextPackRequest = request;
                return Task.FromResult(ContextPackResponse);
            }
        }
    }
}
