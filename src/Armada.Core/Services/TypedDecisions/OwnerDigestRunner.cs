namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Globalization;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Collects owner-decision candidates and returns them ranked by cost of waiting. The runner reads
    /// whatever hits exist from its hit source (D5 Q13 owner rulings, D9 owner rulings, OwnerDecision
    /// preparation claims still in NeedsRecheck, and inbox items classified as questions); an absent or
    /// disabled upstream simply contributes no candidates.
    /// </summary>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The owner-decision candidates found now, in any order; empty when there are none.</returns>
    public delegate Task<IReadOnlyList<OwnerDigestCandidate>> OwnerDigestHitSource(CancellationToken token);

    /// <summary>
    /// The outcome of one <see cref="OwnerDigestRunner.RunOnceAsync"/> pass, for logging and tests.
    /// </summary>
    /// <param name="Posted">Whether a digest event was recorded on this pass.</param>
    /// <param name="CandidateCount">The number of owner-decision candidates ranked into the digest.</param>
    /// <param name="Reason">A short reason the pass did or did not post.</param>
    public sealed record OwnerDigestRunResult(bool Posted, int CandidateCount, string Reason);

    /// <summary>
    /// The D13 <c>owner_digest</c> scheduled runner. Shaped to be driven by the health loop on a daily
    /// cadence: each pass collects the owner-decision candidates its hit source found, ranks each one
    /// with the <see cref="TypedOwnerDigestAdapter"/> (deterministic cost of waiting, escalated only by
    /// a gated model reading), and, at most ONCE PER UTC DAY, emits a single
    /// <c>owner_decisions.digest</c> event carrying the ranking metadata.
    ///
    /// The runner is off until the owner enables the <c>owner_digest</c> decision: while the decision is
    /// Off it is dormant and records nothing, so it is never forced on. It NEVER answers a question,
    /// dispatches, lands, or changes any objective, incident, or claim — its only effect is one
    /// observability event per day. the runner records no ruling. It never throws into its caller.
    /// </summary>
    public sealed class OwnerDigestRunner
    {
        #region Private-Members

        private const string _Header = "[OwnerDigestRunner] ";

        /// <summary>Event type of the once-per-day digest summary.</summary>
        public const string DigestEventType = "owner_decisions.digest";

        private readonly TypedDecisionSettings _Settings;
        private readonly TypedOwnerDigestAdapter _Adapter;
        private readonly OwnerDigestHitSource _HitSource;
        private readonly DatabaseDriver _Database;
        private readonly Func<DateTime> _NowUtc;
        private readonly LoggingModule _Logging;
        private readonly object _Lock = new object();

        private DateTime? _LastPostedDateUtc;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create the owner-digest runner.
        /// </summary>
        /// <param name="settings">Typed-decision settings; the <c>owner_digest</c> mode gates the runner.</param>
        /// <param name="adapter">The D13 adapter used to rank each candidate.</param>
        /// <param name="hitSource">The source of owner-decision candidates.</param>
        /// <param name="database">Database driver whose Events collection receives the digest event.</param>
        /// <param name="nowUtc">Clock returning the current UTC time; injected for the once-per-day guard and tests.</param>
        /// <param name="logging">Logging module.</param>
        public OwnerDigestRunner(
            TypedDecisionSettings settings,
            TypedOwnerDigestAdapter adapter,
            OwnerDigestHitSource hitSource,
            DatabaseDriver database,
            Func<DateTime> nowUtc,
            LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _HitSource = hitSource ?? throw new ArgumentNullException(nameof(hitSource));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _NowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Run one pass. Dormant when the decision is Off; otherwise, at most once per UTC day, collect
        /// the owner-decision candidates, rank them, and emit one digest
        /// event. A pass with no candidates records nothing and does not consume the day. Never throws.
        /// </summary>
        /// <param name="token">Cancellation token, forwarded to the adapter so its client links its timeout to it.</param>
        /// <returns>The outcome of the pass.</returns>
        public async Task<OwnerDigestRunResult> RunOnceAsync(CancellationToken token)
        {
            ResolvedTypedDecision cfg = _Settings.For(TypedOwnerDigestAdapter.DecisionPointName);
            if (cfg.Mode == TypedDecisionModeEnum.Off)
                return new OwnerDigestRunResult(false, 0, "dormant");

            DateTime today = _NowUtc().Date;
            lock (_Lock)
            {
                if (_LastPostedDateUtc.HasValue && _LastPostedDateUtc.Value == today)
                    return new OwnerDigestRunResult(false, 0, "already_posted_today");
            }

            try
            {
                IReadOnlyList<OwnerDigestCandidate> candidates = await _HitSource(token).ConfigureAwait(false)
                    ?? new List<OwnerDigestCandidate>();
                if (candidates.Count == 0)
                    return new OwnerDigestRunResult(false, 0, "no_candidates");

                List<OwnerDigestCandidate> eligible = candidates
                    .Where(candidate => candidate != null && !String.IsNullOrWhiteSpace(candidate.QuestionText))
                    .ToList();

                // The candidates are independent, so they are ranked together in as few requests as the
                // limits allow. The adapter never throws; a slow or unavailable model leaves the
                // deterministic rule entry, so the digest always ranks by at least the fan-out and age.
                List<OwnerDigestEntry> entries = await _Adapter.DecideManyAsync(
                    eligible, eligible.Select(TypedOwnerDigestAdapter.DeterministicRule).ToList(), token).ConfigureAwait(false);

                List<RankedCandidate> ranked = new List<RankedCandidate>(eligible.Count);
                for (int index = 0; index < eligible.Count; index++)
                    ranked.Add(new RankedCandidate(eligible[index], entries[index]));

                if (ranked.Count == 0)
                    return new OwnerDigestRunResult(false, 0, "no_candidates");

                // Highest cost first; break ties by the older question. The comparison never answers a
                // question — it only orders the list the owner reads.
                ranked.Sort((left, right) =>
                {
                    int byCost = right.Entry.CostLevel.CompareTo(left.Entry.CostLevel);
                    if (byCost != 0) return byCost;
                    return right.Candidate.AgeHours.CompareTo(left.Candidate.AgeHours);
                });

                // Reserve the day BEFORE the side effects so a concurrent or immediately following pass
                // cannot record a second digest for the same day even if a write is slow.
                lock (_Lock)
                {
                    if (_LastPostedDateUtc.HasValue && _LastPostedDateUtc.Value == today)
                        return new OwnerDigestRunResult(false, ranked.Count, "already_posted_today");
                    _LastPostedDateUtc = today;
                }

                await EmitDigestEventAsync(ranked, today, token).ConfigureAwait(false);

                _Logging.Info(_Header + "recorded owner digest: " + ranked.Count + " question"
                    + (ranked.Count == 1 ? "" : "s") + ", top cost=" + ranked[0].Entry.CostLevel);
                return new OwnerDigestRunResult(true, ranked.Count, "posted");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The digest is additive observability; a failure must never break the health loop that
                // drives it. Leave the day unreserved so a later pass can retry.
                lock (_Lock)
                {
                    if (_LastPostedDateUtc.HasValue && _LastPostedDateUtc.Value == today)
                        _LastPostedDateUtc = null;
                }
                _Logging.Warn(_Header + "owner digest pass failed: " + ex.Message);
                return new OwnerDigestRunResult(false, 0, "error");
            }
        }

        #endregion

        #region Private-Methods

        private async Task EmitDigestEventAsync(IReadOnlyList<RankedCandidate> ranked, DateTime day, CancellationToken token)
        {
            try
            {
                // The event carries only the ranking metadata — counts, cost levels, sources — never the
                // question text or row titles.
                List<object> entries = new List<object>(ranked.Count);
                foreach (RankedCandidate item in ranked)
                {
                    entries.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["source"] = item.Candidate.Source,
                        ["cost_level"] = item.Entry.CostLevel,
                        ["default_safe"] = item.Entry.DefaultSafe,
                        ["chain_count"] = item.Candidate.ChainCount,
                        ["age_hours"] = Math.Round(item.Candidate.AgeHours, 1),
                        ["outcome"] = item.Entry.Outcome
                    });
                }

                Dictionary<string, object?> payload = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["count"] = ranked.Count,
                    ["top_cost_level"] = ranked[0].Entry.CostLevel,
                    ["posted_date"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["entries"] = entries
                };

                ArmadaEvent evt = new ArmadaEvent(DigestEventType,
                    "owner digest: " + ranked.Count + " question" + (ranked.Count == 1 ? "" : "s")
                    + ", top cost=" + ranked[0].Entry.CostLevel)
                {
                    Payload = JsonSerializer.Serialize(payload),
                    TenantId = Constants.DefaultTenantId
                };

                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to emit digest event: " + ex.Message);
            }
        }

        #endregion

        #region Private-Types

        private sealed class RankedCandidate
        {
            public RankedCandidate(OwnerDigestCandidate candidate, OwnerDigestEntry entry)
            {
                Candidate = candidate;
                Entry = entry;
            }

            public OwnerDigestCandidate Candidate { get; }
            public OwnerDigestEntry Entry { get; }
        }

        #endregion
    }
}
