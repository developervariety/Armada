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
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// Moves WorkProduced missions under ended voyages to a terminal status using <see cref="TerminalVoyageMissionRule"/>.
    /// </summary>
    /// <remarks>
    /// Every path that ends a voyage (the completion check, the landing drain, a halt after a failed stage,
    /// and every cancel surface) treats WorkProduced as finished and leaves it in place, so this pass is the
    /// one place those missions leave WorkProduced. The health loop runs it for recently ended voyages; the
    /// operator runs it with <see cref="TerminalVoyageMissionReconciliationRequest.IncludeHistorical"/> to
    /// repair older rows, first as a dry run. The pass never deletes branches, refs or commits, never runs
    /// mission outcome handlers (no rescue and no wake: the voyage already ended), and writes one event
    /// per changed mission.
    /// </remarks>
    public sealed class TerminalVoyageMissionReconciler
    {
        #region Public-Members

        /// <summary>
        /// Event type recorded for each mission the pass changes.
        /// </summary>
        public const string EventType = "mission.terminal_voyage_reconciled";

        #endregion

        #region Private-Members

        private const int _SummaryPageSize = 500;
        private readonly string _Header = "[TerminalVoyageMissionReconciler] ";
        private readonly LoggingModule _Logging;
        private readonly DatabaseDriver _Database;
        private readonly IGitService _Git;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="git">Git service used for the ancestry probe.</param>
        public TerminalVoyageMissionReconciler(LoggingModule logging, DatabaseDriver database, IGitService git)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Run one reconciliation pass.
        /// </summary>
        /// <param name="request">Pass parameters.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Counts and per-mission detail.</returns>
        public async Task<TerminalVoyageMissionReconciliationResult> ReconcileAsync(
            TerminalVoyageMissionReconciliationRequest request,
            CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            DateTime nowUtc = request.NowUtc ?? DateTime.UtcNow;
            TerminalVoyageMissionReconciliationResult result = new TerminalVoyageMissionReconciliationResult
            {
                DryRun = request.DryRun,
                IncludeHistorical = request.IncludeHistorical,
                EvaluatedUtc = nowUtc
            };

            // Collect every candidate before writing, so status changes cannot shift the pages being read.
            List<MissionSummary> candidates = await ReadWorkProducedSummariesAsync(request, token).ConfigureAwait(false);

            Dictionary<string, Voyage?> voyages = new Dictionary<string, Voyage?>(StringComparer.Ordinal);
            Dictionary<string, Vessel?> vessels = new Dictionary<string, Vessel?>(StringComparer.Ordinal);
            Dictionary<string, string?> targetCommits = new Dictionary<string, string?>(StringComparer.Ordinal);
            List<MergeEntry>? mergeEntries = null;

            foreach (MissionSummary summary in candidates.OrderBy(item => item.CreatedUtc))
            {
                token.ThrowIfCancellationRequested();
                if (String.IsNullOrWhiteSpace(summary.VoyageId)) continue;

                Voyage? voyage = await ReadCachedAsync(voyages, summary.VoyageId!,
                    id => _Database.Voyages.ReadAsync(id, token)).ConfigureAwait(false);
                if (voyage == null || !TerminalVoyageMissionRule.IsTerminalVoyage(voyage.Status)) continue;

                DateTime endedUtc = voyage.CompletedUtc ?? voyage.LastUpdateUtc;
                if (!request.IncludeHistorical && nowUtc - endedUtc > request.Lookback) continue;

                mergeEntries ??= await _Database.MergeEntries.EnumerateAsync(token).ConfigureAwait(false);
                List<MergeEntry> entries = mergeEntries
                    .Where(entry => String.Equals(entry.MissionId, summary.Id, StringComparison.Ordinal))
                    .ToList();
                bool landingInFlight = entries.Any(entry => TerminalVoyageMissionRule.IsLandingInFlight(entry.Status));

                Vessel? vessel = String.IsNullOrWhiteSpace(summary.VesselId)
                    ? null
                    : await ReadCachedAsync(vessels, summary.VesselId!,
                        id => _Database.Vessels.ReadAsync(id, token)).ConfigureAwait(false);
                bool noLandingRequested = (voyage.LandingMode ?? vessel?.LandingMode) == LandingModeEnum.None;

                TerminalVoyageLandingProbeEnum landing = entries.Any(entry => entry.Status == MergeStatusEnum.Landed)
                    ? TerminalVoyageLandingProbeEnum.Landed
                    : await ProbeLandingAsync(vessel, summary.CommitHash, targetCommits, token).ConfigureAwait(false);

                TerminalVoyageMissionDecision decision = TerminalVoyageMissionRule.Decide(
                    voyage.Status,
                    endedUtc,
                    nowUtc,
                    request.Grace,
                    landingInFlight,
                    noLandingRequested,
                    landing);

                if (decision.TargetStatus.HasValue && !request.DryRun)
                {
                    bool applied = await ApplyAsync(summary.Id, voyage, landing, decision, token).ConfigureAwait(false);
                    if (!applied)
                    {
                        decision = TerminalVoyageMissionDecision.Keep(TerminalVoyageMissionRule.ReasonStatusChanged);
                    }
                }

                Record(result, request, summary, voyage, landing, decision);
            }

            result.Summary = BuildSummary(result);
            if (result.Completed + result.Failed + result.Cancelled > 0 || CountUnknown(result) > 0)
            {
                _Logging.Info(_Header + result.Summary);
            }
            else
            {
                _Logging.Debug(_Header + result.Summary);
            }
            return result;
        }

        #endregion

        #region Private-Methods

        private async Task<List<MissionSummary>> ReadWorkProducedSummariesAsync(
            TerminalVoyageMissionReconciliationRequest request,
            CancellationToken token)
        {
            List<MissionSummary> summaries = new List<MissionSummary>();
            int pageNumber = 1;
            while (true)
            {
                EnumerationQuery query = new EnumerationQuery
                {
                    PageNumber = pageNumber,
                    PageSize = _SummaryPageSize,
                    Status = MissionStatusEnum.WorkProduced.ToString(),
                    VoyageId = String.IsNullOrWhiteSpace(request.VoyageId) ? null : request.VoyageId!.Trim(),
                    VesselId = String.IsNullOrWhiteSpace(request.VesselId) ? null : request.VesselId!.Trim()
                };
                EnumerationResult<MissionSummary> page = await _Database.Missions
                    .EnumerateMissionSummariesAsync(query, token).ConfigureAwait(false);
                // The status filter is the contract this pass relies on; re-check it so a provider that
                // ignored the filter cannot hand a non-WorkProduced mission to the rule.
                summaries.AddRange(page.Objects.Where(item => item.Status == MissionStatusEnum.WorkProduced));
                if (page.Objects.Count < _SummaryPageSize || pageNumber >= page.TotalPages) break;
                pageNumber++;
            }
            return summaries;
        }

        private static async Task<T?> ReadCachedAsync<T>(Dictionary<string, T?> cache, string id, Func<string, Task<T?>> read)
            where T : class
        {
            if (cache.TryGetValue(id, out T? cached)) return cached;
            T? value = await read(id).ConfigureAwait(false);
            cache[id] = value;
            return value;
        }

        private async Task<TerminalVoyageLandingProbeEnum> ProbeLandingAsync(
            Vessel? vessel,
            string? commitHash,
            Dictionary<string, string?> targetCommits,
            CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(commitHash)) return TerminalVoyageLandingProbeEnum.NoCommit;
            if (vessel == null || String.IsNullOrWhiteSpace(vessel.LocalPath) || String.IsNullOrWhiteSpace(vessel.DefaultBranch))
                return TerminalVoyageLandingProbeEnum.Unknown;

            // Resolve the target first: only a repository whose default branch resolves can prove that a
            // commit is absent. Every failure before that point is unknown, never "not landed".
            if (!targetCommits.TryGetValue(vessel.Id, out string? target))
            {
                target = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath!, vessel.DefaultBranch, token).ConfigureAwait(false);
                targetCommits[vessel.Id] = target;
            }
            if (String.IsNullOrWhiteSpace(target)) return TerminalVoyageLandingProbeEnum.Unknown;

            string? commit = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath!, commitHash!.Trim(), token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(commit)) return TerminalVoyageLandingProbeEnum.CommitAbsent;

            bool? landed = await _Git.TryIsAncestorAsync(vessel.LocalPath!, commit!, target!, token).ConfigureAwait(false);
            if (landed == true) return TerminalVoyageLandingProbeEnum.Landed;
            if (landed == false) return TerminalVoyageLandingProbeEnum.NotLanded;
            return TerminalVoyageLandingProbeEnum.Unknown;
        }

        private async Task<bool> ApplyAsync(
            string missionId,
            Voyage voyage,
            TerminalVoyageLandingProbeEnum landing,
            TerminalVoyageMissionDecision decision,
            CancellationToken token)
        {
            Mission? mission = await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
            if (mission == null || mission.Status != MissionStatusEnum.WorkProduced) return false;

            MissionStatusEnum target = decision.TargetStatus!.Value;
            if (!MissionStateMachine.IsValidTransition(mission.Status, target))
            {
                throw new InvalidOperationException("terminal-voyage rule chose illegal transition "
                    + mission.Status + " -> " + target + " for mission " + mission.Id);
            }

            DateTime now = DateTime.UtcNow;
            if (target != MissionStatusEnum.Complete)
                TerminalVoyageMissionRule.RecordReconciledOutcome(mission, voyage.Status, decision.Reason, now);
            mission.Status = target;
            mission.ProcessId = null;
            mission.CompletedUtc ??= now;
            mission.LastUpdateUtc = now;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

            ArmadaEvent evt = new ArmadaEvent
            {
                TenantId = mission.TenantId,
                UserId = mission.UserId,
                EventType = EventType,
                EntityType = "mission",
                EntityId = mission.Id,
                MissionId = mission.Id,
                VoyageId = voyage.Id,
                VesselId = mission.VesselId,
                CaptainId = mission.CaptainId,
                Message = "Mission " + mission.Id + " moved from WorkProduced to " + target
                    + " because voyage " + voyage.Id + " ended " + voyage.Status + " (" + decision.Reason + ")",
                Payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    missionId = mission.Id,
                    voyageId = voyage.Id,
                    voyageStatus = voyage.Status.ToString(),
                    fromStatus = MissionStatusEnum.WorkProduced.ToString(),
                    toStatus = target.ToString(),
                    landing = landing.ToString(),
                    reason = decision.Reason,
                    commitHash = mission.CommitHash
                })
            };
            try
            {
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "mission " + mission.Id + " moved to " + target
                    + " but its reconciliation event was not recorded: " + ex.Message);
            }
            return true;
        }

        private static void Record(
            TerminalVoyageMissionReconciliationResult result,
            TerminalVoyageMissionReconciliationRequest request,
            MissionSummary summary,
            Voyage voyage,
            TerminalVoyageLandingProbeEnum landing,
            TerminalVoyageMissionDecision decision)
        {
            result.Examined++;
            if (decision.TargetStatus == MissionStatusEnum.Complete) result.Completed++;
            else if (decision.TargetStatus == MissionStatusEnum.Failed) result.Failed++;
            else if (decision.TargetStatus == MissionStatusEnum.Cancelled) result.Cancelled++;
            else result.Kept++;

            result.Reasons.TryGetValue(decision.Reason, out int count);
            result.Reasons[decision.Reason] = count + 1;

            if (result.Items.Count >= request.MaxItems)
            {
                result.ItemsTruncated = true;
                return;
            }

            result.Items.Add(new TerminalVoyageMissionReconciliationItem
            {
                MissionId = summary.Id,
                VoyageId = voyage.Id,
                VesselId = summary.VesselId,
                Persona = summary.Persona,
                CommitHash = summary.CommitHash,
                VoyageStatus = voyage.Status,
                Landing = landing,
                FromStatus = MissionStatusEnum.WorkProduced,
                ToStatus = decision.TargetStatus,
                Reason = decision.Reason
            });
        }

        private static int CountUnknown(TerminalVoyageMissionReconciliationResult result)
        {
            return result.Reasons.TryGetValue(TerminalVoyageMissionRule.ReasonAncestryUnknown, out int count) ? count : 0;
        }

        private static string BuildSummary(TerminalVoyageMissionReconciliationResult result)
        {
            string reasons = result.Reasons.Count == 0
                ? "none"
                : String.Join(", ", result.Reasons.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => pair.Key + "=" + pair.Value));
            return "terminal voyage mission reconciliation " + (result.DryRun ? "(dry run)" : "(applied)")
                + (result.IncludeHistorical ? " including historical voyages" : " for recently ended voyages")
                + ": examined " + result.Examined
                + ", complete " + result.Completed
                + ", failed " + result.Failed
                + ", cancelled " + result.Cancelled
                + ", kept " + result.Kept
                + "; reasons: " + reasons;
        }

        #endregion
    }
}
