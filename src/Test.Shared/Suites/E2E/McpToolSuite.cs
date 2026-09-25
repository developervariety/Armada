namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// MCP tool coverage exercising the JSON-RPC surface exposed on the Armada MCP port.
    /// Ported from the retired automated <c>McpToolTests</c> suite; every case drives the real
    /// MCP endpoint of the shared in-process server obtained through <see cref="E2EServerFixture"/>.
    /// Because descriptor cases are independent, each case establishes its own MCP session via
    /// <see cref="InitMcpSessionAsync"/> rather than sharing one across the suite.
    /// </summary>
    public sealed class McpToolSuite : IArmadaTestSuite
    {

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the end-to-end MCP tool suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("check_run_tools_run_inspect_and_retry", "CheckRunTools_RunInspectAndRetry", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient mcpClient = fx.McpClient;
                string sessionId = await InitMcpSessionAsync(mcpClient);

                string fleetId = await RestCreateFleetAsync(mcpClient, sessionId, "McpCheckFleet").ConfigureAwait(false);
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-mcp-check-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);
                string vesselId = await RestCreateVesselAsync(mcpClient, sessionId, fleetId, "McpCheckVessel", workingDirectory).ConfigureAwait(false);

                JsonElement runResult = await CallToolAsync(mcpClient, sessionId, "run_check", new
                {
                    vesselId = vesselId,
                    type = "Build",
                    label = "MCP Build Check",
                    commandOverride = "echo mcp-check"
                }).ConfigureAwait(false);
                AssertToolResultValid(runResult);
                CheckRun run = JsonHelper.Deserialize<CheckRun>(GetToolResultText(runResult));
                AssertStartsWith("chk_", run.Id);

                JsonElement getResult = await CallToolAsync(mcpClient, sessionId, "get_check_run", new
                {
                    checkRunId = run.Id
                }).ConfigureAwait(false);
                AssertToolResultValid(getResult);
                CheckRun fetched = JsonHelper.Deserialize<CheckRun>(GetToolResultText(getResult));
                AssertEqual(run.Id, fetched.Id);
                AssertEqual("MCP Build Check", fetched.Label);

                JsonElement retryResult = await CallToolAsync(mcpClient, sessionId, "retry_check_run", new
                {
                    checkRunId = run.Id
                }).ConfigureAwait(false);
                AssertToolResultValid(retryResult);
                CheckRun retried = JsonHelper.Deserialize<CheckRun>(GetToolResultText(retryResult));
                AssertStartsWith("chk_", retried.Id);
                AssertFalse(String.Equals(run.Id, retried.Id, StringComparison.Ordinal), "Retry should create a distinct check run ID");
            }));

            cases.Add(CaseAsync("release_tools_create_read_and_enumerate", "ReleaseTools_CreateReadAndEnumerate", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient mcpClient = fx.McpClient;
                string sessionId = await InitMcpSessionAsync(mcpClient);

                string fleetId = await RestCreateFleetAsync(mcpClient, sessionId, "McpReleaseFleet").ConfigureAwait(false);
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-mcp-release-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);
                string vesselId = await RestCreateVesselAsync(mcpClient, sessionId, fleetId, "McpReleaseVessel", workingDirectory).ConfigureAwait(false);

                JsonElement runResult = await CallToolAsync(mcpClient, sessionId, "run_check", new
                {
                    vesselId = vesselId,
                    type = "ReleaseVersioning",
                    label = "Version Check",
                    commandOverride = "echo 2.3.4"
                }).ConfigureAwait(false);
                AssertToolResultValid(runResult);
                CheckRun run = JsonHelper.Deserialize<CheckRun>(GetToolResultText(runResult));

                JsonElement createResult = await CallToolAsync(mcpClient, sessionId, "create_release", new
                {
                    vesselId = vesselId,
                    title = "MCP Draft Release",
                    checkRunIds = new[] { run.Id }
                }).ConfigureAwait(false);
                AssertToolResultValid(createResult);
                Release release = JsonHelper.Deserialize<Release>(GetToolResultText(createResult));
                AssertStartsWith("rel_", release.Id);

                JsonElement getResult = await CallToolAsync(mcpClient, sessionId, "get_release", new
                {
                    releaseId = release.Id
                }).ConfigureAwait(false);
                AssertToolResultValid(getResult);
                Release fetched = JsonHelper.Deserialize<Release>(GetToolResultText(getResult));
                AssertEqual(release.Id, fetched.Id);
                AssertEqual("MCP Draft Release", fetched.Title);

                JsonElement enumerateResult = await CallToolAsync(mcpClient, sessionId, "armada_enumerate", new
                {
                    entityType = "releases",
                    pageSize = 50,
                    search = "MCP Draft Release"
                }).ConfigureAwait(false);
                AssertToolResultValid(enumerateResult);
                string enumerateText = GetToolResultText(enumerateResult);
                AssertContains(release.Id, enumerateText);
            }));

            cases.Add(CaseAsync("backlog_tools_create_list_update_reorder_and_delete", "BacklogTools_CreateListUpdateReorderAndDelete", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient mcpClient = fx.McpClient;
                string sessionId = await InitMcpSessionAsync(mcpClient);

                JsonElement createResult = await CallToolAsync(mcpClient, sessionId, "create_backlog_item", new
                {
                    title = "MCP Backlog Coverage",
                    description = "Exercise backlog CRUD through MCP.",
                    kind = "Feature",
                    priority = "P1",
                    rank = 25,
                    backlogState = "Inbox",
                    effort = "M"
                }).ConfigureAwait(false);
                AssertToolResultValid(createResult);
                Objective created = JsonHelper.Deserialize<Objective>(GetToolResultText(createResult));
                AssertStartsWith("obj_", created.Id);
                AssertEqual(ObjectiveKindEnum.Feature, created.Kind);
                AssertEqual(ObjectivePriorityEnum.P1, created.Priority);
                AssertEqual(ObjectiveBacklogStateEnum.Inbox, created.BacklogState);

                JsonElement listResult = await CallToolAsync(mcpClient, sessionId, "list_backlog", new
                {
                    search = "MCP Backlog Coverage",
                    pageSize = 25
                }).ConfigureAwait(false);
                AssertToolResultValid(listResult);
                AssertContains(created.Id, GetToolResultText(listResult));

                JsonElement updateResult = await CallToolAsync(mcpClient, sessionId, "update_objective", new
                {
                    objectiveId = created.Id,
                    backlogState = "ReadyForPlanning",
                    rank = 10,
                    targetVersion = "0.8.0"
                }).ConfigureAwait(false);
                AssertToolResultValid(updateResult);
                Objective updated = JsonHelper.Deserialize<Objective>(GetToolResultText(updateResult));
                AssertEqual(ObjectiveBacklogStateEnum.ReadyForPlanning, updated.BacklogState);
                AssertEqual(10, updated.Rank);
                AssertEqual("0.8.0", updated.TargetVersion);

                JsonElement reorderResult = await CallToolAsync(mcpClient, sessionId, "reorder_backlog_items", new
                {
                    items = new[]
                    {
                        new
                        {
                            objectiveId = created.Id,
                            rank = 5
                        }
                    }
                }).ConfigureAwait(false);
                AssertToolResultValid(reorderResult);
                List<Objective> reordered = JsonHelper.Deserialize<List<Objective>>(GetToolResultText(reorderResult));
                AssertEqual(1, reordered.Count);
                AssertEqual(5, reordered[0].Rank);

                JsonElement getResult = await CallToolAsync(mcpClient, sessionId, "get_backlog_item", new
                {
                    objectiveId = created.Id
                }).ConfigureAwait(false);
                AssertToolResultValid(getResult);
                Objective fetched = JsonHelper.Deserialize<Objective>(GetToolResultText(getResult));
                AssertEqual(created.Id, fetched.Id);
                AssertEqual(5, fetched.Rank);
                AssertEqual(ObjectiveBacklogStateEnum.ReadyForPlanning, fetched.BacklogState);

                JsonElement deleteResult = await CallToolAsync(mcpClient, sessionId, "delete_backlog_item", new
                {
                    objectiveId = created.Id
                }).ConfigureAwait(false);
                AssertToolResultValid(deleteResult);
                string deleteText = GetToolResultText(deleteResult);
                AssertContains(created.Id, deleteText);
            }));

            cases.Add(CaseAsync("armada_add_vessel_git_hub_token_override_does_not_leak", "ArmadaAddVessel_GitHubTokenOverrideDoesNotLeak", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient mcpClient = fx.McpClient;
                string sessionId = await InitMcpSessionAsync(mcpClient);

                string fleetId = await RestCreateFleetAsync(mcpClient, sessionId, "AddVesselGitHubOverrideFleet").ConfigureAwait(false);
                string token = "ghp_mcp_" + Guid.NewGuid().ToString("N").Substring(0, 10);
                JsonElement result = await CallToolAsync(mcpClient, sessionId, "armada_add_vessel", new
                {
                    name = "MCP GitHub Override Vessel",
                    repoUrl = TestRepoHelper.GetLocalBareRepoUrl(),
                    fleetId = fleetId,
                    gitHubTokenOverride = token
                }).ConfigureAwait(false);
                AssertToolResultValid(result);
                string text = GetToolResultText(result);
                AssertFalse(text.Contains(token, StringComparison.Ordinal));
                AssertFalse(text.Contains("\"gitHubTokenOverride\"", StringComparison.Ordinal));
                AssertContains("HasGitHubTokenOverride", text);
                Vessel vessel = JsonHelper.Deserialize<Vessel>(text);
                AssertTrue(vessel.HasGitHubTokenOverride);
            }));

            cases.Add(CaseAsync("armada_update_vessel_empty_git_hub_token_override_clears_override", "ArmadaUpdateVessel_EmptyGitHubTokenOverrideClearsOverride", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient mcpClient = fx.McpClient;
                string sessionId = await InitMcpSessionAsync(mcpClient);

                string fleetId = await RestCreateFleetAsync(mcpClient, sessionId, "UpdateVesselGitHubOverrideFleet").ConfigureAwait(false);
                JsonElement addResult = await CallToolAsync(mcpClient, sessionId, "armada_add_vessel", new
                {
                    name = "MCP Clear Override Vessel",
                    repoUrl = TestRepoHelper.GetLocalBareRepoUrl(),
                    fleetId = fleetId,
                    gitHubTokenOverride = "ghp_mcp_clear"
                }).ConfigureAwait(false);
                Vessel added = JsonHelper.Deserialize<Vessel>(GetToolResultText(addResult));
                AssertTrue(added.HasGitHubTokenOverride);

                JsonElement updateResult = await CallToolAsync(mcpClient, sessionId, "armada_update_vessel", new
                {
                    vesselId = added.Id,
                    gitHubTokenOverride = ""
                }).ConfigureAwait(false);
                AssertToolResultValid(updateResult);
                Vessel updated = JsonHelper.Deserialize<Vessel>(GetToolResultText(updateResult));
                AssertFalse(updated.HasGitHubTokenOverride);
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "MCP Tool Tests",
                cases: cases);
        }

        #endregion

        #region Private-Members

        private const string SuiteId = "E2E.McpTool";

        #endregion

        #region Private-Methods

        /// <summary>
        /// Establish a fresh MCP session for a single case and return its session id.
        /// </summary>
        /// <param name="mcpClient">HTTP client targeting the MCP port.</param>
        /// <returns>The initialized session id.</returns>
        private static async Task<string> InitMcpSessionAsync(HttpClient mcpClient)
        {
            string sessionId = Guid.NewGuid().ToString();
            await SendMcpRequestAsync(mcpClient, sessionId, "initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "test-client", version = "1.0" }
            }).ConfigureAwait(false);
            return sessionId;
        }

        /// <summary>
        /// Send an MCP JSON-RPC request and return the <c>result</c> payload, throwing on error.
        /// </summary>
        /// <param name="mcpClient">HTTP client targeting the MCP port.</param>
        /// <param name="sessionId">MCP session id.</param>
        /// <param name="method">JSON-RPC method name.</param>
        /// <param name="parameters">Request parameters.</param>
        /// <returns>The result element of the response.</returns>
        private static async Task<JsonElement> SendMcpRequestAsync(HttpClient mcpClient, string sessionId, string method, object parameters)
        {
            JsonElement result = await SendSingleMcpRequestAsync(mcpClient, sessionId, method, parameters).ConfigureAwait(false);
            if (!String.Equals(method, "tools/list", StringComparison.Ordinal)
                || !result.TryGetProperty("nextCursor", out JsonElement cursorElement))
            {
                return result;
            }

            // The tool catalog is paginated; follow every cursor so a tool on a later page is visible.
            List<JsonElement> tools = result.GetProperty("tools").EnumerateArray().Select(tool => tool.Clone()).ToList();
            string? cursor = cursorElement.GetString();
            while (!String.IsNullOrWhiteSpace(cursor))
            {
                JsonElement page = await SendSingleMcpRequestAsync(mcpClient, sessionId, "tools/list", new { cursor }).ConfigureAwait(false);
                tools.AddRange(page.GetProperty("tools").EnumerateArray().Select(tool => tool.Clone()));
                cursor = page.TryGetProperty("nextCursor", out JsonElement nextCursor) ? nextCursor.GetString() : null;
            }

            return JsonSerializer.SerializeToElement(new { tools });
        }

        /// <summary>
        /// Send one MCP JSON-RPC request and return the <c>result</c> payload, throwing on error.
        /// </summary>
        private static async Task<JsonElement> SendSingleMcpRequestAsync(HttpClient mcpClient, string sessionId, string method, object parameters)
        {
            object request = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = method,
                @params = parameters
            };

            StringContent content = JsonHelper.ToJsonContent(request);

            HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp");
            httpRequest.Content = content;
            httpRequest.Headers.Add("X-Session-Id", sessionId);
            httpRequest.Headers.Add("Accept", "application/json, text/event-stream");

            HttpResponseMessage response = await mcpClient.SendAsync(httpRequest).ConfigureAwait(false);
            string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            Assert(response.IsSuccessStatusCode,
                "MCP request to /mcp failed with " + response.StatusCode + ": " + responseBody);

            JsonElement responseJson = JsonSerializer.Deserialize<JsonElement>(
                ExtractJsonRpcResponse(responseBody, response.Content.Headers.ContentType?.MediaType));

            if (responseJson.TryGetProperty("error", out JsonElement error))
            {
                throw new Exception("MCP error: " + error.GetProperty("message").GetString());
            }

            return responseJson.GetProperty("result");
        }

        /// <summary>
        /// Invoke an MCP tool by name with the supplied arguments.
        /// </summary>
        /// <param name="mcpClient">HTTP client targeting the MCP port.</param>
        /// <param name="sessionId">MCP session id.</param>
        /// <param name="toolName">Tool name.</param>
        /// <param name="arguments">Tool arguments.</param>
        /// <returns>The tool call result element.</returns>
        private static async Task<JsonElement> CallToolAsync(HttpClient mcpClient, string sessionId, string toolName, object arguments)
        {
            return await SendMcpRequestAsync(mcpClient, sessionId, "tools/call", new
            {
                name = toolName,
                arguments = arguments
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Return the JSON-RPC payload of an MCP response, reading the first SSE data event when the
        /// Streamable HTTP endpoint answers with an event stream.
        /// </summary>
        /// <param name="body">Raw response body.</param>
        /// <param name="mediaType">Response media type.</param>
        /// <returns>The JSON-RPC response text.</returns>
        private static string ExtractJsonRpcResponse(string body, string? mediaType)
        {
            if (!String.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                return body;

            foreach (string line in body.Split('\n'))
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                    return line.Substring(5).Trim();
            }

            throw new InvalidDataException("MCP SSE response did not contain a data event.");
        }

        /// <summary>
        /// Assert an MCP tool result has a non-empty text content payload.
        /// </summary>
        /// <param name="result">Tool call result element.</param>
        private static void AssertToolResultValid(JsonElement result)
        {
            Assert(result.TryGetProperty("content", out JsonElement content), "Tool result should have content array");
            Assert(content.GetArrayLength() > 0, "Content array should not be empty");
            AssertEqual("text", content[0].GetProperty("type").GetString());
            AssertFalse(string.IsNullOrEmpty(content[0].GetProperty("text").GetString()), "Tool result text should not be empty");
        }

        /// <summary>
        /// Extract the text payload from an MCP tool result.
        /// </summary>
        /// <param name="result">Tool call result element.</param>
        /// <returns>The text content of the first result element.</returns>
        private static string GetToolResultText(JsonElement result)
        {
            return result.GetProperty("content")[0].GetProperty("text").GetString()!;
        }

        /// <summary>
        /// Create a fleet through the MCP <c>create_fleet</c> tool and return its id.
        /// </summary>
        /// <param name="mcpClient">HTTP client targeting the MCP port.</param>
        /// <param name="sessionId">MCP session id.</param>
        /// <param name="name">Fleet name seed.</param>
        /// <returns>The created fleet id.</returns>
        private static async Task<string> RestCreateFleetAsync(HttpClient mcpClient, string sessionId, string name = "McpTestFleet")
        {
            string uniqueName = name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            JsonElement result = await CallToolAsync(mcpClient, sessionId, "armada_create_fleet", new { name = uniqueName }).ConfigureAwait(false);
            string text = McpToolResults.RequireSuccess("armada_create_fleet", GetToolResultText(result));
            Fleet fleet = JsonHelper.Deserialize<Fleet>(text);
            return fleet.Id;
        }

        /// <summary>
        /// Create a vessel through the MCP <c>add_vessel</c> tool and return its id.
        /// </summary>
        /// <param name="mcpClient">HTTP client targeting the MCP port.</param>
        /// <param name="sessionId">MCP session id.</param>
        /// <param name="fleetId">Owning fleet id.</param>
        /// <param name="name">Vessel name seed.</param>
        /// <param name="workingDirectory">Optional working directory override.</param>
        /// <returns>The created vessel id.</returns>
        private static async Task<string> RestCreateVesselAsync(HttpClient mcpClient, string sessionId, string fleetId, string name = "McpTestVessel", string? workingDirectory = null)
        {
            string uniqueName = name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            JsonElement result = await CallToolAsync(mcpClient, sessionId, "armada_add_vessel", new
            {
                name = uniqueName,
                repoUrl = TestRepoHelper.GetLocalBareRepoUrl(),
                fleetId = fleetId,
                workingDirectory = workingDirectory
            }).ConfigureAwait(false);
            string text = McpToolResults.RequireSuccess("armada_add_vessel", GetToolResultText(result));
            Vessel vessel = JsonHelper.Deserialize<Vessel>(text);
            return vessel.Id;
        }

        private TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: async (CancellationToken ct) =>
                {
                    // Cancel the case's active work so accumulated rows never exhaust fleet capacity for
                    // later cases, and so no case depends on where it sits in the order. The shared fleet
                    // and vessel survive; only missions and voyages are cancelled.
                    try
                    {
                        await body().ConfigureAwait(false);
                    }
                    finally
                    {
                        await E2EServerFixture.CancelActiveWorkAsync(this).ConfigureAwait(false);
                    }
                },
                tags: new List<string> { tag });
        }

        #endregion
    }
}
