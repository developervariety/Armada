namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private const string _FindingAppliedEvent = JudgeFindingTriage.AppliedEvent;

        private async Task<JudgeFindingTriageResult> EvaluateJudgeFindingsAsync(Mission mission, bool allowEmpty, CancellationToken token)
        {
            Vessel? vessel = String.IsNullOrWhiteSpace(mission.VesselId) ? null
                : await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
            JudgeFindingTriageResult result = JudgeFindingTriage.Evaluate(mission.AgentOutput, vessel?.JudgeFindingThreshold ?? 5, allowEmpty);
            if (vessel == null) result.Error = JudgeFindingTriage.InvalidReason + " vessel threshold unavailable";
            await RecordJudgeFindingsAsync(mission, result, "judge.findings_evaluated", token).ConfigureAwait(false);
            return result;
        }

        private async Task RecordJudgeFindingsAsync(Mission mission, JudgeFindingTriageResult result,
            string eventType, CancellationToken token)
        {
            await _Database.Events.CreateAsync(new ArmadaEvent(eventType,
                result.Error ?? ("Judge findings: " + result.Blocking.Count + " blockers, "
                    + result.FollowUps.Count + " follow-ups; threshold " + result.Threshold))
            {
                EntityType = "mission", EntityId = mission.Id, MissionId = mission.Id,
                VesselId = mission.VesselId, VoyageId = mission.VoyageId,
                TenantId = mission.TenantId, UserId = mission.UserId,
                Payload = JsonSerializer.Serialize(result)
            }, token).ConfigureAwait(false);
        }

        private async Task<JudgeFindingTriageResult?> ReadAppliedJudgeFindingsAsync(Mission mission, CancellationToken token)
        {
            List<ArmadaEvent> events = await _Database.Events.EnumerateByMissionAsync(mission.Id, 100, token).ConfigureAwait(false);
            foreach (ArmadaEvent record in events)
            {
                if (record.EventType != _FindingAppliedEvent || String.IsNullOrWhiteSpace(record.Payload)) continue;
                try
                {
                    JudgeFindingTriageResult? result = JudgeFindingTriage.ReadApplied(record.Payload, mission.AgentOutput);
                    if (result != null) return result;
                }
                catch (JsonException ex) { _Logging.Warn(_Header + "could not read stored finding triage: " + ex.Message); }
            }
            return null;
        }

        private static string AppendFindingFormatCorrection(Mission mission, string? reason)
        {
            StringBuilder text = new StringBuilder((mission.Description ?? String.Empty).TrimEnd());
            text.Append("\n\n").Append(JudgeFindingTriage.CorrectionHeading).AppendLine();
            text.AppendLine(reason);
            text.AppendLine("Correct the finding format once on the same reviewed work. Preserve your evidence and judgment.");
            text.AppendLine(JudgeFindingTriage.OutputInstruction);
            text.AppendLine("The previous review is retained below for reference:");
            string prior = RuntimeLogFormatter.RedactSecrets(mission.AgentOutput ?? String.Empty);
            if (prior.Length > _MaxReportOnlyStageOutputChars)
                prior = BuildBoundedMissionOutputPreview(prior, _MaxReportOnlyStageOutputChars / 2);
            foreach (string line in prior.Replace("\r\n", "\n").Split('\n')) text.Append("    ").AppendLine(line);
            return text.ToString();
        }

        private static string? CombineFindingFollowUps(string? original, JudgeFindingTriageResult? result)
        {
            string scored = result?.IsValid == true ? JudgeFindingTriage.Format(result.FollowUps) : String.Empty;
            if (String.IsNullOrEmpty(scored)) return original;
            return String.IsNullOrWhiteSpace(original) ? scored : original.TrimEnd() + "\n" + scored;
        }
    }
}
