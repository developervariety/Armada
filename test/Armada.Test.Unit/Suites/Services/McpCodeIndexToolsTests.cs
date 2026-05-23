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
                AssertTrue(handlers.ContainsKey("armada_fleet_code_search"));
                AssertTrue(handlers.ContainsKey("armada_fleet_context_pack"));
                AssertTrue(handlers.ContainsKey("armada_graph_search_symbols"));
                AssertTrue(handlers.ContainsKey("armada_graph_get_callers"));
                AssertTrue(handlers.ContainsKey("armada_graph_get_callees"));
                AssertTrue(handlers.ContainsKey("armada_graph_get_impact"));
                AssertTrue(handlers.ContainsKey("armada_graph_suggest_affected_tests"));
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

            await RunTest("armada_fleet_code_search delegates typed request", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                service.FleetSearchResponse = new FleetCodeSearchResponse
                {
                    FleetId = "flt_test",
                    Query = "needle"
                };

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    fleetId = "flt_test",
                    query = "needle",
                    limit = 2,
                    includeContent = true
                });

                object result = await handlers["armada_fleet_code_search"](args).ConfigureAwait(false);

                FleetCodeSearchResponse response = (FleetCodeSearchResponse)result;
                AssertNotNull(service.LastFleetSearchRequest);
                AssertEqual("flt_test", service.LastFleetSearchRequest!.FleetId);
                AssertEqual("needle", service.LastFleetSearchRequest.Query);
                AssertEqual(2, service.LastFleetSearchRequest.Limit);
                AssertTrue(service.LastFleetSearchRequest.IncludeContent);
                AssertEqual("needle", response.Query);
            });

            await RunTest("armada_fleet_context_pack delegates and returns prestaged file", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                service.FleetContextPackResponse = new FleetContextPackResponse
                {
                    FleetId = "flt_test",
                    Goal = "build context",
                    Markdown = "# pack\n",
                    EstimatedTokens = 2,
                    MaterializedPath = "C:/tmp/fleet-context.md"
                };
                service.FleetContextPackResponse.PrestagedFiles.Add(new PrestagedFile("C:/tmp/fleet-context.md", "_briefing/context-pack.md"));

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    fleetId = "flt_test",
                    goal = "build context",
                    tokenBudget = 1000,
                    maxResultsPerVessel = 3
                });

                object result = await handlers["armada_fleet_context_pack"](args).ConfigureAwait(false);

                FleetContextPackResponse response = (FleetContextPackResponse)result;
                AssertNotNull(service.LastFleetContextPackRequest);
                AssertEqual("flt_test", service.LastFleetContextPackRequest!.FleetId);
                AssertEqual("build context", service.LastFleetContextPackRequest.Goal);
                AssertEqual(1000, service.LastFleetContextPackRequest.TokenBudget);
                AssertEqual(3, service.LastFleetContextPackRequest.MaxResultsPerVessel);
                AssertEqual("_briefing/context-pack.md", response.PrestagedFiles[0].DestPath);
            });

            await RunTest("armada_graph_search_symbols delegates typed request", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                service.SymbolSearchResponse = new CodeGraphSymbolSearchResponse
                {
                    Query = "Execute",
                    Status = NewStatus("vsl_test")
                };

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    vesselId = "vsl_test",
                    query = "Execute",
                    limit = 5,
                    pathPrefix = "src/"
                });

                object result = await handlers["armada_graph_search_symbols"](args).ConfigureAwait(false);

                CodeGraphSymbolSearchResponse response = (CodeGraphSymbolSearchResponse)result;
                AssertNotNull(service.LastSymbolSearchRequest);
                AssertEqual("vsl_test", service.LastSymbolSearchRequest!.VesselId);
                AssertEqual("Execute", service.LastSymbolSearchRequest.Query);
                AssertEqual(5, service.LastSymbolSearchRequest.Limit);
                AssertEqual("src/", service.LastSymbolSearchRequest.PathPrefix);
                AssertEqual("Execute", response.Query);
            });

            await RunTest("armada_graph_get_callers delegates typed request", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                service.NeighborsResponse = new CodeGraphNeighborsResponse
                {
                    RequestedSymbol = "DoWork",
                    Status = NewStatus("vsl_test")
                };

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    vesselId = "vsl_test",
                    symbol = "DoWork",
                    limit = 10
                });

                object result = await handlers["armada_graph_get_callers"](args).ConfigureAwait(false);

                CodeGraphNeighborsResponse response = (CodeGraphNeighborsResponse)result;
                AssertNotNull(service.LastCallersRequest);
                AssertEqual("vsl_test", service.LastCallersRequest!.VesselId);
                AssertEqual("DoWork", service.LastCallersRequest.Symbol);
                AssertEqual(10, service.LastCallersRequest.Limit);
                AssertEqual("DoWork", response.RequestedSymbol);
            });

            await RunTest("armada_graph_get_callees delegates typed request", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                service.NeighborsResponse = new CodeGraphNeighborsResponse
                {
                    RequestedSymbol = "DoWork",
                    Status = NewStatus("vsl_test")
                };

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    vesselId = "vsl_test",
                    symbol = "DoWork",
                    limit = 15
                });

                object result = await handlers["armada_graph_get_callees"](args).ConfigureAwait(false);

                CodeGraphNeighborsResponse response = (CodeGraphNeighborsResponse)result;
                AssertNotNull(service.LastCalleesRequest);
                AssertEqual("vsl_test", service.LastCalleesRequest!.VesselId);
                AssertEqual("DoWork", service.LastCalleesRequest.Symbol);
                AssertEqual(15, service.LastCalleesRequest.Limit);
                AssertEqual("DoWork", response.RequestedSymbol);
            });

            await RunTest("armada_graph_get_impact delegates typed request", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                service.ImpactResponse = new CodeGraphImpactResponse
                {
                    RequestedSymbol = "Execute",
                    MaxDepth = 4,
                    Status = NewStatus("vsl_test")
                };

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    vesselId = "vsl_test",
                    symbol = "Execute",
                    direction = "Both",
                    maxDepth = 4,
                    maxResults = 30
                });

                object result = await handlers["armada_graph_get_impact"](args).ConfigureAwait(false);

                CodeGraphImpactResponse response = (CodeGraphImpactResponse)result;
                AssertNotNull(service.LastImpactRequest);
                AssertEqual("vsl_test", service.LastImpactRequest!.VesselId);
                AssertEqual("Execute", service.LastImpactRequest.Symbol);
                AssertEqual(4, service.LastImpactRequest.MaxDepth);
                AssertEqual(30, service.LastImpactRequest.MaxResults);
                AssertEqual("Execute", response.RequestedSymbol);
            });

            await RunTest("armada_graph_suggest_affected_tests delegates typed request", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                service.AffectedTestsResponse = new CodeGraphAffectedTestsResponse
                {
                    RequestedSymbol = "Execute",
                    MaxDepth = 3,
                    Status = NewStatus("vsl_test")
                };

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    vesselId = "vsl_test",
                    symbol = "Execute",
                    maxDepth = 3,
                    maxResults = 10
                });

                object result = await handlers["armada_graph_suggest_affected_tests"](args).ConfigureAwait(false);

                CodeGraphAffectedTestsResponse response = (CodeGraphAffectedTestsResponse)result;
                AssertNotNull(service.LastAffectedTestsRequest);
                AssertEqual("vsl_test", service.LastAffectedTestsRequest!.VesselId);
                AssertEqual("Execute", service.LastAffectedTestsRequest.Symbol);
                AssertEqual(3, service.LastAffectedTestsRequest.MaxDepth);
                AssertEqual(10, service.LastAffectedTestsRequest.MaxResults);
                AssertEqual("Execute", response.RequestedSymbol);
            });

            await RunTest("Graph tool handlers reject missing required arguments", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);

                object noArgs = await handlers["armada_graph_search_symbols"](null).ConfigureAwait(false);
                AssertContains("missing args", JsonSerializer.Serialize(noArgs));

                JsonElement missingVessel = JsonSerializer.SerializeToElement(new { query = "Foo" });
                object missingVesselResult = await handlers["armada_graph_search_symbols"](missingVessel).ConfigureAwait(false);
                AssertContains("vesselId is required", JsonSerializer.Serialize(missingVesselResult));

                JsonElement missingQuery = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test" });
                object missingQueryResult = await handlers["armada_graph_search_symbols"](missingQuery).ConfigureAwait(false);
                AssertContains("query is required", JsonSerializer.Serialize(missingQueryResult));

                JsonElement missingSymbolForCallers = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test" });
                object missingSymbolCallersResult = await handlers["armada_graph_get_callers"](missingSymbolForCallers).ConfigureAwait(false);
                AssertContains("symbol is required", JsonSerializer.Serialize(missingSymbolCallersResult));

                JsonElement missingSymbolForImpact = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test" });
                object missingSymbolImpactResult = await handlers["armada_graph_get_impact"](missingSymbolForImpact).ConfigureAwait(false);
                AssertContains("symbol is required", JsonSerializer.Serialize(missingSymbolImpactResult));
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

                JsonElement missingFleetId = JsonSerializer.SerializeToElement(new
                {
                    query = "needle"
                });
                object missingFleetIdResult = await handlers["armada_fleet_code_search"](missingFleetId).ConfigureAwait(false);
                string missingFleetIdJson = JsonSerializer.Serialize(missingFleetIdResult);
                AssertContains("fleetId is required", missingFleetIdJson);
                AssertEqual(null, service.LastFleetSearchRequest, "Invalid fleet search should not delegate to service");

                JsonElement missingFleetQuery = JsonSerializer.SerializeToElement(new
                {
                    fleetId = "flt_test"
                });
                object missingFleetQueryResult = await handlers["armada_fleet_code_search"](missingFleetQuery).ConfigureAwait(false);
                string missingFleetQueryJson = JsonSerializer.Serialize(missingFleetQueryResult);
                AssertContains("query is required", missingFleetQueryJson);
                AssertEqual(null, service.LastFleetSearchRequest, "Invalid fleet search should not delegate to service");

                JsonElement missingFleetGoal = JsonSerializer.SerializeToElement(new
                {
                    fleetId = "flt_test",
                    tokenBudget = 1000
                });
                object missingFleetGoalResult = await handlers["armada_fleet_context_pack"](missingFleetGoal).ConfigureAwait(false);
                string missingFleetGoalJson = JsonSerializer.Serialize(missingFleetGoalResult);
                AssertContains("goal is required", missingFleetGoalJson);
                AssertEqual(null, service.LastFleetContextPackRequest, "Invalid fleet context-pack request should not delegate to service");
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

            public FleetCodeSearchRequest? LastFleetSearchRequest { get; private set; }

            public FleetContextPackRequest? LastFleetContextPackRequest { get; private set; }

            public CodeGraphSymbolSearchRequest? LastSymbolSearchRequest { get; private set; }

            public CodeGraphNeighborsRequest? LastCallersRequest { get; private set; }

            public CodeGraphNeighborsRequest? LastCalleesRequest { get; private set; }

            public CodeGraphImpactRequest? LastImpactRequest { get; private set; }

            public CodeGraphAffectedTestsRequest? LastAffectedTestsRequest { get; private set; }

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

            public FleetCodeSearchResponse FleetSearchResponse { get; set; } = new FleetCodeSearchResponse
            {
                FleetId = "flt_default",
                Query = "default"
            };

            public FleetContextPackResponse FleetContextPackResponse { get; set; } = new FleetContextPackResponse
            {
                FleetId = "flt_default",
                Goal = "default",
                Markdown = "# default\n",
                EstimatedTokens = 3,
                MaterializedPath = "C:/tmp/default-fleet.md"
            };

            public CodeGraphSymbolSearchResponse SymbolSearchResponse { get; set; } = new CodeGraphSymbolSearchResponse
            {
                Status = NewStatus("vsl_default"),
                Query = "default"
            };

            public CodeGraphNeighborsResponse NeighborsResponse { get; set; } = new CodeGraphNeighborsResponse
            {
                Status = NewStatus("vsl_default"),
                RequestedSymbol = "default"
            };

            public CodeGraphImpactResponse ImpactResponse { get; set; } = new CodeGraphImpactResponse
            {
                Status = NewStatus("vsl_default"),
                RequestedSymbol = "default"
            };

            public CodeGraphAffectedTestsResponse AffectedTestsResponse { get; set; } = new CodeGraphAffectedTestsResponse
            {
                Status = NewStatus("vsl_default"),
                RequestedSymbol = "default"
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

            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default)
            {
                LastFleetSearchRequest = request;
                return Task.FromResult(FleetSearchResponse);
            }

            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default)
            {
                LastFleetContextPackRequest = request;
                return Task.FromResult(FleetContextPackResponse);
            }

            public Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default)
            {
                LastSymbolSearchRequest = request;
                return Task.FromResult(SymbolSearchResponse);
            }

            public Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
            {
                LastCallersRequest = request;
                return Task.FromResult(NeighborsResponse);
            }

            public Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
            {
                LastCalleesRequest = request;
                return Task.FromResult(NeighborsResponse);
            }

            public Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default)
            {
                LastImpactRequest = request;
                return Task.FromResult(ImpactResponse);
            }

            public Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default)
            {
                LastAffectedTestsRequest = request;
                return Task.FromResult(AffectedTestsResponse);
            }
        }
    }
}
