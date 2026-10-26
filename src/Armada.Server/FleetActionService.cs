namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>Owns exact-owner mission action definitions and durable run requests.</summary>
    public sealed partial class FleetActionService
    {
        private const int _MaximumTargets = 50;
        private const int _MaximumPromptLength = 16000;
        private const int _MaximumSnapshotBytes = 2 * 1024 * 1024;
        private const int _MaximumRequestIdLength = 128;
        private readonly DatabaseDriver _Database;
        private readonly IAdmiralService _Admiral;

        /// <summary>Create the owner-scoped action service.</summary>
        public FleetActionService(DatabaseDriver database, IAdmiralService admiral)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>List action definitions for the current operator.</summary>
        public async Task<List<FleetAction>> ListAsync(AuthContext caller, CancellationToken token = default)
        {
            AuthContext actor = await RequireOperatorAsync(caller, token).ConfigureAwait(false);
            return await _Database.FleetActions.EnumerateAsync(actor.TenantId!, actor.UserId!, 100, token).ConfigureAwait(false);
        }

        /// <summary>Read one exact-owner action definition.</summary>
        public async Task<FleetAction?> GetAsync(AuthContext caller, string actionId, CancellationToken token = default)
        {
            AuthContext actor = await RequireOperatorAsync(caller, token).ConfigureAwait(false);
            return await _Database.FleetActions.ReadAsync(actor.TenantId!, actor.UserId!, actionId, token).ConfigureAwait(false);
        }

        internal Task<AuthContext> RequireOperatorAsync(AuthContext caller, CancellationToken token) => ValidateOperatorAsync(caller, token);

        internal async Task<AuthContext?> ReadActiveOperatorAsync(string tenantId, string userId, CancellationToken token)
        {
            UserMaster? user = await _Database.Users.ReadByIdAsync(userId, token).ConfigureAwait(false);
            TenantMetadata? tenant = await _Database.Tenants.ReadAsync(tenantId, token).ConfigureAwait(false);
            if (user == null || tenant == null || !user.Active || !tenant.Active || !user.IsAdmin || user.TenantId != tenantId) return null;
            return AuthContext.Authenticated(tenantId, userId, true, user.IsTenantAdmin, "FleetAction");
        }
    }
}
