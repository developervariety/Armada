namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Server;
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
                AssertTrue(handlers.ContainsKey("armada_graph_get_node"));
                AssertTrue(handlers.ContainsKey("armada_graph_get_files"));
                AssertTrue(handlers.ContainsKey("armada_graph_explore"));
            });

            await RunTest("armada_index_update returns accepted job before work completes", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                service.UpdateStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                service.UpdateCompletion = new TaskCompletionSource<CodeIndexStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
                LongRunningJobService jobs = new LongRunningJobService();
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterJobHandlers(service, jobs);
                JsonElement args = JsonSerializer.SerializeToElement(new { vesselId = "vsl_background" });

                object result = await handlers["armada_index_update"](args).ConfigureAwait(false);
                LongRunningJob accepted = (LongRunningJob)result;

                AssertEqual(LongRunningJobStatusEnum.Accepted, accepted.Status);
                AssertTrue(accepted.JobId.StartsWith("job_", StringComparison.Ordinal));
                await service.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                object running = await GetJobStatusAsync(handlers, accepted.JobId).ConfigureAwait(false);
                AssertContains("\"Status\":\"Running\"", JsonSerializer.Serialize(running));

                service.UpdateCompletion.SetResult(NewStatus("vsl_background"));
                object succeeded = await WaitForJobStatusAsync(
                    handlers,
                    accepted.JobId,
                    LongRunningJobStatusEnum.Succeeded).ConfigureAwait(false);
                string succeededJson = JsonSerializer.Serialize(succeeded);
                AssertContains("\"Status\":\"Succeeded\"", succeededJson);
                AssertContains("\"Result\"", succeededJson);
                AssertContains("vsl_background", succeededJson);
                AssertFalse(succeededJson.Contains("\"Error\"", StringComparison.Ordinal));
            });

            await RunTest("armada_index_update reports bounded background failure", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService
                {
                    UpdateException = new InvalidOperationException("failure-prefix-" + new string('x', 2000))
                };
                LongRunningJobService jobs = new LongRunningJobService();
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterJobHandlers(service, jobs);
                JsonElement args = JsonSerializer.SerializeToElement(new { vesselId = "vsl_failure" });

                LongRunningJob accepted = (LongRunningJob)await handlers["armada_index_update"](args).ConfigureAwait(false);
                object failed = await WaitForJobStatusAsync(
                    handlers,
                    accepted.JobId,
                    LongRunningJobStatusEnum.Failed).ConfigureAwait(false);
                string failedJson = JsonSerializer.Serialize(failed);

                AssertContains("\"Status\":\"Failed\"", failedJson);
                AssertContains("failure-prefix-", failedJson);
                AssertFalse(failedJson.Contains(new string('x', 2000), StringComparison.Ordinal));
                AssertFalse(failedJson.Contains("\"Result\"", StringComparison.Ordinal));
            });

            await RunTest("armada_job_status validates missing and unknown job IDs", async () =>
            {
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterJobHandlers(
                    new RecordingCodeIndexService(),
                    new LongRunningJobService());

                object missingArgs = await handlers["armada_job_status"](null).ConfigureAwait(false);
                AssertContains("missing_job_id", JsonSerializer.Serialize(missingArgs));

                JsonElement missingJobId = JsonSerializer.SerializeToElement(new { jobId = "" });
                object missingJobIdResult = await handlers["armada_job_status"](missingJobId).ConfigureAwait(false);
                AssertContains("jobId is required", JsonSerializer.Serialize(missingJobIdResult));

                JsonElement unknownJobId = JsonSerializer.SerializeToElement(new { jobId = "job_unknown" });
                object unknownResult = await handlers["armada_job_status"](unknownJobId).ConfigureAwait(false);
                string unknownJson = JsonSerializer.Serialize(unknownResult);
                AssertContains("job_not_found", unknownJson);
                AssertContains("job_unknown", unknownJson);
            });

            await RunTest("LongRunningJobService returns snapshots and evicts oldest terminal job", async () =>
            {
                LongRunningJobService jobs = new LongRunningJobService(1);
                LongRunningJob firstAccepted = jobs.Start(
                    "first_operation",
                    (_) => Task.FromResult<object?>(new { value = 1 }));
                LongRunningJob firstComplete = await WaitForTrackedJobStatusAsync(
                    jobs,
                    firstAccepted.JobId,
                    LongRunningJobStatusEnum.Succeeded).ConfigureAwait(false);

                firstComplete.Status = LongRunningJobStatusEnum.Failed;
                AssertTrue(jobs.TryGetStatus(firstAccepted.JobId, out LongRunningJob? firstSnapshot));
                AssertEqual(LongRunningJobStatusEnum.Succeeded, firstSnapshot!.Status);

                LongRunningJob secondAccepted = jobs.Start(
                    "second_operation",
                    (_) => Task.FromResult<object?>(new { value = 2 }));
                await WaitForTrackedJobStatusAsync(
                    jobs,
                    secondAccepted.JobId,
                    LongRunningJobStatusEnum.Succeeded).ConfigureAwait(false);

                AssertFalse(jobs.TryGetStatus(firstAccepted.JobId, out LongRunningJob? _));
                AssertTrue(jobs.TryGetStatus(secondAccepted.JobId, out LongRunningJob? secondSnapshot));
                AssertEqual(LongRunningJobStatusEnum.Succeeded, secondSnapshot!.Status);
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
                    includeContent = true,
                    includeEmbeddings = true
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

            await RunTest("armada_code_search omits embedding vectors by default and restores them when requested", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                service.SearchResponse = new CodeSearchResponse
                {
                    Status = NewStatus("vsl_test"),
                    Query = "needle"
                };
                service.SearchResponse.Results.Add(new CodeSearchResult
                {
                    Score = 42,
                    Excerpt = "needle",
                    Record = new CodeIndexRecord
                    {
                        VesselId = "vsl_test",
                        Path = "src/Foo.cs",
                        CommitSha = "abc123",
                        ContentHash = "hash",
                        Language = "csharp",
                        StartLine = 1,
                        EndLine = 2,
                        Freshness = "Fresh",
                        Content = "needle",
                        EmbeddingVector = new[] { 0.25f, 0.5f }
                    }
                });

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);
                JsonElement defaultArgs = JsonSerializer.SerializeToElement(new
                {
                    vesselId = "vsl_test",
                    query = "needle"
                });
                string defaultJson = JsonSerializer.Serialize(await handlers["armada_code_search"](defaultArgs).ConfigureAwait(false));

                AssertFalse(defaultJson.Contains("EmbeddingVector", StringComparison.OrdinalIgnoreCase),
                    "default MCP search response should not include embedding vectors");
                AssertContains("src/Foo.cs", defaultJson);

                JsonElement debugArgs = JsonSerializer.SerializeToElement(new
                {
                    vesselId = "vsl_test",
                    query = "needle",
                    includeEmbeddings = true
                });
                string debugJson = JsonSerializer.Serialize(await handlers["armada_code_search"](debugArgs).ConfigureAwait(false));

                AssertContains("EmbeddingVector", debugJson);
                AssertContains("0.25", debugJson);
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
                    includeContent = true,
                    includeEmbeddings = true
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

            await RunTest("Graph callers/callees/impact/affected-tests reject missing vesselId and null args", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);

                // null args for every graph tool produces "missing args"
                AssertContains("missing args", JsonSerializer.Serialize(await handlers["armada_graph_get_callers"](null).ConfigureAwait(false)));
                AssertContains("missing args", JsonSerializer.Serialize(await handlers["armada_graph_get_callees"](null).ConfigureAwait(false)));
                AssertContains("missing args", JsonSerializer.Serialize(await handlers["armada_graph_get_impact"](null).ConfigureAwait(false)));
                AssertContains("missing args", JsonSerializer.Serialize(await handlers["armada_graph_suggest_affected_tests"](null).ConfigureAwait(false)));
                AssertContains("missing args", JsonSerializer.Serialize(await handlers["armada_graph_get_node"](null).ConfigureAwait(false)));
                AssertContains("missing args", JsonSerializer.Serialize(await handlers["armada_graph_get_files"](null).ConfigureAwait(false)));
                AssertContains("missing args", JsonSerializer.Serialize(await handlers["armada_graph_explore"](null).ConfigureAwait(false)));

                // Missing vesselId on every neighbor/impact/affected-tests tool
                JsonElement callerMissingVessel = JsonSerializer.SerializeToElement(new { symbol = "Foo" });
                AssertContains("vesselId is required", JsonSerializer.Serialize(await handlers["armada_graph_get_callers"](callerMissingVessel).ConfigureAwait(false)));
                AssertNull(service.LastCallersRequest, "Invalid callers request should not delegate to service");

                JsonElement calleeMissingVessel = JsonSerializer.SerializeToElement(new { symbol = "Foo" });
                AssertContains("vesselId is required", JsonSerializer.Serialize(await handlers["armada_graph_get_callees"](calleeMissingVessel).ConfigureAwait(false)));
                AssertNull(service.LastCalleesRequest, "Invalid callees request should not delegate to service");

                JsonElement impactMissingVessel = JsonSerializer.SerializeToElement(new { symbol = "Foo" });
                AssertContains("vesselId is required", JsonSerializer.Serialize(await handlers["armada_graph_get_impact"](impactMissingVessel).ConfigureAwait(false)));
                AssertNull(service.LastImpactRequest, "Invalid impact request should not delegate to service");

                JsonElement affectedMissingVessel = JsonSerializer.SerializeToElement(new { symbol = "Foo" });
                AssertContains("vesselId is required", JsonSerializer.Serialize(await handlers["armada_graph_suggest_affected_tests"](affectedMissingVessel).ConfigureAwait(false)));
                AssertNull(service.LastAffectedTestsRequest, "Invalid affected-tests request should not delegate to service");

                JsonElement nodeMissingVessel = JsonSerializer.SerializeToElement(new { symbol = "Foo" });
                AssertContains("vesselId is required", JsonSerializer.Serialize(await handlers["armada_graph_get_node"](nodeMissingVessel).ConfigureAwait(false)));
                AssertNull(service.LastNodeRequest, "Invalid node request should not delegate to service");

                JsonElement filesMissingVessel = JsonSerializer.SerializeToElement(new { pathPrefix = "src/" });
                AssertContains("vesselId is required", JsonSerializer.Serialize(await handlers["armada_graph_get_files"](filesMissingVessel).ConfigureAwait(false)));
                AssertNull(service.LastFileStructureRequest, "Invalid files request should not delegate to service");

                JsonElement exploreMissingVessel = JsonSerializer.SerializeToElement(new { query = "Foo" });
                AssertContains("vesselId is required", JsonSerializer.Serialize(await handlers["armada_graph_explore"](exploreMissingVessel).ConfigureAwait(false)));
                AssertNull(service.LastExploreRequest, "Invalid explore request should not delegate to service");

                // Missing symbol on callees and affected-tests (search_symbols/callers/impact are covered above)
                JsonElement calleeMissingSymbol = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test" });
                AssertContains("symbol is required", JsonSerializer.Serialize(await handlers["armada_graph_get_callees"](calleeMissingSymbol).ConfigureAwait(false)));
                AssertNull(service.LastCalleesRequest, "Invalid callees request without symbol should not delegate to service");

                JsonElement affectedMissingSymbol = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test" });
                AssertContains("symbol is required", JsonSerializer.Serialize(await handlers["armada_graph_suggest_affected_tests"](affectedMissingSymbol).ConfigureAwait(false)));
                AssertNull(service.LastAffectedTestsRequest, "Invalid affected-tests request without symbol should not delegate to service");

                JsonElement nodeMissingSymbol = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test" });
                AssertContains("symbol is required", JsonSerializer.Serialize(await handlers["armada_graph_get_node"](nodeMissingSymbol).ConfigureAwait(false)));
                AssertNull(service.LastNodeRequest, "Invalid node request without symbol should not delegate to service");

                JsonElement exploreMissingQuery = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test" });
                AssertContains("query is required", JsonSerializer.Serialize(await handlers["armada_graph_explore"](exploreMissingQuery).ConfigureAwait(false)));
                AssertNull(service.LastExploreRequest, "Invalid explore request without query should not delegate to service");
            });

            await RunTest("Graph node/files/explore tools delegate typed requests", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                service.NodeResponse = new CodeGraphNodeResponse
                {
                    RequestedSymbol = "Execute",
                    Status = NewStatus("vsl_test")
                };
                service.FileStructureResponse = new CodeGraphFileStructureResponse
                {
                    Status = NewStatus("vsl_test")
                };
                service.ExploreResponse = new CodeGraphExploreResponse
                {
                    Query = "Execute",
                    Status = NewStatus("vsl_test")
                };

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);

                object nodeResult = await handlers["armada_graph_get_node"](JsonSerializer.SerializeToElement(new
                {
                    vesselId = "vsl_test",
                    symbol = "Execute",
                    includeSource = true,
                    sourcePadding = 4
                })).ConfigureAwait(false);
                AssertEqual("Execute", ((CodeGraphNodeResponse)nodeResult).RequestedSymbol);
                AssertNotNull(service.LastNodeRequest);
                AssertEqual("vsl_test", service.LastNodeRequest!.VesselId);
                AssertEqual("Execute", service.LastNodeRequest.Symbol);
                AssertEqual(4, service.LastNodeRequest.SourcePadding);

                object filesResult = await handlers["armada_graph_get_files"](JsonSerializer.SerializeToElement(new
                {
                    vesselId = "vsl_test",
                    pathPrefix = "src/",
                    limit = 12,
                    includeSymbols = false
                })).ConfigureAwait(false);
                AssertNotNull((CodeGraphFileStructureResponse)filesResult);
                AssertNotNull(service.LastFileStructureRequest);
                AssertEqual("src/", service.LastFileStructureRequest!.PathPrefix);
                AssertEqual(12, service.LastFileStructureRequest.Limit);
                AssertFalse(service.LastFileStructureRequest.IncludeSymbols);

                object exploreResult = await handlers["armada_graph_explore"](JsonSerializer.SerializeToElement(new
                {
                    vesselId = "vsl_test",
                    query = "Execute",
                    maxDepth = 2,
                    maxResults = 9,
                    includeSource = true
                })).ConfigureAwait(false);
                AssertEqual("Execute", ((CodeGraphExploreResponse)exploreResult).Query);
                AssertNotNull(service.LastExploreRequest);
                AssertEqual("vsl_test", service.LastExploreRequest!.VesselId);
                AssertEqual("Execute", service.LastExploreRequest.Query);
                AssertEqual(2, service.LastExploreRequest.MaxDepth);
                AssertEqual(9, service.LastExploreRequest.MaxResults);
            });

            await RunTest("Whitespace-only vesselId, query, and symbol are rejected", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);

                JsonElement wsVesselSearch = JsonSerializer.SerializeToElement(new { vesselId = "   ", query = "Foo" });
                AssertContains("vesselId is required", JsonSerializer.Serialize(await handlers["armada_graph_search_symbols"](wsVesselSearch).ConfigureAwait(false)));

                JsonElement wsQuery = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test", query = "\t" });
                AssertContains("query is required", JsonSerializer.Serialize(await handlers["armada_graph_search_symbols"](wsQuery).ConfigureAwait(false)));

                JsonElement wsSymbolCallers = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test", symbol = "  " });
                AssertContains("symbol is required", JsonSerializer.Serialize(await handlers["armada_graph_get_callers"](wsSymbolCallers).ConfigureAwait(false)));

                JsonElement wsSymbolImpact = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test", symbol = "\n" });
                AssertContains("symbol is required", JsonSerializer.Serialize(await handlers["armada_graph_get_impact"](wsSymbolImpact).ConfigureAwait(false)));

                JsonElement wsSymbolAffected = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test", symbol = " " });
                AssertContains("symbol is required", JsonSerializer.Serialize(await handlers["armada_graph_suggest_affected_tests"](wsSymbolAffected).ConfigureAwait(false)));

                AssertNull(service.LastSymbolSearchRequest, "Whitespace inputs must not reach the service layer");
                AssertNull(service.LastCallersRequest, "Whitespace inputs must not reach the service layer");
                AssertNull(service.LastImpactRequest, "Whitespace inputs must not reach the service layer");
                AssertNull(service.LastAffectedTestsRequest, "Whitespace inputs must not reach the service layer");
            });

            await RunTest("Graph tool responses propagate Warnings without leaking vector fields", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                service.SymbolSearchResponse = new CodeGraphSymbolSearchResponse
                {
                    Query = "Execute",
                    Status = NewStatus("vsl_test")
                };
                service.SymbolSearchResponse.Warnings.Add("graph sidecars are stale for vessel vsl_test");
                service.SymbolSearchResponse.Results.Add(new CodeGraphSymbolSearchResult
                {
                    Score = 0.42,
                    MatchReason = "exact qualified",
                    Symbol = new CodeGraphSymbolRecord
                    {
                        VesselId = "vsl_test",
                        QualifiedName = "Armada.Foo.Execute",
                        SimpleName = "Execute",
                        Path = "src/Foo.cs"
                    }
                });

                service.NeighborsResponse = new CodeGraphNeighborsResponse
                {
                    RequestedSymbol = "Execute",
                    Status = NewStatus("vsl_test")
                };
                service.NeighborsResponse.Warnings.Add("graph sidecars are missing for vessel vsl_test");

                service.ImpactResponse = new CodeGraphImpactResponse
                {
                    RequestedSymbol = "Execute",
                    MaxDepth = 3,
                    Status = NewStatus("vsl_test")
                };
                service.ImpactResponse.Warnings.Add("graph sidecars are stale for vessel vsl_test");

                service.AffectedTestsResponse = new CodeGraphAffectedTestsResponse
                {
                    RequestedSymbol = "Execute",
                    MaxDepth = 3,
                    Status = NewStatus("vsl_test")
                };
                service.AffectedTestsResponse.Warnings.Add("graph sidecars are stale for vessel vsl_test");

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);

                JsonElement searchArgs = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test", query = "Execute" });
                object searchResult = await handlers["armada_graph_search_symbols"](searchArgs).ConfigureAwait(false);
                string searchJson = JsonSerializer.Serialize(searchResult);
                AssertContains("graph sidecars are stale", searchJson);
                AssertContains("Warnings", searchJson);
                AssertFalse(searchJson.Contains("EmbeddingVector", StringComparison.OrdinalIgnoreCase), "search response must not include EmbeddingVector");
                AssertFalse(searchJson.Contains("\"Vector\"", StringComparison.Ordinal), "search response must not include a Vector field");

                JsonElement callersArgs = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test", symbol = "Execute" });
                string callersJson = JsonSerializer.Serialize(await handlers["armada_graph_get_callers"](callersArgs).ConfigureAwait(false));
                AssertContains("graph sidecars are missing", callersJson);
                AssertFalse(callersJson.Contains("EmbeddingVector", StringComparison.OrdinalIgnoreCase), "callers response must not include EmbeddingVector");

                JsonElement calleesArgs = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test", symbol = "Execute" });
                string calleesJson = JsonSerializer.Serialize(await handlers["armada_graph_get_callees"](calleesArgs).ConfigureAwait(false));
                AssertContains("graph sidecars are missing", calleesJson);
                AssertFalse(calleesJson.Contains("EmbeddingVector", StringComparison.OrdinalIgnoreCase), "callees response must not include EmbeddingVector");

                JsonElement impactArgs = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test", symbol = "Execute" });
                string impactJson = JsonSerializer.Serialize(await handlers["armada_graph_get_impact"](impactArgs).ConfigureAwait(false));
                AssertContains("graph sidecars are stale", impactJson);
                AssertFalse(impactJson.Contains("EmbeddingVector", StringComparison.OrdinalIgnoreCase), "impact response must not include EmbeddingVector");

                JsonElement affectedArgs = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test", symbol = "Execute" });
                string affectedJson = JsonSerializer.Serialize(await handlers["armada_graph_suggest_affected_tests"](affectedArgs).ConfigureAwait(false));
                AssertContains("graph sidecars are stale", affectedJson);
                AssertFalse(affectedJson.Contains("EmbeddingVector", StringComparison.OrdinalIgnoreCase), "affected-tests response must not include EmbeddingVector");
            });

            await RunTest("Graph handlers omit optional fields and rely on model defaults", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);

                // Only required fields supplied; optional limit/maxDepth/maxResults/direction omitted.
                JsonElement searchArgs = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test", query = "Foo" });
                await handlers["armada_graph_search_symbols"](searchArgs).ConfigureAwait(false);
                AssertNotNull(service.LastSymbolSearchRequest);
                AssertTrue(String.IsNullOrEmpty(service.LastSymbolSearchRequest!.PathPrefix), "PathPrefix should default to empty when omitted");

                JsonElement callersArgs = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test", symbol = "Foo" });
                await handlers["armada_graph_get_callers"](callersArgs).ConfigureAwait(false);
                AssertNotNull(service.LastCallersRequest);
                AssertEqual("Foo", service.LastCallersRequest!.Symbol);

                JsonElement calleesArgs = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test", symbol = "Foo" });
                await handlers["armada_graph_get_callees"](calleesArgs).ConfigureAwait(false);
                AssertNotNull(service.LastCalleesRequest);
                AssertEqual("Foo", service.LastCalleesRequest!.Symbol);

                JsonElement impactArgs = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test", symbol = "Foo" });
                await handlers["armada_graph_get_impact"](impactArgs).ConfigureAwait(false);
                AssertNotNull(service.LastImpactRequest);
                AssertEqual("Foo", service.LastImpactRequest!.Symbol);

                JsonElement affectedArgs = JsonSerializer.SerializeToElement(new { vesselId = "vsl_test", symbol = "Foo" });
                await handlers["armada_graph_suggest_affected_tests"](affectedArgs).ConfigureAwait(false);
                AssertNotNull(service.LastAffectedTestsRequest);
                AssertEqual("Foo", service.LastAffectedTestsRequest!.Symbol);
            });

            await RunTest("Register rejects null delegate and null code index service", () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                AssertThrows<ArgumentNullException>(() => McpCodeIndexTools.Register(null!, service));
                AssertThrows<ArgumentNullException>(() => McpCodeIndexTools.Register((_, _, _, _) => { }, null!));
                return Task.CompletedTask;
            });

            await RunTest("Graph tools are registered with required-property schemas", () =>
            {
                Dictionary<string, object> schemas = new Dictionary<string, object>();
                McpCodeIndexTools.Register(
                    (name, _, schema, _) => { schemas[name] = schema; },
                    new RecordingCodeIndexService());

                string searchSchema = JsonSerializer.Serialize(schemas["armada_graph_search_symbols"]);
                AssertContains("vesselId", searchSchema);
                AssertContains("query", searchSchema);
                AssertContains("required", searchSchema);

                string callersSchema = JsonSerializer.Serialize(schemas["armada_graph_get_callers"]);
                AssertContains("vesselId", callersSchema);
                AssertContains("symbol", callersSchema);

                string calleesSchema = JsonSerializer.Serialize(schemas["armada_graph_get_callees"]);
                AssertContains("vesselId", calleesSchema);
                AssertContains("symbol", calleesSchema);

                string impactSchema = JsonSerializer.Serialize(schemas["armada_graph_get_impact"]);
                AssertContains("direction", impactSchema);
                AssertContains("maxDepth", impactSchema);
                AssertContains("maxResults", impactSchema);

                string affectedSchema = JsonSerializer.Serialize(schemas["armada_graph_suggest_affected_tests"]);
                AssertContains("maxDepth", affectedSchema);
                AssertContains("maxResults", affectedSchema);

                return Task.CompletedTask;
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

            await RunTest("ContextPack_TimeoutResolution_UsesSharedEnvVar", async () =>
            {
                string? priorTimeout = Environment.GetEnvironmentVariable(CodeContextTimeouts.TimeoutEnvVar);
                Environment.SetEnvironmentVariable(CodeContextTimeouts.TimeoutEnvVar, "5000");
                try
                {
                    RecordingCodeIndexService service = new RecordingCodeIndexService();
                    service.ContextPackResponse = new ContextPackResponse
                    {
                        Status = NewStatus("vsl_timeout_env"),
                        Goal = "resolve via env var",
                        MaterializedPath = "C:/tmp/timeout-env.md"
                    };

                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = "vsl_timeout_env",
                        goal = "resolve via env var",
                        tokenBudget = 500
                    });

                    object result = await handlers["armada_context_pack"](args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertFalse(resultJson.Contains("\"Error\""), "5 second env-var timeout should be sufficient for fast service: " + resultJson);
                }
                finally
                {
                    Environment.SetEnvironmentVariable(CodeContextTimeouts.TimeoutEnvVar, priorTimeout);
                }
            });

            await RunTest("ContextPack_TimeoutResolution_DefaultExplicitTimeout_Is120Seconds", () =>
            {
                string? priorTimeout = Environment.GetEnvironmentVariable(CodeContextTimeouts.TimeoutEnvVar);
                Environment.SetEnvironmentVariable(CodeContextTimeouts.TimeoutEnvVar, null);
                try
                {
                    TimeSpan defaultTimeout = CodeContextTimeouts.Resolve(CodeContextTimeouts.DefaultExplicitTimeoutMs);
                    AssertEqual(120_000, (int)defaultTimeout.TotalMilliseconds,
                        "Default explicit context-pack timeout must be 120 seconds");
                }
                finally
                {
                    Environment.SetEnvironmentVariable(CodeContextTimeouts.TimeoutEnvVar, priorTimeout);
                }
                return Task.CompletedTask;
            });

            await RunTest("ContextPack_TimeoutResolution_PerRequestTimeoutMs_OverridesEnvVar", async () =>
            {
                string? priorTimeout = Environment.GetEnvironmentVariable(CodeContextTimeouts.TimeoutEnvVar);
                Environment.SetEnvironmentVariable(CodeContextTimeouts.TimeoutEnvVar, "999999");
                try
                {
                    RecordingCodeIndexService service = new RecordingCodeIndexService();
                    service.ContextPackResponse = new ContextPackResponse
                    {
                        Status = NewStatus("vsl_req_timeout"),
                        Goal = "per-request override",
                        MaterializedPath = "C:/tmp/req-timeout.md"
                    };
                    service.ContextPackResponse.PrestagedFiles.Add(new PrestagedFile("C:/tmp/req-timeout.md", "_briefing/context-pack.md"));

                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = "vsl_req_timeout",
                        goal = "per-request override",
                        tokenBudget = 500,
                        timeoutMs = 10000
                    });

                    object result = await handlers["armada_context_pack"](args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertFalse(resultJson.Contains("\"Error\""), "Per-request 10s timeout should not fail fast service: " + resultJson);
                }
                finally
                {
                    Environment.SetEnvironmentVariable(CodeContextTimeouts.TimeoutEnvVar, priorTimeout);
                }
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

        private static Dictionary<string, Func<JsonElement?, Task<object>>> RegisterJobHandlers(
            RecordingCodeIndexService service,
            LongRunningJobService jobs)
        {
            Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
            McpLongRunningJobTools.Register(
                (name, _, _, handler) => handlers[name] = handler,
                jobs);
            McpCodeIndexTools.Register(
                (name, _, _, handler) => handlers[name] = handler,
                service,
                jobs);
            return handlers;
        }

        private static Task<object> GetJobStatusAsync(
            Dictionary<string, Func<JsonElement?, Task<object>>> handlers,
            string jobId)
        {
            JsonElement args = JsonSerializer.SerializeToElement(new { jobId });
            return handlers["armada_job_status"](args);
        }

        private static async Task<object> WaitForJobStatusAsync(
            Dictionary<string, Func<JsonElement?, Task<object>>> handlers,
            string jobId,
            LongRunningJobStatusEnum expectedStatus)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                object result = await GetJobStatusAsync(handlers, jobId).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(result);
                if (json.Contains("\"Status\":\"" + expectedStatus + "\"", StringComparison.Ordinal))
                    return result;
                await Task.Delay(10).ConfigureAwait(false);
            }

            throw new TimeoutException("Job did not reach expected status " + expectedStatus + ".");
        }

        private static async Task<LongRunningJob> WaitForTrackedJobStatusAsync(
            LongRunningJobService jobs,
            string jobId,
            LongRunningJobStatusEnum expectedStatus)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (jobs.TryGetStatus(jobId, out LongRunningJob? job)
                    && job != null
                    && job.Status == expectedStatus)
                    return job;
                await Task.Delay(10).ConfigureAwait(false);
            }

            throw new TimeoutException("Tracked job did not reach expected status " + expectedStatus + ".");
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

            public CodeGraphNodeRequest? LastNodeRequest { get; private set; }

            public CodeGraphFileStructureRequest? LastFileStructureRequest { get; private set; }

            public CodeGraphExploreRequest? LastExploreRequest { get; private set; }

            public string? LastStatusVesselId { get; private set; }

            public string? LastUpdateVesselId { get; private set; }

            public TaskCompletionSource<bool>? UpdateStarted { get; set; }

            public TaskCompletionSource<CodeIndexStatus>? UpdateCompletion { get; set; }

            public Exception? UpdateException { get; set; }

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

            public CodeGraphNodeResponse NodeResponse { get; set; } = new CodeGraphNodeResponse
            {
                Status = NewStatus("vsl_default"),
                RequestedSymbol = "default"
            };

            public CodeGraphFileStructureResponse FileStructureResponse { get; set; } = new CodeGraphFileStructureResponse
            {
                Status = NewStatus("vsl_default")
            };

            public CodeGraphExploreResponse ExploreResponse { get; set; } = new CodeGraphExploreResponse
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
                UpdateStarted?.TrySetResult(true);
                if (UpdateException != null) return Task.FromException<CodeIndexStatus>(UpdateException);
                if (UpdateCompletion != null) return UpdateCompletion.Task;
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

            public Task<CodeGraphNodeResponse> GetNodeAsync(CodeGraphNodeRequest request, CancellationToken token = default)
            {
                LastNodeRequest = request;
                return Task.FromResult(NodeResponse);
            }

            public Task<CodeGraphFileStructureResponse> GetFileStructureAsync(CodeGraphFileStructureRequest request, CancellationToken token = default)
            {
                LastFileStructureRequest = request;
                return Task.FromResult(FileStructureResponse);
            }

            public Task<CodeGraphExploreResponse> ExploreAsync(CodeGraphExploreRequest request, CancellationToken token = default)
            {
                LastExploreRequest = request;
                return Task.FromResult(ExploreResponse);
            }

            public Task WarmBaselineCacheAsync(string vesselId, CancellationToken token = default)
                => Task.CompletedTask;

            public Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default)
                => Task.FromResult<ContextPackResponse?>(null);
        }
    }
}
