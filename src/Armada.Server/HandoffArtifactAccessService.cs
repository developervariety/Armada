namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// The caller-scoped API over handoff artifacts, shared by REST and MCP. Tenant-scoped like stage outcomes: a global
    /// admin sees every tenant and everyone else their own tenant. Unlike stage outcomes, a captain session (a
    /// mission-scoped token) is allowed, because the complete artifact is what a rendered handoff points the captain at:
    /// it sees the artifacts of its own voyage (or of its own mission and the stage it depends on when it has no voyage).
    /// Ask thread sessions are refused. Lists leave out the output and the diff (their lengths stay); read one artifact
    /// for them. Errors are typed: <see cref="KeyNotFoundException"/> (missing or out of scope) and
    /// <see cref="UnauthorizedAccessException"/> (not allowed).
    /// </summary>
    public class HandoffArtifactAccessService
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <exception cref="ArgumentNullException">Thrown when database is null.</exception>
        public HandoffArtifactAccessService(DatabaseDriver database)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Read one artifact visible to the caller, with its output and diff.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="artifactId">Artifact identifier (hoa_ prefix).</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The artifact.</returns>
        /// <exception cref="ArgumentNullException">Thrown when caller is null.</exception>
        /// <exception cref="KeyNotFoundException">Thrown when the artifact is missing or outside the caller's scope.</exception>
        /// <exception cref="UnauthorizedAccessException">Thrown for an Ask session or a caller without a tenant.</exception>
        public async Task<HandoffArtifact> GetAsync(AuthContext caller, string? artifactId, CancellationToken token = default)
        {
            RequireCaller(caller);
            if (String.IsNullOrWhiteSpace(artifactId)) throw new KeyNotFoundException("Handoff artifact not found.");
            HandoffArtifact? artifact = await _Database.HandoffArtifacts.ReadAsync(artifactId.Trim(), token).ConfigureAwait(false);
            if (artifact == null || !await CanSeeAsync(caller, artifact.TenantId, artifact.MissionId, artifact.VoyageId, token).ConfigureAwait(false))
                throw new KeyNotFoundException("Handoff artifact not found.");
            return artifact;
        }

        /// <summary>
        /// One page of a mission's artifacts, newest first, without output and diff text.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="missionId">Mission identifier (msn_ prefix).</param>
        /// <param name="pageNumber">Page number (default 1).</param>
        /// <param name="pageSize">Page size (1-1000, default 25).</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The page.</returns>
        /// <exception cref="ArgumentNullException">Thrown when caller is null.</exception>
        /// <exception cref="KeyNotFoundException">Thrown when the mission is missing or outside the caller's scope.</exception>
        /// <exception cref="UnauthorizedAccessException">Thrown for an Ask session or a caller without a tenant.</exception>
        public async Task<EnumerationResult<HandoffArtifact>> ListByMissionAsync(AuthContext caller, string? missionId, int? pageNumber = null, int? pageSize = null, CancellationToken token = default)
        {
            RequireCaller(caller);
            if (String.IsNullOrWhiteSpace(missionId)) throw new KeyNotFoundException("Mission not found.");
            Mission? mission = await _Database.Missions.ReadAsync(missionId.Trim(), token).ConfigureAwait(false);
            if (mission == null || !await CanSeeAsync(caller, mission.TenantId, mission.Id, mission.VoyageId, token).ConfigureAwait(false))
                throw new KeyNotFoundException("Mission not found.");

            HandoffArtifactQuery query = new HandoffArtifactQuery
            {
                MissionId = mission.Id,
                PageNumber = pageNumber.HasValue && pageNumber.Value > 0 ? pageNumber.Value : 1,
                PageSize = pageSize.HasValue ? (pageSize.Value < 1 ? 1 : (pageSize.Value > 1000 ? 1000 : pageSize.Value)) : 25
            };
            EnumerationResult<HandoffArtifact> page = await _Database.HandoffArtifacts.EnumerateAsync(query, token).ConfigureAwait(false);
            foreach (HandoffArtifact artifact in page.Objects)
            {
                artifact.Output = null;
                artifact.Diff = null;
            }

            return page;
        }

        #endregion

        #region Private-Methods

        private static void RequireCaller(AuthContext caller)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (!caller.IsAuthenticated) throw new UnauthorizedAccessException("Authentication required.");
            if (!String.IsNullOrEmpty(caller.AskThreadId)) throw new UnauthorizedAccessException("An Ask session cannot read handoff artifacts.");
            if (!caller.IsAdmin && String.IsNullOrEmpty(caller.TenantId)) throw new UnauthorizedAccessException("A tenant is required.");
        }

        private async Task<bool> CanSeeAsync(AuthContext caller, string? tenantId, string missionId, string? voyageId, CancellationToken token)
        {
            if (caller.IsAdmin && String.IsNullOrEmpty(caller.MissionId)) return true;
            if (String.IsNullOrEmpty(tenantId) || !String.Equals(caller.TenantId, tenantId, StringComparison.Ordinal)) return false;
            if (String.IsNullOrEmpty(caller.MissionId)) return true;

            // A captain session: its own voyage, or its own mission and the stage it depends on.
            Mission? own = await _Database.Missions.ReadAsync(caller.TenantId!, caller.MissionId!, token).ConfigureAwait(false);
            if (own == null) return false;
            if (String.Equals(own.Id, missionId, StringComparison.Ordinal) || String.Equals(own.DependsOnMissionId, missionId, StringComparison.Ordinal)) return true;
            return !String.IsNullOrEmpty(own.VoyageId) && String.Equals(own.VoyageId, voyageId, StringComparison.Ordinal);
        }

        #endregion
    }
}
