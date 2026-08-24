namespace Armada.Test.Unit.Suites.Services
{
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.Json;
    using Armada.Server.Mcp;
    using Armada.Test.Common;

    /// <summary>
    /// Protocol compatibility tests for Armada's official MCP SDK HTTP adapter.
    /// </summary>
    public class ArmadaMcpHttpServerTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Armada MCP HTTP Server";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("ModernDiscoveryAndToolCalls_Use2026Protocol", async () =>
            {
                int port = GetAvailablePort();
                await using ArmadaMcpHttpServer server = CreateServer(port);
                server.RegisterTool(
                    "armada_echo",
                    "Echo a value",
                    new
                    {
                        type = "object",
                        properties = new { value = new { type = "string" } },
                        required = new[] { "value" }
                    },
                    args => Task.FromResult((object)new
                    {
                        Echo = args!.Value.GetProperty("value").GetString()
                    }));
                await server.StartAsync().ConfigureAwait(false);

                using HttpClient client = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };

                JsonElement discovery = await PostModernAsync(
                    client,
                    "/mcp",
                    1,
                    "server/discover",
                    new { }).ConfigureAwait(false);
                JsonElement discoveryResult = discovery.GetProperty("result");
                AssertEqual("complete", discoveryResult.GetProperty("resultType").GetString());
                AssertTrue(
                    discoveryResult.GetProperty("supportedVersions")
                        .EnumerateArray()
                        .Any(version => version.GetString() == "2026-07-28"),
                    "server/discover should advertise MCP 2026-07-28");

                JsonElement list = await PostModernAsync(
                    client,
                    "/mcp",
                    2,
                    "tools/list",
                    new { }).ConfigureAwait(false);
                JsonElement listResult = list.GetProperty("result");
                AssertEqual("armada_echo", listResult.GetProperty("tools")[0].GetProperty("name").GetString());
                AssertEqual("public", listResult.GetProperty("cacheScope").GetString());
                AssertTrue(listResult.GetProperty("ttlMs").GetInt64() > 0, "tools/list should be cacheable");

                JsonElement call = await PostModernAsync(
                    client,
                    "/mcp",
                    3,
                    "tools/call",
                    new
                    {
                        name = "armada_echo",
                        arguments = new { value = "modern" }
                    },
                    requestName: "armada_echo").ConfigureAwait(false);
                string text = call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
                AssertContains("\"Echo\":\"modern\"", text);
            }).ConfigureAwait(false);

            await RunTest("LegacyInitializeAndRpcRoute_RemainCompatible", async () =>
            {
                int port = GetAvailablePort();
                await using ArmadaMcpHttpServer server = CreateServer(port);
                server.RegisterTool(
                    "armada_legacy",
                    "Legacy compatibility tool",
                    new { type = "object" },
                    args => Task.FromResult((object)new { Status = "ok" }));
                await server.StartAsync().ConfigureAwait(false);

                using HttpClient client = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };

                JsonElement initialize = await PostLegacyAsync(
                    client,
                    "/rpc",
                    10,
                    "initialize",
                    new
                    {
                        protocolVersion = "2025-11-25",
                        capabilities = new { },
                        clientInfo = new { name = "legacy-test", version = "1" }
                    }).ConfigureAwait(false);
                AssertEqual(
                    "2025-11-25",
                    initialize.GetProperty("result").GetProperty("protocolVersion").GetString());

                JsonElement list = await PostLegacyAsync(
                    client,
                    "/rpc",
                    11,
                    "tools/list",
                    new { }).ConfigureAwait(false);
                AssertEqual(
                    "armada_legacy",
                    list.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());

                JsonElement call = await PostLegacyAsync(
                    client,
                    "/rpc",
                    12,
                    "tools/call",
                    new { name = "armada_legacy", arguments = new { } }).ConfigureAwait(false);
                string text = call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
                AssertContains("\"Status\":\"ok\"", text);
            }).ConfigureAwait(false);

            await RunTest("ToolsList_WithoutParams_RemainsCompatible", async () =>
            {
                int port = GetAvailablePort();
                await using ArmadaMcpHttpServer server = CreateServer(port);
                server.RegisterTool(
                    "armada_parameterless_list",
                    "Verify parameterless discovery",
                    new { type = "object" },
                    args => Task.FromResult((object)new { Status = "ok" }));
                await server.StartAsync().ConfigureAwait(false);

                using HttpClient client = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };

                JsonElement list = await SendAsync(
                    client,
                    CreateRequestWithoutParams("/mcp", 12, "tools/list")).ConfigureAwait(false);
                AssertEqual(
                    "armada_parameterless_list",
                    list.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());
            }).ConfigureAwait(false);

            await RunTest("ToolExceptions_RemainJsonRpcErrors", async () =>
            {
                int port = GetAvailablePort();
                await using ArmadaMcpHttpServer server = CreateServer(port);
                server.RegisterTool(
                    "armada_failure",
                    "Throw an expected test failure",
                    new { type = "object" },
                    args => Task.FromException<object>(
                        new InvalidOperationException("expected failure")));
                await server.StartAsync().ConfigureAwait(false);

                using HttpClient client = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };
                JsonElement response = await PostLegacyAsync(
                    client,
                    "/rpc",
                    13,
                    "tools/call",
                    new { name = "armada_failure", arguments = new { } }).ConfigureAwait(false);
                AssertTrue(response.TryGetProperty("error", out _), "tool exception should be a JSON-RPC error");
            }).ConfigureAwait(false);

            await RunTest("ToolsList_PaginatesDeterministically", async () =>
            {
                int port = GetAvailablePort();
                await using ArmadaMcpHttpServer server = CreateServer(port);
                for (int index = 0; index < 501; index++)
                {
                    string name = "armada_tool_" + index.ToString("D3");
                    server.RegisterTool(
                        name,
                        "Tool " + index,
                        new { type = "object" },
                        args => Task.FromResult((object)new { Status = "ok" }));
                }
                await server.StartAsync().ConfigureAwait(false);

                using HttpClient client = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };
                JsonElement first = await PostModernAsync(
                    client,
                    "/mcp",
                    20,
                    "tools/list",
                    new { }).ConfigureAwait(false);
                JsonElement firstResult = first.GetProperty("result");
                AssertEqual(500, firstResult.GetProperty("tools").GetArrayLength());
                AssertEqual("500", firstResult.GetProperty("nextCursor").GetString());

                JsonElement second = await PostModernAsync(
                    client,
                    "/mcp",
                    21,
                    "tools/list",
                    new { cursor = "500" }).ConfigureAwait(false);
                JsonElement secondResult = second.GetProperty("result");
                AssertEqual(1, secondResult.GetProperty("tools").GetArrayLength());
                AssertFalse(secondResult.TryGetProperty("nextCursor", out _), "last page should not have a cursor");
                AssertEqual(
                    "armada_tool_500",
                    secondResult.GetProperty("tools")[0].GetProperty("name").GetString());
            }).ConfigureAwait(false);
        }

        private static ArmadaMcpHttpServer CreateServer(int port)
        {
            return new ArmadaMcpHttpServer("127.0.0.1", port)
            {
                ServerName = "Armada Test",
                ServerVersion = "9.9.9"
            };
        }

        private static async Task<JsonElement> PostModernAsync(
            HttpClient client,
            string path,
            int id,
            string method,
            object parameters,
            string? requestName = null)
        {
            JsonElement paramsElement = JsonSerializer.SerializeToElement(parameters);
            Dictionary<string, object?> paramsDictionary = new Dictionary<string, object?>();
            foreach (JsonProperty property in paramsElement.EnumerateObject())
                paramsDictionary[property.Name] = property.Value.Clone();
            paramsDictionary["_meta"] = new Dictionary<string, object>
            {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientCapabilities"] = new Dictionary<string, object>()
            };

            HttpRequestMessage request = CreateRequest(path, id, method, paramsDictionary);
            request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
            request.Headers.Add("Mcp-Method", method);
            if (requestName != null)
                request.Headers.Add("Mcp-Name", requestName);
            return await SendAsync(client, request).ConfigureAwait(false);
        }

        private static async Task<JsonElement> PostLegacyAsync(
            HttpClient client,
            string path,
            int id,
            string method,
            object parameters)
        {
            return await SendAsync(client, CreateRequest(path, id, method, parameters)).ConfigureAwait(false);
        }

        private static HttpRequestMessage CreateRequest(
            string path,
            int id,
            string method,
            object parameters)
        {
            string json = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters
            });
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Accept", "application/json, text/event-stream");
            return request;
        }

        private static HttpRequestMessage CreateRequestWithoutParams(
            string path,
            int id,
            string method)
        {
            string json = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                method
            });
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Accept", "application/json, text/event-stream");
            return request;
        }

        private static async Task<JsonElement> SendAsync(HttpClient client, HttpRequestMessage request)
        {
            using HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new Exception("MCP HTTP request failed with " + response.StatusCode + ": " + body);

            string json = ExtractJsonRpc(body, response.Content.Headers.ContentType?.MediaType);
            return JsonSerializer.Deserialize<JsonElement>(json);
        }

        private static string ExtractJsonRpc(string body, string? mediaType)
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

        private static int GetAvailablePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
