namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Exercises saved Ask actions through the live REST and MCP endpoints.</summary>
    public sealed class AskActionApiSuite : IArmadaTestSuite
    {
        private const string SuiteId = "E2E.AskActionApi";

        /// <summary>Builds the saved Ask action integration case.</summary>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>
            {
                new TestCaseDescriptor(SuiteId, "proposal_approve_and_scope", "Proposal defers and gates an exact MCP action", async (CancellationToken _) => await VerifyProposalFlowAsync().ConfigureAwait(false), new List<string> { TestTags.Positive })
            };
            return new TestSuiteDescriptor(SuiteId, "Ask Action API", cases);
        }

        private static async Task VerifyProposalFlowAsync()
        {
            E2EServerFixture fx = await E2EServerFixture.AcquireAsync(typeof(AskActionApiSuite)).ConfigureAwait(false);
            string title = "Saved Ask proposal " + Guid.NewGuid().ToString("N");
            AskThread thread;
            using (HttpResponseMessage response = await fx.AuthClient.PostAsync("/api/v1/ask/threads", JsonHelper.ToJsonContent(new { title = "Action approval E2E" })).ConfigureAwait(false))
            {
                AssertEqual(HttpStatusCode.OK, response.StatusCode, "human owner creates the saved thread");
                thread = await JsonHelper.DeserializeAsync<AskThread>(response).ConfigureAwait(false);
            }

            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            using (DatabaseDriver database = DatabaseDriverFactory.Create(fx.Settings.Database, logging))
            {
                string requestId = "ask_" + Guid.NewGuid().ToString("N");
                AskTurn turn = new AskTurn { ThreadId = thread.Id, RequestId = requestId, CaptainId = "cpt_e2e", Content = "Create the prepared objective." };
                AskTurnReservation reservation = await database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, turn).ConfigureAwait(false);
                AssertEqual(AskTurnReservationResultEnum.Created, reservation.Result, "the E2E turn is stored before MCP access");
                AssertTrue(await database.AskThreads.MarkRunningAsync(thread.TenantId, thread.UserId, thread.Id, requestId).ConfigureAwait(false), "the stored turn is active");

                SessionTokenService tokens = new SessionTokenService(fx.Settings.SessionTokenEncryptionKey);
                string askToken = tokens.CreateAskTurnToken(thread.TenantId, thread.UserId, thread.Id, requestId).Token!;
                using (HttpClient askMcp = CreateBearerClient("http://127.0.0.1:" + fx.McpPort, askToken))
                using (HttpClient askRest = CreateBearerClient(fx.BaseUrl, askToken))
                {
                    using (HttpResponseMessage restDenied = await askRest.GetAsync("/api/v1/ask/threads/" + Uri.EscapeDataString(thread.Id) + "/proposals").ConfigureAwait(false))
                        AssertEqual(HttpStatusCode.Unauthorized, restDenied.StatusCode, "the Ask-turn token is not a REST credential");
                    using (HttpResponseMessage exchangeDenied = await askRest.PostAsync("/api/v1/authenticate", JsonHelper.ToJsonContent(new { })).ConfigureAwait(false))
                        AssertEqual(HttpStatusCode.Forbidden, exchangeDenied.StatusCode, "the Ask-turn token cannot be exchanged for a REST session");

                    string sessionId = await InitializeMcpAsync(askMcp).ConfigureAwait(false);
                    McpToolResult toolResult = await CallToolAsync(askMcp, sessionId, "create_objective", new { title }).ConfigureAwait(false);
                    AssertToolResultValid(toolResult);
                    AssertContains("ask_action_proposed", GetToolText(toolResult), "the MCP handler returns a saved proposal acknowledgement");

                    List<AskActionProposal> proposals = await ReadProposalsAsync(fx.AuthClient, thread.Id).ConfigureAwait(false);
                    AssertEqual(1, proposals.Count, "one proposal is persisted for this tool call");
                    AskActionProposal proposal = proposals.Single();
                    AssertEqual(AskProposalStateEnum.Pending, proposal.State, "MCP did not execute the write");
                    AssertEqual("create_objective", proposal.ToolName);
                    AssertContains(title, proposal.ArgumentsJson, "the stored canonical arguments preserve the unique title");
                    AssertEqual(0, (await FindObjectivesAsync(database, thread, title).ConfigureAwait(false)).Count, "proposing the action creates no objective");

                    await VerifyDecisionScopeAsync(fx, thread, proposal).ConfigureAwait(false);
                    using (HttpResponseMessage malformed = await fx.AuthClient.PostAsync(DecisionPath(thread.Id, proposal.Id, "approve"), new StringContent("{", Encoding.UTF8, "application/json")).ConfigureAwait(false))
                        AssertEqual(HttpStatusCode.BadRequest, malformed.StatusCode, "malformed decision JSON is rejected");
                    using (HttpResponseMessage missing = await fx.AuthClient.PostAsync(DecisionPath(thread.Id, proposal.Id, "approve"), new StringContent(String.Empty, Encoding.UTF8, "application/json")).ConfigureAwait(false))
                        AssertEqual(HttpStatusCode.BadRequest, missing.StatusCode, "the required fingerprint body cannot be omitted");
                    using (HttpResponseMessage wrongFingerprint = await PostDecisionAsync(fx.AuthClient, thread.Id, proposal.Id, "approve", new String('0', 64)).ConfigureAwait(false))
                        AssertEqual(HttpStatusCode.Conflict, wrongFingerprint.StatusCode, "a changed fingerprint cannot approve");
                    AssertEqual(0, (await FindObjectivesAsync(database, thread, title).ConfigureAwait(false)).Count, "refused decisions leave the original tool unapplied");

                    using (HttpResponseMessage approved = await PostDecisionAsync(fx.AuthClient, thread.Id, proposal.Id, "approve", proposal.Fingerprint).ConfigureAwait(false))
                    {
                        AssertEqual(HttpStatusCode.OK, approved.StatusCode, "the owner approves the exact reviewed action");
                        AskProposalDecision decision = await JsonHelper.DeserializeAsync<AskProposalDecision>(approved).ConfigureAwait(false);
                        AssertEqual(200, decision.StatusCode);
                        AssertEqual(AskProposalStateEnum.Executed, decision.Proposal!.State);
                    }
                    List<Objective> created = await FindObjectivesAsync(database, thread, title).ConfigureAwait(false);
                    AssertEqual(1, created.Count, "the original objective handler ran once");
                    using (HttpResponseMessage repeated = await PostDecisionAsync(fx.AuthClient, thread.Id, proposal.Id, "approve", proposal.Fingerprint).ConfigureAwait(false))
                        AssertEqual(HttpStatusCode.OK, repeated.StatusCode, "a repeated approval reads the recorded decision");
                    AssertEqual(1, (await FindObjectivesAsync(database, thread, title).ConfigureAwait(false)).Count, "repeated approval does not execute the tool twice");

                    using (HttpResponseMessage workResponse = await fx.AuthClient.GetAsync("/api/v1/ask/threads/" + Uri.EscapeDataString(thread.Id) + "/work").ConfigureAwait(false))
                    {
                        AssertEqual(HttpStatusCode.OK, workResponse.StatusCode);
                        List<AskWorkReport> work = await JsonHelper.DeserializeAsync<List<AskWorkReport>>(workResponse).ConfigureAwait(false);
                        AssertEqual(0, work.Count, "an objective is not reported as landed mission or voyage work");
                    }
                }
            }
        }

        private static async Task VerifyDecisionScopeAsync(E2EServerFixture fx, AskThread thread, AskActionProposal proposal)
        {
            using (HttpClient otherAdmin = await AskThreadApiTestClients.CreateOtherAdminAsync(fx).ConfigureAwait(false))
            using (HttpResponseMessage denied = await PostDecisionAsync(otherAdmin, thread.Id, proposal.Id, "approve", proposal.Fingerprint).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.NotFound, denied.StatusCode, "a different administrator cannot see the owner's proposal");

            SessionTokenService tokens = new SessionTokenService(fx.Settings.SessionTokenEncryptionKey);
            string missionToken = tokens.CreateMissionToken(thread.TenantId, thread.UserId, "mis_" + Guid.NewGuid().ToString("N")).Token!;
            using (HttpClient missionCaller = CreateBearerClient(fx.BaseUrl, missionToken))
            using (HttpResponseMessage denied = await PostDecisionAsync(missionCaller, thread.Id, proposal.Id, "approve", proposal.Fingerprint).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.Forbidden, denied.StatusCode, "a mission caller cannot decide a human proposal");
        }

        private static async Task<List<Objective>> FindObjectivesAsync(DatabaseDriver database, AskThread thread, string title)
        {
            List<Objective> all = await database.Objectives.EnumerateAsync(thread.TenantId, thread.UserId).ConfigureAwait(false);
            return all.Where(item => String.Equals(item.Title, title, StringComparison.Ordinal)).ToList();
        }

        private static async Task<List<AskActionProposal>> ReadProposalsAsync(HttpClient client, string threadId)
        {
            using (HttpResponseMessage response = await client.GetAsync("/api/v1/ask/threads/" + Uri.EscapeDataString(threadId) + "/proposals").ConfigureAwait(false))
            {
                AssertEqual(HttpStatusCode.OK, response.StatusCode, "the human owner can read proposals");
                return await JsonHelper.DeserializeAsync<List<AskActionProposal>>(response).ConfigureAwait(false);
            }
        }

        private static Task<HttpResponseMessage> PostDecisionAsync(HttpClient client, string threadId, string proposalId, string decision, string fingerprint)
        {
            return client.PostAsync(DecisionPath(threadId, proposalId, decision), JsonHelper.ToJsonContent(new AskProposalDecisionRequest { Fingerprint = fingerprint }));
        }

        private static string DecisionPath(string threadId, string proposalId, string decision)
        {
            return "/api/v1/ask/threads/" + Uri.EscapeDataString(threadId) + "/proposals/" + Uri.EscapeDataString(proposalId) + "/" + decision;
        }

        private static HttpClient CreateBearerClient(string baseUrl, string token)
        {
            HttpClient client = new HttpClient { BaseAddress = new Uri(baseUrl) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        private static async Task<string> InitializeMcpAsync(HttpClient client)
        {
            string sessionId = Guid.NewGuid().ToString("N");
            await SendMcpAsync(client, sessionId, "initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "ask-action-e2e", version = "1.0" } }).ConfigureAwait(false);
            return sessionId;
        }

        private static async Task<McpToolResult> CallToolAsync(HttpClient client, string sessionId, string name, object arguments)
        {
            McpRpcEnvelope response = await SendMcpAsync(client, sessionId, "tools/call", new { name, arguments }).ConfigureAwait(false);
            return response.Result ?? throw new InvalidOperationException("MCP tools/call response has no result.");
        }

        private static async Task<McpRpcEnvelope> SendMcpAsync(HttpClient client, string sessionId, string method, object parameters)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/mcp"))
            {
                request.Content = JsonHelper.ToJsonContent(new { jsonrpc = "2.0", id = 1, method, @params = parameters });
                request.Headers.Add("X-Session-Id", sessionId);
                request.Headers.Add("Accept", "application/json, text/event-stream");
                using (HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Assert(response.IsSuccessStatusCode, "MCP request failed with " + response.StatusCode + ": " + body);
                    string json = ExtractResponseJson(body, response.Content.Headers.ContentType?.MediaType);
                    McpRpcEnvelope envelope = JsonHelper.Deserialize<McpRpcEnvelope>(json);
                    if (envelope.Error != null) throw new InvalidOperationException("MCP request failed: " + envelope.Error.Message);
                    if (envelope.Result == null) throw new InvalidOperationException("MCP response has no result.");
                    return envelope;
                }
            }
        }

        private static string ExtractResponseJson(string body, string? mediaType)
        {
            if (!String.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)) return body;
            foreach (string line in body.Split('\n'))
                if (line.StartsWith("data:", StringComparison.Ordinal)) return line.Substring(5).Trim();
            throw new InvalidOperationException("MCP event stream has no data event.");
        }

        private static void AssertToolResultValid(McpToolResult result)
        {
            Assert(result.Content.Count > 0, "MCP tool result has text content");
            AssertEqual("text", result.Content[0].Type);
            AssertFalse(String.IsNullOrWhiteSpace(result.Content[0].Text), "MCP tool result text is not empty");
        }

        private static string GetToolText(McpToolResult result) => result.Content[0].Text;

        private sealed class McpRpcEnvelope
        {
            public McpRpcError? Error { get; set; }
            public McpToolResult? Result { get; set; }
        }

        private sealed class McpRpcError
        {
            public string Message { get; set; } = String.Empty;
        }

        private sealed class McpToolResult
        {
            public List<McpToolContent> Content { get; set; } = new List<McpToolContent>();
        }

        private sealed class McpToolContent
        {
            public string Type { get; set; } = String.Empty;
            public string Text { get; set; } = String.Empty;
        }
    }
}
