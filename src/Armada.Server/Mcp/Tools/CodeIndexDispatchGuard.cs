namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    internal static class CodeIndexDispatchGuard
    {
        /// <summary>
        /// Evaluate the code-index dispatch precondition for a vessel.
        /// </summary>
        /// <param name="codeIndexService">Code index service, or null when indexing is disabled.</param>
        /// <param name="vesselId">Vessel being dispatched to.</param>
        /// <param name="actionName">Action name echoed back in the blocked response.</param>
        /// <param name="settings">Code-index settings; supplies the dispatch staleness policy. Null defaults to Proceed.</param>
        /// <param name="logging">Logging module used to schedule a background refresh. Null skips the refresh kick.</param>
        /// <param name="logWarning">Optional sink for the timeout warning.</param>
        /// <param name="token">Caller cancellation token.</param>
        /// <returns>A blocked-response object, or null when dispatch may proceed.</returns>
        public static async Task<object?> BuildVoyageDispatchBlockedResponseAsync(
            ICodeIndexService? codeIndexService,
            string vesselId,
            string actionName,
            CodeIndexSettings? settings = null,
            LoggingModule? logging = null,
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

            CodeIndexDispatchStalenessPolicyEnum policy =
                settings?.DispatchStalenessPolicy ?? CodeIndexDispatchStalenessPolicyEnum.Proceed;

            // An update is already running. Only Block waits for it; Proceed and RefreshInline dispatch
            // against the current index so a landing-triggered refresh never gates the next voyage.
            if (status.UpdateInProgress)
            {
                if (policy == CodeIndexDispatchStalenessPolicyEnum.Block)
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

                logging?.Info("[CodeIndexDispatchGuard] code index for vessel " + vesselId
                    + " is refreshing; dispatch proceeds against the current index (policy " + policy + ").");
                return null;
            }

            if (!IsStale(status)) return null;

            // Is the staleness relevant? A stale index whose diff since the indexed commit touches no
            // indexable source (docs-only, excluded paths, non-source) is byte-identical in indexable
            // content to a fresh one, so dispatch proceeds with no refresh under any policy -- Block
            // included, because the operator's freshness intent is already met for what the index covers.
            // This is the authoritative deterministic rule a dispatch_staleness decision tie-breaks over.
            CodeIndexStalenessRelevance relevance;
            try
            {
                relevance = await codeIndexService.GetStalenessRelevanceAsync(vesselId, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logWarning?.Invoke("staleness relevance lookup for vessel " + vesselId + " failed (" + ex.Message + "); treating as relevant");
                relevance = new CodeIndexStalenessRelevance { VesselId = vesselId, IsRelevant = true, DiffUnavailable = true };
            }

            if (!relevance.IsRelevant && !relevance.DiffUnavailable)
            {
                logging?.Info("[CodeIndexDispatchGuard] code index for vessel " + vesselId
                    + " is stale but the change since the indexed commit touches no indexable source ("
                    + relevance.ChangedFileCount + " files changed, 0 source); dispatch proceeds without a refresh (policy " + policy + ").");
                return null;
            }

            // The index is stale on indexable source: apply the configured policy.
            switch (policy)
            {
                case CodeIndexDispatchStalenessPolicyEnum.Block:
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

                case CodeIndexDispatchStalenessPolicyEnum.RefreshInline:
                {
                    await RefreshInlineAsync(codeIndexService, settings, logging, vesselId, token).ConfigureAwait(false);
                    return null;
                }

                default:
                {
                    // Proceed: dispatch now against the current index and schedule a debounced background
                    // refresh so the next dispatch sees the newest landed code without any manual step.
                    ScheduleBackgroundRefresh(codeIndexService, settings, logging, vesselId, "stale index at dispatch");
                    logging?.Info("[CodeIndexDispatchGuard] code index for vessel " + vesselId
                        + " is stale; dispatch proceeds against the current index and a background refresh was scheduled.");
                    return null;
                }
            }
        }

        private static void ScheduleBackgroundRefresh(
            ICodeIndexService? codeIndexService,
            CodeIndexSettings? settings,
            LoggingModule? logging,
            string vesselId,
            string reason)
        {
            if (codeIndexService == null || settings == null || logging == null) return;
            CodeIndexRefreshScheduler.Schedule(codeIndexService, settings, logging, "[CodeIndexDispatchGuard] ", vesselId, reason);
        }

        private static async Task RefreshInlineAsync(
            ICodeIndexService? codeIndexService,
            CodeIndexSettings? settings,
            LoggingModule? logging,
            string vesselId,
            CancellationToken token)
        {
            if (codeIndexService == null) return;

            // The refresh is incremental (unchanged files keep their embeddings), so a refresh after a
            // small landing is fast. It is bounded so a large or wedged refresh cannot re-introduce the
            // dispatch stall; on timeout or failure the refresh continues in the background.
            TimeSpan timeout = CodeContextTimeouts.Resolve(CodeContextTimeouts.DefaultDispatchTimeoutMs);
            Task updateTask;
            try
            {
                updateTask = codeIndexService.UpdateAsync(vesselId, token);
            }
            catch (Exception ex)
            {
                logging?.Warn("[CodeIndexDispatchGuard] inline refresh could not start for vessel " + vesselId + ": " + ex.Message);
                ScheduleBackgroundRefresh(codeIndexService, settings, logging, vesselId, "inline refresh start failed");
                return;
            }

            Task completed = await Task.WhenAny(updateTask, Task.Delay(timeout, token)).ConfigureAwait(false);
            if (completed != updateTask)
            {
                logging?.Info("[CodeIndexDispatchGuard] inline refresh for vessel " + vesselId
                    + " exceeded " + timeout.TotalSeconds.ToString("F0") + "s; it continues in the background and dispatch proceeds.");
                _ = updateTask.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return;
            }

            try
            {
                await updateTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logging?.Warn("[CodeIndexDispatchGuard] inline refresh failed for vessel " + vesselId + ": " + ex.Message
                    + "; dispatch proceeds against the current index.");
            }
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
