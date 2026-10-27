namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Chooses the embedding endpoint for unified context search. Only an explicitly pinned, enabled
    /// Embedding endpoint on a local host qualifies, because the text it receives is private guidance
    /// and memory.
    /// </summary>
    public static class ContextSemanticEndpointPolicy
    {
        #region Public-Members

        /// <summary>No endpoint is pinned.</summary>
        public const string NotConfigured = "not_configured";

        /// <summary>The pinned endpoint is absent, disabled or not an Embedding endpoint.</summary>
        public const string EndpointUnavailable = "endpoint_unavailable";

        /// <summary>The pinned endpoint uses a hosted protocol or a public host.</summary>
        public const string EndpointNotLocal = "endpoint_not_local";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Select the pinned endpoint. Returns null with a named reason when semantic ranking must stay off.
        /// </summary>
        /// <param name="pin">Configured endpoint identifier.</param>
        /// <param name="endpoints">Registered model endpoints.</param>
        /// <param name="reason">Why no endpoint was selected; null on success.</param>
        public static ModelEndpoint? Select(string? pin, IReadOnlyList<ModelEndpoint>? endpoints, out string? reason)
        {
            reason = null;
            if (String.IsNullOrWhiteSpace(pin))
            {
                reason = NotConfigured;
                return null;
            }
            ModelEndpoint? endpoint = endpoints?.FirstOrDefault(item => item != null && String.Equals(item.Id, pin.Trim(), StringComparison.OrdinalIgnoreCase));
            if (endpoint == null || !endpoint.Enabled || endpoint.Kind != ModelEndpointKindEnum.Embedding
                || String.IsNullOrWhiteSpace(endpoint.BaseUrl) || String.IsNullOrWhiteSpace(endpoint.Model))
            {
                reason = EndpointUnavailable;
                return null;
            }
            if (!IsLocal(endpoint))
            {
                reason = EndpointNotLocal;
                return null;
            }
            return endpoint;
        }

        /// <summary>True when the endpoint speaks a self-hostable protocol on a local host.</summary>
        /// <param name="endpoint">Endpoint to classify.</param>
        public static bool IsLocal(ModelEndpoint endpoint)
        {
            if (endpoint == null) return false;
            if (endpoint.Provider != ModelProviderEnum.Ollama && endpoint.Provider != ModelProviderEnum.OpenAICompatible) return false;
            return IsLocalHost(endpoint.BaseUrl);
        }

        /// <summary>
        /// True when the URL host is loopback, a private, link-local or unique-local address, a
        /// single-label name such as a container service, or a name under .internal, .local,
        /// .lan or .localhost.
        /// </summary>
        /// <param name="baseUrl">Absolute HTTP or HTTPS URL.</param>
        public static bool IsLocalHost(string? baseUrl)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            string host = uri.IdnHost.Trim('[', ']').TrimEnd('.');
            if (host.Length == 0) return false;
            if (IPAddress.TryParse(host, out IPAddress? address)) return IsPrivate(address);
            if (String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (!host.Contains('.')) return true;
            foreach (string suffix in new[] { ".internal", ".local", ".lan", ".localhost" })
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        #endregion

        #region Private-Methods

        private static bool IsPrivate(IPAddress address)
        {
            if (IPAddress.IsLoopback(address)) return true;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
                return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
            byte[] bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }

        #endregion
    }
}
