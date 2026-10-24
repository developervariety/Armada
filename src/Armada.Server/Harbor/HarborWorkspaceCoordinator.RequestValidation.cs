namespace Armada.Server.Harbor
{
    using System;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public sealed partial class HarborWorkspaceCoordinator
    {
        private static HarborWorkspaceOperationRequest CopyForSession(HarborWorkspaceOperationRequest request, HarborRunnerSession session)
        {
            return new HarborWorkspaceOperationRequest
            {
                Version = request.Version, Operation = request.Operation, Binding = CopyBinding(request.Binding),
                OperationKey = request.OperationKey,
                DockId = request.DockId, VesselId = request.VesselId, RepositoryGrantId = request.RepositoryGrantId,
                TenantId = session.Identity.TenantId, UserId = session.Identity.UserId, RunnerId = session.Identity.RunnerId,
                EnrollmentGeneration = session.EnrollmentGeneration, ConnectionGeneration = session.Generation,
                ExpectedHead = request.ExpectedHead, RelativePath = request.RelativePath, Text = request.Text,
                ExpectedRepositoryFingerprint = request.ExpectedRepositoryFingerprint,
                RequestedRevision = request.RequestedRevision,
                BytesBase64 = request.BytesBase64, MaxBytes = request.MaxBytes,
                MaxEntries = request.MaxEntries, TimeoutSeconds = request.TimeoutSeconds,
                Recursive = request.Recursive, Overwrite = request.Overwrite, ReadOnly = request.ReadOnly, Detached = request.Detached,
                Git = CopyGitRequest(request.Git), Verification = CopyVerification(request.Verification),
                VerificationOutput = CopyVerificationOutput(request.VerificationOutput)
            };
        }
        private static HarborCheckoutBinding? CopyBinding(HarborCheckoutBinding? binding)
        {
            if (binding == null) return null;
            return new HarborCheckoutBinding
            {
                Version = binding.Version, RunnerId = binding.RunnerId, EnrollmentGeneration = binding.EnrollmentGeneration,
                TenantId = binding.TenantId, UserId = binding.UserId, RepositoryGrantId = binding.RepositoryGrantId,
                RepositoryFingerprint = binding.RepositoryFingerprint, CheckoutId = binding.CheckoutId,
                BaseCommit = binding.BaseCommit, BranchName = binding.BranchName, Revision = binding.Revision
            };
        }
        private static bool MatchesOperation(HarborWorkspaceOperationResult result, HarborWorkspaceOperationRequest request)
        {
            return result != null && result.Operation == request.Operation
                && String.Equals(result.RequestId, request.RequestId, StringComparison.Ordinal)
                && (request.Operation != HarborWorkspaceOperationEnum.ReadVerificationOutput
                    || result.VerificationOutputId == request.VerificationOutput?.OutputId)
                && (request.Operation != HarborWorkspaceOperationEnum.RunVerification || !result.Accepted
                    || (result.VerificationOutputId != null && result.VerificationCheckId == request.Verification?.CheckId))
                && (request.Operation != HarborWorkspaceOperationEnum.ReadVerificationOutput
                    || result.VerificationCheckId == request.VerificationOutput?.CheckId)
                && (!result.Accepted || request.ExpectedRepositoryFingerprint == null
                    || String.Equals(result.RepositoryFingerprint, request.ExpectedRepositoryFingerprint, StringComparison.Ordinal))
                && SameBinding(result.Binding, request.Binding);
        }

        private static bool RequestWithinLimit(HarborWorkspaceOperationRequest request)
        {
            if (request.Text != null && (request.Text.Length > 2 * 1024 * 1024
                || System.Text.Encoding.UTF8.GetByteCount(request.Text) > 2 * 1024 * 1024)) return false;
            if (request.BytesBase64 != null && request.BytesBase64.Length > 3 * 1024 * 1024) return false;
            if (request.RelativePath != null && request.RelativePath.Length > 4096) return false;
            if (request.OperationKey != null && (request.OperationKey.Length > 200
                || request.OperationKey.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)) return false;
            try
            {
                return System.Text.Encoding.UTF8.GetByteCount(HarborProtocol.Serialize(request)) <= MaximumResultBytes;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException || exception is System.Text.Json.JsonException)
            {
                return false;
            }
        }

        private static bool SameBinding(HarborCheckoutBinding? left, HarborCheckoutBinding? right)
        {
            if (left == null || right == null) return left == null && right == null;
            return left.Version == right.Version && left.EnrollmentGeneration == right.EnrollmentGeneration
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

        private static HarborWorkspaceOperationResult Refuse(HarborWorkspaceOperationRequest? request, string reason)
        {
            return new HarborWorkspaceOperationResult
            {
                RequestId = request?.RequestId ?? String.Empty,
                Operation = request?.Operation ?? HarborWorkspaceOperationEnum.InspectRepository,
                Binding = request?.Binding,
                Accepted = false,
                Reason = reason
            };
        }
    }
}
