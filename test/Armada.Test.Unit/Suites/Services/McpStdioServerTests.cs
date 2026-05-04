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

        private sealed record FrameReadResult(string Body, int TotalBytesRead);
    }
}
