namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    /// <summary>Runs bounded workspace requests without blocking runner receive and stop messages.</summary>
    public sealed partial class HarborRunnerClient
    {
        private const int MaximumConcurrentWorkspaceOperations = 4;

        private async Task StartWorkspaceOperationAsync(
            HarborWorkspaceOperationRequest request,
            HarborHandshakeAck ack,
            Func<HarborMessage, Task> send,
            List<Task> background,
            CancellationToken token)
        {
            if (_Workspace == null)
            {
                await send(RefuseWorkspace(request, "workspace_capability_unavailable")).ConfigureAwait(false);
                return;
            }
            int active = Interlocked.Increment(ref _ActiveWorkspaceOperations);
            if (active > MaximumConcurrentWorkspaceOperations)
            {
                Interlocked.Decrement(ref _ActiveWorkspaceOperations);
                await send(RefuseWorkspace(request, "workspace_capacity_exhausted")).ConfigureAwait(false);
                return;
            }
            background.Add(ExecuteWorkspaceOperationAsync(request, ack, send, token));
        }

        private async Task ExecuteWorkspaceOperationAsync(
            HarborWorkspaceOperationRequest request,
            HarborHandshakeAck ack,
            Func<HarborMessage, Task> send,
            CancellationToken token)
        {
            try
            {
                HarborWorkspaceOperationResult result = await _Workspace!.ExecuteAsync(request, ack, token).ConfigureAwait(false);
                await send(result).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _Log("Harbor workspace request canceled: " + SafeRequestId(request.RequestId));
            }
            catch (Exception exception) when (exception is IOException || exception is InvalidOperationException
                || exception is UnauthorizedAccessException || exception is ArgumentException || exception is System.Security.SecurityException)
            {
                _Log("Harbor workspace request failed: " + SafeRequestId(request.RequestId) + " workspace_operation_failed");
                await send(RefuseWorkspace(request, "workspace_operation_failed")).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _ActiveWorkspaceOperations);
            }
        }

        private static HarborWorkspaceOperationResult RefuseWorkspace(HarborWorkspaceOperationRequest request, string reason)
        {
            return new HarborWorkspaceOperationResult
            {
                CorrelationId = request.CorrelationId,
                RequestId = request.RequestId,
                Operation = request.Operation,
                Binding = request.Binding,
                Accepted = false,
                Reason = reason
            };
        }

        private static string SafeRequestId(string requestId)
        {
            if (String.IsNullOrWhiteSpace(requestId) || requestId.Length > 200) return "unknown";
            foreach (char character in requestId)
                if (Char.IsControl(character)) return "unknown";
            return requestId;
        }
    }
}
