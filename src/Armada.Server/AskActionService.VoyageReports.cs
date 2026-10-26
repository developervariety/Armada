namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    public sealed partial class AskActionService
    {
        private async Task AddVoyageReportAsync(AuthContext human, string id, Dictionary<string, AskWorkReport> reports, CancellationToken token)
        {
            string key = AskWorkKindEnum.Voyage + ":" + id;
            Voyage? voyage = await CallerScopedRead.ReadVoyageAsync(_Database, human, id, token).ConfigureAwait(false);
            if (voyage == null) { reports[key] = Missing(AskWorkKindEnum.Voyage, id); return; }
            List<MissionSummary> missions = await _Database.Missions.EnumerateMissionSummariesByVoyageAsync(id, token).ConfigureAwait(false);
            List<MissionSummary> visible = missions.Where(item => OwnershipPolicy.CanView(human, item.TenantId, item.UserId, OwnershipScopeEnum.UserSpecific)).ToList();
            reports[key] = new AskWorkReport
            {
                Kind = AskWorkKindEnum.Voyage, Id = voyage.Id, Title = SecretRedactor.Redact(voyage.Title),
                Status = voyage.Status.ToString(), DeliveryState = "See mission delivery", UpdatedUtc = voyage.LastUpdateUtc,
                ProducedWork = visible.Any(item => !String.IsNullOrWhiteSpace(item.CommitHash) || item.Status == MissionStatusEnum.WorkProduced),
                Summary = "Voyage completion and source delivery are separate. Delivery is reported for each linked mission."
            };
            foreach (MissionSummary mission in visible.Take(64))
                await AddMissionReportAsync(human, mission.Id, reports, token).ConfigureAwait(false);
        }

        private AskWorkReport ReadJobReport(string id)
        {
            if (!_Jobs.TryGetStatus(id, out LongRunningJob? job) || job == null) return Missing(AskWorkKindEnum.Job, id);
            bool known = job.Status == LongRunningJobStatusEnum.Succeeded || job.Status == LongRunningJobStatusEnum.Failed;
            return new AskWorkReport
            {
                Kind = AskWorkKindEnum.Job, Id = id, Title = job.Operation, Status = job.Status.ToString(),
                DeliveryState = known ? "Job outcome recorded; see linked work" : "Execution outcome unconfirmed",
                UpdatedUtc = job.CompletedAtUtc ?? job.StartedAtUtc ?? job.SubmittedAtUtc,
                Summary = known ? "The job has a recorded execution result. This is separate from work delivery."
                    : "The job is running or its outcome is uncertain. It will not be repeated by this conversation."
            };
        }
    }
}
