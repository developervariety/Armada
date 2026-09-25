namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// The orchestrator side of the change_quality decision: review a focused diff and route the
    /// authoritative, deterministically-backed weaknesses (the routable set) to a single Triaged
    /// objective through the existing follow-up router. The model only ADDS informational weaknesses;
    /// only the deterministic MustFix ones are routed, and the router creates the row with auto-dispatch
    /// OFF, so nothing is ever dispatched. When there is no routable weakness, or no router, nothing is
    /// filed. Never throws into the caller.
    /// </summary>
    public static class ChangeQualityGate
    {
        #region Public-Methods

        /// <summary>
        /// Review a diff and, when it carries a routable weakness, file exactly one Triaged objective.
        /// </summary>
        /// <param name="diff">The focused unified diff.</param>
        /// <param name="vesselId">The vessel the change belongs to.</param>
        /// <param name="reviewedMissionId">The mission whose change is reviewed, for the follow-up record.</param>
        /// <param name="centralPackageManagement">Whether CPM is enabled, for the Slop project rules.</param>
        /// <param name="adapter">The change_quality adapter; null runs the deterministic rule only.</param>
        /// <param name="router">The follow-up router; null files nothing.</param>
        /// <param name="logging">Optional logging.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The verdict and the created objective id, if one was filed.</returns>
        public static async Task<ChangeQualityGateResult> ReviewAndRouteAsync(
            string? diff,
            string? vesselId,
            string? reviewedMissionId,
            bool centralPackageManagement,
            TypedChangeQualityAdapter? adapter,
            IFollowUpRouter? router,
            SyslogLogging.LoggingModule? logging,
            CancellationToken token)
        {
            IReadOnlyList<ChangeQualityWeakness> ruleWeaknesses = ChangeQualityRules.Evaluate(diff, centralPackageManagement);
            ChangeQualityVerdict verdict = ChangeQualityVerdict.From(ruleWeaknesses);

            if (adapter != null)
            {
                try
                {
                    ChangeQualityInput input = new ChangeQualityInput { UnifiedDiff = diff ?? String.Empty, CentralPackageManagement = centralPackageManagement, MissionId = reviewedMissionId, VesselId = vesselId };
                    verdict = await adapter.DecideAsync(input, ChangeQualityVerdict.From(ruleWeaknesses), token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The decision must never break the review; keep the deterministic verdict.
                    logging?.Warn("[ChangeQualityGate] change_quality decision failed, deterministic verdict stands: " + ex.Message);
                    verdict = ChangeQualityVerdict.From(ruleWeaknesses);
                }
            }

            IReadOnlyList<ChangeQualityWeakness> routable = verdict.RoutableWeaknesses;
            if (routable.Count == 0 || router == null)
                return new ChangeQualityGateResult { Verdict = verdict, CreatedObjectiveId = null };

            string? createdId = null;
            try
            {
                FollowUpRouteRequest request = BuildRouteRequest(vesselId, reviewedMissionId, routable);
                createdId = await router.CreateTriagedObjectiveAsync(request, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logging?.Warn("[ChangeQualityGate] could not file the change_quality Triaged row: " + ex.Message);
            }

            return new ChangeQualityGateResult { Verdict = verdict, CreatedObjectiveId = createdId };
        }

        #endregion

        #region Private-Methods

        private static FollowUpRouteRequest BuildRouteRequest(
            string? vesselId,
            string? reviewedMissionId,
            IReadOnlyList<ChangeQualityWeakness> routable)
        {
            string mission = String.IsNullOrWhiteSpace(reviewedMissionId) ? "change_quality" : reviewedMissionId!;
            List<string> dims = routable.Select(w => w.Dimension).Distinct(StringComparer.Ordinal).ToList();

            StringBuilder body = new StringBuilder();
            body.Append("change_quality flagged ").Append(routable.Count).Append(" routable weakness")
                .Append(routable.Count == 1 ? "" : "es").Append(": ");
            body.Append(String.Join("; ", routable.Select(w => w.Dimension + " (" + w.Reason + ")")));

            JudgeFollowUp followUp = new JudgeFollowUp
            {
                VesselId = vesselId,
                JudgeMissionId = mission,
                ReviewedMissionId = mission,
                JudgeVerdict = "PASS",
                SuggestedFollowUps = body.ToString()
            };

            return new FollowUpRouteRequest
            {
                FollowUp = followUp,
                ItemText = body.ToString(),
                ObjectiveTitle = "change_quality: " + String.Join(", ", dims)
            };
        }

        #endregion
    }

    /// <summary>The result of a change_quality gate review.</summary>
    public sealed class ChangeQualityGateResult
    {
        /// <summary>The verdict (deterministic weaknesses plus any the model added).</summary>
        public ChangeQualityVerdict Verdict { get; init; } = ChangeQualityVerdict.Empty();

        /// <summary>The Triaged objective filed for the routable weaknesses, or null when none was filed.</summary>
        public string? CreatedObjectiveId { get; init; } = null;
    }
}
