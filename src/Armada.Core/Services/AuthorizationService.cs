namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Authorization;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Service for authorizing API requests using the authorization matrix.
    /// </summary>
    public class AuthorizationService : IAuthorizationService
    {
        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public AuthorizationService()
        {
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public bool IsAuthorized(AuthContext ctx, string method, string path)
        {
            if (ctx != null && (!String.IsNullOrWhiteSpace(ctx.AskThreadId) || !String.IsNullOrWhiteSpace(ctx.AskTurnId))) return false;
            // Mission identity constrains REST writes at this boundary while the underlying services remain
            // available to trusted internal workflows that preserve owner roles.
            if (ctx != null && !String.IsNullOrWhiteSpace(ctx.MissionId)
                && IsMissionForbiddenRoute(method, path))
                return false;

            PermissionLevel required = AuthorizationConfig.GetPermissionLevel(method, path);

            switch (required)
            {
                case PermissionLevel.NoAuthRequired:
                    return true;

                case PermissionLevel.Authenticated:
                    return ctx.IsAuthenticated;

                case PermissionLevel.AdminOnly:
                    return ctx.IsAuthenticated && ctx.IsAdmin;

                case PermissionLevel.TenantAdmin:
                    return ctx.IsAuthenticated && (ctx.IsAdmin || ctx.IsTenantAdmin);

                default:
                    return false;
            }
        }

        private static bool IsMissionForbiddenRoute(string method, string path)
        {
            if (String.IsNullOrWhiteSpace(method) || String.IsNullOrWhiteSpace(path)) return false;

            string normalizedMethod = method.ToUpperInvariant();
            string normalizedPath = path.Split('?', 2)[0].TrimEnd('/').ToLowerInvariant();
            if (normalizedMethod == "GET" || normalizedMethod == "HEAD" || normalizedMethod == "OPTIONS") return false;
            if (normalizedMethod == "POST" && normalizedPath.EndsWith("/enumerate", StringComparison.Ordinal)) return false;

            return (normalizedMethod == "POST"
                    && normalizedPath.StartsWith("/api/v1/captains/", StringComparison.Ordinal)
                    && normalizedPath.EndsWith("/chat", StringComparison.Ordinal))
                || normalizedPath == "/api/v1/objectives"
                || normalizedPath.StartsWith("/api/v1/objectives/", StringComparison.Ordinal)
                || normalizedPath == "/api/v1/backlog"
                || normalizedPath.StartsWith("/api/v1/backlog/", StringComparison.Ordinal)
                || normalizedPath == "/api/v1/objective-refinement-sessions"
                || normalizedPath.StartsWith("/api/v1/objective-refinement-sessions/", StringComparison.Ordinal)
                || normalizedPath == "/api/v1/planning-sessions"
                || normalizedPath.StartsWith("/api/v1/planning-sessions/", StringComparison.Ordinal)
                || normalizedPath == "/api/v1/voyages"
                || normalizedPath.StartsWith("/api/v1/voyages/", StringComparison.Ordinal)
                || normalizedPath == "/api/v1/releases"
                || normalizedPath.StartsWith("/api/v1/releases/", StringComparison.Ordinal)
                || normalizedPath == "/api/v1/deployments"
                || normalizedPath.StartsWith("/api/v1/deployments/", StringComparison.Ordinal)
                || normalizedPath == "/api/v1/incidents"
                || normalizedPath.StartsWith("/api/v1/incidents/", StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public void RequireAuth(AuthContext ctx)
        {
            if (ctx == null || !ctx.IsAuthenticated)
                throw new UnauthorizedAccessException("Authentication required.");
        }

        /// <inheritdoc />
        public void RequireAdmin(AuthContext ctx)
        {
            RequireAuth(ctx);
            if (!ctx.IsAdmin)
                throw new UnauthorizedAccessException("Admin privileges required.");
        }

        /// <inheritdoc />
        public void RequireTenantAdmin(AuthContext ctx)
        {
            RequireAuth(ctx);
            if (!ctx.IsAdmin && !ctx.IsTenantAdmin)
                throw new UnauthorizedAccessException("Tenant admin privileges required.");
        }

        #endregion
    }
}
