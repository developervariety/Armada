namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private async Task<string?> ReadIncompleteReportStatementAsync(Mission mission, CancellationToken token)
        {
            if (!ReportIncompleteResult.TryRead(mission.AgentOutput, out string statement)) return null;
            if (String.IsNullOrWhiteSpace(mission.VoyageId)) return statement;
            ReportIncompleteResult.PendingReviewObligations? obligations = null;
            try
            {
                List<Mission> stages = await _Database.Missions.EnumerateByVoyageAsync(mission.VoyageId, token).ConfigureAwait(false);
                Dictionary<string, Mission> owned = stages.Where(stage =>
                    String.Equals(stage.VesselId, mission.VesselId, StringComparison.Ordinal)
                    && String.Equals(stage.TenantId ?? Constants.DefaultTenantId, mission.TenantId ?? Constants.DefaultTenantId, StringComparison.Ordinal)
                    && String.Equals(stage.UserId ?? Constants.DefaultUserId, mission.UserId ?? Constants.DefaultUserId, StringComparison.Ordinal))
                    .ToDictionary(stage => stage.Id, StringComparer.Ordinal);
                foreach (Mission judge in owned.Values.Where(stage => stage.Status == MissionStatusEnum.Pending
                    && String.Equals(stage.Persona, "Judge", StringComparison.Ordinal)))
                {
                    Dictionary<string, string> duties = new Dictionary<string, string>(StringComparer.Ordinal) { [judge.Id] = PersonaCatalog.Judge };
                    string? parentId = judge.DependsOnMissionId;
                    HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
                    while (!String.IsNullOrWhiteSpace(parentId) && visited.Add(parentId))
                    {
                        if (String.Equals(parentId, mission.Id, StringComparison.Ordinal))
                        {
                            obligations = new ReportIncompleteResult.PendingReviewObligations(mission.Persona, duties);
                            break;
                        }
                        if (!owned.TryGetValue(parentId, out Mission? parent) || parent.Status != MissionStatusEnum.Pending) break;
                        string role = PersonaCatalog.NormalizeName(parent.Persona);
                        duties.Add(parent.Id, role);
                        parentId = parent.DependsOnMissionId;
                    }
                    if (obligations != null) break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not verify downstream Judge eligibility for mission " + mission.Id
                    + "; unmet report criteria remain blocking: " + ex.GetType().Name);
                return statement;
            }
            return ReportIncompleteResult.TryRead(mission.AgentOutput, obligations, out statement) ? statement : null;
        }
    }
}
