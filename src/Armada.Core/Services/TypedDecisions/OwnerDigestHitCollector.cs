namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Collects owner-decision candidates for the D13 <c>owner_digest</c> runner from the sources that
    /// carry a question only the owner can answer. On this tip the concrete source is the objective
    /// preparation ledger: an <see cref="ObjectivePreparationClaimKindEnum.OwnerDecision"/> claim still
    /// in <see cref="ObjectivePreparationClaimStateEnum.NeedsRecheck"/> is a recorded owner decision an
    /// anchor change has re-opened, so its text is the question and the objective is the row it blocks.
    ///
    /// The other named sources (a D5 Q13 <c>needs_owner_ruling</c> model flag, a D9 <c>owner_ruling</c>
    /// hit, a board note D11 classified as a question) attach here as further readers when those signals
    /// land with a retrievable question text; each reader is best-effort and a source that finds nothing
    /// simply contributes no candidate, which is why the runner is a no-op when there are no hits. The
    /// collector reads only; it changes no record and never throws.
    /// </summary>
    public sealed class OwnerDigestHitCollector
    {
        #region Private-Members

        private const string _Header = "[OwnerDigestHitCollector] ";
        private const int _MaxCandidates = 30;

        private readonly DatabaseDriver _Database;
        private readonly Func<DateTime> _NowUtc;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create the collector.
        /// </summary>
        /// <param name="database">Database driver read for objectives and their preparation claims.</param>
        /// <param name="nowUtc">Clock returning the current UTC time, for candidate age.</param>
        /// <param name="logging">Logging module.</param>
        public OwnerDigestHitCollector(DatabaseDriver database, Func<DateTime> nowUtc, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _NowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Collect the owner-decision candidates present now. Best-effort: any read failure yields an
        /// empty list rather than an exception, so the runner it feeds is never broken by a slow or
        /// unavailable store.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The candidates found, bounded and in no particular order; the runner ranks them.</returns>
        public async Task<IReadOnlyList<OwnerDigestCandidate>> CollectAsync(CancellationToken token)
        {
            List<OwnerDigestCandidate> candidates = new List<OwnerDigestCandidate>();
            try
            {
                List<Objective> objectives = await _Database.Objectives.EnumerateAsync(token).ConfigureAwait(false);
                if (objectives == null || objectives.Count == 0) return candidates;

                // A row's fan-out: how many other objectives are blocked by it.
                Dictionary<string, int> chainBehind = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (Objective objective in objectives)
                {
                    if (objective?.BlockedByObjectiveIds == null) continue;
                    foreach (string blocker in objective.BlockedByObjectiveIds)
                    {
                        if (String.IsNullOrWhiteSpace(blocker)) continue;
                        chainBehind.TryGetValue(blocker, out int count);
                        chainBehind[blocker] = count + 1;
                    }
                }

                DateTime now = _NowUtc();
                foreach (Objective objective in objectives)
                {
                    if (objective?.Preparation?.Claims == null) continue;
                    foreach (ObjectivePreparationClaim claim in objective.Preparation.Claims)
                    {
                        if (claim == null) continue;
                        if (claim.Kind != ObjectivePreparationClaimKindEnum.OwnerDecision) continue;
                        if (claim.State != ObjectivePreparationClaimStateEnum.NeedsRecheck) continue;
                        if (String.IsNullOrWhiteSpace(claim.Text)) continue;

                        DateTime since = claim.InvalidatedUtc ?? objective.LastUpdateUtc;
                        double ageHours = Math.Max(0.0, (now - since).TotalHours);
                        chainBehind.TryGetValue(objective.Id, out int chain);

                        candidates.Add(new OwnerDigestCandidate
                        {
                            QuestionText = claim.Text,
                            BlockedRow = ShortRow(objective),
                            ChainCount = chain,
                            AgeHours = ageHours,
                            ProposedDefault = String.IsNullOrWhiteSpace(claim.InvalidationReason) ? String.Empty : claim.InvalidationReason,
                            Source = "owner_decision_recheck",
                            ObjectiveId = objective.Id,
                            VesselId = objective.VesselIds != null && objective.VesselIds.Count > 0 ? objective.VesselIds[0] : null,
                            VesselIds = objective.VesselIds != null ? new List<string>(objective.VesselIds) : new List<string>()
                        });

                        if (candidates.Count >= _MaxCandidates) return candidates;
                    }
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "collect failed, no candidates this pass: " + ex.Message);
                return new List<OwnerDigestCandidate>();
            }

            return candidates;
        }

        #endregion

        #region Private-Methods

        private static string ShortRow(Objective objective)
        {
            string title = String.IsNullOrWhiteSpace(objective.Title) ? "untitled" : objective.Title.Trim();
            return objective.Id + " (" + title + ")";
        }

        #endregion
    }
}
