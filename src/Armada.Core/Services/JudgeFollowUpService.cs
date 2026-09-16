namespace Armada.Core.Services
{
    using Armada.Core.Database;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Persists Judge-requested follow-up work and associates it with delivery records when they exist.
    /// </summary>
    public class JudgeFollowUpService
    {
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private readonly string _Header = "[JudgeFollowUpService] ";
        private static readonly SemaphoreSlim _AssociationAuditLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Optional D12 <c>followup_routing</c> adapter. When set (only when the live typed-decision
        /// client exists) each captured follow-up section is routed to a home in Gate mode: a Triaged
        /// objective, an evidence note, or a link to an existing objective; a blocking item is only
        /// flagged for the operator, never created as a voyage. With it unset or the decision Off,
        /// capture behaves exactly as before. Routing is best-effort and never changes capture.
        /// </summary>
        public FollowUpRoutingAdapter? RoutingAdapter { get; set; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module.</param>
        public JudgeFollowUpService(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <summary>
        /// Persist one canonical follow-up record before delivery association is attempted.
        /// </summary>
        /// <param name="judgeMission">Judge mission that requested the follow-up.</param>
        /// <param name="judgeVerdict">Normalized Judge verdict.</param>
        /// <param name="suggestedFollowUps">Complete Suggested Follow-ups section, when supplied.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The durable follow-up record.</returns>
        public async Task<JudgeFollowUp> CaptureAsync(
            Mission judgeMission,
            string judgeVerdict,
            string? suggestedFollowUps,
            CancellationToken token = default)
        {
            if (judgeMission == null) throw new ArgumentNullException(nameof(judgeMission));
            if (String.IsNullOrWhiteSpace(judgeVerdict)) throw new ArgumentNullException(nameof(judgeVerdict));

            JudgeFollowUp followUp = new JudgeFollowUp
            {
                TenantId = judgeMission.TenantId,
                UserId = judgeMission.UserId,
                JudgeMissionId = judgeMission.Id,
                ReviewedMissionId = judgeMission.DependsOnMissionId ?? judgeMission.Id,
                VoyageId = judgeMission.VoyageId,
                VesselId = judgeMission.VesselId,
                JudgeVerdict = judgeVerdict,
                SuggestedFollowUps = suggestedFollowUps,
                AuditVerdict = "Pending"
            };

            followUp = await _Database.JudgeFollowUps.UpsertAsync(followUp, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(followUp.MergeEntryId))
                await TryAssociateWithAvailableMergeEntryAsync(followUp, token).ConfigureAwait(false);

            // D12 followup_routing runs after the follow-up is stored: it only gives each item a home
            // (Triaged objective, evidence note, or link), never dispatches, and never throws. With no
            // adapter or the decision Off, this is a no-op and capture is unchanged.
            if (RoutingAdapter != null && !String.IsNullOrWhiteSpace(followUp.SuggestedFollowUps))
            {
                try
                {
                    await RoutingAdapter.RouteAsync(followUp, null, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "follow-up routing failed, capture unaffected: " + ex.Message);
                }
            }

            return followUp;
        }

        /// <summary>
        /// Associate unlinked follow-ups when a merge entry is created later.
        /// </summary>
        /// <param name="entry">New or existing merge entry.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Number of follow-ups associated.</returns>
        public async Task<int> AssociateForMergeEntryAsync(MergeEntry entry, CancellationToken token = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (String.IsNullOrWhiteSpace(entry.MissionId)) return 0;

            List<JudgeFollowUp> matches = await _Database.JudgeFollowUps
                .EnumerateUnassociatedByReviewedMissionAsync(entry.MissionId, token)
                .ConfigureAwait(false);
            List<JudgeFollowUp> judgeMatches = await _Database.JudgeFollowUps
                .EnumerateUnassociatedByJudgeMissionAsync(entry.MissionId, token)
                .ConfigureAwait(false);
            foreach (JudgeFollowUp judgeMatch in judgeMatches)
            {
                if (!matches.Any(match => String.Equals(match.Id, judgeMatch.Id, StringComparison.Ordinal)))
                    matches.Add(judgeMatch);
            }

            int associated = 0;
            foreach (JudgeFollowUp followUp in matches)
            {
                if (await AssociateAsync(followUp, entry, token).ConfigureAwait(false)) associated++;
            }
            return associated;
        }

        /// <summary>
        /// Retry association for pending follow-ups before the audit queue is returned.
        /// </summary>
        /// <param name="vesselId">Optional vessel filter.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Number of associations completed.</returns>
        public async Task<int> ReconcilePendingAssociationsAsync(string? vesselId, CancellationToken token = default)
        {
            List<JudgeFollowUp> unassociated = await _Database.JudgeFollowUps
                .EnumerateUnassociatedAsync(vesselId, token)
                .ConfigureAwait(false);
            int associated = 0;
            foreach (JudgeFollowUp followUp in unassociated)
            {
                if (await TryAssociateWithAvailableMergeEntryAsync(followUp, token).ConfigureAwait(false))
                    associated++;
            }
            return associated;
        }

        /// <summary>
        /// Complete the canonical audit item and its merge-entry mirror under the same process lock
        /// used by late association.
        /// </summary>
        public async Task<JudgeFollowUp> CompleteAuditAsync(
            string followUpId,
            string verdict,
            string notes,
            string? recommendedAction,
            DateTime completedUtc,
            CancellationToken token = default)
        {
            await _AssociationAuditLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                JudgeFollowUp followUp = await _Database.JudgeFollowUps.CompleteAuditAsync(
                    followUpId, verdict, notes, recommendedAction, completedUtc, token).ConfigureAwait(false);
                if (!String.IsNullOrWhiteSpace(followUp.MergeEntryId))
                {
                    MergeEntry? entry = await _Database.MergeEntries.ReadAsync(followUp.MergeEntryId, token).ConfigureAwait(false);
                    if (entry != null)
                        await MirrorAuditAsync(followUp, entry, token).ConfigureAwait(false);
                }
                return followUp;
            }
            finally
            {
                _AssociationAuditLock.Release();
            }
        }

        private async Task<bool> TryAssociateWithAvailableMergeEntryAsync(JudgeFollowUp followUp, CancellationToken token)
        {
            try
            {
                List<MergeEntry> entries = await _Database.MergeEntries.EnumerateAsync(token).ConfigureAwait(false);
                MergeEntry? match = entries
                    .Where(entry => String.Equals(entry.MissionId, followUp.ReviewedMissionId, StringComparison.Ordinal))
                    .OrderByDescending(entry => entry.CreatedUtc)
                    .ThenByDescending(entry => entry.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
                match ??= entries
                    .Where(entry => String.Equals(entry.MissionId, followUp.JudgeMissionId, StringComparison.Ordinal))
                    .OrderByDescending(entry => entry.CreatedUtc)
                    .ThenByDescending(entry => entry.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (match == null) return false;

                return await AssociateAsync(followUp, match, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not associate durable follow-up " + followUp.Id + ": " + ex.Message);
                return false;
            }
        }

        private async Task<bool> AssociateAsync(JudgeFollowUp followUp, MergeEntry entry, CancellationToken token)
        {
            await _AssociationAuditLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                bool wonAssociation = await _Database.JudgeFollowUps
                    .TryAssociateAsync(followUp.Id, entry.Id, token)
                    .ConfigureAwait(false);
                if (!wonAssociation) return false;
                followUp.MergeEntryId = entry.Id;

                JudgeFollowUp? canonical = await _Database.JudgeFollowUps.ReadAsync(followUp.Id, token).ConfigureAwait(false);
                if (canonical == null) throw new InvalidOperationException("Associated Judge follow-up disappeared: " + followUp.Id);
                await MirrorAuditAsync(canonical, entry, token).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _AssociationAuditLock.Release();
            }
        }

        private async Task MirrorAuditAsync(JudgeFollowUp followUp, MergeEntry entry, CancellationToken token)
        {
            entry.AuditDeepPicked = true;
            entry.AuditDeepVerdict = followUp.AuditVerdict;
            entry.AuditDeepNotes = followUp.AuditCompletedUtc.HasValue
                ? followUp.AuditNotes
                : BuildMergeAuditNotes(followUp);
            entry.AuditDeepRecommendedAction = followUp.AuditRecommendedAction;
            entry.AuditDeepCompletedUtc = followUp.AuditCompletedUtc;
            entry.LastUpdateUtc = DateTime.UtcNow;
            await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
        }

        private static string BuildMergeAuditNotes(JudgeFollowUp followUp)
        {
            string notes = "Judge " + followUp.JudgeVerdict + " (mission " + followUp.JudgeMissionId + ")";
            if (!String.IsNullOrWhiteSpace(followUp.SuggestedFollowUps))
                notes += "\n\n## Suggested Follow-ups\n" + followUp.SuggestedFollowUps;
            return notes;
        }
    }
}
