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
            bool pendingJudge = false;
            int pendingReviewCount = 0;
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
                    HashSet<string> reviewRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PersonaCatalog.Judge };
                    string? parentId = judge.DependsOnMissionId;
                    HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
                    while (!String.IsNullOrWhiteSpace(parentId) && visited.Add(parentId))
                    {
                        if (String.Equals(parentId, mission.Id, StringComparison.Ordinal))
                        {
                            pendingJudge = true;
                            pendingReviewCount = reviewRoles.Count;
                            break;
                        }
                        if (!owned.TryGetValue(parentId, out Mission? parent) || parent.Status != MissionStatusEnum.Pending) break;
                        string role = PersonaCatalog.NormalizeName(parent.Persona);
                        if (PersonaCatalog.Matches(role, "PortingReferenceAnalyst") || PersonaCatalog.Matches(role, PersonaCatalog.TestEngineer)
                            || PersonaCatalog.Matches(role, PersonaCatalog.Linter) || PersonaCatalog.Matches(role, PersonaCatalog.Judge))
                            reviewRoles.Add(role);
                        parentId = parent.DependsOnMissionId;
                    }
                    if (pendingJudge) break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not verify downstream Judge eligibility for mission " + mission.Id
                    + "; unmet report criteria remain blocking: " + ex.GetType().Name);
                return statement;
            }
            return ReportIncompleteResult.TryRead(mission.AgentOutput, pendingJudge, pendingReviewCount, out statement) ? statement : null;
        }
    }
}
