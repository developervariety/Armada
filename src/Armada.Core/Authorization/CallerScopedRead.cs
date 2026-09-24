namespace Armada.Core.Authorization
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
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
            Scope scope = Resolve(database, caller, id);
            if (scope.Kind == ScopeKindEnum.None) return null;
            if (scope.Kind == ScopeKindEnum.Global) return await database.Vessels.ReadAsync(id!, token).ConfigureAwait(false);
            if (scope.Kind == ScopeKindEnum.Tenant) return await database.Vessels.ReadAsync(scope.TenantId!, id!, token).ConfigureAwait(false);
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
            Scope scope = Resolve(database, caller, id);
            if (scope.Kind == ScopeKindEnum.None) return null;
            if (scope.Kind == ScopeKindEnum.Global) return await database.Voyages.ReadAsync(id!, token).ConfigureAwait(false);
            if (scope.Kind == ScopeKindEnum.Tenant) return await database.Voyages.ReadAsync(scope.TenantId!, id!, token).ConfigureAwait(false);
            return await database.Voyages.ReadAsync(scope.TenantId!, scope.UserId!, id!, token).ConfigureAwait(false);
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
            Scope scope = Resolve(database, caller, id);
            if (scope.Kind == ScopeKindEnum.None) return null;
            if (scope.Kind == ScopeKindEnum.Global) return await database.Missions.ReadAsync(id!, token).ConfigureAwait(false);
            if (scope.Kind == ScopeKindEnum.Tenant) return await database.Missions.ReadAsync(scope.TenantId!, id!, token).ConfigureAwait(false);
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
            Scope scope = Resolve(database, caller, id);
            if (scope.Kind == ScopeKindEnum.None) return null;
            if (scope.Kind == ScopeKindEnum.Global) return await database.Captains.ReadAsync(id!, token).ConfigureAwait(false);
            if (scope.Kind == ScopeKindEnum.Tenant) return await database.Captains.ReadAsync(scope.TenantId!, id!, token).ConfigureAwait(false);
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
            Scope scope = Resolve(database, caller, id);
            if (scope.Kind == ScopeKindEnum.None) return null;
            if (scope.Kind == ScopeKindEnum.Global) return await database.Fleets.ReadAsync(id!, token).ConfigureAwait(false);
            if (scope.Kind == ScopeKindEnum.Tenant) return await database.Fleets.ReadAsync(scope.TenantId!, id!, token).ConfigureAwait(false);
            return await database.Fleets.ReadAsync(scope.TenantId!, scope.UserId!, id!, token).ConfigureAwait(false);
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
            Scope scope = Resolve(database, caller, id);
            if (scope.Kind == ScopeKindEnum.None) return null;
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
            Scope scope = Resolve(database, caller, id);
            if (scope.Kind == ScopeKindEnum.None) return null;
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
            Scope scope = Resolve(database, caller, id);
            if (scope.Kind == ScopeKindEnum.None) return null;
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
            Scope scope = Resolve(database, caller, id);
            if (scope.Kind == ScopeKindEnum.None) return null;
            return await database.Environments.ReadAsync(id!, new DeploymentEnvironmentQuery { TenantId = scope.TenantId, UserId = scope.UserId }, token).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private static Scope Resolve(DatabaseDriver database, AuthContext caller, string? id)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (String.IsNullOrWhiteSpace(id)) return new Scope(ScopeKindEnum.None, null, null);
            if (caller.IsAdmin) return new Scope(ScopeKindEnum.Global, null, null);
            if (String.IsNullOrWhiteSpace(caller.TenantId)) return new Scope(ScopeKindEnum.None, null, null);
            if (caller.IsTenantAdmin) return new Scope(ScopeKindEnum.Tenant, caller.TenantId, null);
            if (String.IsNullOrWhiteSpace(caller.UserId)) return new Scope(ScopeKindEnum.None, null, null);
            return new Scope(ScopeKindEnum.User, caller.TenantId, caller.UserId);
        }

        #endregion

        #region Private-Types

        private enum ScopeKindEnum
        {
            None,
            Global,
            Tenant,
            User
        }

        private sealed class Scope
        {
            public Scope(ScopeKindEnum kind, string? tenantId, string? userId)
            {
                Kind = kind;
                TenantId = tenantId;
                UserId = userId;
            }

            public ScopeKindEnum Kind { get; }

            public string? TenantId { get; }

            public string? UserId { get; }
        }

        #endregion
    }
}
