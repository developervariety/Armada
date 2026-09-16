namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The outcome of one <see cref="PapercutMemorySweepRunner.RunOnceAsync"/> pass, for logging and tests.
    /// </summary>
    /// <param name="Ran">Whether the pass considered any group.</param>
    /// <param name="Considered">Number of papercut groups offered to the D18 adapter.</param>
    /// <param name="Nominated">Number of proposals the adapter stored.</param>
    /// <param name="Reason">A short reason the pass did or did not run.</param>
    public sealed record PapercutMemorySweepResult(bool Ran, int Considered, int Nominated, string Reason);

    /// <summary>
    /// The weekly papercut sweep that feeds the D18 <c>memory_candidate</c> decision. At most once per
    /// seven days it groups the papercuts reported in the last seven days, applies the D6 listing merge
    /// when that adapter is present, and offers the largest repeated groups to the
    /// <see cref="MemoryCandidateAdapter"/>, which stores a proposal only in Gate mode at or above the
    /// threshold. While the decision is Off the runner is dormant: it reads nothing and calls nothing.
    /// It never writes memory, never dismisses a proposal, and never throws into its caller.
    /// </summary>
    public sealed class PapercutMemorySweepRunner
    {
        #region Public-Members

        /// <summary>The sweep interval.</summary>
        public static readonly TimeSpan Interval = TimeSpan.FromDays(7);

        /// <summary>Stored papercut events read per pass.</summary>
        public const int ScanLimit = 2000;

        /// <summary>Maximum groups offered to the model per pass, largest first.</summary>
        public const int MaxGroupsPerPass = 20;

        /// <summary>Minimum reports in a group before it is offered; a single report is a one-off.</summary>
        public const int MinimumGroupCount = 2;

        #endregion

        #region Private-Members

        private const string _Header = "[PapercutMemorySweepRunner] ";

        private readonly TypedDecisionSettings _Settings;
        private readonly MemoryCandidateAdapter _Adapter;
        private readonly PapercutMergeAdapter? _MergeAdapter;
        private readonly DatabaseDriver _Database;
        private readonly Func<DateTime> _NowUtc;
        private readonly LoggingModule _Logging;
        private readonly object _Lock = new object();

        private DateTime? _LastRunUtc;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create the weekly papercut memory sweep.
        /// </summary>
        /// <param name="settings">Typed-decision settings; the <c>memory_candidate</c> mode gates the runner.</param>
        /// <param name="adapter">The D18 adapter that nominates and stores proposals.</param>
        /// <param name="mergeAdapter">The D6 merge adapter, or null to use the plain grouping.</param>
        /// <param name="database">Database driver holding the papercut events.</param>
        /// <param name="nowUtc">Clock returning the current UTC time.</param>
        /// <param name="logging">Logging module.</param>
        public PapercutMemorySweepRunner(
            TypedDecisionSettings settings,
            MemoryCandidateAdapter adapter,
            PapercutMergeAdapter? mergeAdapter,
            DatabaseDriver database,
            Func<DateTime> nowUtc,
            LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _MergeAdapter = mergeAdapter;
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _NowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Run one pass. Dormant when the decision is Off; otherwise at most once per seven days. The
        /// seven days are reserved before the model is called and released again on a failure, so a
        /// later pass can retry. Never throws, except for cancellation of the caller's token.
        /// </summary>
        /// <param name="token">Cancellation token, forwarded to the adapter so its client links its timeout to it.</param>
        /// <returns>The outcome of the pass.</returns>
        public async Task<PapercutMemorySweepResult> RunOnceAsync(CancellationToken token)
        {
            ResolvedTypedDecision cfg = _Settings.For(MemoryCandidateAdapter.DecisionPoint);
            if (cfg.Mode == TypedDecisionModeEnum.Off)
                return new PapercutMemorySweepResult(false, 0, 0, "dormant");

            DateTime now = _NowUtc();
            lock (_Lock)
            {
                if (_LastRunUtc.HasValue && now - _LastRunUtc.Value < Interval)
                    return new PapercutMemorySweepResult(false, 0, 0, "already_ran_this_week");
                _LastRunUtc = now;
            }

            try
            {
                DateTime since = now - Interval;
                List<ArmadaEvent> events = await _Database.Events
                    .EnumerateByTypeAsync(PapercutParser.EventType, ScanLimit, token)
                    .ConfigureAwait(false);

                List<Papercut> papercuts = new List<Papercut>();
                foreach (ArmadaEvent evt in events)
                {
                    Papercut? papercut = PapercutService.TryFromEvent(evt);
                    if (papercut == null || papercut.ReportedUtc < since) continue;
                    papercuts.Add(papercut);
                }

                List<PapercutGroup> groups = PapercutService.Group(papercuts);
                if (_MergeAdapter != null)
                    groups = await _MergeAdapter.MergeAsync(groups, token).ConfigureAwait(false);

                List<PapercutGroup> offered = groups
                    .Where(group => group != null && group.Count >= MinimumGroupCount)
                    .OrderByDescending(group => group.Count)
                    .ThenByDescending(group => group.DistinctCaptainCount)
                    .ThenBy(group => group.Key, StringComparer.Ordinal)
                    .Take(MaxGroupsPerPass)
                    .ToList();

                if (offered.Count == 0)
                    return new PapercutMemorySweepResult(true, 0, 0, "no_groups");

                List<MemoryCandidateProposal> nominated = await _Adapter.NominateAsync(offered, token).ConfigureAwait(false);
                _Logging.Info(_Header + "weekly papercut memory sweep offered " + offered.Count + " group(s), nominated " + nominated.Count);
                return new PapercutMemorySweepResult(true, offered.Count, nominated.Count, "swept");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                ReleaseReservation(now);
                throw;
            }
            catch (Exception ex)
            {
                ReleaseReservation(now);
                _Logging.Warn(_Header + "weekly papercut memory sweep failed: " + ex.Message);
                return new PapercutMemorySweepResult(false, 0, 0, "error");
            }
        }

        #endregion

        #region Private-Methods

        private void ReleaseReservation(DateTime reservedAt)
        {
            lock (_Lock)
            {
                if (_LastRunUtc.HasValue && _LastRunUtc.Value == reservedAt) _LastRunUtc = null;
            }
        }

        #endregion
    }
}
