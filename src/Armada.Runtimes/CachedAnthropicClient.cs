namespace Armada.Runtimes
{
    using System.Net.Http;
    using System.Text;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;
    using PolyPrompt.Clients;
    using SyslogLogging;

    /// <summary>
    /// Marks the immutable launch message as an Anthropic cache breakpoint. The prefix includes
    /// tools and the system prompt; later tool results never move this breakpoint.
    /// </summary>
    internal sealed class CachedAnthropicClient : AnthropicClient
    {
        internal CachedAnthropicClient(string endpoint, string? apiKey, LoggingModule logging, HttpClient transport)
            : base(endpoint, apiKey, logging, transport)
        {
        }

        /// <inheritdoc />
        protected override async Task PrepareRequestAsync(HttpRequestMessage request, byte[] body, CancellationToken token)
        {
            await base.PrepareRequestAsync(request, body, token).ConfigureAwait(false);
            if (request.Method != HttpMethod.Post || body.Length == 0) return;

            JsonNode? payload = JsonNode.Parse(body);
            if (payload?["messages"] is not JsonArray messages || messages.Count == 0
                || messages[0] is not JsonObject launch || launch["role"]?.GetValue<string>() != "user"
                || launch["content"] is not JsonValue content || !content.TryGetValue<string>(out string? text)
                || string.IsNullOrEmpty(text)) return;

            launch["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
            });
            HttpContent? previous = request.Content;
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            previous?.Dispose();
        }
    }
}
