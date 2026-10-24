namespace Armada.Server.Harbor
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public sealed partial class HarborWorkspaceCoordinator
    {
        private async Task<HarborWorkspaceOperationResult> ExecuteCoreAsync(AuthContext auth, HarborWorkspaceOperationRequest request, CancellationToken token)
        {
            if (request == null) return Refuse(null, "workspace_request_missing");
            if (!Enum.IsDefined(typeof(HarborWorkspaceOperationEnum), request.Operation)) return Refuse(request, "workspace_operation_invalid");
            if (request.Version != HarborWorkspaceProtocol.Version) return Refuse(request, "workspace_protocol_unsupported");
            if (!RequestShapeIsValid(request)) return Refuse(request, "workspace_request_invalid");
            HarborRunnerSession? session = null;
            if (!String.IsNullOrWhiteSpace(request.RunnerId)) _Registry.TryGetCurrent(request.RunnerId, out session);
            if (session == null || !session.HasWorkspaceCapability) return Refuse(request, "workspace_runner_unavailable");
            HarborRunnerCheck current = await _Registry.RevalidateAsync(session, token).ConfigureAwait(false);
            if (!current.Accepted) return Refuse(request, current.FailureReason);
            if (!await HarborRunnerAuthorization.IsOwnerOrHasAuthorityAsync(
                _Authority, auth, session.Identity.TenantId, session.Identity.UserId, token).ConfigureAwait(false))
                return Refuse(request, "harbor_command_unauthorized");

            HarborWorkspaceOperationRequest outbound = CopyForSession(request, session);
            string? denial = await _Verifier.ValidateAsync(auth, session, outbound, token).ConfigureAwait(false);
            if (denial != null) return Refuse(request, denial);
            if (!RequestWithinLimit(outbound)) return Refuse(request, "workspace_request_too_large");
            if (!TryGetLink(session, out Func<HarborMessage, CancellationToken, Task>? send) || send == null)
                return Refuse(request, "workspace_runner_disconnected");
            TimeSpan timeout = request.Operation == HarborWorkspaceOperationEnum.RunVerification
                ? TimeSpan.FromSeconds(request.Verification!.TimeoutSeconds + VerificationResponseGraceSeconds)
                : TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, MaximumRequestTimeoutSeconds));
            if (timeout > MaximumOperationTimeout) timeout = MaximumOperationTimeout;
            HarborPendingRegistration<HarborWorkspaceOperationResult> registration = await _Registry.RegisterPendingAsync<HarborWorkspaceOperationResult>(session, token).ConfigureAwait(false);
            if (!registration.Accepted || registration.Pending == null) return Refuse(request, registration.FailureReason);

            HarborPendingRequest<HarborWorkspaceOperationResult> pending = registration.Pending;
            outbound.RequestId = pending.RequestId;
            outbound.CorrelationId = pending.RequestId;
            try
            {
                await send(outbound, token).ConfigureAwait(false);
                using (CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    wait.CancelAfter(timeout);
                    try
                    {
                        HarborWorkspaceOperationResult result = await pending.Completion.WaitAsync(wait.Token).ConfigureAwait(false);
                        if (!MatchesOperation(result, outbound)) return Refuse(request, "workspace_response_mismatch");
                        return result;
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        _Registry.CancelPending(session, pending.RequestId);
                        return Refuse(request, wait.IsCancellationRequested ? "workspace_operation_timeout" : "workspace_runner_disconnected");
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _Registry.CancelPending(session, pending.RequestId);
                throw;
            }
            catch (Exception)
            {
                _Registry.CancelPending(session, pending.RequestId);
                _Logging.Warn(_Header + "workspace send failed: workspace_send_failed");
                return Refuse(request, "workspace_send_failed");
            }
        }

    }
}
