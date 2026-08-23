namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Registers MCP tools for testing the CD webhook endpoint. Only registered when a CD webhook
    /// dispatcher is configured.
    /// </summary>
    public static class McpCdWebhookTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Registers CD webhook MCP tools.
        /// </summary>
        public static void Register(RegisterToolDelegate register, IReleaseWebhookDispatcher dispatcher)
        {
            register(
                "test_release_webhook",
                "Send a synthetic release.shipped payload to the configured CD webhook endpoint and return the delivery outcome. Use to verify endpoint configuration, reachability, and authentication before approving real releases.",
                new
                {
                    type = "object",
                    properties = new { }
                },
                async (args) =>
                {
                    ReleaseWebhookPayload payload = new ReleaseWebhookPayload
                    {
                        Event = "release.shipped",
                        ReleaseId = "rel_webhook_test",
                        VesselId = "vsl_webhook_test",
                        Title = "CD Webhook Test Payload",
                        Version = "0.0.0",
                        TagName = "v0.0.0-test",
                        Summary = "Synthetic payload from the test_release_webhook tool; no release was approved.",
                        Status = "Shipped"
                    };

                    WebhookDispatchResult result = await dispatcher.DispatchAsync(payload).ConfigureAwait(false);
                    return (object)new
                    {
                        result.Outcome,
                        result.StatusCode,
                        result.ErrorMessage,
                        result.Attempts
                    };
                });
        }
    }
}
