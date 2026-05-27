namespace Armada.Test.Unit.Suites.Services
{
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Server.Mcp;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for the Armada-owned MCP stdio transport.
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
                FramedMessage message = ReadSingleFramedMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);
                JsonElement result = response.GetProperty("result");
                JsonElement serverInfo = result.GetProperty("serverInfo");

                AssertEqual("2.0", response.GetProperty("jsonrpc").GetString());
                AssertEqual(1, response.GetProperty("id").GetInt32());
                AssertEqual("Armada Test", serverInfo.GetProperty("name").GetString());
                AssertEqual("9.9.9", serverInfo.GetProperty("version").GetString());
                AssertEqual(output.Length, message.TotalBytesRead, "stdout should contain only one Content-Length framed initialize response");
                AssertEqual(message.DeclaredContentLength, Encoding.UTF8.GetByteCount(message.Body), "Content-Length must equal the UTF-8 byte length of the body");
            }).ConfigureAwait(false);

            await RunTest("LineDelimitedInitialize_ReturnsLineDelimitedServerInfo", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                string request = "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"codex\",\"version\":\"1.0\"}}}\n";

                byte[] output = await RunServerAsync(server, Encoding.UTF8.GetBytes(request)).ConfigureAwait(false);
                LineMessage message = ReadSingleLineMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);

                AssertEqual("2.0", response.GetProperty("jsonrpc").GetString());
                AssertEqual(11, response.GetProperty("id").GetInt32());
                AssertEqual("Armada Test", response.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
                AssertFalse(message.Body.StartsWith("Content-Length", StringComparison.OrdinalIgnoreCase), "stdout should be bare JSON-RPC, not Content-Length framed");
                AssertEqual(output.Length, message.TotalBytesRead, "stdout should contain only one line-delimited initialize response");
            }).ConfigureAwait(false);

            await RunTest("FramedInitialize_WithUtf8Payload_UsesByteContentLength", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                string request = "{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"cod\u00e9x\",\"version\":\"1.0\"}}}";

                byte[] output = await RunServerAsync(server, CreateFrame(request)).ConfigureAwait(false);
                FramedMessage message = ReadSingleFramedMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);

                AssertEqual(12, response.GetProperty("id").GetInt32());
                AssertEqual("Armada Test", response.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
                AssertEqual(output.Length, message.TotalBytesRead, "stdout should contain only one Content-Length framed initialize response");
                AssertEqual(message.DeclaredContentLength, Encoding.UTF8.GetByteCount(message.Body), "Content-Length must equal the UTF-8 byte length of the body");
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
                FramedMessage message = ReadSingleFramedMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);
                JsonElement tools = response.GetProperty("result").GetProperty("tools");

                AssertEqual(1, tools.GetArrayLength());
                AssertEqual("armada_test_tool", tools[0].GetProperty("name").GetString());
                AssertEqual("Test tool", tools[0].GetProperty("description").GetString());
                AssertEqual("object", tools[0].GetProperty("inputSchema").GetProperty("type").GetString());
                AssertEqual(output.Length, message.TotalBytesRead, "stdout should contain only the framed tools/list response");
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
                FramedMessage message = ReadSingleFramedMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);
                JsonElement content = response.GetProperty("result").GetProperty("content");
                string text = content[0].GetProperty("text").GetString()!;

                AssertEqual("codex", observedValue);
                AssertEqual("call-1", response.GetProperty("id").GetString());
                AssertEqual(1, content.GetArrayLength());
                AssertEqual("text", content[0].GetProperty("type").GetString());
                AssertContains("\"status\":\"ok\"", text);
                AssertContains("\"echo\":\"codex\"", text);
                AssertEqual(output.Length, message.TotalBytesRead, "stdout should contain only the framed tools/call response");
            }).ConfigureAwait(false);

            await RunTest("FramedNotificationBeforeRequest_OnlyWritesRequestResponse", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                string notification = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}";
                string request = "{\"jsonrpc\":\"2.0\",\"id\":22,\"method\":\"initialize\",\"params\":{}}";

                byte[] output = await RunServerAsync(server, CombineFrames(notification, request)).ConfigureAwait(false);
                FramedMessage message = ReadSingleFramedMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);

                AssertEqual(22, response.GetProperty("id").GetInt32());
                AssertTrue(response.TryGetProperty("result", out _), "initialize request should still receive a response");
                AssertEqual(output.Length, message.TotalBytesRead, "notification should not produce an extra stdout frame");
            }).ConfigureAwait(false);

            await RunTest("Error_FramedRequestReceivesFramedError", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                string request = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"missing/method\",\"params\":{}}";

                byte[] output = await RunServerAsync(server, CreateFrame(request)).ConfigureAwait(false);
                FramedMessage message = ReadSingleFramedMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);

                AssertEqual(output.Length, message.TotalBytesRead, "stdout should contain only the framed error response");
                AssertTrue(response.TryGetProperty("error", out JsonElement error), "unknown method should return a JSON-RPC error");
                AssertEqual(-32601, error.GetProperty("code").GetInt32());
                AssertContains("Method not found", error.GetProperty("message").GetString()!);
            }).ConfigureAwait(false);

            await RunTest("LineInvalidJson_ReturnsLineDelimitedParseError", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                byte[] input = Encoding.ASCII.GetBytes("not-json\n");

                byte[] output = await RunServerAsync(server, input).ConfigureAwait(false);
                LineMessage message = ReadSingleLineMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);
                JsonElement error = response.GetProperty("error");

                AssertEqual(-32700, error.GetProperty("code").GetInt32());
                AssertContains("Parse error", error.GetProperty("message").GetString()!);
                AssertEqual(output.Length, message.TotalBytesRead, "parser error should be line-delimited with no trailing stdout");
            }).ConfigureAwait(false);

            await RunTest("ToolCallErrorResult_FramedRequestReceivesFramedInternalError", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                server.RegisterTool(
                    "armada_fail",
                    "Failing tool",
                    new { type = "object" },
                    args => Task.FromResult((object)new { Error = "tool failed" }));

                string request = "{\"jsonrpc\":\"2.0\",\"id\":\"fail-1\",\"method\":\"tools/call\",\"params\":{\"name\":\"armada_fail\",\"arguments\":{}}}";

                byte[] output = await RunServerAsync(server, CreateFrame(request)).ConfigureAwait(false);
                FramedMessage message = ReadSingleFramedMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);
                JsonElement error = response.GetProperty("error");

                AssertEqual("fail-1", response.GetProperty("id").GetString());
                AssertEqual(-32603, error.GetProperty("code").GetInt32());
                AssertContains("tool failed", error.GetProperty("message").GetString()!);
                AssertEqual(output.Length, message.TotalBytesRead, "tool errors should not write trailing stdout");
            }).ConfigureAwait(false);

            await RunTest("FramedMultipleRequests_ReturnsFramedResponsesInOrder", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                string first = "{\"jsonrpc\":\"2.0\",\"id\":101,\"method\":\"initialize\",\"params\":{}}";
                string second = "{\"jsonrpc\":\"2.0\",\"id\":102,\"method\":\"ping\",\"params\":{}}";

                byte[] output = await RunServerAsync(server, CombineFrames(first, second)).ConfigureAwait(false);

                FramedMessage firstMessage = ReadFramedMessage(output, 0);
                FramedMessage secondMessage = ReadFramedMessage(output, firstMessage.TotalBytesRead);
                JsonElement firstResponse = JsonSerializer.Deserialize<JsonElement>(firstMessage.Body);
                JsonElement secondResponse = JsonSerializer.Deserialize<JsonElement>(secondMessage.Body);

                AssertEqual(101, firstResponse.GetProperty("id").GetInt32());
                AssertEqual(102, secondResponse.GetProperty("id").GetInt32());
                AssertEqual(output.Length, firstMessage.TotalBytesRead + secondMessage.TotalBytesRead, "stdout should contain exactly two back-to-back framed responses");
            }).ConfigureAwait(false);

            await RunTest("FramedRun_StdoutContainsNoUnframedText", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                server.RegisterTool(
                    "armada_payload",
                    "Payload tool",
                    new { type = "object" },
                    args => Task.FromResult((object)new { Status = "ok" }));
                string request = "{\"jsonrpc\":\"2.0\",\"id\":\"only-one\",\"method\":\"tools/call\",\"params\":{\"name\":\"armada_payload\",\"arguments\":{}}}";

                byte[] output = await RunServerAsync(server, CreateFrame(request)).ConfigureAwait(false);
                FramedMessage message = ReadSingleFramedMessage(output);

                AssertEqual(output.Length, message.TotalBytesRead, "stdout should contain only the framed response and nothing else");
                AssertTrue(StartsWithAscii(output, "Content-Length:"), "stdout should begin with the Content-Length header");
            }).ConfigureAwait(false);

            await RunTest("McpConfigHelper_CodexUsesStdioAndClaudeRemainsHttp", () =>
            {
                string helper = ReadRepositoryFile("src", "Armada.Helm", "Commands", "McpConfigHelper.cs");

                AssertContains("\"mcp\", \"add\", \"armada\", \"--\"", helper, "Codex install should use a subprocess separator for stdio");
                AssertContains("BuildCodexStdioCommandParts()", helper, "Codex install should be backed by the stdio command builder");
                AssertContains("\"armada\", \"mcp\", \"stdio\"", helper, "Codex fallback command should launch Armada stdio");
                AssertContains("private const int CodexMcpStartupTimeoutSeconds = 120;", helper, "Codex install should pin a startup timeout for cold stdio launches");
                AssertContains("EnsureTomlMcpServerStartupTimeoutAsync", helper, "Codex native CLI install should patch startup_timeout_sec after adding the server");
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

        private static FramedMessage ReadSingleFramedMessage(byte[] output)
        {
            FramedMessage message = ReadFramedMessage(output, 0);
            if (output.Length != message.TotalBytesRead)
                throw new InvalidDataException("Unexpected extra bytes after framed MCP response.");
            return message;
        }

        private static FramedMessage ReadFramedMessage(byte[] output, int offset)
        {
            string headerSeparator = "\r\n\r\n";
            byte[] separator = Encoding.ASCII.GetBytes(headerSeparator);
            int separatorIndex = IndexOfByteSequence(output, offset, separator);
            if (separatorIndex < 0)
                throw new InvalidDataException("Framed MCP response is missing the header separator.");

            string headerSection = Encoding.ASCII.GetString(output, offset, separatorIndex - offset);
            int declaredLength = ParseContentLengthHeader(headerSection);
            int bodyStart = separatorIndex + separator.Length;
            if (bodyStart + declaredLength > output.Length)
                throw new InvalidDataException("Framed MCP response is truncated.");

            string body = Encoding.UTF8.GetString(output, bodyStart, declaredLength);
            int total = (bodyStart + declaredLength) - offset;
            return new FramedMessage(body, declaredLength, total);
        }

        private static LineMessage ReadSingleLineMessage(byte[] output)
        {
            string text = Encoding.UTF8.GetString(output);
            int newlineIndex = text.IndexOf('\n');
            if (newlineIndex < 0)
                throw new InvalidDataException("No line-delimited MCP response found.");

            string body = text.Substring(0, newlineIndex).TrimEnd('\r');
            string trailing = text.Substring(newlineIndex + 1);
            if (!String.IsNullOrWhiteSpace(trailing))
                throw new InvalidDataException("Unexpected extra line-delimited MCP response.");

            int bytesRead = Encoding.UTF8.GetByteCount(text.Substring(0, newlineIndex + 1));
            return new LineMessage(body, bytesRead);
        }

        private static int ParseContentLengthHeader(string headerSection)
        {
            string[] lines = headerSection.Split(new[] { "\r\n" }, StringSplitOptions.None);
            foreach (string line in lines)
            {
                if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
                string value = line.Substring("Content-Length:".Length).Trim();
                if (Int32.TryParse(value, out int length)) return length;
            }

            throw new InvalidDataException("Framed MCP response is missing a Content-Length header.");
        }

        private static int IndexOfByteSequence(byte[] haystack, int startIndex, byte[] needle)
        {
            if (needle.Length == 0) return startIndex;
            for (int i = startIndex; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }

            return -1;
        }

        private static bool StartsWithAscii(byte[] output, string prefix)
        {
            byte[] prefixBytes = Encoding.ASCII.GetBytes(prefix);
            if (output.Length < prefixBytes.Length) return false;
            for (int i = 0; i < prefixBytes.Length; i++)
            {
                if (output[i] != prefixBytes[i]) return false;
            }

            return true;
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

        private sealed record FramedMessage(string Body, int DeclaredContentLength, int TotalBytesRead);

        private sealed record LineMessage(string Body, int TotalBytesRead);
    }
}

