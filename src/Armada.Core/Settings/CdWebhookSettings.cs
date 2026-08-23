namespace Armada.Core.Settings
{
    /// <summary>
    /// Configuration for outbound CD webhooks. When a release transitions to Shipped, the admiral
    /// POSTs a JSON evidence payload to the configured endpoint so an external continuous-delivery
    /// system (Argo CD, Flux, GitHub Actions, or any webhook consumer) can pick up deployment.
    /// Lives under the <c>cdWebhook</c> key in ~/.armada/settings.json. Null or absent means the
    /// feature is disabled; the admiral runs as today.
    /// </summary>
    public sealed class CdWebhookSettings
    {
        /// <summary>Master enable; false (or section absent) disables webhook dispatch entirely.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Absolute HTTPS or HTTP endpoint that receives the release-shipped payload.</summary>
        public string? Url { get; set; }

        /// <summary>Optional bearer token sent as the Authorization header when non-empty.</summary>
        public string? BearerToken { get; set; }

        /// <summary>
        /// Per-request timeout in seconds. Defaults to 10. Values less than or equal to zero fall
        /// back to 10.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 10;

        /// <summary>Returns true if the section has the minimum config to dispatch webhooks.</summary>
        public bool IsConfigured()
        {
            return Enabled && !String.IsNullOrEmpty(Url);
        }
    }
}
