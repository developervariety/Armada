namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    /// <summary>Serializes workspace operations against checkout, repository, and process writers.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private async Task<HarborWorkspaceOperationResult> ExecuteWithLeasesAsync(
            HarborWorkspaceOperationRequest request,
            HarborHandshakeAck ack,
            HarborRepositoryGrant grant,
            HarborWorkspaceOperationResult result,
            bool mutation,
            CancellationToken token)
        {
            FileStream? repositoryLease = null;
            FileStream? targetLease = null;
            try
            {
                if (NeedsRepositoryLease(request, mutation))
                    repositoryLease = HarborCheckoutOperationLease.Acquire(_Settings.Supervisor.StateDirectory,
                        "repository:" + RepositoryFingerprint(grant.RepositoryUrl));
                if (NeedsPrimaryTargetLease(request, mutation, grant))
                    targetLease = HarborCheckoutOperationLease.Acquire(_Settings.Supervisor.StateDirectory,
                        "primary:" + Path.GetFullPath(grant.WorkingDirectory!));
                return await ExecuteUnderCheckoutGateAsync(request, ack, grant, result, mutation, token).ConfigureAwait(false);
            }
            finally
            {
                targetLease?.Dispose();
                repositoryLease?.Dispose();
            }
        }

        private static bool NeedsRepositoryLease(HarborWorkspaceOperationRequest request, bool mutation)
        {
            if (!mutation) return false;
            return request.Operation == HarborWorkspaceOperationEnum.Git
                || request.Operation == HarborWorkspaceOperationEnum.ProvisionCheckout
                || request.Operation == HarborWorkspaceOperationEnum.RemoveCheckout;
        }

        private static bool NeedsPrimaryTargetLease(HarborWorkspaceOperationRequest request, bool mutation, HarborRepositoryGrant grant)
        {
            return mutation && request.Operation == HarborWorkspaceOperationEnum.Git
                && request.Git?.UseWorkingDirectory == true && !String.IsNullOrWhiteSpace(grant.WorkingDirectory);
        }

        private async Task<HarborWorkspaceOperationResult> ExecuteUnderCheckoutGateAsync(
            HarborWorkspaceOperationRequest request,
            HarborHandshakeAck ack,
            HarborRepositoryGrant grant,
            HarborWorkspaceOperationResult result,
            bool mutation,
            CancellationToken token)
        {
            if (request.Binding == null)
            {
                if (mutation) throw Failure("workspace_binding_required", "A checkout binding is required for mutations.");
                await ExecuteOperationAsync(request, ack, grant, result, token).ConfigureAwait(false);
                return result;
            }
            if (mutation)
                _VerificationOutputs.EnsureNoUncertainWriter(request.Binding.CheckoutId, request.OperationKey);
            SemaphoreSlim gate = _CheckoutLocks.GetOrAdd(request.Binding.CheckoutId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await ExecuteWithCheckoutLeaseAsync(request, ack, grant, result, mutation, token).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        private async Task<HarborWorkspaceOperationResult> ExecuteWithCheckoutLeaseAsync(
            HarborWorkspaceOperationRequest request,
            HarborHandshakeAck ack,
            HarborRepositoryGrant grant,
            HarborWorkspaceOperationResult result,
            bool mutation,
            CancellationToken token)
        {
            if (mutation)
            {
                using (HarborCheckoutOperationLease.Acquire(_Settings.Supervisor.StateDirectory, request.Binding!.CheckoutId))
                {
                    EnsureNoActiveCheckoutJob(request.Binding.CheckoutId);
                    _VerificationOutputs.EnsureNoUncertainWriter(request.Binding.CheckoutId, request.OperationKey);
                    using (FileStream operationLease = _Operations.Begin(request, out HarborWorkspaceOperationResult? replay))
                    {
                        if (replay != null) return replay;
                        await ExecuteOperationAsync(request, ack, grant, result, token).ConfigureAwait(false);
                        if (result.Accepted) _Operations.Complete(request, result);
                        return result;
                    }
                }
            }
            await ExecuteOperationAsync(request, ack, grant, result, token).ConfigureAwait(false);
            return result;
        }
    }
}
