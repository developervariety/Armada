namespace Armada.Test.Unit.Suites.Services
{
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Server.Mcp;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for the Armada-owned MCP stdio Content-Length transport.
    /// </summary>
    public class McpStdioServerTests : TestSuite
    {
        /// <summary>
        /// Gets the suite name.
        /// </summary>
        public override string Name => "MCP Stdio Server";

        /// <summary>
        /// Runs MCP stdio transport tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("FramedInitialize_ReturnsFramedServerInfo", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                string request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"codex\",\"version\":\"1.0\"}}}";

                byte[] output = await RunServerAsync(server, CreateFrame(request)).ConfigureAwait(false);
                FrameReadResult frame = ReadSingleFrame(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(frame.Body);
                JsonElement result = response.GetProperty("result");
                JsonElement serverInfo = result.GetProperty("serverInfo");

                AssertEqual("2.0", response.GetProperty("jsonrpc").GetString());
                AssertEqual(1, response.GetProperty("id").GetInt32());
                AssertEqual("Armada Test", serverInfo.GetProperty("name").GetString());
                AssertEqual("9.9.9", serverInfo.GetProperty("version").GetString());
                AssertTrue(output.Length == frame.TotalBytesRead, "stdout should contain only one framed initialize response");
            }).ConfigureAwait(false);

            await RunTest("FramedToolsList_ReturnsRegisteredTools", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                server.RegisterTool(
                    "armada_test_tool",
                    "Test tool",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            value = new { type = "string" }
                        }
                    },
                    args => Task.FromResult((object)new { Status = "ok" }));

                string request = "{\"jsonrpc\":\"2.0\",\"id\":\"tools-1\",\"method\":\"tools/list\",\"params\":{}}";

                byte[] output = await RunServerAsync(server, CreateFrame(request)).ConfigureAwait(false);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(ReadSingleFrame(output).Body);
                JsonElement tools = response.GetProperty("result").GetProperty("tools");

                AssertEqual(1, tools.GetArrayLength());
                AssertEqual("armada_test_tool", tools[0].GetProperty("name").GetString());
                AssertEqual("Test tool", tools[0].GetProperty("description").GetString());
                AssertEqual("object", tools[0].GetProperty("inputSchema").GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("FramedToolsCall_PassesArgumentsAndReturnsContent", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                string? observedValue = null;
                server.RegisterTool(
                    "armada_echo",
                    "Echo tool",
                    new { type = "object" },
                    args =>
                    {
                        if (!args.HasValue)
                            throw new InvalidDataException("handler should receive arguments.");

                        JsonElement arguments = args.Value;
                        observedValue = arguments.GetProperty("value").GetString();
                        return Task.FromResult((object)new { Status = "ok", Echo = observedValue });
                    });

                string request = "{\"jsonrpc\":\"2.0\",\"id\":\"call-1\",\"method\":\"tools/call\",\"params\":{\"name\":\"armada_echo\",\"arguments\":{\"value\":\"codex\"}}}";

                byte[] output = await RunServerAsync(server, CreateFrame(request)).ConfigureAwait(false);
                FrameReadResult frame = ReadSingleFrame(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(frame.Body);
                JsonElement content = response.GetProperty("result").GetProperty("content");
                string text = content[0].GetProperty("text").GetString()!;

                AssertEqual("codex", observedValue);
                AssertEqual("call-1", response.GetProperty("id").GetString());
                AssertEqual(1, content.GetArrayLength());
                AssertEqual("text", content[0].GetProperty("type").GetString());
                AssertContains("\"status\":\"ok\"", text);
                AssertContains("\"echo\":\"codex\"", text);
                AssertEqual(output.Length, frame.TotalBytesRead, "stdout should contain only the framed tools/call response");
            }).ConfigureAwait(false);

            await RunTest("FramedNotificationBeforeRequest_OnlyWritesRequestResponse", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                string notification = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}";
                string request = "{\"jsonrpc\":\"2.0\",\"id\":22,\"method\":\"initialize\",\"params\":{}}";

                byte[] output = await RunServerAsync(server, CombineFrames(notification, request)).ConfigureAwait(false);
                FrameReadResult frame = ReadSingleFrame(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(frame.Body);

                AssertEqual(22, response.GetProperty("id").GetInt32());
                AssertTrue(response.TryGetProperty("result", out _), "initialize request should still receive a response");
                AssertEqual(output.Length, frame.TotalBytesRead, "notification should not produce an extra stdout frame");
            }).ConfigureAwait(false);

            await RunTest("FramedError_DoesNotWriteUnframedText", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                string request = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"missing/method\",\"params\":{}}";

                byte[] output = await RunServerAsync(server, CreateFrame(request)).ConfigureAwait(false);
                FrameReadResult frame = ReadSingleFrame(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(frame.Body);

                AssertTrue(Encoding.ASCII.GetString(output, 0, Math.Min(output.Length, 15)).StartsWith("Content-Length"), "stdout should start with an MCP frame header");
                AssertEqual(output.Length, frame.TotalBytesRead, "stdout should not contain trailing unframed text");
                AssertTrue(response.TryGetProperty("error", out JsonElement error), "unknown method should return a framed JSON-RPC error");
                AssertEqual(-32601, error.GetProperty("code").GetInt32());
                AssertContains("Method not found", error.GetProperty("message").GetString()!);
            }).ConfigureAwait(false);

            await RunTest("FrameMissingContentLength_ReturnsFramedParseError", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                byte[] input = Encoding.ASCII.GetBytes("Content-Type: application/json\r\n\r\n{}");

                byte[] output = await RunServerAsync(server, input).ConfigureAwait(false);
                FrameReadResult frame = ReadSingleFrame(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(frame.Body);
                JsonElement error = response.GetProperty("error");

                AssertEqual(-32700, error.GetProperty("code").GetInt32());
                AssertContains("missing Content-Length", error.GetProperty("message").GetString()!);
                AssertEqual(output.Length, frame.TotalBytesRead, "parser error should be framed with no trailing stdout");
            }).ConfigureAwait(false);

            await RunTest("ToolCallErrorResult_ReturnsFramedInternalError", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                server.RegisterTool(
                    "armada_fail",
                    "Failing tool",
                    new { type = "object" },
                    args => Task.FromResult((object)new { Error = "tool failed" }));

                string request = "{\"jsonrpc\":\"2.0\",\"id\":\"fail-1\",\"method\":\"tools/call\",\"params\":{\"name\":\"armada_fail\",\"arguments\":{}}}";

                byte[] output = await RunServerAsync(server, CreateFrame(request)).ConfigureAwait(false);
                FrameReadResult frame = ReadSingleFrame(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(frame.Body);
                JsonElement error = response.GetProperty("error");

                AssertEqual("fail-1", response.GetProperty("id").GetString());
                AssertEqual(-32603, error.GetProperty("code").GetInt32());
                AssertContains("tool failed", error.GetProperty("message").GetString()!);
                AssertEqual(output.Length, frame.TotalBytesRead, "tool errors should not write unframed stdout");
            }).ConfigureAwait(false);

            await RunTest("McpConfigHelper_CodexUsesStdioAndClaudeRemainsHttp", () =>
            {
                string helper = ReadRepositoryFile("src", "Armada.Helm", "Commands", "McpConfigHelper.cs");

                AssertContains("\"mcp\", \"add\", \"armada\", \"--\"", helper, "Codex install should use a subprocess separator for stdio");
                AssertContains("BuildCodexStdioCommandParts()", helper, "Codex install should be backed by the stdio command builder");
                AssertContains("\"armada\", \"mcp\", \"stdio\"", helper, "Codex fallback command should launch framed Armada stdio");
                AssertContains("claude mcp add --transport http --scope user armada", helper, "Claude default install should remain HTTP");
                AssertContains("claude mcp add --scope user armada -- armada mcp stdio", helper, "Claude stdio alternative should use the fixed Armada stdio path");
            }).ConfigureAwait(false);
        }

        private static ArmadaMcpStdioServer CreateServer()
        {
            return new ArmadaMcpStdioServer
            {
                ServerName = "Armada Test",
                ServerVersion = "9.9.9"
            };
        }

        private static async Task<byte[]> RunServerAsync(ArmadaMcpStdioServer server, byte[] inputBytes)
        {
            using MemoryStream input = new MemoryStream(inputBytes);
            using MemoryStream output = new MemoryStream();
            await server.RunAsync(input, output).ConfigureAwait(false);
            return output.ToArray();
        }

        private static byte[] CreateFrame(string body)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            byte[] headerBytes = Encoding.ASCII.GetBytes("Content-Length: " + bodyBytes.Length + "\r\n\r\n");
            byte[] frame = new byte[headerBytes.Length + bodyBytes.Length];
            Buffer.BlockCopy(headerBytes, 0, frame, 0, headerBytes.Length);
            Buffer.BlockCopy(bodyBytes, 0, frame, headerBytes.Length, bodyBytes.Length);
            return frame;
        }

        private static byte[] CombineFrames(params string[] bodies)
        {
            using MemoryStream stream = new MemoryStream();
            foreach (string body in bodies)
            {
                byte[] frame = CreateFrame(body);
                stream.Write(frame, 0, frame.Length);
            }

            return stream.ToArray();
        }

        private static FrameReadResult ReadSingleFrame(byte[] output)
        {
            byte[] delimiter = new byte[] { 13, 10, 13, 10 };
            int headerEnd = -1;
            for (int i = 0; i <= output.Length - delimiter.Length; i++)
            {
                if (output[i] == delimiter[0]
                    && output[i + 1] == delimiter[1]
                    && output[i + 2] == delimiter[2]
                    && output[i + 3] == delimiter[3])
                {
                    headerEnd = i;
                    break;
                }
            }

            if (headerEnd < 0)
                throw new InvalidDataException("No MCP frame header delimiter found.");

            string header = Encoding.ASCII.GetString(output, 0, headerEnd);
            int contentLength = 0;
            string[] headerLines = header.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in headerLines)
            {
                if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
                contentLength = Int32.Parse(line.Substring("Content-Length:".Length).Trim());
            }

            if (contentLength <= 0)
                throw new InvalidDataException("No Content-Length header found.");

            int bodyStart = headerEnd + delimiter.Length;
            if (output.Length < bodyStart + contentLength)
                throw new InvalidDataException("Incomplete MCP frame body.");

            string body = Encoding.UTF8.GetString(output, bodyStart, contentLength);
            return new FrameReadResult(body, bodyStart + contentLength);
        }

        private static string ReadRepositoryFile(params string[] segments)
        {
            string path = FindRepositoryRoot();
            foreach (string segment in segments)
            {
                path = Path.Combine(path, segment);
            }

            return File.ReadAllText(path);
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "Armada.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new InvalidDataException("Unable to find repository root.");
        }

        private sealed record FrameReadResult(string Body, int TotalBytesRead);
    }
}
