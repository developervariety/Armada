namespace Armada.Server.WebSocket
{
    using System;
    using Armada.Core.Authorization;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Who may receive one live WebSocket event.
    ///
    /// An event is delivered to the sessions that could read the record it describes: its owning
    /// user, the administrators of its tenant, and global administrators. An event with no known
    /// owner is delivered to global administrators only, so a missing owner can never widen delivery.
    /// Private conversation scopes instead admit only their exact interactive owner.
    /// </summary>
    public sealed class WebSocketDeliveryScope
    {
        #region Public-Members

        /// <summary>
        /// Delivered to global administrators only.
        /// </summary>
        public static WebSocketDeliveryScope AdminOnly { get; } = new WebSocketDeliveryScope(null, null, true);

        /// <summary>
        /// Owning tenant, or null when the event has no known owner.
        /// </summary>
        public string? TenantId { get; }

        /// <summary>
        /// Owning user, or null when only tenant administrators may receive the event.
        /// </summary>
        public string? UserId { get; }

        /// <summary>
        /// True when only global administrators may receive the event.
        /// </summary>
        public bool IsAdminOnly { get; }

        /// <summary>True when only the exact interactive owner may receive this event.</summary>
        public bool IsInteractiveOwnerOnly { get; }

        #endregion

        #region Constructors-and-Factories

        private WebSocketDeliveryScope(string? tenantId, string? userId, bool adminOnly, bool interactiveOwnerOnly = false)
        {
            TenantId = String.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
            UserId = String.IsNullOrWhiteSpace(userId) ? null : userId;
            IsAdminOnly = !interactiveOwnerOnly && (adminOnly || TenantId == null);
            IsInteractiveOwnerOnly = interactiveOwnerOnly;
        }

        /// <summary>
        /// Scope for an event about a record owned by a tenant and, optionally, a user. A record with
        /// no tenant is delivered to global administrators only.
        /// </summary>
        /// <param name="tenantId">Owning tenant.</param>
        /// <param name="userId">Owning user.</param>
        /// <returns>The delivery scope.</returns>
        public static WebSocketDeliveryScope ForOwner(string? tenantId, string? userId)
        {
            return new WebSocketDeliveryScope(tenantId, userId, false);
        }

        /// <summary>Scope for a private conversation: no administrator or mission-session exception.</summary>
        /// <param name="tenantId">Owning tenant.</param>
        /// <param name="userId">Owning user.</param>
        /// <returns>Exact interactive-owner scope; a missing owner reaches no session.</returns>
        public static WebSocketDeliveryScope ForInteractiveOwner(string? tenantId, string? userId)
        {
            return new WebSocketDeliveryScope(tenantId, userId, false, true);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Decide whether an authenticated session may receive an event with this scope.
        /// </summary>
        /// <param name="auth">Session identity.</param>
        /// <returns>True when the session may receive the event.</returns>
        public bool CanReceive(AuthContext? auth)
        {
            if (auth == null || !auth.IsAuthenticated) return false;
            if (IsInteractiveOwnerOnly)
                return TenantId != null && UserId != null && String.IsNullOrWhiteSpace(auth.MissionId)
                    && String.IsNullOrWhiteSpace(auth.AskThreadId) && String.IsNullOrWhiteSpace(auth.AskTurnId)
                    && String.Equals(TenantId, auth.TenantId, StringComparison.Ordinal)
                    && String.Equals(UserId, auth.UserId, StringComparison.Ordinal);
            if (auth.IsAdmin) return true;
            if (IsAdminOnly) return false;

            // Operational records are private to their owner inside the tenant; the shared ownership
            // rule then admits the owner and the tenant's administrators. An event with a tenant but
            // no user reaches the tenant's administrators only.
            if (UserId == null)
                return auth.IsTenantAdmin && String.Equals(TenantId, OwnershipPolicy.TenantOf(auth), StringComparison.Ordinal);
            return OwnershipPolicy.CanView(auth, TenantId, UserId, OwnershipScopeEnum.UserSpecific);
        }

        #endregion
    }
}
