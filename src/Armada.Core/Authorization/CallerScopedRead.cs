namespace Armada.Core.Authorization
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The one caller-scoped read for records a request names by id. A global administrator reads any
    /// record, a tenant administrator reads the records of its own tenant, and any other caller reads only
    /// the records it owns. A record outside that scope reads as absent, so its existence is not disclosed.
    ///
    /// A route reads an id in its path through this rule, and an id in its body must be read through the
    /// same rule before it is stored or acted on: downstream services read by id without a caller, so an
    /// unchecked body id lets one tenant act on another tenant's record.
    /// </summary>
    public static class CallerScopedRead
    {
        #region Public-Methods

        /// <summary>
        /// Read a vessel within the caller's scope.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="id">Vessel identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The vessel, or null when it is absent or outside the caller's scope.</returns>
        public static async Task<Vessel?> ReadVesselAsync(DatabaseDriver database, AuthContext caller, string? id, CancellationToken token = default)
        {
            CallerRecordScope scope = Resolve(database, caller, id);
            if (scope.Kind == CallerRecordScopeKindEnum.None) return null;
            if (scope.Kind == CallerRecordScopeKindEnum.Global) return await database.Vessels.ReadAsync(id!, token).ConfigureAwait(false);
            if (scope.Kind == CallerRecordScopeKindEnum.Tenant) return await database.Vessels.ReadAsync(scope.TenantId!, id!, token).ConfigureAwait(false);
            return await database.Vessels.ReadAsync(scope.TenantId!, scope.UserId!, id!, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Read a voyage within the caller's scope.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="id">Voyage identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The voyage, or null when it is absent or outside the caller's scope.</returns>
        public static async Task<Voyage?> ReadVoyageAsync(DatabaseDriver database, AuthContext caller, string? id, CancellationToken token = default)
        {
            CallerRecordScope scope = Resolve(database, caller, id);
            if (scope.Kind == CallerRecordScopeKindEnum.None) return null;
            if (scope.Kind == CallerRecordScopeKindEnum.Global) return await database.Voyages.ReadAsync(id!, token).ConfigureAwait(false);
            Voyage? voyage = scope.Kind == CallerRecordScopeKindEnum.Tenant
                ? await database.Voyages.ReadAsync(scope.TenantId!, id!, token).ConfigureAwait(false)
                : await database.Voyages.ReadAsync(scope.TenantId!, scope.UserId!, id!, token).ConfigureAwait(false);
            if (voyage != null) return voyage;

            // Scoped queries use exact stored values. Fall back by ID so legacy null-owner rows can use the
            // same default ownership rule as every other caller-scoped record.
            voyage = await database.Voyages.ReadAsync(id!, token).ConfigureAwait(false);
            if (voyage == null) return null;
            if (scope.Kind == CallerRecordScopeKindEnum.Tenant)
                return OwnershipPolicy.SameTenant(voyage.TenantId, scope.TenantId) ? voyage : null;
            return OwnershipPolicy.CanUseFor(
                scope.TenantId,
                scope.UserId,
                voyage.TenantId,
                voyage.UserId,
                OwnershipScopeEnum.UserSpecific,
                false)
                ? voyage
                : null;
        }

        /// <summary>
        /// Read a mission within the caller's scope.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="id">Mission identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The mission, or null when it is absent or outside the caller's scope.</returns>
        public static async Task<Mission?> ReadMissionAsync(DatabaseDriver database, AuthContext caller, string? id, CancellationToken token = default)
        {
            CallerRecordScope scope = Resolve(database, caller, id);
            if (scope.Kind == CallerRecordScopeKindEnum.None) return null;
            if (scope.Kind == CallerRecordScopeKindEnum.Global) return await database.Missions.ReadAsync(id!, token).ConfigureAwait(false);
            if (scope.Kind == CallerRecordScopeKindEnum.Tenant) return await database.Missions.ReadAsync(scope.TenantId!, id!, token).ConfigureAwait(false);
            return await database.Missions.ReadAsync(scope.TenantId!, scope.UserId!, id!, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Read a captain within the caller's scope.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="id">Captain identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The captain, or null when it is absent or outside the caller's scope.</returns>
        public static async Task<Captain?> ReadCaptainAsync(DatabaseDriver database, AuthContext caller, string? id, CancellationToken token = default)
        {
            CallerRecordScope scope = Resolve(database, caller, id);
            if (scope.Kind == CallerRecordScopeKindEnum.None) return null;
            if (scope.Kind == CallerRecordScopeKindEnum.Global) return await database.Captains.ReadAsync(id!, token).ConfigureAwait(false);
            if (scope.Kind == CallerRecordScopeKindEnum.Tenant) return await database.Captains.ReadAsync(scope.TenantId!, id!, token).ConfigureAwait(false);
            return await database.Captains.ReadAsync(scope.TenantId!, scope.UserId!, id!, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Read a fleet within the caller's scope.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="id">Fleet identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The fleet, or null when it is absent or outside the caller's scope.</returns>
        public static async Task<Fleet?> ReadFleetAsync(DatabaseDriver database, AuthContext caller, string? id, CancellationToken token = default)
        {
            CallerRecordScope scope = Resolve(database, caller, id);
            if (scope.Kind == CallerRecordScopeKindEnum.None) return null;
            if (scope.Kind == CallerRecordScopeKindEnum.Global) return await database.Fleets.ReadAsync(id!, token).ConfigureAwait(false);
            if (scope.Kind == CallerRecordScopeKindEnum.Tenant) return await database.Fleets.ReadAsync(scope.TenantId!, id!, token).ConfigureAwait(false);
            return await database.Fleets.ReadAsync(scope.TenantId!, scope.UserId!, id!, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Read a dock within the caller's scope.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="id">Dock identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The dock, or null when it is absent or outside the caller's scope.</returns>
        public static async Task<Dock?> ReadDockAsync(DatabaseDriver database, AuthContext caller, string? id, CancellationToken token = default)
        {
            CallerRecordScope scope = Resolve(database, caller, id);
            if (scope.Kind == CallerRecordScopeKindEnum.None) return null;
            if (scope.Kind == CallerRecordScopeKindEnum.Global) return await database.Docks.ReadAsync(id!, token).ConfigureAwait(false);
            if (scope.Kind == CallerRecordScopeKindEnum.Tenant) return await database.Docks.ReadAsync(scope.TenantId!, id!, token).ConfigureAwait(false);
            return await database.Docks.ReadAsync(scope.TenantId!, scope.UserId!, id!, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Read an objective row within the caller's scope.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="id">Objective identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The objective, or null when it is absent or outside the caller's scope.</returns>
        public static async Task<Objective?> ReadObjectiveAsync(DatabaseDriver database, AuthContext caller, string? id, CancellationToken token = default)
        {
            CallerRecordScope scope = Resolve(database, caller, id);
            if (scope.Kind == CallerRecordScopeKindEnum.None) return null;
            if (scope.Kind == CallerRecordScopeKindEnum.Global) return await database.Objectives.ReadAsync(id!, token).ConfigureAwait(false);
            if (scope.Kind == CallerRecordScopeKindEnum.Tenant) return await database.Objectives.ReadAsync(scope.TenantId!, id!, token).ConfigureAwait(false);
            return await database.Objectives.ReadAsync(scope.TenantId!, scope.UserId!, id!, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Read a deployment within the caller's scope.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="id">Deployment identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The deployment, or null when it is absent or outside the caller's scope.</returns>
        public static async Task<Deployment?> ReadDeploymentAsync(DatabaseDriver database, AuthContext caller, string? id, CancellationToken token = default)
        {
            CallerRecordScope scope = Resolve(database, caller, id);
            if (scope.Kind == CallerRecordScopeKindEnum.None) return null;
            return await database.Deployments.ReadAsync(id!, new DeploymentQuery { TenantId = scope.TenantId, UserId = scope.UserId }, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Read a release within the caller's scope.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="id">Release identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The release, or null when it is absent or outside the caller's scope.</returns>
        public static async Task<Release?> ReadReleaseAsync(DatabaseDriver database, AuthContext caller, string? id, CancellationToken token = default)
        {
            CallerRecordScope scope = Resolve(database, caller, id);
            if (scope.Kind == CallerRecordScopeKindEnum.None) return null;
            return await database.Releases.ReadAsync(id!, new ReleaseQuery { TenantId = scope.TenantId, UserId = scope.UserId }, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Read a check run within the caller's scope.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="id">Check run identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The check run, or null when it is absent or outside the caller's scope.</returns>
        public static async Task<CheckRun?> ReadCheckRunAsync(DatabaseDriver database, AuthContext caller, string? id, CancellationToken token = default)
        {
            CallerRecordScope scope = Resolve(database, caller, id);
            if (scope.Kind == CallerRecordScopeKindEnum.None) return null;
            return await database.CheckRuns.ReadAsync(id!, new CheckRunQuery { TenantId = scope.TenantId, UserId = scope.UserId }, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Read a deployment environment within the caller's scope.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="id">Environment identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The environment, or null when it is absent or outside the caller's scope.</returns>
        public static async Task<DeploymentEnvironment?> ReadEnvironmentAsync(DatabaseDriver database, AuthContext caller, string? id, CancellationToken token = default)
        {
            CallerRecordScope scope = Resolve(database, caller, id);
            if (scope.Kind == CallerRecordScopeKindEnum.None) return null;
            return await database.Environments.ReadAsync(id!, new DeploymentEnvironmentQuery { TenantId = scope.TenantId, UserId = scope.UserId }, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Resolve the records a caller may read. The per-id reads above and every caller-scoped listing
        /// use this one rule, so a listing never shows a record its per-id read would refuse.
        /// </summary>
        /// <param name="caller">Authenticated caller.</param>
        /// <returns>The caller's read scope.</returns>
        public static CallerRecordScope ResolveScope(AuthContext caller)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (caller.IsAdmin) return new CallerRecordScope(CallerRecordScopeKindEnum.Global, null, null);
            if (String.IsNullOrWhiteSpace(caller.TenantId)) return new CallerRecordScope(CallerRecordScopeKindEnum.None, null, null);
            if (caller.IsTenantAdmin) return new CallerRecordScope(CallerRecordScopeKindEnum.Tenant, caller.TenantId, null);
            if (String.IsNullOrWhiteSpace(caller.UserId)) return new CallerRecordScope(CallerRecordScopeKindEnum.None, null, null);
            return new CallerRecordScope(CallerRecordScopeKindEnum.User, caller.TenantId, caller.UserId);
        }

        /// <summary>
        /// List one page of records within a read scope through the store's unscoped, tenant and owner
        /// listings. An empty scope lists nothing.
        /// </summary>
        /// <typeparam name="T">Record type.</typeparam>
        /// <param name="scope">Read scope from <see cref="ResolveScope"/>.</param>
        /// <param name="query">Paging and filters.</param>
        /// <param name="all">Lists every record.</param>
        /// <param name="byTenant">Lists the records of one tenant.</param>
        /// <param name="byOwner">Lists the records one user owns in one tenant.</param>
        /// <returns>One page of records within the scope.</returns>
        public static async Task<EnumerationResult<T>> EnumerateAsync<T>(
            CallerRecordScope scope,
            EnumerationQuery query,
            Func<EnumerationQuery, Task<EnumerationResult<T>>> all,
            Func<string, EnumerationQuery, Task<EnumerationResult<T>>> byTenant,
            Func<string, string, EnumerationQuery, Task<EnumerationResult<T>>> byOwner)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (all == null) throw new ArgumentNullException(nameof(all));
            if (byTenant == null) throw new ArgumentNullException(nameof(byTenant));
            if (byOwner == null) throw new ArgumentNullException(nameof(byOwner));
            if (scope.Kind == CallerRecordScopeKindEnum.Global) return await all(query).ConfigureAwait(false);
            if (scope.Kind == CallerRecordScopeKindEnum.Tenant) return await byTenant(scope.TenantId!, query).ConfigureAwait(false);
            if (scope.Kind == CallerRecordScopeKindEnum.User) return await byOwner(scope.TenantId!, scope.UserId!, query).ConfigureAwait(false);
            return EnumerationResult<T>.Create(query, new List<T>(), 0);
        }

        #endregion

        #region Private-Methods

        private static CallerRecordScope Resolve(DatabaseDriver database, AuthContext caller, string? id)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (String.IsNullOrWhiteSpace(id)) return new CallerRecordScope(CallerRecordScopeKindEnum.None, null, null);
            return ResolveScope(caller);
        }

        #endregion
    }
}
