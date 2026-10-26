namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private async Task<ArchitectAsyncParseResult> ParseArchitectOutputAsync(
            Mission architectMission, CancellationToken token)
        {
            ArchitectAsyncParseResult result = new ArchitectAsyncParseResult();
            int maximum = _Settings.Architect.MaxMissionsPerVoyage;
            if (!String.IsNullOrWhiteSpace(architectMission.VesselId))
            {
                Vessel? vessel = String.IsNullOrWhiteSpace(architectMission.TenantId)
                    ? await _Database.Vessels.ReadAsync(architectMission.VesselId!, token).ConfigureAwait(false)
                    : await _Database.Vessels.ReadAsync(architectMission.TenantId!, architectMission.VesselId!, token).ConfigureAwait(false);
                if (vessel?.ArchitectMaxMissionsPerVoyage.HasValue == true)
                    maximum = Math.Max(1, Math.Min(50, vessel.ArchitectMaxMissionsPerVoyage.Value));
            }

            string?[] sources = { architectMission.AgentOutput, architectMission.DiffSnapshot };
            foreach (string? source in sources)
            {
                if (String.IsNullOrWhiteSpace(source) || !ArchitectVersionedPlanParser.ContainsPlanFence(source)) continue;
                result.Missions = ParseVersionedArchitectOutput(source, maximum, out string? failure);
                if (result.Missions.Count == 0) result.InvalidPlanReason = failure;
                return result;
            }

            List<ParsedArchitectMission> legacy = ParseArchitectOutput(architectMission, out string? fallbackReason);
            result.Missions = legacy;
            result.FallbackReason = fallbackReason;
            return result;
        }

        private static List<ParsedArchitectMission> ParseVersionedArchitectOutput(
            string source, int maximum, out string? failureReason)
        {
            failureReason = null;
            ArchitectParseResult result = new ArchitectOutputParser().Parse(source, maximum);
            if (result.Verdict != ArchitectParseVerdict.Valid)
            {
                failureReason = result.Errors.Count > 0
                    ? String.Join("; ", result.Errors.Select(error => error.Message))
                    : result.Verdict == ArchitectParseVerdict.OverCap
                        ? "mission count exceeds the configured Architect limit"
                        : result.Verdict.ToString();
                return new List<ParsedArchitectMission>();
            }
            List<ParsedArchitectMission> parsed = new List<ParsedArchitectMission>();
            foreach (ArchitectMissionEntry entry in result.Missions)
            {
                ParsedArchitectMission mission = new ParsedArchitectMission
                {
                    Title = entry.Title,
                    Description = ArchitectDerivedBriefPreamble + "\n\n" + entry.Description,
                    DependsOnReference = entry.DependsOnMissionAlias,
                    WaitForOtherMissions = entry.WaitForOtherMissions
                };
                parsed.Add(mission);
            }
            return parsed;
        }

    }
}
