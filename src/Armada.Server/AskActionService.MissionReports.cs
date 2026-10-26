namespace Armada.Server
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    public sealed partial class AskActionService
    {
        private async Task<AskWorkReport> ReadMissionReportAsync(Mission mission, CancellationToken token)
        {
            ProductionFactPage<MissionAttemptFact> facts = await ReadMissionFactsAsync(mission.TenantId, mission.Id, token).ConfigureAwait(false);
            int lastReset = facts.Items.FindLastIndex(item => item.FactType == MissionAttemptFactTypeEnum.AttemptStarted
                || item.FactType == MissionAttemptFactTypeEnum.Restarted || item.FactType == MissionAttemptFactTypeEnum.Retried);
            bool landed = facts.Items.Skip(lastReset + 1).Any(item => item.FactType == MissionAttemptFactTypeEnum.Landed);
            bool delivered = mission.Status == MissionStatusEnum.Complete && landed;
            bool produced = !String.IsNullOrWhiteSpace(mission.CommitHash) || mission.Status == MissionStatusEnum.WorkProduced
                || mission.Status == MissionStatusEnum.PullRequestOpen || delivered;
            string delivery = delivered ? "Landed" : mission.Status == MissionStatusEnum.LandingFailed ? "Landing failed"
                : produced ? "Work produced; delivery unconfirmed" : "Delivery unconfirmed";
            string summary = delivered ? "A current-attempt landing fact confirms delivery."
                : mission.Status == MissionStatusEnum.LandingFailed ? "Recorded work remains separate from its failed landing."
                : "The mission's current status does not by itself establish source delivery.";
            EnumerationResult<CheckRun> checks = await _Database.CheckRuns.EnumerateAsync(new CheckRunQuery
            {
                TenantId = mission.TenantId, MissionId = mission.Id, PageSize = 200, IncludeOutput = false
            }, token).ConfigureAwait(false);
            if (checks.Objects.Count > 0)
                summary += " Recent check records: " + checks.Objects.Count(item => item.Status == CheckRunStatusEnum.Passed)
                    + " passed, " + checks.Objects.Count(item => item.Status == CheckRunStatusEnum.Failed)
                    + " failed, " + checks.Objects.Count(item => item.Status == CheckRunStatusEnum.Running || item.Status == CheckRunStatusEnum.Pending)
                    + " pending or running. These counts are not a readiness verdict.";
            return new AskWorkReport
            {
                Kind = AskWorkKindEnum.Mission, Id = mission.Id, Title = SecretRedactor.Redact(mission.Title),
                Status = mission.Status.ToString(), DeliveryState = delivery, ProducedWork = produced, Delivered = delivered,
                ParentMissionId = mission.ParentMissionId ?? facts.Items.LastOrDefault(item => item.IsRescue)?.ParentMissionId,
                Summary = summary, UpdatedUtc = mission.LastUpdateUtc
            };
        }
    }
}
