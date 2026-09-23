namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

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
                AssertTrue(handlers.ContainsKey("armada_code_duplicates"));
            });

            await RunTest("armada_code_duplicates forwards the request and stays operator-only", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(service);
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    vesselId = "vsl_dups",
                    threshold = 0.95,
                    minLines = 9,
                    pathPrefix = "src/",
                    language = "csharp",
                    excludePathFragments = new[] { "/generated/" },
                    maxGroups = 7,
                    includeContent = true
                });

                object result = await handlers["armada_code_duplicates"](args).ConfigureAwait(false);

                AssertNotNull(service.LastDuplicateRequest);
                AssertEqual("vsl_dups", service.LastDuplicateRequest!.VesselId);
                AssertEqual(0.95, service.LastDuplicateRequest.Threshold);
                AssertEqual(9, service.LastDuplicateRequest.MinLines);
                AssertEqual("src/", service.LastDuplicateRequest.PathPrefix);
                AssertEqual("csharp", service.LastDuplicateRequest.Language);
                AssertEqual("/generated/", service.LastDuplicateRequest.ExcludePathFragments.Single());
                AssertEqual(7, service.LastDuplicateRequest.MaxGroups);
                AssertTrue(service.LastDuplicateRequest.IncludeContent);
                AssertTrue(result is CodeDuplicateReport);

                AuthContext captain = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "Bearer");
                AssertFalse(McpToolAccessPolicy.IsAllowed(captain, "armada_code_duplicates"), "the repository-wide comparison is an operator tool");
            });

            await RunTest("armada_code_duplicates runs as a background job when jobs are available", async () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                LongRunningJobService jobs = new LongRunningJobService();
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterJobHandlers(service, jobs);
                JsonElement args = JsonSerializer.SerializeToElement(new { vesselId = "vsl_dups_job" });

                LongRunningJob accepted = (LongRunningJob)await handlers["armada_code_duplicates"](args).ConfigureAwait(false);
                object succeeded = await WaitForJobStatusAsync(handlers, accepted.JobId, LongRunningJobStatusEnum.Succeeded).ConfigureAwait(false);

                AssertContains("vsl_dups_job", JsonSerializer.Serialize(succeeded));
                AssertEqual("vsl_dups_job", service.LastDuplicateRequest!.VesselId);
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

            await RunTest("LongRunningJobService retains active jobs during terminal eviction", async () =>
            {
                LongRunningJobService jobs = new LongRunningJobService(1);
                TaskCompletionSource<bool> activeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<object?> activeCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationToken? observedToken = null;
                LongRunningJob activeAccepted = jobs.Start(
                    " active_operation ",
                    async (token) =>
                    {
                        observedToken = token;
                        activeStarted.TrySetResult(true);
                        return await activeCompletion.Task.ConfigureAwait(false);
                    });

                await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                AssertEqual("active_operation", activeAccepted.Operation);
                AssertTrue(observedToken.HasValue);
                CancellationToken executionToken = observedToken.GetValueOrDefault();
                // The job runs independently of the request that started it: only reaping cancels it.
                AssertFalse(executionToken.IsCancellationRequested, "a running job's token is not cancelled");

                LongRunningJob firstTerminal = jobs.Start(
                    "first_terminal",
                    (_) => Task.FromResult<object?>(new { value = 1 }));
                await WaitForTrackedJobStatusAsync(
                    jobs,
                    firstTerminal.JobId,
                    LongRunningJobStatusEnum.Succeeded).ConfigureAwait(false);

                LongRunningJob secondTerminal = jobs.Start(
                    "second_terminal",
                    (_) => Task.FromResult<object?>(new { value = 2 }));
                await WaitForTrackedJobStatusAsync(
                    jobs,
                    secondTerminal.JobId,
                    LongRunningJobStatusEnum.Succeeded).ConfigureAwait(false);

                AssertTrue(jobs.TryGetStatus(activeAccepted.JobId, out LongRunningJob? activeSnapshot));
                AssertEqual(LongRunningJobStatusEnum.Running, activeSnapshot!.Status);
                AssertFalse(jobs.TryGetStatus(firstTerminal.JobId, out LongRunningJob? _));

                activeCompletion.TrySetResult(new { value = 3 });
                await WaitForTrackedJobStatusAsync(
                    jobs,
                    activeAccepted.JobId,
                    LongRunningJobStatusEnum.Succeeded).ConfigureAwait(false);
            });

            await RunTest("LongRunningJobService validates start arguments", () =>
            {
                LongRunningJobService jobs = new LongRunningJobService();

                AssertThrows<ArgumentException>(() => jobs.Start(
                    " ",
                    (_) => Task.FromResult<object?>(null)));
                AssertThrows<ArgumentNullException>(() => jobs.Start(
                    "valid_operation",
                    (Func<CancellationToken, Task<object?>>)null!));
                AssertFalse(jobs.TryGetStatus(" ", out LongRunningJob? _));
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

            await RunTest("Mission code search is in mission scope and the operator search tools are not", () =>
            {
                AuthContext captain = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "Bearer");
                AssertTrue(McpToolAccessPolicy.IsAllowed(captain, McpMissionCodeSearchTools.ToolName), "a mission caller may use the mission code search");
                AssertFalse(McpToolAccessPolicy.IsAllowed(captain, "armada_code_search"), "vessel-argument search stays operator-only");
                AssertFalse(McpToolAccessPolicy.IsAllowed(captain, "armada_fleet_code_search"), "fleet search stays operator-only");
            });

            await RunTest("Mission code search searches only the mission's own vessel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RecordingCodeIndexService service = new RecordingCodeIndexService();
                    MissionSearchHarness harness = MissionSearchHarness.Create(testDb, service);
                    string ownVesselId = await harness.SeedVesselAsync("OwnVessel").ConfigureAwait(false);
                    string otherVesselId = await harness.SeedVesselAsync("OtherVessel").ConfigureAwait(false);
                    string missionId = await harness.SeedMissionAsync(ownVesselId, MissionStatusEnum.InProgress).ConfigureAwait(false);

                    // A vesselId argument naming another vessel is not part of the schema and is ignored.
                    string json = await harness.CallAsync(new { missionId, query = "needle", vesselId = otherVesselId }).ConfigureAwait(false);

                    AssertContains("\"Available\":true", json);
                    AssertNotNull(service.LastSearchRequest);
                    AssertEqual(ownVesselId, service.LastSearchRequest!.VesselId);
                    AssertEqual(ownVesselId, service.LastStatusVesselId);
                    AssertFalse(service.LastSearchRequest.IncludeReferenceOnly, "captains never see reference-only records");
                }
            });

            await RunTest("Mission code search refuses a mission from another tenant without searching", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RecordingCodeIndexService service = new RecordingCodeIndexService();
                    MissionSearchHarness harness = MissionSearchHarness.Create(testDb, service);
                    string vesselId = await harness.SeedVesselAsync("TenantVessel").ConfigureAwait(false);
                    string missionId = await harness.SeedMissionAsync(vesselId, MissionStatusEnum.InProgress).ConfigureAwait(false);

                    AuthContext outsider = AuthContext.Authenticated("ten_other", "usr_other", false, false, "Bearer");
                    string json = await harness.CallAsync(new { missionId, query = "needle" }, outsider).ConfigureAwait(false);

                    AssertContains("\"UnavailableReason\":\"mission_not_found\"", json);
                    AssertNull(service.LastSearchRequest, "no search runs for another tenant's mission");
                }
            });

            await RunTest("Mission code search refuses a finished mission without searching", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RecordingCodeIndexService service = new RecordingCodeIndexService();
                    MissionSearchHarness harness = MissionSearchHarness.Create(testDb, service);
                    string vesselId = await harness.SeedVesselAsync("DoneVessel").ConfigureAwait(false);
                    string missionId = await harness.SeedMissionAsync(vesselId, MissionStatusEnum.Complete).ConfigureAwait(false);

                    string json = await harness.CallAsync(new { missionId, query = "needle" }).ConfigureAwait(false);

                    AssertContains("\"UnavailableReason\":\"mission_not_active\"", json);
                    AssertNull(service.LastSearchRequest);
                }
            });

            await RunTest("Mission code search on a missing index says no search ran instead of returning no results", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RecordingCodeIndexService service = new RecordingCodeIndexService();
                    service.StatusFreshness = "Missing";
                    MissionSearchHarness harness = MissionSearchHarness.Create(testDb, service);
                    string vesselId = await harness.SeedVesselAsync("UnindexedVessel").ConfigureAwait(false);
                    string missionId = await harness.SeedMissionAsync(vesselId, MissionStatusEnum.InProgress).ConfigureAwait(false);

                    string json = await harness.CallAsync(new { missionId, query = "needle" }).ConfigureAwait(false);

                    AssertContains("\"Available\":false", json);
                    AssertContains("\"UnavailableReason\":\"index_missing\"", json);
                    AssertFalse(json.Contains("\"Results\"", StringComparison.Ordinal), "a missing index returns no result list at all");
                    AssertNull(service.LastSearchRequest, "no search runs against a missing index");
                }
            });

            await RunTest("Mission code search warns when the index is stale or lexical only", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RecordingCodeIndexService service = new RecordingCodeIndexService();
                    service.StatusFreshness = "Stale";
                    service.StatusUseSemanticSearch = false;
                    MissionSearchHarness harness = MissionSearchHarness.Create(testDb, service);
                    string vesselId = await harness.SeedVesselAsync("StaleVessel").ConfigureAwait(false);
                    string missionId = await harness.SeedMissionAsync(vesselId, MissionStatusEnum.InProgress).ConfigureAwait(false);

                    string json = await harness.CallAsync(new { missionId, query = "needle" }).ConfigureAwait(false);

                    AssertContains("\"Available\":true", json);
                    AssertContains("The index is Stale", json);
                    AssertContains("lexical only", json);
                }
            });

            await RunTest("Mission code search clamps the limit and enforces the per-mission budget", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RecordingCodeIndexService service = new RecordingCodeIndexService();
                    MissionSearchHarness harness = MissionSearchHarness.Create(testDb, service, maxCalls: 2, maxResults: 3);
                    string vesselId = await harness.SeedVesselAsync("BudgetVessel").ConfigureAwait(false);
                    string missionId = await harness.SeedMissionAsync(vesselId, MissionStatusEnum.InProgress).ConfigureAwait(false);

                    string first = await harness.CallAsync(new { missionId, query = "a", limit = 50 }).ConfigureAwait(false);
                    AssertEqual(3, service.LastSearchRequest!.Limit, "the limit is clamped to the captain maximum");
                    string second = await harness.CallAsync(new { missionId, query = "b" }).ConfigureAwait(false);
                    string third = await harness.CallAsync(new { missionId, query = "c" }).ConfigureAwait(false);

                    AssertContains("\"Available\":true", first);
                    AssertContains("\"Available\":true", second);
                    AssertContains("\"UnavailableReason\":\"budget\"", third);
                    AssertEqual("b", service.LastSearchRequest.Query, "the over-budget call runs no search");
                }
            });

            await RunTest("Mission code search on a never-indexed vessel creates no index and calls no embedding provider", async () =>
            {
                string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada-mission-search-noindex-" + Guid.NewGuid().ToString("N"));
                string repo = System.IO.Path.Combine(root, "repo");
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(repo, "src"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(repo, "src", "Needle.cs"), "namespace Sample { public class Needle { } }\n");

                try
                {
                    await TestGit.InitializeAsync(repo).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = new ArmadaSettings
                        {
                            DataDirectory = System.IO.Path.Combine(root, "data"),
                            ReposDirectory = System.IO.Path.Combine(root, "repos")
                        };
                        settings.CodeIndex.IndexDirectory = System.IO.Path.Combine(root, "code-index");
                        settings.CodeIndex.UseSemanticSearch = true;
                        CountingEmbeddingClient embeddingClient = new CountingEmbeddingClient();
                        SyslogLogging.LoggingModule logging = new SyslogLogging.LoggingModule();
                        logging.Settings.EnableConsole = false;
                        Armada.Core.Services.CodeIndexService codeIndex = new Armada.Core.Services.CodeIndexService(
                            logging, testDb.Driver, settings, new Armada.Core.Services.GitService(logging), embeddingClient, null);

                        Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("NeverIndexedVessel", repo)
                        {
                            WorkingDirectory = repo,
                            DefaultBranch = "main"
                        }).ConfigureAwait(false);
                        Mission mission = new Mission();
                        mission.TenantId = Constants.DefaultTenantId;
                        mission.UserId = Constants.DefaultUserId;
                        mission.VesselId = vessel.Id;
                        mission.Title = "code search mission";
                        mission.Status = MissionStatusEnum.InProgress;
                        mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                        McpMissionCodeSearchTools.ResetBudgetForTests();
                        Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                        McpMissionCodeSearchTools.Register((name, _, _, handler) => { handlers[name] = handler; }, codeIndex, testDb.Driver, settings);

                        string json;
                        using (McpCallerContext.Begin(AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "Bearer")))
                        {
                            object result = await handlers[McpMissionCodeSearchTools.ToolName](
                                JsonSerializer.SerializeToElement(new { missionId = mission.Id, query = "Needle" })).ConfigureAwait(false);
                            json = JsonSerializer.Serialize(result);
                        }

                        AssertFalse(System.IO.Directory.Exists(System.IO.Path.Combine(settings.CodeIndex.IndexDirectory, vessel.Id)), "mission code search must not index a never-indexed vessel");
                        AssertEqual(0, embeddingClient.CallCount, "mission code search on a never-indexed vessel must send nothing to the embedding provider");
                        AssertContains("\"Available\":false", json);
                        AssertContains("\"UnavailableReason\":\"index_missing\"", json);
                    }
                }
                finally
                {
                    try { if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true); } catch { }
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

        private sealed class CountingEmbeddingClient : IEmbeddingClient
        {
            private int _CallCount;

            public int CallCount => Volatile.Read(ref _CallCount);

            public Task<float[]> EmbedAsync(string text, CancellationToken token = default)
            {
                Interlocked.Increment(ref _CallCount);
                return Task.FromResult(new[] { 1F, 0F });
            }
        }

        private sealed class MissionSearchHarness
        {
            private readonly TestDatabase _TestDb;

            private readonly Dictionary<string, Func<JsonElement?, Task<object>>> _Handlers =
                new Dictionary<string, Func<JsonElement?, Task<object>>>();

            private MissionSearchHarness(TestDatabase testDb)
            {
                _TestDb = testDb;
            }

            public static MissionSearchHarness Create(TestDatabase testDb, RecordingCodeIndexService service, int maxCalls = 40, int maxResults = 10)
            {
                McpMissionCodeSearchTools.ResetBudgetForTests();
                ArmadaSettings settings = new ArmadaSettings();
                settings.CodeIndex.CaptainSearchMaxCallsPerMission = maxCalls;
                settings.CodeIndex.CaptainSearchMaxResults = maxResults;
                MissionSearchHarness harness = new MissionSearchHarness(testDb);
                McpMissionCodeSearchTools.Register(
                    (name, _, _, handler) => { harness._Handlers[name] = handler; },
                    service,
                    testDb.Driver,
                    settings);
                return harness;
            }

            public async Task<string> SeedVesselAsync(string name)
            {
                Vessel vessel = await _TestDb.Driver.Vessels.CreateAsync(
                    new Vessel(name, "https://github.com/test/" + name + ".git")).ConfigureAwait(false);
                return vessel.Id;
            }

            public async Task<string> SeedMissionAsync(string vesselId, MissionStatusEnum status)
            {
                Mission mission = new Mission();
                mission.TenantId = Constants.DefaultTenantId;
                mission.UserId = Constants.DefaultUserId;
                mission.VesselId = vesselId;
                mission.Title = "code search mission";
                mission.Status = status;
                Mission created = await _TestDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                return created.Id;
            }

            public async Task<string> CallAsync(object args, AuthContext? caller = null)
            {
                JsonElement element = JsonSerializer.SerializeToElement(args);
                AuthContext effectiveCaller = caller ?? AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "Bearer");
                using (McpCallerContext.Begin(effectiveCaller))
                {
                    object result = await _Handlers[McpMissionCodeSearchTools.ToolName](element).ConfigureAwait(false);
                    return JsonSerializer.Serialize(result);
                }
            }
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

            public string StatusFreshness { get; set; } = "Fresh";

            public bool StatusUseSemanticSearch { get; set; } = true;

            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
            {
                LastStatusVesselId = vesselId;
                CodeIndexStatus status = NewStatus(vesselId);
                status.Freshness = StatusFreshness;
                status.UseSemanticSearch = StatusUseSemanticSearch;
                return Task.FromResult(status);
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

            public CodeDuplicateRequest? LastDuplicateRequest { get; private set; }

            public Task<CodeDuplicateReport> FindDuplicatesAsync(CodeDuplicateRequest request, CancellationToken token = default)
            {
                LastDuplicateRequest = request;
                return Task.FromResult(new CodeDuplicateReport { VesselId = request.VesselId, Available = true });
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
