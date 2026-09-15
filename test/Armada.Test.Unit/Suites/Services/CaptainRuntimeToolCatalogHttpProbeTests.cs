namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Server;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Tests the captain MCP connectivity probe against Streamable HTTP servers, which may answer with a plain
    /// JSON body or with a text/event-stream body framed as data lines.
    /// </summary>
    public class CaptainRuntimeToolCatalogHttpProbeTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Captain Runtime Tool Catalog HTTP Probe";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("HTTP probe advertises both JSON and event-stream in Accept", async () =>
            {
                ScriptedMcpHandler handler = new ScriptedMcpHandler(sse: false);
                List<CaptainToolSummary> tools = await ProbeAsync(handler).ConfigureAwait(false);

                AssertEqual(1, tools.Count, "a JSON-answering server lists its tool");
                AssertTrue(handler.AcceptHeaders.Count > 0, "the probe sent requests");
                foreach (string accept in handler.AcceptHeaders)
                {
                    AssertContains("application/json", accept);
                    AssertContains("text/event-stream", accept);
                }
            }).ConfigureAwait(false);

            await RunTest("HTTP probe reads tools from a plain JSON response body", async () =>
            {
                List<CaptainToolSummary> tools = await ProbeAsync(new ScriptedMcpHandler(sse: false)).ConfigureAwait(false);
                AssertEqual("armada_status", tools.Single().Name, "tool name from the JSON body");
            }).ConfigureAwait(false);

            await RunTest("HTTP probe reads tools from an SSE data-framed response body", async () =>
            {
                List<CaptainToolSummary> tools = await ProbeAsync(new ScriptedMcpHandler(sse: true)).ConfigureAwait(false);
                AssertEqual(1, tools.Count, "an SSE-answering server must not read as unreachable");
                AssertEqual("armada_status", tools.Single().Name, "tool name from the SSE data line");
            }).ConfigureAwait(false);
        }

        private static async Task<List<CaptainToolSummary>> ProbeAsync(ScriptedMcpHandler handler)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            using (HttpClient client = new HttpClient(handler))
            {
                CaptainRuntimeToolCatalogService service = new CaptainRuntimeToolCatalogService(logging, null, client);
                CaptainRuntimeToolCatalogService.RuntimeMcpServerDefinition server = new CaptainRuntimeToolCatalogService.RuntimeMcpServerDefinition
                {
                    Name = "armada",
                    TransportType = "http",
                    Url = "http://127.0.0.1:1/mcp"
                };
                return await service.ProbeHttpToolsAsync(server, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private sealed class ScriptedMcpHandler : HttpMessageHandler
        {
            private readonly bool _Sse;

            public ScriptedMcpHandler(bool sse)
            {
                _Sse = sse;
            }

            public List<string> AcceptHeaders { get; } = new List<string>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                AcceptHeaders.Add(String.Join(", ", request.Headers.Accept.Select(item => item.ToString())));
                string body = request.Content == null ? String.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (body.Contains("notifications/initialized", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(String.Empty) };

                string json = body.Contains("\"initialize\"", StringComparison.Ordinal)
                    ? "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{}}}"
                    : "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":[{\"name\":\"armada_status\",\"description\":\"Status\"}]}}";

                HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Content = _Sse
                    ? new StringContent("event: message\ndata: " + json + "\n\n", Encoding.UTF8, "text/event-stream")
                    : new StringContent(json, Encoding.UTF8, "application/json");
                return response;
            }
        }
    }
}
