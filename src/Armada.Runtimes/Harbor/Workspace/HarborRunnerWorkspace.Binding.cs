namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>Validates and resolves persisted checkout identities.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private HarborCheckoutBinding RequireBinding(
            HarborWorkspaceOperationRequest request,
            HarborHandshakeAck ack,
            HarborRepositoryGrant grant)
        {
            HarborCheckoutBinding? binding = request.Binding;
            if (binding == null || !binding.IsComplete
                || !String.Equals(binding.RunnerId, _Settings.RunnerId, StringComparison.Ordinal)
                || binding.EnrollmentGeneration != ack.EnrollmentGeneration
                || !String.Equals(binding.TenantId, ack.TenantId, StringComparison.Ordinal)
                || !String.Equals(binding.UserId, ack.UserId, StringComparison.Ordinal)
                || !String.Equals(binding.RepositoryGrantId, grant.Id, StringComparison.Ordinal)
                || !String.Equals(binding.RepositoryFingerprint, RepositoryFingerprint(grant.RepositoryUrl), StringComparison.OrdinalIgnoreCase))
                throw Failure("workspace_binding_mismatch", "The checkout binding does not match this authenticated runner and repository grant.");
            if (String.IsNullOrWhiteSpace(request.DockId) || request.DockId.Length > 160)
                throw Failure("workspace_dock_identity_invalid", "The durable dock identity is missing or invalid.");
            return binding;
        }

        private HarborCheckoutIntent ReadReadyIntent(
            HarborWorkspaceOperationRequest request,
            HarborCheckoutBinding binding,
            HarborRepositoryGrant grant)
        {
            HarborCheckoutIntent intent = ReadIntent(request, binding, grant);
            if (!String.Equals(intent.State, "Ready", StringComparison.Ordinal))
                throw Failure("workspace_checkout_not_ready", "The durable checkout is not ready for this operation.");
            return intent;
        }

        private HarborCheckoutIntent ReadIntent(
            HarborWorkspaceOperationRequest request,
            HarborCheckoutBinding binding,
            HarborRepositoryGrant grant)
        {
            HarborCheckoutIntent intent = _Journal.Read(binding.CheckoutId);
            if (!SameBinding(intent.Binding, binding)
                || !String.Equals(intent.DockId, request.DockId, StringComparison.Ordinal)
                || !String.Equals(intent.VesselId, request.VesselId, StringComparison.Ordinal)
                || !String.Equals(binding.RepositoryGrantId, grant.Id, StringComparison.Ordinal))
                throw Failure("workspace_checkout_not_ready", "The durable checkout record does not match this operation.");
            return intent;
        }

        private string CheckoutPath(HarborRepositoryGrant grant, HarborCheckoutBinding binding, bool requireExists)
        {
            grant.Validate();
            string path = Path.GetFullPath(Path.Combine(grant.CheckoutRoot, binding.CheckoutId));
            if (!Armada.Core.Services.PathContainment.IsWithin(grant.CheckoutRoot, path))
                throw Failure("workspace_checkout_path_invalid", "The checkout path is outside its configured root.");
            EnsureNoLinks(path, requireExists);
            if (requireExists) HarborCheckoutGitIdentity.Validate(path, grant.RepositoryPath);
            return path;
        }

        private static bool SameBinding(HarborCheckoutBinding left, HarborCheckoutBinding right)
        {
            return left.RunnerId == right.RunnerId && left.EnrollmentGeneration == right.EnrollmentGeneration
                && left.TenantId == right.TenantId && left.UserId == right.UserId
                && left.RepositoryGrantId == right.RepositoryGrantId && left.RepositoryFingerprint == right.RepositoryFingerprint
                && left.CheckoutId == right.CheckoutId && left.BaseCommit == right.BaseCommit
                && left.BranchName == right.BranchName && left.Revision == right.Revision;
        }
        private static void CopyVerificationOutputResult(HarborWorkspaceOperationResult source, HarborWorkspaceOperationResult destination)
        {
            destination.BytesBase64 = source.BytesBase64;
            destination.Length = source.Length;
            destination.VerificationOutputId = source.VerificationOutputId;
            destination.VerificationCheckId = source.VerificationCheckId;
        }

    }
}
