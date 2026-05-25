namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading.Tasks;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Coalesces post-landing code-index refresh requests per vessel.
    /// </summary>
    public static class CodeIndexRefreshScheduler
    {
        private static readonly ConcurrentDictionary<string, RefreshState> _States =
            new ConcurrentDictionary<string, RefreshState>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Schedule a refresh after the configured debounce interval. Repeated calls for the
        /// same vessel collapse into one update; calls arriving while an update is running
        /// trigger one follow-up update after the current run finishes.
        /// </summary>
        public static void Schedule(
            ICodeIndexService? service,
            CodeIndexSettings settings,
            LoggingModule logging,
            string logHeader,
            string? vesselId,
            string reason)
        {
            if (service == null || String.IsNullOrWhiteSpace(vesselId)) return;
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (logging == null) throw new ArgumentNullException(nameof(logging));

            string capturedVesselId = vesselId.Trim();
            RefreshState state = _States.GetOrAdd(capturedVesselId, _ => new RefreshState());
            bool startWorker = false;
            lock (state.Gate)
            {
                state.Generation++;
                state.LastReason = String.IsNullOrWhiteSpace(reason) ? "successful landing" : reason;
                if (!state.WorkerStarted)
                {
                    state.WorkerStarted = true;
                    startWorker = true;
                }
            }

            if (!startWorker) return;

            _ = Task.Run(async () =>
            {
                await RunWorkerAsync(
                    capturedVesselId,
                    state,
                    service,
                    settings,
                    logging,
                    String.IsNullOrWhiteSpace(logHeader) ? "[CodeIndexRefreshScheduler] " : logHeader).ConfigureAwait(false);
            });
        }

        private static async Task RunWorkerAsync(
            string vesselId,
            RefreshState state,
            ICodeIndexService service,
            CodeIndexSettings settings,
            LoggingModule logging,
            string logHeader)
        {
            while (true)
            {
                int generation;
                string reason;
                lock (state.Gate)
                {
                    generation = state.Generation;
                    reason = state.LastReason;
                }

                int delayMs = settings.PostLandRefreshDebounceSeconds * 1000;
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }

                lock (state.Gate)
                {
                    if (generation != state.Generation)
                    {
                        continue;
                    }
                }

                try
                {
                    logging.Info(logHeader + "auto-refreshing code index for vessel " + vesselId + " after " + reason);
                    await service.UpdateAsync(vesselId).ConfigureAwait(false);
                    logging.Info(logHeader + "code index refresh complete for vessel " + vesselId);
                }
                catch (Exception ex)
                {
                    logging.Warn(logHeader + "code index refresh failed for vessel " + vesselId + ": " + ex.Message);
                }

                lock (state.Gate)
                {
                    if (generation == state.Generation)
                    {
                        _States.TryRemove(vesselId, out _);
                        return;
                    }
                }
            }
        }

        private sealed class RefreshState
        {
            public object Gate { get; } = new object();

            public int Generation { get; set; }

            public bool WorkerStarted { get; set; }

            public string LastReason { get; set; } = "successful landing";
        }
    }
}
