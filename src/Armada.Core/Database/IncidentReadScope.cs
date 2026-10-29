namespace Armada.Core.Database
{
    using System;

    /// <summary>
    /// What an incident read can see: every incident, the incidents of one tenant, the incidents of one user in a
    /// tenant, or none.
    /// </summary>
    public sealed class IncidentReadScope
    {
        private IncidentReadScope(bool matchesNothing, string? tenantId, string? userId)
        {
            MatchesNothing = matchesNothing;
            TenantId = tenantId;
            UserId = userId;
        }

        /// <summary>True when the scope sees no incident.</summary>
        public bool MatchesNothing { get; }

        /// <summary>Tenant the scope is limited to, or null for every tenant.</summary>
        public string? TenantId { get; }

        /// <summary>User the scope is limited to, or null for every user of the tenant.</summary>
        public string? UserId { get; }

        /// <summary>Every incident.</summary>
        public static IncidentReadScope All() => new IncidentReadScope(false, null, null);

        /// <summary>The incidents of one tenant.</summary>
        public static IncidentReadScope Tenant(string? tenantId) => String.IsNullOrEmpty(tenantId) ? None() : new IncidentReadScope(false, tenantId, null);

        /// <summary>The incidents of one user in one tenant.</summary>
        public static IncidentReadScope User(string? tenantId, string? userId) =>
            String.IsNullOrEmpty(tenantId) || String.IsNullOrEmpty(userId) ? None() : new IncidentReadScope(false, tenantId, userId);

        /// <summary>No incident.</summary>
        public static IncidentReadScope None() => new IncidentReadScope(true, null, null);
    }
}
