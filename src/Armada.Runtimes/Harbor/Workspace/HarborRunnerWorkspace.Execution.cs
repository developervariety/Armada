namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    /// <summary>Runs and reports one owner-checked workspace operation.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        /// <summary>Execute one typed operation against the exact accepted connection identity.</summary>
        /// <param name="request">Server operation.</param>
        /// <param name="ack">Accepted handshake identity and generation.</param>
        /// <param name="token">Connection cancellation token.</param>
        /// <returns>Bounded result for the same request.</returns>
        public async Task<HarborWorkspaceOperationResult> ExecuteAsync(
            HarborWorkspaceOperationRequest request,
            HarborHandshakeAck ack,
            CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (ack == null) throw new ArgumentNullException(nameof(ack));
            HarborWorkspaceOperationResult result = new HarborWorkspaceOperationResult
            {
                CorrelationId = request.CorrelationId,
                RequestId = request.RequestId,
                Operation = request.Operation,
                Binding = request.Binding
            };
            try { return await ExecuteRequestAsync(request, ack, result, token).ConfigureAwait(false); }
            catch (HarborJobJournalException exception)
            {
                result.Accepted = false;
                result.Reason = exception.ReasonCode;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) when (IsWorkspaceFailure(exception))
            {
                result.Accepted = false;
                result.Reason = "workspace_operation_failed";
            }
            return result;
        }

        private async Task<HarborWorkspaceOperationResult> ExecuteRequestAsync(
            HarborWorkspaceOperationRequest request,
            HarborHandshakeAck ack,
            HarborWorkspaceOperationResult result,
            CancellationToken token)
        {
            ValidateRequest(request, ack);
            HarborRepositoryGrant grant = FindGrant(request.RepositoryGrantId);
            ValidateGrantOwner(grant, request);
            await VerifyRepositoryAsync(grant, token).ConfigureAwait(false);
            result.RepositoryFingerprint = RepositoryFingerprint(grant.RepositoryUrl);
            bool mutation = HarborWorkspaceOperationRules.IsMutation(request);
            return await ExecuteWithLeasesAsync(request, ack, grant, result, mutation, token).ConfigureAwait(false);
        }

        private static bool IsWorkspaceFailure(Exception exception)
        {
            return exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException
                || exception is InvalidOperationException || exception is System.Security.SecurityException
                || exception is System.ComponentModel.Win32Exception;
        }
    }
}
