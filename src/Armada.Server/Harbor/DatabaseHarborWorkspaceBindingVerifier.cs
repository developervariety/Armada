namespace Armada.Server.Harbor
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>Checks caller scope and exact durable identity for Harbor workspace requests.</summary>
    public sealed partial class DatabaseHarborWorkspaceBindingVerifier : IHarborWorkspaceBindingVerifier
    {
        private readonly DatabaseDriver _Database;

        /// <summary>Instantiate the verifier.</summary>
        public DatabaseHarborWorkspaceBindingVerifier(DatabaseDriver database)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        /// <inheritdoc />
        public async Task<string?> ValidateAsync(AuthContext auth, HarborRunnerSession session, HarborWorkspaceOperationRequest request, CancellationToken token)
        {
            if (auth == null || !auth.IsAuthenticated || String.IsNullOrWhiteSpace(auth.TenantId) || String.IsNullOrWhiteSpace(auth.UserId)) return "workspace_caller_unverified";
            if (!RequestMatchesSession(session, request)) return "workspace_runner_owner_mismatch";
            if (String.IsNullOrWhiteSpace(request.VesselId) || String.IsNullOrWhiteSpace(request.RepositoryGrantId)) return "workspace_repository_identity_missing";
            if (HarborWorkspaceOperationRules.IsMutation(request)
                && !ValidOperationKey(request.OperationKey)) return "workspace_operation_key_required";
            Vessel? vessel = await ReadVesselAsync(auth, request.VesselId, token).ConfigureAwait(false);
            if (vessel == null) return "workspace_vessel_unavailable";
            string? missionFailure = await ValidateMissionScopeAsync(auth, session, vessel, token).ConfigureAwait(false);
            if (missionFailure != null) return missionFailure;
            if (request.Operation == HarborWorkspaceOperationEnum.InspectRepository
                || request.Operation == HarborWorkspaceOperationEnum.ReadRepositoryHealth)
                return request.Binding == null && String.IsNullOrWhiteSpace(request.DockId) ? null : "workspace_repository_binding_invalid";
            if (request.Operation == HarborWorkspaceOperationEnum.ProbeCodexInventory)
            {
                if (request.Binding != null || !String.IsNullOrWhiteSpace(request.DockId)) return "workspace_repository_binding_invalid";
                return CodexInventoryCommand.TryParse(request.InventoryCommand, out _) ? null : "workspace_codex_probe_arguments_invalid";
            }
            return await ValidateDockBindingAsync(auth, session, request, vessel, token).ConfigureAwait(false);
        }

        private async Task<Vessel?> ReadVesselAsync(AuthContext auth, string vesselId, CancellationToken token)
        {
            if (auth.IsAdmin) return await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            if (auth.IsTenantAdmin) return await _Database.Vessels.ReadAsync(auth.TenantId!, vesselId, token).ConfigureAwait(false);
            return await _Database.Vessels.ReadAsync(auth.TenantId!, auth.UserId!, vesselId, token).ConfigureAwait(false);
        }

        private async Task<string?> ValidateDockBindingAsync(AuthContext auth, HarborRunnerSession session, HarborWorkspaceOperationRequest request, Vessel vessel, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(request.DockId) || request.Binding == null) return "workspace_checkout_binding_missing";
            Dock? dock = auth.IsAdmin
                ? await _Database.Docks.ReadAsync(request.DockId, token).ConfigureAwait(false)
                : auth.IsTenantAdmin
                    ? await _Database.Docks.ReadAsync(auth.TenantId!, request.DockId, token).ConfigureAwait(false)
                    : await _Database.Docks.ReadAsync(auth.TenantId!, auth.UserId!, request.DockId, token).ConfigureAwait(false);
            if (dock == null || !String.Equals(dock.VesselId, vessel.Id, StringComparison.Ordinal)) return "workspace_dock_unavailable";
            if (!String.IsNullOrWhiteSpace(auth.MissionId))
            {
                Mission? mission = auth.IsAdmin
                    ? await _Database.Missions.ReadAsync(auth.MissionId!, token).ConfigureAwait(false)
                    : await _Database.Missions.ReadAsync(auth.TenantId!, auth.MissionId!, token).ConfigureAwait(false);
                if (mission == null || !String.Equals(mission.DockId, dock.Id, StringComparison.Ordinal)
                    || !String.Equals(mission.CaptainId, dock.CaptainId, StringComparison.Ordinal)) return "workspace_mission_dock_mismatch";
            }
            HarborCheckoutBinding? stored = dock.HostCheckout;
            if (stored == null || !SameBinding(stored, request.Binding)) return "workspace_checkout_binding_mismatch";
            if (!String.Equals(request.ExpectedRepositoryFingerprint, stored.RepositoryFingerprint, StringComparison.Ordinal))
                return "workspace_repository_fingerprint_mismatch";
            if (HarborWorkspaceOperationRules.IsMutation(request))
            {
                bool? hasUnconfirmed = await HasUnconfirmedHarborJobAsync(dock, token).ConfigureAwait(false);
                if (!hasUnconfirmed.HasValue) return "workspace_dock_stop_state_unavailable";
                if (hasUnconfirmed.Value) return "workspace_dock_runner_stop_unconfirmed";
            }
            return stored != null
                && String.Equals(stored.RunnerId, session.Identity.RunnerId, StringComparison.Ordinal)
                && stored.EnrollmentGeneration == session.EnrollmentGeneration
                && String.Equals(stored.RepositoryGrantId, request.RepositoryGrantId, StringComparison.Ordinal)
                && String.Equals(stored.TenantId, session.Identity.TenantId, StringComparison.Ordinal)
                && String.Equals(stored.UserId, session.Identity.UserId, StringComparison.Ordinal)
                ? null : "workspace_checkout_binding_mismatch";
        }

        private static bool RequestMatchesSession(HarborRunnerSession session, HarborWorkspaceOperationRequest request)
        {
            return session != null && request.Version == HarborWorkspaceProtocol.Version
                && String.Equals(request.RunnerId, session.Identity.RunnerId, StringComparison.Ordinal)
                && String.Equals(request.TenantId, session.Identity.TenantId, StringComparison.Ordinal)
                && String.Equals(request.UserId, session.Identity.UserId, StringComparison.Ordinal)
                && request.EnrollmentGeneration == session.EnrollmentGeneration
                && request.ConnectionGeneration == session.Generation;
        }

        private static bool SameBinding(HarborCheckoutBinding left, HarborCheckoutBinding right)
        {
            return left.Version == right.Version
                && left.EnrollmentGeneration == right.EnrollmentGeneration
                && String.Equals(left.RunnerId, right.RunnerId, StringComparison.Ordinal)
                && String.Equals(left.TenantId, right.TenantId, StringComparison.Ordinal)
                && String.Equals(left.UserId, right.UserId, StringComparison.Ordinal)
                && String.Equals(left.RepositoryGrantId, right.RepositoryGrantId, StringComparison.Ordinal)
                && String.Equals(left.RepositoryFingerprint, right.RepositoryFingerprint, StringComparison.Ordinal)
                && String.Equals(left.CheckoutId, right.CheckoutId, StringComparison.Ordinal)
                && String.Equals(left.BaseCommit, right.BaseCommit, StringComparison.Ordinal)
                && String.Equals(left.BranchName, right.BranchName, StringComparison.Ordinal)
                && String.Equals(left.Revision, right.Revision, StringComparison.Ordinal);
        }

    }
}
