namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Memory;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Background sweep service that evaluates each vessel's learned-facts reflection threshold
    /// and auto-dispatches a consolidate reflection when one is due.
    ///
    /// Before this existed, <see cref="ReflectionDispatcher.TryAutoDispatchAfterAuditDrainAsync"/>
    /// was reachable only from the manual MCP audit-drain tool, so reflections fired only when an
    /// operator remembered to drain the audit queue. In practice that meant three reflection
    /// missions across a thousand missions, while every vessel advertised a reflection threshold --
    /// the learned-facts playbooks consumed by every mission were being produced almost never.
    /// The dispatcher still owns all eligibility rules (threshold, in-flight, evidence); this
    /// sweeper only gives them a heartbeat.
    /// </summary>
    public sealed class ReflectionSweeper
    {
        #region Public-Members

        /// <summary>
        /// Whether the sweeper is allowed to auto-dispatch reflections.
        /// </summary>
        public bool Enabled { get; private set; } = true;

        /// <summary>
        /// Minutes between sweep ticks. Reflections are batched by the per-vessel threshold, so
        /// this only controls how promptly a vessel that crossed its threshold is noticed.
        /// </summary>
        public int IntervalMinutes { get; private set; } = 30;

        /// <summary>
        /// UTC timestamp of the last completed sweep tick, or null if no tick has run.
        /// </summary>
        public DateTime? LastTickUtc { get; private set; }

        /// <summary>
        /// Short plain-text summary of the last sweep result.
        /// </summary>
        public string? LastResultSummary { get; private set; }

        /// <summary>
        /// Number of reflection missions dispatched on the last sweep tick.
        /// </summary>
        public int LastDispatchedCount { get; private set; }

        /// <summary>
        /// Maximum reflections dispatched in a single sweep. Guards the thundering herd on a fleet
        /// where many vessels are overdue at once (every vessel crossing its threshold in the same
        /// tick would otherwise fan out one reflection voyage each). Remaining vessels are picked up
        /// on later ticks. Clamped to 1-20.
        /// </summary>
        public int MaxDispatchesPerSweep { get; private set; } = 2;

        #endregion

        #region Private-Members

        private const string _Header = "[ReflectionSweeper] ";
        private readonly DatabaseDriver _Database;
        private readonly ReflectionDispatcher _Reflections;
        private readonly LoggingModule _Logging;
        private readonly SemaphoreSlim _SweepLock = new SemaphoreSlim(1, 1);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the sweeper with required dependencies.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="reflections">Reflection dispatcher that owns eligibility and dispatch.</param>
        /// <param name="settings">Armada settings.</param>
        /// <param name="logging">Logging module.</param>
        public ReflectionSweeper(
            DatabaseDriver database,
            ReflectionDispatcher reflections,
            ArmadaSettings settings,
            LoggingModule logging)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Reflections = reflections ?? throw new ArgumentNullException(nameof(reflections));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Allow the sweeper to dispatch reflections on subsequent sweeps.
        /// </summary>
        public void Enable() => Enabled = true;

        /// <summary>
        /// Prevent the sweeper from dispatching reflections.
        /// </summary>
        public void Disable() => Enabled = false;

        /// <summary>
        /// Set the sweep interval, clamped to 1-1440 minutes.
        /// </summary>
        /// <param name="minutes">New interval in minutes.</param>
        public void SetIntervalMinutes(int minutes)
        {
            IntervalMinutes = Math.Max(1, Math.Min(1440, minutes));
        }

        /// <summary>
        /// Set the per-sweep dispatch cap, clamped to 1-20.
        /// </summary>
        /// <param name="max">New per-sweep cap.</param>
        public void SetMaxDispatchesPerSweep(int max)
        {
            MaxDispatchesPerSweep = Math.Max(1, Math.Min(20, max));
        }

        /// <summary>
        /// Returns true when a sweep tick is due. The health loop calls the sweeper on every
        /// heartbeat, so this gate is what keeps reflection evaluation on its own cadence.
        /// A vessel that has never ticked is always due.
        /// </summary>
        /// <param name="lastTickUtc">UTC timestamp of the previous tick, or null if none.</param>
        /// <param name="nowUtc">Current UTC time.</param>
        /// <param name="intervalMinutes">Configured interval in minutes.</param>
        internal static bool ShouldRunTick(DateTime? lastTickUtc, DateTime nowUtc, int intervalMinutes)
        {
            if (!lastTickUtc.HasValue) return true;
            return (nowUtc - lastTickUtc.Value).TotalMinutes >= intervalMinutes;
        }

        /// <summary>
        /// Fire-and-forget background sweep. The caller is never blocked.
        /// OperationCanceledException is swallowed silently; all other errors are logged as warnings.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        public void TriggerBackgroundSweep(CancellationToken token = default)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await SweepAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "background sweep failed: " + ex.Message);
                }
            }, CancellationToken.None);
        }

        /// <summary>
        /// Run one bounded sweep: ask the reflection dispatcher whether each vessel is due.
        /// Non-reentrant; concurrent calls return immediately. Skips the work portion if the last
        /// tick ran within IntervalMinutes, so the health loop can call this on every heartbeat.
        /// A vessel that throws is logged and skipped so one bad vessel cannot abort the sweep.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        public async Task SweepAsync(CancellationToken token = default)
        {
            if (!await _SweepLock.WaitAsync(0, token).ConfigureAwait(false)) return;

            try
            {
                if (!ShouldRunTick(LastTickUtc, DateTime.UtcNow, IntervalMinutes))
                {
                    _Logging.Debug(_Header + "sweep skipped: interval not elapsed (" + IntervalMinutes + " min).");
                    return;
                }

                LastTickUtc = DateTime.UtcNow;

                if (!Enabled)
                {
                    _Logging.Debug(_Header + "sweep skipped: sweeper is disabled.");
                    LastResultSummary = "skipped (disabled)";
                    return;
                }

                List<Vessel> vessels = await _Database.Vessels.EnumerateAsync(token).ConfigureAwait(false);
                int dispatched = 0;
                int evaluated = 0;

                foreach (Vessel vessel in vessels)
                {
                    if (token.IsCancellationRequested) break;
                    if (dispatched >= MaxDispatchesPerSweep) break;
                    if (vessel == null || String.IsNullOrEmpty(vessel.Id)) continue;

                    evaluated++;

                    try
                    {
                        ReflectionDispatcher.DispatchResult? result = await _Reflections
                            .TryAutoDispatchAfterAuditDrainAsync(vessel, token)
                            .ConfigureAwait(false);

                        if (result != null)
                        {
                            dispatched++;
                            _Logging.Info(_Header + "dispatched reflection " + result.MissionId +
                                " for vessel " + vessel.Id);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "reflection evaluation failed for vessel " +
                            vessel.Id + ": " + ex.Message);
                    }
                }

                LastDispatchedCount = dispatched;
                LastResultSummary = "evaluated " + evaluated + " vessel(s), dispatched " + dispatched + " reflection(s)";
                _Logging.Debug(_Header + LastResultSummary);
            }
            finally
            {
                _SweepLock.Release();
            }
        }

        #endregion
    }
}
