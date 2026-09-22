namespace Armada.Test.Runtimes.Suites
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Runtimes;
    using Armada.Test.Common;
    using PolyPrompt.Models;
    using SyslogLogging;

    /// <summary>Exercises the provider wire request and streamed usage through the runtime loop.</summary>
    public sealed class ApiAgentRuntimeCacheTests : TestSuite
    {
        public override string Name => "Api Agent Runtime Cache";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Stable launch prefix is cached across turns without caching tool results", async () =>
            {
                using RecordingHandler handler = new RecordingHandler(false);
                using HttpClient transport = new HttpClient(handler);
                using CachedAnthropicClient client = new CachedAnthropicClient("http://localhost", "test-key", new LoggingModule(), transport);
                client.Model = "test-model";
                ToolChatRequest request = new ToolChatRequest
                {
                    Messages = new List<ChatMessage> { ChatMessage.System("fixed system"), ChatMessage.User("fixed launch") }
                };
                await client.ToolChatAsync(request).ConfigureAwait(false);
                request.Messages.Add(ChatMessage.Assistant("later answer"));
                request.Messages.Add(ChatMessage.User("later request"));
                await client.ToolChatAsync(request).ConfigureAwait(false);
                AssertEqual(2, handler.Bodies.Count);
                JsonNode first = JsonNode.Parse(handler.Bodies[0])!;
                JsonNode second = JsonNode.Parse(handler.Bodies[1])!;
                AssertEqual("fixed system", first["system"]!.GetValue<string>());
                AssertEqual("fixed launch", first["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>());
                AssertEqual("ephemeral", first["messages"]![0]!["content"]![0]!["cache_control"]!["type"]!.GetValue<string>());
                AssertEqual(first["messages"]![0]!.ToJsonString(), second["messages"]![0]!.ToJsonString());
                AssertEqual("later request", second["messages"]![2]!["content"]!.GetValue<string>());
                AssertEqual(1, handler.Bodies[1].Split("cache_control").Length - 1);
            });

            await RunTest("Streamed Anthropic cache usage reaches the runtime usage event", async () =>
            {
                using RecordingHandler handler = new RecordingHandler(true);
                using HttpClient transport = new HttpClient(handler);
                ModelEndpoint endpoint = new ModelEndpoint
                {
                    Name = "cache-wire-test", Provider = ModelProviderEnum.Anthropic,
                    BaseUrl = "http://localhost", Model = "test-model", Enabled = true
                };
                ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, new LoggingModule(), clientFactory:
                    (ep, log) => new CachedAnthropicClient(ep.BaseUrl, "test-key", log, transport));
                List<RuntimeTokenUsage> usage = new List<RuntimeTokenUsage>();
                TaskCompletionSource<int?> exited = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
                runtime.OnTokenUsageReceived += (_, value) => usage.Add(value);
                runtime.OnProcessExited += (_, code) => exited.TrySetResult(code);
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await runtime.StartAsync(Path.GetTempPath(), "fixed launch", token: timeout.Token).ConfigureAwait(false);
                AssertEqual<int?>(0, await exited.Task.WaitAsync(timeout.Token).ConfigureAwait(false));
                AssertEqual(1, usage.Count);
                AssertEqual(7L, usage[0].InputTokens);
                AssertEqual(11L, usage[0].OutputTokens);
                AssertEqual(2048L, usage[0].CacheReadTokens);
                AssertEqual(1024L, usage[0].CacheWriteTokens);
            });
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly bool _Streaming;
            internal List<string> Bodies { get; } = new List<string>();
            internal RecordingHandler(bool streaming) { _Streaming = streaming; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Bodies.Add(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false));
                string body = _Streaming
                    ? "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":7,\"cache_read_input_tokens\":2048,\"cache_creation_input_tokens\":1024}}}\n\n"
                        + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"done\"}}\n\n"
                        + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":11}}\n\n"
                        + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
                    : "{\"content\":[{\"type\":\"text\",\"text\":\"done\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":7,\"output_tokens\":11}}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, _Streaming ? "text/event-stream" : "application/json")
                };
            }
        }
    }
}
