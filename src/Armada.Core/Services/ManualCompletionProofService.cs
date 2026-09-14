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

    /// <summary>
    /// Proves the immutable conditions required by an operator status transition to Complete.
    /// </summary>
    /// <remarks>
    /// This service is deliberately separate from the HTTP route. A route must not be able to turn
    /// an unlanded Implementation mission into a terminal success just because a dock was removed.
    /// Read-only missions keep their report-only completion contract. Code missions require target
    /// ancestry when no active landing pipeline will perform the landing, and every participating
    /// failed, unresolved, or stale Check blocks completion.
    /// </remarks>
    public sealed class ManualCompletionProofService
    {
        private readonly DatabaseDriver _Database;
        private readonly IGitService _Git;

        /// <summary>
        /// Initializes the proof service.
        /// </summary>
        /// <param name="database">Database used to resolve the vessel and Checks.</param>
        /// <param name="git">Git service used for the three-state ancestry proof.</param>
        public ManualCompletionProofService(DatabaseDriver database, IGitService git)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
        }

        /// <summary>
        /// Evaluates manual completion before a route mutates the mission.
        /// </summary>
        /// <param name="mission">Mission proposed for manual completion.</param>
        /// <param name="activeLandingPipeline">True when the active dock will perform the landing.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A passing or fail-closed proof result.</returns>
        public async Task<ManualCompletionProofResult> EvaluateAsync(
            Mission mission,
            bool activeLandingPipeline,
            CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            if (mission.Status == MissionStatusEnum.Review || mission.RequiresReview)
            {
                return ManualCompletionProofResult.Fail("manual_completion_review_required");
            }

            if (PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Judge))
            {
                return ManualCompletionProofResult.Fail("manual_completion_judge_required");
            }

            List<Mission>? voyageMissions = null;
            if (!String.IsNullOrWhiteSpace(mission.VoyageId))
            {
                voyageMissions = await _Database.Missions
                    .EnumerateByVoyageAsync(mission.VoyageId, token).ConfigureAwait(false);
                bool hasDependentPipelineStage = voyageMissions.Any(candidate =>
                    String.Equals(candidate.DependsOnMissionId, mission.Id, StringComparison.Ordinal));
                if (!hasDependentPipelineStage && voyageMissions.Any(candidate => candidate.Id != mission.Id
                    && PersonaCatalog.Matches(candidate.Persona, PersonaCatalog.Judge)
                    && candidate.Status != MissionStatusEnum.Complete))
                {
                    return ManualCompletionProofResult.Fail("manual_completion_judge_required");
                }
            }

            if (mission.IsReadOnlyMode && (voyageMissions == null
                || VoyageReportOnlyClassifier.IsFullyReportOnly(voyageMissions)))
            {
                return ManualCompletionProofResult.Pass("report_only");
            }

            List<CheckRun> checks = await ReadChecksAsync(mission, token).ConfigureAwait(false);
            foreach (CheckRun check in checks)
            {
                if (!CheckRunGateRules.ParticipatesInRealSignalGate(check, mission.CommitHash)) continue;
                if (CheckRunGateRules.IsStale(check, mission.CommitHash))
                {
                    return ManualCompletionProofResult.Fail("manual_completion_stale_check");
                }
                if (check.Status == CheckRunStatusEnum.Failed)
                {
                    return ManualCompletionProofResult.Fail("manual_completion_failed_check");
                }
                if (CheckRunGateRules.IsUnresolved(check, mission.CommitHash))
                {
                    return ManualCompletionProofResult.Fail("manual_completion_pending_check");
                }
            }

            if (activeLandingPipeline)
            {
                return ManualCompletionProofResult.Pass("landing_pipeline");
            }

            Vessel? vessel = String.IsNullOrWhiteSpace(mission.VesselId)
                ? null
                : await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
            if (vessel == null || String.IsNullOrWhiteSpace(vessel.LocalPath)
                || String.IsNullOrWhiteSpace(vessel.DefaultBranch)
                || String.IsNullOrWhiteSpace(mission.CommitHash))
            {
                return ManualCompletionProofResult.Fail("manual_completion_ancestry_unavailable");
            }

            bool? isLanded;
            try
            {
                isLanded = await _Git.TryIsAncestorAsync(
                    vessel.LocalPath,
                    mission.CommitHash,
                    vessel.DefaultBranch,
                    token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                isLanded = null;
            }
            if (isLanded != true)
            {
                return ManualCompletionProofResult.Fail(
                    isLanded == false
                        ? "manual_completion_unlanded"
                        : "manual_completion_ancestry_unknown");
            }

            return ManualCompletionProofResult.Pass("target_ancestry");
        }

        private async Task<List<CheckRun>> ReadChecksAsync(Mission mission, CancellationToken token)
        {
            List<CheckRunQuery> queries = new List<CheckRunQuery>();
            if (!String.IsNullOrWhiteSpace(mission.VoyageId))
            {
                queries.Add(new CheckRunQuery { VoyageId = mission.VoyageId });
            }

            queries.Add(new CheckRunQuery { MissionId = mission.Id });
            Dictionary<string, CheckRun> checks = await CheckRunEnumeration
                .ReadAllAsync(_Database, queries, token).ConfigureAwait(false);
            return checks.Values.ToList();
        }
    }

    /// <summary>
    /// Immutable result of the manual completion proof.
    /// </summary>
    public sealed class ManualCompletionProofResult
    {
        private ManualCompletionProofResult(bool allowed, string reason)
        {
            Allowed = allowed;
            Reason = reason;
        }

        /// <summary>True when the route may proceed.</summary>
        public bool Allowed { get; }

        /// <summary>Stable reason suitable for an API response and audit event.</summary>
        public string Reason { get; }

        /// <summary>Creates a passing result.</summary>
        public static ManualCompletionProofResult Pass(string reason) => new ManualCompletionProofResult(true, reason);

        /// <summary>Creates a fail-closed result.</summary>
        public static ManualCompletionProofResult Fail(string reason) => new ManualCompletionProofResult(false, reason);
    }
}
