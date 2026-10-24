namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Linq;
    using System.Text;
    using Armada.Core.Harbor;

    /// <summary>Checks workspace operation owner, session, and payload bounds.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private void ValidateRequest(HarborWorkspaceOperationRequest request, HarborHandshakeAck ack)
        {
            if (!ack.Accepted || String.IsNullOrWhiteSpace(ack.TenantId) || String.IsNullOrWhiteSpace(ack.UserId)
                || ack.EnrollmentGeneration <= 0 || ack.ConnectionGeneration <= 0)
                throw Failure("workspace_session_identity_missing", "The accepted connection has no verified owner identity.");
            if (request.Version != HarborWorkspaceProtocol.Version)
                throw Failure("workspace_protocol_unsupported", "The workspace protocol version is not supported.");
            if (String.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 200 || request.RequestId.Any(Char.IsControl))
                throw Failure("workspace_request_invalid", "The workspace request identity is invalid.");
            if (!String.Equals(request.RunnerId, _Settings.RunnerId, StringComparison.Ordinal))
                throw Failure("workspace_runner_mismatch", "The workspace request names a different runner.");
            if (!String.Equals(request.TenantId, ack.TenantId, StringComparison.Ordinal)
                || !String.Equals(request.UserId, ack.UserId, StringComparison.Ordinal)
                || request.EnrollmentGeneration != ack.EnrollmentGeneration
                || request.ConnectionGeneration != ack.ConnectionGeneration)
                throw Failure("workspace_session_mismatch", "The workspace request is not bound to this authenticated connection.");
            if (String.IsNullOrWhiteSpace(request.RepositoryGrantId) || request.RepositoryGrantId.Length > 160
                || String.IsNullOrWhiteSpace(request.VesselId) || request.VesselId.Length > 160)
                throw Failure("workspace_request_invalid", "The workspace repository identity is incomplete.");
            if (request.MaxBytes < 1 || request.MaxBytes > MaximumResponseBytes || request.MaxEntries < 1 || request.MaxEntries > MaximumEntries)
                throw Failure("workspace_limit_invalid", "A workspace output limit is outside the allowed range.");
            if (request.Text != null && Encoding.UTF8.GetByteCount(request.Text) > MaximumRequestTextBytes)
                throw Failure("workspace_request_too_large", "The workspace text input exceeds its size limit.");
            if (!String.IsNullOrEmpty(request.BytesBase64) && request.BytesBase64.Length > ((MaximumRequestTextBytes + 2) / 3) * 4)
                throw Failure("workspace_request_too_large", "The workspace byte input exceeds its size limit.");
            if ((request.Operation == HarborWorkspaceOperationEnum.ReadVerificationOutput) != (request.VerificationOutput != null))
                throw Failure("workspace_request_invalid", "Verification output fields do not match the requested operation.");
        }

        private HarborRepositoryGrant FindGrant(string grantId)
        {
            foreach (HarborRepositoryGrant grant in _Settings.RepositoryGrants)
                if (String.Equals(grant.Id, grantId, StringComparison.Ordinal)) return grant;
            throw Failure("workspace_grant_not_found", "No local repository grant matches this request.");
        }

        private static void ValidateGrantOwner(HarborRepositoryGrant grant, HarborWorkspaceOperationRequest request)
        {
            if (!String.Equals(grant.VesselId, request.VesselId, StringComparison.Ordinal)
                || !String.Equals(grant.TenantId, request.TenantId, StringComparison.Ordinal)
                || !String.Equals(grant.UserId, request.UserId, StringComparison.Ordinal))
                throw Failure("workspace_grant_owner_mismatch", "The request owner does not match the local repository grant.");
        }

        private void EnsureNoActiveCheckoutJob(string checkoutId)
        {
            foreach (HarborLocalJobRecord record in _Jobs.Enumerate())
                if (String.Equals(record.CheckoutId, checkoutId, StringComparison.Ordinal) && !record.StopConfirmed)
                    throw Failure("checkout_job_unconfirmed", "A retained checkout job has no confirmed stop.");
        }
    }
}
