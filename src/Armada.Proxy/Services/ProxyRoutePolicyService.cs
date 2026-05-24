namespace Armada.Proxy.Services
{
    using Armada.Core;
    using Armada.Core.Models;

    /// <summary>
    /// Central policy gate for generic dashboard relay requests.
    /// </summary>
    public class ProxyRoutePolicyService
    {
        #region Public-Methods

        /// <summary>
        /// Evaluate whether a relayed dashboard request is allowed.
        /// </summary>
        public bool TryAuthorize(RemoteTunnelHttpRelayRequest? request, out int statusCode, out string? message)
        {
            statusCode = 200;
            message = null;

            if (request == null)
            {
                statusCode = 400;
                message = "Relay request payload is required.";
                return false;
            }

            string method = (request.Method ?? String.Empty).Trim().ToUpperInvariant();
            string path = NormalizePath(request.Path);

            if (String.IsNullOrWhiteSpace(path) || !path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase))
            {
                statusCode = 400;
                message = "Only Armada API routes under /api/v1/* can be relayed.";
                return false;
            }

            if (String.Equals(path, "/api/v1/status/shutdown", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(path, "/api/v1/server/stop", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(path, "/api/v1/status/factory-reset", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(path, "/api/v1/server/reset", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(path, "/api/v1/restore", StringComparison.OrdinalIgnoreCase))
            {
                statusCode = 403;
                message = "This Armada route is blocked by proxy policy for remote access.";
                return false;
            }

            if (String.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
                (String.Equals(path, "/api/v1/authenticate", StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(path, "/api/v1/tenants/lookup", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (!String.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                if (path.StartsWith("/api/v1/settings", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/api/v1/tenants", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/api/v1/users", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/api/v1/credentials", StringComparison.OrdinalIgnoreCase))
                {
                    statusCode = 403;
                    message = "This administrative Armada route is blocked by proxy policy for remote access.";
                    return false;
                }
            }

            return true;
        }

        #endregion

        #region Private-Methods

        private static string NormalizePath(string? path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                return String.Empty;
            }

            string normalized = path.Trim();
            return normalized.StartsWith("/", StringComparison.Ordinal) ? normalized : "/" + normalized;
        }

        #endregion
    }
}
