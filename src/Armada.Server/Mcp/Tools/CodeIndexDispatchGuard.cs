namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Threading;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    internal static class CodeIndexDispatchGuard
    {
        /// <summary>
        /// Evaluate the code-index dispatch precondition for a vessel.
        /// </summary>
        /// <param name="codeIndexService">Code index service, or null when indexing is disabled.</param>
        /// <param name="vesselId">Vessel being dispatched to.</param>
        /// <param name="actionName">Action name echoed back in the blocked response.</param>
        /// <param name="logWarning">Optional sink for the timeout warning.</param>
        /// <param name="token">Caller cancellation token.</param>
        /// <returns>A blocked-response object, or null when dispatch may proceed.</returns>
        public static async Task<object?> BuildVoyageDispatchBlockedResponseAsync(
            ICodeIndexService? codeIndexService,
            string vesselId,
            string actionName,
            Action<string>? logWarning = null,
            CancellationToken token = default)
        {
            if (codeIndexService == null || String.IsNullOrWhiteSpace(vesselId))
                return null;

            // This guard runs on EVERY dispatch during precondition validation. It previously called
            // GetStatusAsync with no token and no timeout, so a stalled index backend blocked
            // armada_dispatch before a voyage row or a single log line was produced. Never block
            // dispatch on an unavailable guard: on timeout, warn and let dispatch proceed.
            CodeIndexStatus status;
            try
            {
                status = await GetStatusWithTimeoutAsync(codeIndexService, vesselId, token).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                logWarning?.Invoke(
                    "code index status lookup for vessel " + vesselId + " timed out (" + ex.Message
                    + "); proceeding with dispatch without the staleness precondition");
                return null;
            }

            if (status.UpdateInProgress)
            {
                string vesselName = String.IsNullOrWhiteSpace(status.VesselName) ? vesselId : status.VesselName;
                string started = status.UpdateStartedUtc.HasValue ? status.UpdateStartedUtc.Value.ToString("o") : "unknown time";
                string reason =
                    "Voyage dispatch is blocked because Armada is currently refreshing the code index for vessel "
                    + vesselId + " (" + vesselName + ") since " + started
                    + ". Dispatch is delayed until indexing finishes so generated context packs and search results include the most recently landed code. Retry after codeIndex.updateInProgress is false.";

                return new
                {
                    Error = reason,
                    Code = "code_index_update_in_progress",
                    Reason = reason,
                    Action = actionName,
                    VesselId = vesselId,
                    VesselName = vesselName,
                    CodeIndex = status
                };
            }

            if (IsStale(status))
            {
                string vesselName = String.IsNullOrWhiteSpace(status.VesselName) ? vesselId : status.VesselName;
                string reason =
                    "Voyage dispatch is blocked because Armada's code index is stale for vessel "
                    + vesselId + " (" + vesselName + "). Indexed commit "
                    + (String.IsNullOrWhiteSpace(status.IndexedCommitSha) ? "unknown" : status.IndexedCommitSha)
                    + " does not match current commit "
                    + (String.IsNullOrWhiteSpace(status.CurrentCommitSha) ? "unknown" : status.CurrentCommitSha)
                    + ". Run armada_index_update and retry after codeIndex.freshness is Fresh.";

                return new
                {
                    Error = reason,
                    Code = "code_index_stale",
                    Reason = reason,
                    Action = actionName,
                    VesselId = vesselId,
                    VesselName = vesselName,
                    CodeIndex = status
                };
            }

            return null;
        }

        /// <summary>
        /// Fetch index status bounded by the shared code-context timeout so a hung status lookup
        /// cannot stall dispatch precondition validation.
        /// </summary>
        private static async Task<CodeIndexStatus> GetStatusWithTimeoutAsync(
            ICodeIndexService codeIndexService,
            string vesselId,
            CancellationToken token)
        {
            TimeSpan timeout = CodeContextTimeouts.Resolve(CodeContextTimeouts.DefaultDispatchTimeoutMs);
            CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<CodeIndexStatus> statusTask;

            try
            {
                statusTask = codeIndexService.GetStatusAsync(vesselId, timeoutCts.Token);
            }
            catch
            {
                timeoutCts.Dispose();
                throw;
            }

            Task completed = await Task.WhenAny(statusTask, Task.Delay(timeout, token)).ConfigureAwait(false);
            if (completed != statusTask)
            {
                try { timeoutCts.Cancel(); }
                catch (ObjectDisposedException) { }

                // Observe the abandoned task's exception so it does not surface as unobserved.
                _ = statusTask.ContinueWith(
                    task =>
                    {
                        _ = task.Exception;
                        timeoutCts.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                throw new TimeoutException(
                    "code index status lookup exceeded " + timeout.TotalSeconds.ToString("F0") + " seconds");
            }

            try
            {
                return await statusTask.ConfigureAwait(false);
            }
            finally
            {
                timeoutCts.Dispose();
            }
        }

        private static bool IsStale(CodeIndexStatus status)
        {
            if (String.Equals(status.Freshness, "Stale", StringComparison.OrdinalIgnoreCase))
                return true;

            return !String.IsNullOrWhiteSpace(status.IndexedCommitSha)
                && !String.IsNullOrWhiteSpace(status.CurrentCommitSha)
                && !String.Equals(status.IndexedCommitSha, status.CurrentCommitSha, StringComparison.OrdinalIgnoreCase);
        }
    }
}
