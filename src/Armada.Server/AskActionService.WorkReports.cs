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
        /// <summary>Read bounded, fresh observations of the exact work returned by approved tools.</summary>
        public async Task<List<AskWorkReport>?> WorkReportsAsync(AuthContext caller, string threadId, CancellationToken token = default)
        {
            AuthContext? human = await HumanForThreadAsync(caller, threadId, token).ConfigureAwait(false);
            if (human == null) return null;
            List<AskActionProposal>? proposals = await ListAsync(human, threadId, token).ConfigureAwait(false);
            if (proposals == null) return null;
            Dictionary<string, AskWorkReport> reports = new Dictionary<string, AskWorkReport>(StringComparer.Ordinal);
            foreach (AskWorkReference reference in proposals.SelectMany(item => item.WorkReferences).Take(200))
            {
                string key = reference.Kind + ":" + reference.Id;
                if (reports.ContainsKey(key)) continue;
                if (reference.Kind == AskWorkKindEnum.Job) reports[key] = ReadJobReport(reference.Id);
                else if (reference.Kind == AskWorkKindEnum.Mission)
                    await AddMissionReportAsync(human, reference.Id, reports, token).ConfigureAwait(false);
                else if (reference.Kind == AskWorkKindEnum.Voyage)
                    await AddVoyageReportAsync(human, reference.Id, reports, token).ConfigureAwait(false);
            }
            return reports.Values.Take(200).ToList();
        }

        private async Task AddMissionReportAsync(AuthContext human, string id, Dictionary<string, AskWorkReport> reports, CancellationToken token)
        {
            string key = AskWorkKindEnum.Mission + ":" + id;
            if (reports.ContainsKey(key) || reports.Count >= 200) return;
            Mission? mission = await CallerScopedRead.ReadMissionAsync(_Database, human, id, token).ConfigureAwait(false);
            if (mission == null) { reports[key] = Missing(AskWorkKindEnum.Mission, id); return; }
            reports[key] = await ReadMissionReportAsync(mission, token).ConfigureAwait(false);
            List<MissionSummary> children = await _Database.Missions.EnumerateMissionSummariesByParentAsync(id, token).ConfigureAwait(false);
            foreach (MissionSummary child in children.OrderByDescending(item => item.LastUpdateUtc).Take(16))
            {
                if (!OwnershipPolicy.CanView(human, child.TenantId, child.UserId, OwnershipScopeEnum.UserSpecific)) continue;
                ProductionFactPage<MissionAttemptFact> facts = await ReadMissionFactsAsync(child.TenantId, child.Id, token).ConfigureAwait(false);
                if (!facts.Items.Any(item => item.IsRescue && item.ParentMissionId == id)) continue;
                reports[key].RescueMissionId ??= child.Id;
                string childKey = AskWorkKindEnum.Mission + ":" + child.Id;
                if (reports.ContainsKey(childKey) || reports.Count >= 200) continue;
                Mission? rescue = await CallerScopedRead.ReadMissionAsync(_Database, human, child.Id, token).ConfigureAwait(false);
                if (rescue != null) reports[childKey] = await ReadMissionReportAsync(rescue, token).ConfigureAwait(false);
            }
        }

        private async Task<ProductionFactPage<MissionAttemptFact>> ReadMissionFactsAsync(string? tenantId, string id, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) return new ProductionFactPage<MissionAttemptFact>();
            return await _Database.MissionAttemptFacts.EnumerateByMissionAsync(tenantId, id, 1000, token).ConfigureAwait(false);
        }

        private static AskWorkReport Missing(AskWorkKindEnum kind, string id) => new AskWorkReport
        {
            Kind = kind, Id = id, Title = id, Status = "Missing", DeliveryState = "Unconfirmed",
            Summary = "The exact recorded work identifier is no longer readable. No replacement or title match is inferred."
        };
    }
}
